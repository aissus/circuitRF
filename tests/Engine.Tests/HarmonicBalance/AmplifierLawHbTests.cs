using System.Globalization;
using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine.HarmonicBalance;
using RfCore.Data;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.HarmonicBalance;

/// <summary>
/// The amplifier's bandpass laws (<c>Law=Rapp</c>, <c>Law=Saleh</c>) in harmonic balance against their closed forms
/// (<see cref="AmplifierLaw"/>): one tone's output and phase at every drive are the law's own, its 1 dB point and
/// saturation where the closed forms put them, with no harmonics; two tones' third-order products where the law's
/// third-order term puts them, through the 2-D solver that has no weighted terms — the quadrature reaches it as a
/// linear companion.
/// </summary>
public class AmplifierLawHbTests(ITestOutputHelper output)
{
    private static string N(double x) => x.ToString("R", CultureInfo.InvariantCulture);

    private static DataSet Run(string cnl)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        var nl = new Elaborator(lib).Elaborate(tb);
        var hba = tb.Analyses.OfType<HarmonicBalanceAnalysis>().First();
        var ds = (DataSet)new HbEngine(nl, tb).Run(HbEngine.Resolve(hba, nl.ResolvedGlobals));
        Assert.True(ds["Converged"].RealValues[0] > 0.5, "HB did not converge");
        return ds;
    }

    private static Complex V(DataSet ds, string node, int k)
    {
        var cube = ds["V"];
        int i = Array.FindIndex(cube.Axes[0].Labels!, n => n.Equals(node, StringComparison.Ordinal));
        return (Complex)cube[i, k];
    }

    private static double Dbm(Complex v) => 10 * Math.Log10(v.Magnitude * v.Magnitude / 100) + 30;

    private const string Rapp = "Law=Rapp  Psat=33 dBm  P=2";
    private const string Saleh = "Law=Saleh  Psat=33 dBm  PhiSat=10 deg  PhiMax=20 deg";

    private static AmplifierLaw LawOf(string law) => law == Rapp ? AmplifierLaw.Rapp(33, 30, 2) : AmplifierLaw.Saleh(33, 30, 10, 20);

    private static string OneTone(double pin, string law) => $@"
P1Tone:S  in 0  Num=1  Freq=3.5 GHz  Pavl={N(pin)} dBm  Z=50 Ohm
Amp:PA  in 0 out 0  Gain=30 dB  {law}
R:RL  out 0  R=50 Ohm
analysis HB1 type=hb Tone=""3.5e9"" ToneUnit=Hz MaxHarm=5 Tol=1e-12
";

    [Theory]
    [InlineData(Rapp, -30.0)]
    [InlineData(Rapp, 1.835)]
    [InlineData(Rapp, 12.0)]
    [InlineData(Saleh, -20.0)]
    [InlineData(Saleh, 9.0206)]
    [InlineData(Saleh, 15.0)]
    public void OneTone_TheOutputAndPhaseAreTheLawsOwn_AndNothingElse(string law, double pin)
    {
        var ds = Run(OneTone(pin, law));
        var l = LawOf(law);
        double pout = Dbm(V(ds, "out", 1));
        double phase = (V(ds, "out", 1) / V(ds, "in", 1)).Phase * 180 / Math.PI;
        output.WriteLine($"{law} at {pin} dBm: {pout:F6} dBm (law {l.OutputDbm(pin, 30):F6}), {phase:F6}° (law {l.AmPmDeg(pin):F6}°)");
        Assert.InRange(pout - l.OutputDbm(pin, 30), -1e-6, 1e-6);
        Assert.InRange(phase - l.AmPmDeg(pin), -1e-6, 1e-6);
        // A bandpass law makes no harmonics: what is left is the solve's own round-off.
        for (int k = 2; k <= 5; k++) Assert.True(Dbm(V(ds, "out", k)) < pout - 150, $"harmonic {k} is {Dbm(V(ds, "out", k)):F1} dBm");
    }

    [Theory]
    [InlineData(Rapp)]
    [InlineData(Saleh)]
    public void TheClosedForms_OneDbAtIP1dB_AndTheSaturatedOutput(string law)
    {
        var l = LawOf(law);
        double atP1 = Dbm(V(Run(OneTone(l.Ip1dbDbm, law)), "out", 1)) - l.Ip1dbDbm;
        output.WriteLine($"{law}: at IP1dB {l.Ip1dbDbm:F4} dBm the gain is {atP1:F6} dB, of 30");
        Assert.InRange(atP1 - 29, -1e-6, 1e-6);
        // Rapp's output tends to Psat; Saleh's peaks at it, 6.02 dB above its saturating input's linear output.
        double top = Dbm(V(Run(OneTone(law == Rapp ? 30 : 9.0206, law)), "out", 1));
        output.WriteLine($"{law}: Psat {l.PsatDbm(30):F4} dBm, simulated {top:F6} dBm");
        Assert.InRange(top - l.PsatDbm(30), law == Rapp ? -1e-3 : -1e-9, 1e-9);
    }

    /// <summary>Two tones 40 dB below the intercept: Saleh's third-order term, AM-PM in it, and Rapp's at p = 1, both in
    /// the 2-D solver. Rapp's budget intercept at p = 1 is the cubic's at its 1 dB point, 0.76 dB above its own.</summary>
    [Theory]
    [InlineData("Law=Saleh  Psat=33 dBm  PhiSat=10 deg  PhiMax=20 deg", 8.7707)]
    [InlineData("Law=Rapp  Psat=33 dBm  P=1", 6.0103)]
    public void TwoTones_TheThirdOrderProductsAreTheLawsThirdOrderTerm(string law, double iip3)
    {
        double pt = iip3 - 40;
        var ds = Run($@"
PnTone:S  in 0  Freq[1]=3.4995 GHz  Pavl[1]={N(pt)} dBm  Phase[1]=0 deg  Freq[2]=3.5005 GHz  Pavl[2]={N(pt)} dBm  Phase[2]=0 deg  Z=50 Ohm
Amp:PA  in 0 out 0  Gain=30 dB  {law}
R:RL  out 0  R=50 Ohm
analysis HB2 type=hb NumFreqs=2 Tone[1]=3.4995e9 Tone[2]=3.5005e9 MaxMixOrder=7 MaxHarm=3 Tol=1e-12
");
        double p1 = Dbm(TwoToneMeasurements.Tone(ds, 0, "out", 1, 0)), p3 = Dbm(TwoToneMeasurements.Tone(ds, 0, "out", 2, -1));
        double oiip3 = p1 + (p1 - p3) / 2 - 30;
        output.WriteLine($"{law}: IIP3 {oiip3:F4} dBm against {iip3:F4}");
        Assert.InRange(oiip3 - iip3, -0.01, 0.01);
        if (law.Contains("Saleh")) Assert.InRange(iip3 - AmplifierLaw.Saleh(33, 30, 10, 20).Iip3Dbm, -1e-3, 1e-3);
    }

    /// <summary>At small signal the law is the gain and nothing else: an S-parameter run of the amplifier reads it.</summary>
    [Fact]
    public void SmallSignal_TheLawIsTheGain()
    {
        var (lib, tb) = new CnlReader().Read($@"
Port:P1  in 0  Num=1  Z=50 Ohm
Amp:PA  in 0 out 0  Gain=30 dB  {Saleh}
Port:P2  out 0  Num=2  Z=50 Ohm
");
        var ds = SParameterEngine.Run(new Elaborator(lib).Elaborate(tb), [3.5e9]);
        var s21 = (Complex)ds["S"][0, 1, 0];
        Assert.InRange(20 * Math.Log10(s21.Magnitude) - 30, -1e-9, 1e-9);
    }
}
