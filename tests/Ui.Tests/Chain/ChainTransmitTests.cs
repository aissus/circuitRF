// ================================================================
//  ChainTransmitTests.cs — a transmit chain's budget: the direction, a converter's sideband, the amplifier's laws, the IQ
//  modulator, the output's readouts and the PA bench.
//
//  Every expected number is worked here from its textbook form — Rapp's and Saleh's laws, the tanh limiter's describing
//  function by its own integral, the cascaded intercept, the image-rejection ratio, the ACLR relation — not read back
//  from ChainTransmit or AmplifierLaw: a second copy of the same arithmetic agreeing with itself proves nothing.
// ================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using CircuitRF.Design.Chain;
using CircuitRF.Design.Schematic;
using CircuitRF.Ui.Schematic;
using RfCore.Data;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Chain;

[Collection(CellStatGlobalsCollection.Name)]
public sealed class ChainTransmitTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _ws = Path.Combine(Path.GetTempPath(), "crf-chaintx-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose() { try { Directory.Delete(_ws, true); } catch (IOException) { } }

    private static double Lin(double db) => Math.Pow(10, db / 10);
    private static double Db(double x) => 10 * Math.Log10(x);

    private static ChainStage S(string name, string part, params (string K, string V)[] p)
    {
        var s = new ChainStage { Name = name, Part = part };
        foreach (var (k, v) in p) s.Parameters[k] = v;
        return s;
    }

    /// <summary>A driver (tanh, OIP3 30 dBm), a Rapp PA (30 dB, Psat 37 dBm, p = 2) and a 1 dB loss, at 3.5 GHz.</summary>
    private static ChainDesign Tx() => new()
    {
        Direction = ChainDesign.Transmit, FrequencyHz = 3.5e9, BandwidthHz = 100e6, InputPowerDbm = -20,
        Stages =
        [
            S("DRV", "Amp", ("Gain", "20"), ("NF", "4"), ("IP3", "30"), ("IP3Ref", "Output")),
            S("PA", "Amp", ("Gain", "30"), ("NF", "6"), ("Law", "Rapp"), ("Psat", "37"), ("P", "2"), ("Pdc", "10")),
            S("LOSS", "Atten", ("Loss", "1")),
        ],
    };

    /// <summary>The tanh limiter's gain change at an input u = 2·√(P/IIP3) times its saturation, by Simpson's rule over
    /// a quarter period of (2/πu)·∫ tanh(u·cos θ)·cos θ dθ.</summary>
    private static double TanhDb(double pinDbm, double iip3Dbm)
    {
        double u = 2 * Math.Sqrt(Lin(pinDbm - iip3Dbm));
        const int n = 2000;
        double h = Math.PI / 2 / n, sum = 0;
        for (int i = 0; i <= n; i++)
        {
            double th = i * h, w = i == 0 || i == n ? 1 : i % 2 == 1 ? 4 : 2;
            sum += w * Math.Tanh(u * Math.Cos(th)) * Math.Cos(th);
        }
        return 20 * Math.Log10(4 / Math.PI * sum * h / 3 / u);
    }

    [Fact]
    public void TheLaws_RappAndSaleh_AreTheirClosedForms()
    {
        // Rapp: IP1dB = Psat − G + (10/p)·log10(10^(p/10) − 1); the intercept the cubic's at that point, + 9.636 dB.
        double p1 = 37 - 30 + 10.0 / 2 * Math.Log10(Math.Pow(10, 0.2) - 1);
        var pa = ChainBudget.Evaluate(Tx()).Stages[1].Stage;
        Assert.Equal(p1, pa.Ip1dbDbm, 9);
        Assert.Equal(p1 - 10 * Math.Log10(1 - Math.Pow(10, -0.05)), pa.Iip3Dbm, 9);
        Assert.Equal(5.8354, pa.Ip1dbDbm, 4);

        // Saleh, its AM-AM peaking at Psat: s_sat = 4·Psat/G; IP1dB = s_sat·(10^(1/20) − 1); the third-order term with the
        // AM-PM's slope a = PhiMax·PhiSat/(PhiMax − PhiSat) per unit drive: IIP3 = s_sat/√(1 + a²).
        var d = Tx();
        d.Stages[1].Parameters["Law"] = "Saleh";
        d.Stages[1].Parameters["PhiSat"] = "10";
        d.Stages[1].Parameters["PhiMax"] = "20";
        var sl = ChainBudget.Evaluate(d).Stages[1].Stage;
        double ssat = 37 - 30 + Db(4), a = 20.0 * 10 / (20 - 10) * Math.PI / 180;
        Assert.Equal(ssat + Db(Math.Pow(10, 0.05) - 1), sl.Ip1dbDbm, 9);
        Assert.Equal(ssat - 5 * Math.Log10(1 + a * a), sl.Iip3Dbm, 9);

        // Tanh, untouched: IIP3 = OIP3 − G, IP1dB 8.9625 dB below it.
        var drv = ChainBudget.Evaluate(Tx()).Stages[0].Stage;
        Assert.Equal(10, drv.Iip3Dbm, 12);
        Assert.Equal(10 - 8.9625, drv.Ip1dbDbm, 12);
    }

    [Fact]
    public void ATransmitChain_ReadsItsOutput_ByHand()
    {
        var r = ChainBudget.Evaluate(Tx());
        Assert.Empty(r.Refusals);
        var x = ChainTransmit.Read(r)!;
        var t = r.Totals!;

        // The cascade's intercept: 1/IIP3 = 1/IIP3_DRV + G_DRV/IIP3_PA, mW.
        double iip3Pa = 37 - 30 + 5 * Math.Log10(Math.Pow(10, 0.2) - 1) - 10 * Math.Log10(1 - Math.Pow(10, -0.05));
        double iip3 = -Db(1 / Lin(10) + Lin(20) / Lin(iip3Pa));
        Assert.Equal(iip3 + 49, t.Oip3Dbm, 9);
        Assert.Equal(29, x.PoutDbm, 12);
        // Psat at the output: the PA's, less the loss after it; the back-off from it and from the OP1dB.
        Assert.Equal(36, x.PsatDbm, 12);
        Assert.Equal(7, x.BackoffPsatDb, 12);
        Assert.Equal(t.Op1dbDbm - 29, x.BackoffP1dbDb, 12);
        // ACLR = 2·(Pout − OIP3) − 10·log10(3).
        Assert.Equal(2 * (29 - (iip3 + 49)) - Db(3), x.AclrDbc, 9);
        // The CW output: the driver's tanh at −20 dBm against 10 dBm, then Rapp's law, then the loss.
        double atPa = -20 + 20 + TanhDb(-20, 10);
        double s = Lin(atPa - 30) / Lin(37 - 30 - 30);
        double cw = atPa + 30 + 20 * Math.Log10(Math.Pow(1 + s * s, -1.0 / 4)) - 1;
        output.WriteLine($"CW {x.PoutCwDbm:F6} against {cw:F6}; ACLR {x.AclrDbc:F3} dBc");
        Assert.Equal(cw, x.PoutCwDbm, 6);
        // The noise floor, dBm/Hz: k·(T0 + Te)·G.
        Assert.Equal(Db(1.380649e-23 * (290 + t.TeK) * Lin(49)) + 30, x.NoisePsdDbmHz, 9);
    }

    [Fact]
    public void AReceiveChain_HasNoTransmitReadouts_AndSavesAsItDid()
    {
        var d = ChainBudgetTests.Superhet();
        Assert.Null(ChainTransmit.Read(ChainBudget.Evaluate(d)));
        Assert.Contains("\"FormatVersion\": 1", ChainDesignIo.Serialize(d));
        Assert.DoesNotContain("Direction", ChainDesignIo.Serialize(d));
        // A transmit chain is version 3, and reads back as itself.
        string text = ChainDesignIo.Serialize(Tx());
        Assert.Contains("\"FormatVersion\": 3", text);
        Assert.Equal(text, ChainDesignIo.Serialize(ChainDesignIo.Deserialize(text)));
    }

    [Fact]
    public void TheUpperSideband_IsTheSum()
    {
        var d = ChainBudgetTests.Superhet();
        d.Stages[2].Parameters["Sideband"] = "Upper";
        var r = ChainBudget.Evaluate(d);
        Assert.Empty(r.Refusals);
        Assert.Equal(2.45e9 + 2.1e9, r.Totals!.OutputFrequencyHz, 3);
        Assert.Equal(2.45e9 + 2.1e9, r.Design!.StageInputFrequencies()[3], 3);
        d.Stages[2].Parameters["Sideband"] = "Middle";
        Assert.Contains(ChainBudget.Evaluate(d).Refusals, x => x.Contains("Sideband", StringComparison.Ordinal));
    }

    [Fact]
    public void TheIqModulator_ItsGainInterceptAndImage()
    {
        var d = new ChainDesign
        {
            Direction = ChainDesign.Transmit, FrequencyHz = 200e6, BandwidthHz = 20e6, InputPowerDbm = -10,
            Stages = [S("IQ", "IQMod", ("ConvGain", "-6"), ("IIP3", "15"), ("Plo", "7"), ("Flo", "3.3 GHz"), ("GainImb", "0.2"),
                        ("PhaseImb", "1.5"), ("IsoLO_IF", "50"))],
        };
        var r = ChainBudget.Evaluate(d);
        Assert.Empty(r.Refusals);
        // The sum sideband, Flo + f; the gain the wanted sideband's, |1 + g·e^(jφ)|²/4 of one mixer's; the intercept a
        // mixer's 3.01 dB up, each mixer taking half the input.
        var ge = Complex.FromPolarCoordinates(Math.Pow(10, 0.2 / 20), 1.5 * Math.PI / 180);
        Assert.Equal(3.5e9, r.Totals!.OutputFrequencyHz, 3);
        Assert.Equal(-6 + 20 * Math.Log10((1 + ge).Magnitude / 2), r.Stages[0].Stage.GainDb, 9);
        Assert.Equal(15 + Db(2), r.Stages[0].Stage.Iip3Dbm, 9);
        var spec = IqModSpec.Read(d.Stages[0]);
        double irr = Db(Math.Pow((1 + ge).Magnitude, 2) / Math.Pow((1 - ge).Magnitude, 2));
        output.WriteLine($"image rejection {irr:F4} dB");
        Assert.Equal(irr, spec.ImageRejectionDb, 9);
        Assert.Equal(35.1727, spec.ImageRejectionDb, 4);
        // The LO: each mixer's Plo − IsoLO_IF, the two in quadrature off by φ, summed at half each.
        Assert.Equal(7 - 50 + Db(1 - Math.Sin(1.5 * Math.PI / 180)), spec.LoLeakDbm, 9);
        var x = ChainTransmit.Read(r)!;
        Assert.Equal(-irr, x.ImageDbc, 9);
    }

    [Fact]
    public void WhatATransmitChainRefuses()
    {
        var d = Tx();
        d.Stages[1].Parameters["Law"] = "Cubic";
        Assert.Contains(ChainBudget.Evaluate(d).Refusals, x => x.Contains("not a law", StringComparison.Ordinal));
        d = Tx();
        d.Stages[1].Parameters["Law"] = "Saleh";
        d.Stages[1].Parameters["PhiSat"] = "10";
        d.Stages[1].Parameters["PhiMax"] = "5";
        Assert.Contains(ChainBudget.Evaluate(d).Refusals, x => x.Contains("PhiMax", StringComparison.Ordinal));
        d = Tx();
        d.Link = new ChainLink();
        Assert.Contains(ChainBudget.Evaluate(d).Refusals, x => x.Contains("no link", StringComparison.Ordinal));
        d = Tx();
        d.Direction = "Sideways";
        Assert.Contains(ChainBudget.Evaluate(d).Refusals, x => x.Contains("direction", StringComparison.Ordinal));
    }

    /// <summary>A zero-IF digital transmitter: a DAC pair, a low-pass per rail, an IQ modulator at 3.5 GHz.</summary>
    private static ChainDesign Digital() => new()
    {
        Direction = ChainDesign.Transmit, FrequencyHz = 0, BandwidthHz = 19.08e6, InputPowerDbm = -10, TemperatureK = 0,
        Stages =
        [
            S("DAC", "DAC", ("Bits", "8"), ("FS", "0.32"), ("Fs", "122.88 MHz")),
            S("LPF", "Filter", ("Response", "Butterworth"), ("Form", "Lowpass"), ("Order", "5"), ("Fc", "49.152 MHz"), ("IL", "0")),
            S("IQ", "IQMod", ("ConvGain", "-7"), ("NF", ""), ("IIP3", "200"), ("Flo", "3.5 GHz"), ("Sideband", "Upper")),
            S("PA", "Amp", ("Gain", "20"), ("NF", ""), ("IP3", "200")),
        ],
    };

    /// <summary>The DAC's floor is a noise temperature: the output's SNR in the bandwidth is the textbook SQNR,
    /// 6.02·N + 1.76 − BO + 10·log10(Fs/B), worked here from N, FS, the drive and the rate; the IQ modulator fed by the
    /// rails gains 3.01 dB over one mixer's conversion; a DAC chain may sit at 0 Hz, no other may.</summary>
    [Fact]
    public void ADacsFloor_IsTheTextbookSqnr_AndTheRailsGain3dB()
    {
        var d = Digital();
        var r = ChainBudget.Evaluate(d);
        Assert.Empty(r.Refusals);
        double bo = 20 * Math.Log10(0.32 / Math.Sqrt(2 * 50 * 1e-3 * Lin(-10)));
        double sqnr = 6.0206 * 8 + 1.7609 - bo + Db(122.88e6 / 19.08e6);
        Assert.InRange(r.Totals!.SnrDb - sqnr, -1e-3, 1e-3);
        Assert.Equal(-7 + Db(2), r.Stages[2].Stage.GainDb, 9);
        Assert.Equal(-7 + Db(2) + 20, r.Totals.GainDb, 9);
        var x = ChainTransmit.Read(r)!;
        // The droop's EVM for a centred band: (π²/(6·√180))·(B/Fs)², and the image of the band's edge, (B/2)/(Fs − B/2).
        double droop = Math.PI * Math.PI / (6 * Math.Sqrt(180)) * Math.Pow(19.08 / 122.88, 2);
        Assert.InRange(x.EvmFloorPct - 100 * Math.Sqrt(Math.Pow(10, -sqnr / 10) + droop * droop), -2e-3, 2e-3);
        Assert.Equal(20 * Math.Log10(9.54 / (122.88 - 9.54)), DacSpec.Read(d.Stages[0]).ImageDb(0, 19.08e6), 9);
        var rx = Digital();
        rx.Stages.RemoveAt(0);
        rx.Stages.Insert(0, S("AMP", "Amp", ("Gain", "0")));
        Assert.Contains(ChainBudget.Evaluate(rx).Refusals, s => s.Contains("0 Hz only for a DAC", StringComparison.Ordinal));
    }

    [Fact]
    public void WhatAStreamRefuses()
    {
        static IReadOnlyList<string> No(Action<ChainDesign> edit) { var d = Digital(); edit(d); return ChainBudget.Evaluate(d).Refusals; }
        Assert.Contains(No(d => d.Direction = ChainDesign.Receive), s => s.Contains("a transmitter's", StringComparison.Ordinal));
        Assert.Contains(No(d => { var x = d.Stages[0]; d.Stages.RemoveAt(0); d.Stages.Insert(1, x); }), s => s.Contains("first stage", StringComparison.Ordinal));
        Assert.Contains(No(d => d.Stages.RemoveAt(2)), s => s.Contains("has none after it", StringComparison.Ordinal));
        Assert.Contains(No(d => d.Stages.Insert(1, S("MIX", "Mixer", ("Flo", "1 GHz")))), s => s.Contains("is none of them", StringComparison.Ordinal));
        Assert.Contains(No(d => d.Stages[2].Parameters["Sideband"] = "Lower"), s => s.Contains("conjugate", StringComparison.Ordinal));
        Assert.Contains(No(d => d.Stages[0].Parameters["Bits"] = "40"), s => s.Contains("Bits is 1 to 32", StringComparison.Ordinal));
        Assert.Contains(No(d => { d.FrequencyHz = 3.5e9; d.Stages[0] = S("POL", "PolarTx", ("Ks", "0")); }), s => s.Contains("Ks is above 0", StringComparison.Ordinal));
        Assert.Contains(No(d => { d.FrequencyHz = 3.5e9; d.Stages[0] = S("POL", "PolarTx", ("Order", "0")); }), s => s.Contains("Order is 1 to 10", StringComparison.Ordinal));
    }

    /// <summary>A chain from a stream has its Envelope bench and its PA's only, and says why; its Envelope bench drives
    /// the stream and places the first stage as its System blocks.</summary>
    [Fact]
    public void AStream_TransfersToItsEnvelopeBench()
    {
        var d = Digital();
        d.Stages[3] = S("PA", "Amp", ("Gain", "20"), ("Law", "Rapp"), ("Psat", "40"));
        var why = new List<string>();
        Assert.Equal([ChainBench.Envelope, ChainBench.Pa], ChainBenches.BenchesFor(d, why));
        Assert.Contains(why, s => s.Contains("harmonic balance cannot run", StringComparison.Ordinal));
        var env = ChainBenches.Build(d, ChainBudget.Evaluate(d), "D").Single(b => b.Kind == ChainBench.Envelope).Netlist;
        foreach (string line in new[] { "Baseband=i", "Baseband=q", "DAC:DAC_I  bi", "DAC:DAC_Q  bq", "Filter:LPF_Q  q_DAC", "Mixer:IQ_Q  q_LPF", "Accuracy=standard", "EVM_calc =" })
            Assert.Contains(line, env, StringComparison.Ordinal);
        Assert.DoesNotContain("Coupler:IQ_HI", env, StringComparison.Ordinal);

        var p = new ChainDesign
        {
            Direction = ChainDesign.Transmit, FrequencyHz = 3.5e9, BandwidthHz = 19.08e6, InputPowerDbm = 0, TemperatureK = 0,
            Stages = [S("POL", "PolarTx", ("Ks", "10"), ("Fam", "80 MHz"), ("dT", "-1 ns"))],
        };
        Assert.Equal([ChainBench.Envelope], ChainBenches.BenchesFor(p));
        var pol = ChainBenches.Build(p, ChainBudget.Evaluate(p), "P").Single().Netlist;
        // The phase the later: its own lowpass's DC group delay, 2/(2π·80 MHz), and 1 ns more on the phase path.
        Assert.Contains("Delay:POL_DPM  POL_pm  0  POL_rf  0  T=4.97887357729738", pol, StringComparison.Ordinal);
        foreach (string line in new[] { "Polar=amp", "Polar=phase", "PhaseMod:POL_PM", "SupplyAmp:POL  POL_rf  0  out  0  POL_vs  0  Ks=10", "Filter:POL_FAM  POL_am  0  POL_vs" })
            Assert.Contains(line, pol, StringComparison.Ordinal);
    }

    /// <summary>The polar stage's mismatch term is the polar pull request's first-order formula, π·B·dT/√12 over the band,
    /// its in-band and adjacent shares the model's constants; its output is Ks² times its input.</summary>
    [Fact]
    public void APolarStage_ItsMismatchOnTheFirstOrderFormula()
    {
        var p = new ChainDesign
        {
            Direction = ChainDesign.Transmit, FrequencyHz = 3.5e9, BandwidthHz = 20e6, InputPowerDbm = 0, TemperatureK = 0,
            Stages = [S("POL", "PolarTx", ("Ks", "10"), ("Fam", "0"), ("dT", "2 ns"))],
        };
        var x = ChainTransmit.Read(ChainBudget.Evaluate(p))!;
        double whole = Math.PI * 20e6 * 2e-9 / Math.Sqrt(12);
        Assert.Equal(20, x.PoutDbm, 9);
        Assert.Equal(100 * Math.Sqrt(0.436) * whole, x.EvmFloorPct, 9);
        Assert.Equal(Db(0.215 * whole * whole), x.AclrDbc, 9);
        // A Vsat that the envelope's peaks reach costs gain: the mean of tanh² under the linear law.
        p.Stages[0].Parameters["Vsat"] = "0.5";
        Assert.InRange(ChainTransmit.Read(ChainBudget.Evaluate(p))!.PoutDbm, 10, 19.9);
    }

    /// <summary>An amplifier whose law is the default writes nothing it did not before: not into the .cchain from the
    /// window, not onto its line on a bench.</summary>
    [Fact]
    public void TheDefaultLaw_IsWrittenNowhere()
    {
        var amp = ChainParts.Find("Amp")!;
        Assert.True(amp.Parameters.Single(p => p.Name == "Law").Unstated("Tanh"));
        Assert.True(amp.Parameters.Single(p => p.Name == "Law").Unstated(""));
        Assert.False(amp.Parameters.Single(p => p.Name == "Law").Unstated("Rapp"));
        var b = ChainBenches.Build(ChainBudgetTests.Superhet(), ChainBudget.Evaluate(ChainBudgetTests.Superhet()), "S");
        Assert.All(b, x => Assert.DoesNotContain("Law=", x.Netlist, StringComparison.Ordinal));
        Assert.All(b, x => Assert.DoesNotContain("  Sideband=", x.Netlist, StringComparison.Ordinal));
    }

    [Fact]
    public void ATransmitChain_TransfersToItsOwnBenches()
    {
        var d = Tx();
        var r = ChainBudget.Evaluate(d);
        Assert.Equal([ChainBench.OneTone, ChainBench.TwoTone, ChainBench.Envelope, ChainBench.Pa], ChainBenches.BenchesFor(d));
        var left = new List<string>();
        var benches = ChainBenches.Build(d, r, "T", leftOut: left);
        Assert.Empty(left);
        Assert.Equal(4, benches.Count);
        Assert.Contains("Law=Rapp", benches.Single(b => b.Kind == ChainBench.Pa).Netlist, StringComparison.Ordinal);
        Assert.DoesNotContain("IP3=", benches.Single(b => b.Kind == ChainBench.Pa).Netlist, StringComparison.Ordinal);
    }

    /// <summary>A Rapp or Saleh law its Psat switches off is linear in the budget, as the block is, whatever the line's
    /// IP3 says.</summary>
    [Fact]
    public void ALawSwitchedOff_IsLinear()
    {
        var d = Tx();
        d.Stages[1].Parameters["Psat"] = "200";
        var pa = ChainBudget.Evaluate(d).Stages[1].Stage;
        Assert.True(double.IsPositiveInfinity(pa.Iip3Dbm));
        Assert.True(double.IsPositiveInfinity(pa.Ip1dbDbm));
        Assert.Null(pa.Law);
    }

    /// <summary>The receive band's noise passes a Combiner at its noise gain, not its coherent signal gain: a flat chain
    /// with a 4-way combiner reads the same noise in the band as at the carrier.</summary>
    [Fact]
    public void TheReceiveBand_TakesACombinersNoiseGain()
    {
        var d = new ChainDesign
        {
            Direction = ChainDesign.Transmit, FrequencyHz = 3.5e9, BandwidthHz = 100e6, InputPowerDbm = -20, RxBandOffsetHz = 200e6,
            Stages = [S("A1", "Amp", ("Gain", "20"), ("NF", "3"), ("IP3", "30")), S("CMB", "Combiner", ("N", "4"), ("IL", "0.5")),
                      S("A2", "Amp", ("Gain", "10"), ("NF", "5"), ("IP3", "40"))],
        };
        var x = ChainTransmit.Read(ChainBudget.Evaluate(d))!;
        Assert.InRange(x.RxNoisePsdDbmHz - x.NoisePsdDbmHz, -1e-9, 1e-9);
    }

    /// <summary>The lower sideband from an LO above the output: the IQ modulator's image and LO on their closed forms at
    /// its own output and at the chain's, simulated (its 1-Tone bench's harmonic-balance sweep, 26 drives: about 6 s).</summary>
    [Fact]
    [Trait("Category", "Benchmark")]
    public void TheLowerSideband_AHighSideLo_ItsImageAndLoOnTheBudget()
    {
        var d = new ChainDesign
        {
            Direction = ChainDesign.Transmit, FrequencyHz = 200e6, BandwidthHz = 20e6, InputPowerDbm = -10,
            Stages = [S("IQ", "IQMod", ("ConvGain", "-6"), ("Flo", "3.7 GHz"), ("Sideband", "Lower"), ("GainImb", "0.3"),
                        ("PhaseImb", "2"), ("IsoLO_IF", "40")),
                      S("AMP", "Amp", ("Gain", "10"), ("IP3", "200"))],
        };
        Assert.Equal(3.5e9, ChainBudget.Evaluate(d).Totals!.OutputFrequencyHz, 3);
        var ds = Bench(d, "L", ChainBench.OneTone, hbOnly: true);
        foreach (var q in new[] { "ImageIq", "LoIq", "Image", "LoLeak" })
        {
            output.WriteLine($"{q}: budget {M(ds, q + "_calc"):F4} dBc, simulated {M(ds, q + "_sim"):F4} dBc");
            Assert.InRange(M(ds, q + "_sim") - M(ds, q + "_calc"), -1e-2, 1e-2);
        }
        Assert.InRange(M(ds, "Pout_op_sim") - M(ds, "Pout_op_calc"), -1e-3, 1e-3);
    }

    /// <summary>A transmit chain's benches go through the receive chain's checks before they are written: an LO at twice
    /// the IF puts the modulator's LO, which the 1-Tone bench reads, on the IF's second harmonic, and the envelope run's two
    /// fundamentals on one product too, so neither is written, and Transfer says why.</summary>
    [Fact]
    public void ATransmitBench_IsCheckedBeforeItIsWritten()
    {
        var d = new ChainDesign
        {
            Direction = ChainDesign.Transmit, FrequencyHz = 200e6, BandwidthHz = 20e6, InputPowerDbm = -10,
            Stages = [S("IQ", "IQMod", ("Flo", "400 MHz")), S("PA", "Amp", ("Gain", "20"), ("Law", "Rapp"), ("Psat", "30"))],
        };
        var left = new List<string>();
        var benches = ChainBenches.Build(d, ChainBudget.Evaluate(d), "T", leftOut: left);
        foreach (var x in left) output.WriteLine(x);
        Assert.Contains(left, x => x.StartsWith("T 1-Tone is not written", StringComparison.Ordinal) && x.Contains("same frequency", StringComparison.Ordinal));
        Assert.Contains(left, x => x.StartsWith("T Envelope is not written", StringComparison.Ordinal));
        Assert.DoesNotContain(benches, b => b.Kind is ChainBench.OneTone or ChainBench.Envelope);
    }

    /// <summary>The PA bench, simulated: its AM-AM and AM-PM over the drive are Rapp's closed form, its 1 dB point where
    /// the closed form puts it, its efficiency the power it adds over its stated DC power.</summary>
    [Fact]
    public void ThePaBench_CompressesWhereTheLawSays()
    {
        var ds = Bench(Tx(), "T", ChainBench.Pa);
        double worst = ds["measurements.AMAM_err"].RealValues.Max(Math.Abs);
        output.WriteLine($"PA: worst AM-AM error {worst:G3} dB, IP1dB {M(ds, "IP1dB_sim"):F4} dBm against {M(ds, "IP1dB_calc"):F4}");
        Assert.InRange(worst, 0, 1e-6);
        Assert.InRange(ds["measurements.AMPM_err"].RealValues.Max(Math.Abs), 0, 1e-6);
        Assert.InRange(M(ds, "IP1dB_sim") - M(ds, "IP1dB_calc"), -0.02, 0.02);
        Assert.InRange(M(ds, "Ptop_err"), -1e-6, 1e-6);
        Assert.InRange(ds["measurements.PAE_sim"].RealValues.Zip(ds["measurements.PAE_calc"].RealValues, (a, b) => Math.Abs(a - b)).Max(), 0, 1e-6);
    }

    private static double M(DataSet ds, string name) => ds["measurements." + name].RealValues[0];

    /// <summary>A bench of the chain written into the test's workspace and run as Simulate runs it (its HB analyses);
    /// <paramref name="hbOnly"/> leaves out its harmonic-balance noise run too.</summary>
    private DataSet Bench(ChainDesign d, string chain, ChainBench which, bool hbOnly = false)
    {
        Directory.CreateDirectory(_ws);
        File.WriteAllText(Path.Combine(_ws, ".cws"), "{\"FormatVersion\": 2, \"LibraryRefs\": [], \"KnownFiles\": []}");
        var w = ChainBenches.Write(d, _ws, chain);
        Assert.Empty(w.Refusals);
        string cell = ChainBenches.CellName(chain, which);
        var (model, _, _) = SchematicPersistence.LoadFromFile(Path.Combine(_ws, cell, "schematic", cell + ".csch"));
        // Its envelope run is the TX display's, minutes on a loaded machine: left out here, the HB sweep is what is read.
        string text = string.Join("\n", SchematicCircuit.CnlTextOf(model, cell).Split('\n')
                                         .Where(l => !l.StartsWith("analysis ENV", StringComparison.Ordinal) && !l.Contains("ENVP.", StringComparison.Ordinal)
                                                     && !(hbOnly && (l.StartsWith("analysis HBN", StringComparison.Ordinal) || l.Contains("HBN.", StringComparison.Ordinal) || l.Contains("Noise_", StringComparison.Ordinal)))));
        string netlist = Path.Combine(_ws, "netlist.cnl");
        File.WriteAllText(netlist, text);
        var run = SchematicRunService.RunNetlist(netlist, _ws);
        Assert.True(run.Status == RunStatus.Success, $"{cell}: {run.StatusMessage}");
        Assert.Empty(run.Errors);
        Assert.DoesNotContain(run.Warnings, x => x.Contains("failed to evaluate", StringComparison.Ordinal));
        return run.GroupedResults!;
    }
}
