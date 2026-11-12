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

    /// <summary>A bench of the chain written into the test's workspace and run as Simulate runs it (its HB analyses).</summary>
    private DataSet Bench(ChainDesign d, string chain, ChainBench which)
    {
        Directory.CreateDirectory(_ws);
        File.WriteAllText(Path.Combine(_ws, ".cws"), "{\"FormatVersion\": 2, \"LibraryRefs\": [], \"KnownFiles\": []}");
        var w = ChainBenches.Write(d, _ws, chain);
        Assert.Empty(w.Refusals);
        string cell = ChainBenches.CellName(chain, which);
        var (model, _, _) = SchematicPersistence.LoadFromFile(Path.Combine(_ws, cell, "schematic", cell + ".csch"));
        // Its envelope run is the TX display's, minutes on a loaded machine: left out here, the HB sweep is what is read.
        string text = string.Join("\n", SchematicCircuit.CnlTextOf(model, cell).Split('\n')
                                         .Where(l => !l.StartsWith("analysis ENV", StringComparison.Ordinal) && !l.Contains("ENVP.", StringComparison.Ordinal)));
        string netlist = Path.Combine(_ws, "netlist.cnl");
        File.WriteAllText(netlist, text);
        var run = SchematicRunService.RunNetlist(netlist, _ws);
        Assert.True(run.Status == RunStatus.Success, $"{cell}: {run.StatusMessage}");
        Assert.Empty(run.Errors);
        Assert.DoesNotContain(run.Warnings, x => x.Contains("failed to evaluate", StringComparison.Ordinal));
        return run.GroupedResults!;
    }
}
