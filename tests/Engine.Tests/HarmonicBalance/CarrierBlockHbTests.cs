using System.Globalization;
using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine.HarmonicBalance;
using RfCore.Data;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.HarmonicBalance;

/// <summary>
/// The polar-transmitter blocks in single-tone harmonic balance: each law, written out here from the netlist line, against
/// a solve that carries the quadrature node; then on two and three tones, against the single-tone solve. A 50 Ω P1Tone into a 50 Ω input puts <c>a = √(2·50·P)</c> across it, at the
/// phase the solve reports at the input node; every output is read across a 50 Ω load, where the law itself appears.
/// The laws make no harmonics, so the harmonics are gated at the solve's floor. A control input is driven as the polar
/// source drives it, from 50 Ω: a Vdc of twice the wanted voltage behind 50 Ω into the block's matched 50 Ω.
/// </summary>
public class CarrierBlockHbTests(ITestOutputHelper output)
{
    private static string N(double x) => x.ToString("R", CultureInfo.InvariantCulture);
    private static double Amp(double dbm) => Math.Sqrt(2 * 50 * 1e-3 * Math.Pow(10, dbm / 10));

    private static DataSet Run(string cnl, AnalysisSettings? settings = null)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        var nl = new Elaborator(lib).Elaborate(tb);
        var hba = tb.Analyses.OfType<HarmonicBalanceAnalysis>().First();
        var ds = (DataSet)new HbEngine(nl, tb, settings ?? new AnalysisSettings()).Run(HbEngine.Resolve(hba, nl.ResolvedGlobals));
        Assert.True(ds["Converged"].RealValues[0] > 0.5, "HB did not converge");
        return ds;
    }

    private static Complex V(DataSet ds, string node, int k)
    {
        var cube = ds["V"];
        int i = Array.FindIndex(cube.Axes[0].Labels!, n => n.Equals(node, StringComparison.Ordinal));
        Assert.True(i >= 0, $"node '{node}' missing from the V cube's node axis");
        return (Complex)cube[i, k];
    }

    private const string Hb = "analysis HB1 type=hb Tone=3.5e9 MaxHarm=5 Tol=1e-12";

    private void NoHarmonics(DataSet ds, string node, double scale, int from = 2)
    {
        for (int k = from; k <= 5; k++)
        {
            double h = V(ds, node, k).Magnitude;
            output.WriteLine($"{node} k={k}: {h:E2} V");
            Assert.True(h < 1e-9 * scale, $"{node}: {h:E3} V at harmonic {k}");
        }
    }

    [Theory]
    [InlineData(-30.0)]
    [InlineData(0.0)]
    [InlineData(10.0)]
    public void Limiter_HoldsItsOutputLevel_AtTheInputsPhase(double pin)
    {
        var ds = Run($"""
            P1Tone:Ps n1 0 Pavl={N(pin)} Z=50 Freq=3.5e9 Phase=40
            Limiter:L1 n1 0 n2 0 Pout=7 Knee=-100
            R:RL n2 0 R=50
            {Hb}
            """);
        var vin = V(ds, "n1", 1);
        var vout = V(ds, "n2", 1);
        double a = Amp(pin), vk = Amp(-100), a0 = Amp(7);
        output.WriteLine($"Pin {pin} dBm: |Vin| {vin.Magnitude:F9} (expect {a:F9}), |Vout| {vout.Magnitude:F9}, phase {vout.Phase - vin.Phase:E2} rad");
        Assert.Equal(a, vin.Magnitude, 9);
        Assert.Equal(a0 * a / Math.Sqrt(a * a + vk * vk), vout.Magnitude, 9);
        Assert.Equal(0.0, Math.IEEERemainder(vout.Phase - vin.Phase, 2 * Math.PI), 9);
        NoHarmonics(ds, "n2", a0);
    }

    [Fact]
    public void Limiter_IsThreeDecibelsDownAtItsKnee()
    {
        var ds = Run($"""
            P1Tone:Ps n1 0 Pavl=-20 Z=50 Freq=3.5e9 Phase=0
            Limiter:L1 n1 0 n2 0 Pout=0 Knee=-20
            R:RL n2 0 R=50
            {Hb}
            """);
        Assert.Equal(Amp(0) / Math.Sqrt(2), V(ds, "n2", 1).Magnitude, 9);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.7)]
    [InlineData(-2.4)]
    public void PhaseModulator_TurnsTheCarrierByKpTimesThePhaseInput(double vph)
    {
        var ds = Run($"""
            P1Tone:Ps n1 0 Pavl=-10 Z=50 Freq=3.5e9 Phase=15
            Vdc:VP phs 0 Vdc={N(2 * vph)}
            R:RP phs ph R=50
            PhaseMod:PM1 n1 0 n2 0 ph 0 Kp=1.5 Gain=3
            R:RL n2 0 R=50
            {Hb}
            """);
        var ratio = V(ds, "n2", 1) / V(ds, "n1", 1);
        var expect = Math.Pow(10, 3.0 / 20) * Complex.FromPolarCoordinates(1, 1.5 * vph);
        output.WriteLine($"PH {vph} V: S21 {ratio} (expect {expect})");
        Assert.Equal(expect.Real, ratio.Real, 9);
        Assert.Equal(expect.Imaginary, ratio.Imaginary, 9);
        NoHarmonics(ds, "n2", 1);
    }

    [Theory]
    [InlineData(1.0, 0.0, 0.0, -20.0)]
    [InlineData(1.0, 0.0, 0.0, 5.0)]
    [InlineData(0.6, 0.0, 12.0, -5.0)]
    [InlineData(2.5, 1.5, -8.0, -5.0)]
    public void SupplyAmp_OutputAmplitudeIsTheSupplyLaw_AtTheInputsPhasePlusAmPm(double vs, double vsat, double ampm, double pin)
    {
        var ds = Run($"""
            P1Tone:Ps n1 0 Pavl={N(pin)} Z=50 Freq=3.5e9 Phase=-30
            Vdc:VS ss 0 Vdc={N(2 * vs)}
            R:RS ss s R=50
            SupplyAmp:A1 n1 0 n2 0 s 0 Ks=4 Vsat={N(vsat)} AMPM={N(ampm)} Knee=-100
            R:RL n2 0 R=50
            {Hb}
            """);
        double ain = Amp(pin), vk = Amp(-100);
        double a = (vsat > 0 ? 4 * vsat * Math.Tanh(vs / vsat) : 4 * vs) * ain / Math.Sqrt(ain * ain + vk * vk);
        var ratio = V(ds, "n2", 1) / V(ds, "n1", 1);
        var vout = V(ds, "n2", 1);
        output.WriteLine($"VS {vs} V: |Vout| {vout.Magnitude:F9} (expect {a:F9}), phase {ratio.Phase * 180 / Math.PI:F6} deg (expect {ampm * vs:F6})");
        Assert.Equal(a, vout.Magnitude, 8);
        Assert.Equal(ampm * vs * Math.PI / 180, ratio.Phase, 9);
        NoHarmonics(ds, "n2", a);
    }

    [Theory]
    [InlineData(-20.0)]
    [InlineData(10.0)]
    public void Detector_OutputsTheAmplitudeAtDc_AndNothingAtTheCarrier(double pin)
    {
        var ds = Run($"""
            P1Tone:Ps n1 0 Pavl={N(pin)} Z=50 Freq=3.5e9 Phase=70
            EnvDetector:D1 n1 0 n2 0 Kd=2 BW=10e6
            R:RL n2 0 R=50
            {Hb}
            """);
        Assert.Equal(2 * Amp(pin), V(ds, "n2", 0).Real, 9);
        NoHarmonics(ds, "n2", Amp(pin), from: 1);
    }

    /// <summary>The polar halves at harmonic balance's operating point: |x| at its RMS of 1 and the phase at 0, at DC.</summary>
    [Fact]
    public void APolarPair_IsTheUnmodulatedOperatingPointAtDc()
    {
        const string carrier = "Carrier[1]=\"LTE;BW=5MHz;Mod=64QAM;Off=0;P=0;Seed=3\" Record=0.5e-3";
        var ds = Run($"""
            MOD_Source:SA a 0 Fc=3.5e9 Pavg=4 Z=50 Polar=amp {carrier}
            MOD_Source:SP p 0 Fc=3.5e9 Pavg=4 Z=50 Polar=phase {carrier}
            R:RA a 0 R=50
            R:RP p 0 R=50
            {Hb}
            """);
        Assert.Equal(Amp(4), V(ds, "a", 0).Real, 9);
        Assert.Equal(0.0, V(ds, "p", 0).Magnitude, 12);
        Assert.Equal(0.0, V(ds, "a", 1).Magnitude, 12);
    }

    /// <summary>
    /// Each block on two tones, 100 MHz at −10 dBm and 110 MHz at −20 dBm (unequal, so the two-tone envelope has no
    /// null), against single-tone harmonic balance at 10 MHz, where every mixing product is a harmonic: the spectra
    /// compare frequency by frequency (<see cref="MultiToneWeightedTermsTests.Spectrum"/>), complex, so a lost or turned
    /// quadrature fails. The quadrature node's w = 2 term is carried by both two-tone paths.
    /// <para>The Phase Modulator at a DC phase is linear in its RF input, so each tone comes out turned by
    /// <c>Kp·V(PH)</c> as a single tone does, on either path. The other three are laws of the two tones' envelope, whose
    /// products run to every order: the FFT path keeps a box of them and the lattice a diamond, so the lattice misses
    /// more of what the reference keeps on the same frequencies — truncation, which shrinks with the order. Measured at
    /// order 9: 1.2e-15 at worst on the FFT path and for the Phase Modulator on both, 1.4e-5 to 2.2e-5 for the other three
    /// on the lattice (at their weakest product, the upper IM3 or the second beat). A review's drive of the same blocks
    /// read up to 4.3e-8 on the FFT path and 1.8e-3 to 4.4e-3 on the lattice; the bounds hold either.</para>
    /// </summary>
    [Theory]
    [InlineData("PhaseMod", false)]
    [InlineData("PhaseMod", true)]
    [InlineData("Limiter", false)]
    [InlineData("Limiter", true)]
    [InlineData("SupplyAmp", false)]
    [InlineData("SupplyAmp", true)]
    [InlineData("EnvDetector", false)]
    [InlineData("EnvDetector", true)]
    public void TwoToneHarmonicBalance_IsTheSingleToneAnswer(string block, bool onLattice)
    {
        var (lines, freqs) = TwoToneBlocks[block];
        var settings = new AnalysisSettings { HbTwoToneOnLattice = onLattice };
        double worst = AgreesWithSingleTone(lines, [(100, -10, 20), (110, -20, -35)], freqs, 9, settings);
        output.WriteLine($"{block} on the {(onLattice ? "lattice" : "FFT path")}: worst relative error {worst:E2}");
        Assert.True(worst <= (onLattice && block != "PhaseMod" ? 1e-2 : 1e-6), $"{block}: {worst:E2}");
    }

    /// <summary>The Phase Modulator on three tones (the lattice, <c>MaxMixOrder=3</c>): each tone turned as a single tone is.</summary>
    [Fact]
    public void ThreeToneHarmonicBalance_TurnsEachToneAsSingleToneDoes()
    {
        double worst = AgreesWithSingleTone(TwoToneBlocks["PhaseMod"].Lines, [(100, -10, 20), (110, -20, -35), (140, -15, 60)],
                                            [100, 110, 140], 3, new AnalysisSettings());
        output.WriteLine($"PhaseMod on three tones: worst relative error {worst:E2}");   // measured 5.3e-16
        Assert.True(worst <= 1e-9, $"{worst:E2}");
    }

    /// <summary>Each block's lines between <c>n1</c> and <c>n2</c>, and the frequencies (MHz) its output is compared at:
    /// the carriers and the third-order products for an RF output, DC and the first two beats for the detector's.</summary>
    private static readonly Dictionary<string, (string Lines, double[] Freqs)> TwoToneBlocks = new()
    {
        ["PhaseMod"]    = ("Vdc:VP phs 0 Vdc=1.4\nR:RP phs ph R=50\nPhaseMod:B n1 0 n2 0 ph 0 Kp=1.5 Gain=3", [100, 110]),
        ["Limiter"]     = ("Limiter:B n1 0 n2 0 Pout=0 Knee=-10", [100, 110, 90, 120]),
        ["SupplyAmp"]   = ("Vdc:VS ss 0 Vdc=2\nR:RS ss s R=50\nSupplyAmp:B n1 0 n2 0 s 0 Ks=4 Vsat=1.5 AMPM=-8 Knee=-10", [100, 110, 90, 120]),
        ["EnvDetector"] = ("EnvDetector:B n1 0 n2 0 Kd=2", [0, 10, 20]),
    };

    /// <summary>The worst relative error of the multi-tone run's output against the single-tone one's, over <paramref name="freqs"/>.</summary>
    private double AgreesWithSingleTone(string lines, (double Mhz, double Dbm, double Deg)[] tones, double[] freqs, int order, AnalysisSettings settings)
    {
        string circuit = "PnTone:S n1 0 "
                       + string.Join(" ", tones.Select((t, i) => $"Freq[{i + 1}]={N(t.Mhz)} MHz Pavl[{i + 1}]={N(t.Dbm)} Phase[{i + 1}]={N(t.Deg)}"))
                       + $" Z=50\n{lines}\nR:RL n2 0 R=50";
        var want = MultiToneWeightedTermsTests.Spectrum(Run($"{circuit}\nanalysis HB1 type=hb Tone=10e6 MaxHarm=40 Tol=1e-12"), "V", "n2");
        var got  = MultiToneWeightedTermsTests.Spectrum(Run(
            $"{circuit}\nanalysis HB1 type=hb NumFreqs={tones.Length} "
          + string.Join(" ", tones.Select((t, i) => $"Tone[{i + 1}]={N(t.Mhz * 1e6)}"))
          + $" MaxMixOrder={order} MaxHarm={order} Tol=1e-12", settings), "V", "n2");
        double worst = 0;
        foreach (double f in freqs)
        {
            var (g, w) = (got[(long)(f * 1e6)], want[(long)(f * 1e6)]);
            double err = (g - w).Magnitude / w.Magnitude;
            output.WriteLine($"  {f,4} MHz: {g.Magnitude:E6} V ∠{g.Phase * 180 / Math.PI:F4}°, single-tone {w.Magnitude:E6} V ∠{w.Phase * 180 / Math.PI:F4}°, rel {err:E2}");
            worst = Math.Max(worst, err);
        }
        return worst;
    }
}
