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
/// a solve that carries the quadrature node. A 50 Ω P1Tone into a 50 Ω input puts <c>a = √(2·50·P)</c> across it, at the
/// phase the solve reports at the input node; every output is read across a 50 Ω load, where the law itself appears.
/// The laws make no harmonics, so the harmonics are gated at the solve's floor. A control input is driven as the polar
/// source drives it, from 50 Ω: a Vdc of twice the wanted voltage behind 50 Ω into the block's matched 50 Ω.
/// </summary>
public class CarrierBlockHbTests(ITestOutputHelper output)
{
    private static string N(double x) => x.ToString("R", CultureInfo.InvariantCulture);
    private static double Amp(double dbm) => Math.Sqrt(2 * 50 * 1e-3 * Math.Pow(10, dbm / 10));

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

    /// <summary>Two-tone harmonic balance does not carry the quadrature bucket, so it refuses the block by name.</summary>
    [Fact]
    public void TwoToneHarmonicBalance_RefusesACarrierBlockByName()
    {
        var (lib, tb) = new CnlReader().Read("""
            PnTone:Ps n1 0 Freq[1]=3.49 GHz Pavl[1]=-10 Phase[1]=0 Freq[2]=3.51 GHz Pavl[2]=-10 Phase[2]=0 Z=50
            Limiter:LIM7 n1 0 n2 0 Pout=0
            R:RL n2 0 R=50
            analysis HB1 type=hb NumFreqs=2 Tone[1]=3.49e9 Tone[2]=3.51e9 MaxMixOrder=5 MaxHarm=3
            """);
        var nl = new Elaborator(lib).Elaborate(tb);
        var hba = tb.Analyses.OfType<HarmonicBalanceAnalysis>().First();
        var ex = Assert.ThrowsAny<InvalidOperationException>(() => new HbEngine(nl, tb).Run(HbEngine.Resolve(hba, nl.ResolvedGlobals)));
        Assert.Contains("LIM7", ex.Message);
        Assert.Contains("quadrature", ex.Message);
    }
}
