// ================================================================
//  ChainCancellerTests.cs — a canceller's nonlinearity, its residual through the stages after it, and Tune canceller
//
//    - with no IP3 a canceller reads as it did before it had one: linear, its depth the settings' alone, nothing said;
//    - the example's front end is an independent calculation's (testdata/chain/canceller_reference.py: the Tanh law's
//      two-tone coefficients by quadrature, the cascade by hand), and the budget's fifth-order series is that law's;
//    - the Canceller bench's harmonic-balance part sits on the closed forms: each tone's residual and the canceller's own
//      IM3 at its output, and through the stages after it the IM3, the residual's products with the signal and the
//      signal's compression by it, for a nonlinear canceller and for a linear one ahead of a hot stage;
//    - Tune canceller, from a mistuned start, recovers the closed form's null within what its goal holds it to, by the
//      core, the window's write-back (one undo step) and the CLI's --apply;
//    - the level diagram draws the canceller's own IM3 from its output on, and nothing for a linear one.
//
//  The harmonic-balance and tuning runs take 20 s to two minutes each, so those are Category=Benchmark.
// ================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using CircuitRF.Cli;
using CircuitRF.Design.Chain;
using CircuitRF.Design.Schematic;
using CircuitRF.Ui.Chain;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.Tests.Em3d;
using CircuitRF.Ui.Tests.Examples;
using RfCore.Data;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Chain;

[Collection(CellStatGlobalsCollection.Name)]
public sealed class ChainCancellerTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _ws = Path.Combine(Path.GetTempPath(), "crf-canc-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose() { try { Directory.Delete(_ws, true); } catch (IOException) { } }

    private static ChainStage S(string name, string part, params (string K, string V)[] p) => new()
    {
        Name = name, Part = part, Parameters = p.ToDictionary(x => x.K, x => x.V, StringComparer.Ordinal),
    };

    private static ChainDesign FrontEnd() => ChainDesignIo.LoadFromFile(Path.Combine(ChainBudgetExampleTests.Workspace, "Front End.cchain"));

    /// <summary>The front end from a deliberately mistuned start: 1 dB, 10° and 0.5 ns off.</summary>
    private static ChainDesign Mistuned()
    {
        var d = FrontEnd();
        var c = d.Stages.First(s => s.Part == ChainParts.Canceller);
        c.Parameters["AmpErr"] = "1";
        c.Parameters["PhaseErr"] = "10";
        c.Parameters["DelayErr"] = "0.5 ns";
        return d;
    }

    // ── the budget ───────────────────────────────────────────────────────────

    [Fact]
    public void ACanceller_WithNoIp3_ReadsAsItDidBefore()
    {
        var d = FrontEnd();
        var st = d.Stages.First(s => s.Part == ChainParts.Canceller);
        st.Parameters.Remove("IP3");
        var c = CancellerSpec.Read(st);
        Assert.True(c.Linear);
        Assert.Equal(0, c.CompressionDb);
        Assert.Equal(-10 * Math.Log10(CancellerSpec.Residual(0.02, 0.2, 0.05e-9, 10e6)), c.DepthDb, 12);
        Assert.Equal(double.NegativeInfinity, c.Im3Dbm);
        var r = ChainBudget.Evaluate(d);
        Assert.DoesNotContain(r.Notes, n => n.StartsWith("CANC:", StringComparison.Ordinal));
        Assert.All(ChainCanceller.Levels(r), n => Assert.True(double.IsNaN(n.CancellerIm3Dbm)));
        Assert.Null(CircuitRF.Render.Chain.ChainLevelDiagram.For(d, r)!.CancellerIm3Dbm);
        // A blank IP3 is not written: a chain that never states one saves as it always did.
        Assert.True(ChainParts.Find(ChainParts.Canceller)!.Parameters.First(p => p.Name == "IP3").Unstated(""));
    }

    /// <summary>
    /// The example's front end against testdata/chain/canceller_reference.py: the canceller's compression, depth,
    /// residual, own IM3, the null's amplitude and the tune's target, and at every stage after it the residual, the
    /// canceller's IM3 carried there, the residual's own IM3, its two products with the signal and the signal's
    /// compression by it, to 10⁻⁶ dB. And the series the budget takes against the Tanh law's own two-tone coefficients,
    /// integrated there: 2×10⁻⁵ dB in gain and 0.003 dB in IM3 at 25 dB under the intercept.
    /// </summary>
    [Fact]
    public void TheFrontEnd_IsTheIndependentReference()
    {
        var refs = JsonNode.Parse(File.ReadAllText(Path.Combine(PalaceBackendTests.RepoRoot(), "testdata", "chain", "canceller_reference.json")))!;
        double R(string key) => (double)refs[key]!;
        var d = FrontEnd();
        var r = ChainBudget.Evaluate(d);
        var c = CancellerSpec.Read(r.Design!.Stages[0]);
        (double Budget, string Key)[] pairs =
        [
            (c.ThroughLossDb, "through_loss_dB"), (c.RefGainDb, "ref_gain_dB"), (r.Stages[0].Stage.NfDb, "nf_canc_dB"),
            (c.RefOutDbm, "ref_out_dBm"), (c.CompressionDb, "compression_dB"), (c.RefOp1dbDbm, "op1db_dBm"),
            (c.DepthDb, "depth_dB"), (c.ResidualDbm, "residual_dBm"), (c.Im3Dbm, "im3_dBm"), (c.AmpErrForNullDb, "amp_null_dB"),
            (ChainCancellerTune.FloorDbm(r), "floor_dBm"), (ChainCancellerTune.TargetDbm(r), "target_dBm"),
            (r.Stages[0].NoiseOutDbm, "noise_canc_dBm"),
        ];
        foreach (var (budget, key) in pairs)
        {
            output.WriteLine($"{key}: budget {budget:F9}, reference {R(key):F9}");
            Assert.InRange(budget - R(key), -1e-6, 1e-6);
        }
        var nodes = ChainCanceller.Levels(r);
        var refNodes = refs["nodes"]!.AsArray();
        Assert.Equal(d.Stages.Count + 1, nodes.Count);
        Assert.True(double.IsNaN(nodes[0].ResidualDbm));
        for (int k = 0; k < refNodes.Count; k++)
        {
            var n = nodes[k + 1];
            var x = refNodes[k]!;
            Assert.Equal((string)x["node"]!, n.Node);
            Assert.InRange(n.ResidualDbm - (double)x["res"]!, -1e-6, 1e-6);
            Assert.InRange(n.CancellerIm3Dbm - (double)x["canc_im3"]!, -1e-6, 1e-6);
            if (k == 0) continue;   // at the canceller's own output nothing after it has acted yet
            Assert.InRange(n.ResidualIm3Dbm - (double)x["res_im3"]!, -1e-6, 1e-6);
            Assert.InRange(n.SignalSignalResidualDbm - (double)x["im_ssr"]!, -1e-6, 1e-6);
            Assert.InRange(n.ResidualResidualSignalDbm - (double)x["im_rrs"]!, -1e-6, 1e-6);
            Assert.InRange(n.DesenseDb / (double)x["desense"]! - 1, -1e-6, 1e-6);
        }
        // The levels carry the same residual; the margin counts it as a carrier, under the signal here.
        var lv = ChainLevels.Evaluate(d, r);
        Assert.Equal(c.ResidualDbm, lv[1].InterferenceDbm, 9);
        // The series against the law.
        Assert.InRange(20 * Math.Log10(R("gain_series") / R("gain_tanh")), -2e-5, 2e-5);
        Assert.InRange(20 * Math.Log10(R("im3_factor_series") / R("im3_factor_tanh")), -0.003, 0.003);
        Assert.InRange(R("amp_null_dB") - R("amp_null_tanh_dB"), -1e-4, 1e-4);
        // The readout says where its own IM3 sits.
        Assert.Contains(r.Notes, n => n.StartsWith("CANC: its own IM3", StringComparison.Ordinal) && n.Contains("under the residual", StringComparison.Ordinal));
    }

    [Fact]
    public void TheLevelDiagram_DrawsTheCancellersOwnIm3_FromItsOutputOn()
    {
        var d = FrontEnd();
        var r = ChainBudget.Evaluate(d);
        var im3 = CircuitRF.Render.Chain.ChainLevelDiagram.For(d, r)!.CancellerIm3Dbm!;
        Assert.True(double.IsNaN(im3[0]));
        Assert.Equal(CancellerSpec.Read(r.Design!.Stages[0]).Im3Dbm, im3[1], 12);
        Assert.Equal(im3[1] + r.Stages[^1].GainDb - r.Stages[0].GainDb, im3[^1], 9);
    }

    // ── the bench's harmonic-balance part ──────────────────────────────────────

    /// <summary>The Canceller bench as Transfer writes it, its harmonic-balance runs alone (the envelope ones and every
    /// row of theirs left out), simulated as Simulate runs a bench.</summary>
    private DataSet RunHb(ChainDesign d)
    {
        var r = ChainBudget.Evaluate(d);
        var bench = ChainBenches.Build(d, r, "Canc").Single(b => b.Kind == ChainBench.Canceller);
        string[] hb = ["HBC", "HBA", "SWHI", "SWHA"];
        var lines = bench.Netlist.Split('\n').Where(l =>
            !(l.StartsWith("analysis ", StringComparison.Ordinal) && !hb.Contains(l.Split(' ')[1])) &&
            !(l.StartsWith("measure ", StringComparison.Ordinal) && !(l.StartsWith("measure Res", StringComparison.Ordinal) ||
                                                                      l.StartsWith("measure IM3c", StringComparison.Ordinal) ||
                                                                      l.StartsWith("measure HA_", StringComparison.Ordinal))) &&
            !l.StartsWith("tune ", StringComparison.Ordinal) && !l.StartsWith("goal ", StringComparison.Ordinal) &&
            !l.StartsWith("optimize ", StringComparison.Ordinal));
        Directory.CreateDirectory(_ws);
        File.WriteAllText(Path.Combine(_ws, ".cws"), "{\"FormatVersion\": 2, \"LibraryRefs\": [], \"KnownFiles\": []}");
        string netlist = Path.Combine(_ws, "netlist.cnl");
        File.WriteAllText(netlist, string.Join('\n', lines));
        var run = SchematicRunService.RunNetlist(netlist, _ws);
        Assert.True(run.Status == RunStatus.Success, run.StatusMessage);
        Assert.Empty(run.Errors);
        Assert.DoesNotContain(run.Warnings, x => x.Contains("failed to evaluate", StringComparison.Ordinal));
        return run.GroupedResults!;
    }

    private void Near(DataSet ds, string name, double tolDb, double? relative = null)
    {
        var sim = ds[$"measurements.{name}_sim"].RealValues;
        var calc = ds[$"measurements.{name}_calc"].RealValues;
        Assert.Equal(calc.Length, sim.Length);
        for (int i = 0; i < sim.Length; i++)
        {
            output.WriteLine($"{name}[{i}]: simulated {sim[i]:G7}, budget {calc[i]:G7}");
            if (relative is { } rel) Assert.InRange(sim[i] / calc[i] - 1, -rel, rel);
            else Assert.InRange(sim[i] - calc[i], -tolDb, tolDb);
        }
    }

    /// <summary>
    /// The example's front end, its canceller's reference path at 25 dB under its intercept: each tone's residual and its
    /// own IM3 at its output on the closed forms to 0.01 dB; over the steps of the amplitude error the same to 0.02 dB,
    /// and at AMP2's output the interference's IM3 (its own, carried) to 0.05 dB, the residual's products with the signal
    /// to 0.15 and 0.25 dB and the signal's compression by both to 5 %. The 0.25 dB is the run's: the product at 2·f_r − f_s
    /// sits 100 dB or more under the drive, where a mixing order of 5 folds a few tenths of a dB onto it — more where the
    /// canceller's own IM3 takes part of it back, a reference set long — and 7 reads it 0.02 dB from the closed form. The
    /// steps stay 1.5 dB or more off the null for that reason.
    /// </summary>
    [Fact]
    [Trait("Category", "Benchmark")]
    public void TheHarmonicBalancePart_IsTheClosedForm_ForTheFrontEndsCanceller()
    {
        var ds = RunHb(FrontEnd());
        foreach (var q in new[] { "ResA", "ResB", "IM3c" }) Near(ds, q, 0.01);
        Near(ds, "HA_ResA", 0.02);
        Near(ds, "HA_IM3c", 0.02);
        Near(ds, "HA_IM3o", 0.05);
        Near(ds, "HA_IMssr", 0.15);
        Near(ds, "HA_IMrrs", 0.25);
        Near(ds, "HA_Desense", 0, relative: 0.05);
    }

    /// <summary>A linear canceller left 1 dB and 5° off, ahead of an LNA its residual drives 30 dB or more under its
    /// intercept: the LNA's own IM3 of the residual to 0.1 dB, its products with the signal to 0.15 dB (0.06, 0.10 and 0.09
    /// dB read, the Tanh law's fifth order at the hottest step) and the signal's compression by it to 3 % (0.6 %).</summary>
    [Fact]
    [Trait("Category", "Benchmark")]
    public void TheHarmonicBalancePart_IsTheClosedForm_ThroughAHotStageAfterALinearCanceller()
    {
        var d = new ChainDesign
        {
            FrequencyHz = 2.45e9, BandwidthHz = 4.5e6, InputPowerDbm = -60, TemperatureK = 290,
            Stages =
            [
                S("CANC", ChainParts.Canceller, ("Coupling", "10"), ("IL", "0.2"), ("Pint", "-30"), ("Bint", "10 MHz"), ("Pref", "-20"),
                  ("AmpErr", "1"), ("PhaseErr", "5"), ("DelayErr", "0")),
                S("LNA", ChainParts.Amp, ("Gain", "15"), ("NF", "2"), ("IP3", "10"), ("IP3Ref", "Output")),
            ],
        };
        var ds = RunHb(d);
        Assert.False(ds.Contains("measurements.IM3c_sim"));
        Near(ds, "HA_ResA", 0.02);
        Near(ds, "HA_IM3o", 0.1);
        Near(ds, "HA_IMssr", 0.15);
        Near(ds, "HA_IMrrs", 0.15);
        Near(ds, "HA_Desense", 0, relative: 0.03);
    }

    // ── Tune canceller ────────────────────────────────────────────────────────

    /// <summary>
    /// From 1 dB, 10° and 0.5 ns off, the optimizer meets the goal and lands on the closed form's null: the amplitude error
    /// that takes the compression back, no phase error and no delay mismatch, each within what the goal holds it to
    /// (canceller_reference.json's tolerance: 0.0025 dB, 0.016° and 15 ps at the example's target, 20 dB under its own
    /// IM3). Written back, the budget reads the canceller's own IM3 over the residual.
    /// </summary>
    [Fact]
    [Trait("Category", "Benchmark")]
    public void TuneCanceller_FromAMistunedStart_RecoversTheClosedFormsNull()
    {
        var d = Mistuned();
        var t = ChainCancellerTune.Run(d, out string? why, parallel: 4);
        Assert.True(t is not null, why);
        output.WriteLine($"{t.Algorithm}, {t.Evaluations} evaluations: AmpErr {t.AmpErrDb:G6} dB (null {t.OptimumAmpErrDb:G6}), " +
                         $"PhaseErr {t.PhaseErrDeg:G4} deg, DelayErr {t.DelayErrS * 1e12:G4} ps; residual {t.ResidualBeforeDbm:F2} -> " +
                         $"{t.ResidualAfterDbm:F2} dBm, target {t.TargetDbm:F2}");
        Assert.True(t.Met);
        Assert.True(t.ResidualAfterDbm <= t.TargetDbm);
        Assert.InRange(t.ResidualBeforeDbm, -60, -50);
        Assert.InRange(t.AmpErrDb - t.OptimumAmpErrDb, -0.003, 0.003);
        Assert.InRange(t.PhaseErrDeg, -0.02, 0.02);
        Assert.InRange(t.DelayErrS, -20e-12, 20e-12);

        var tuned = ChainCancellerTune.Apply(d, t);
        var r = ChainBudget.Evaluate(tuned);
        Assert.True(CancellerSpec.Read(r.Design!.Stages[0]).DepthDb > 65);
        Assert.Contains(r.Notes, n => n.StartsWith("CANC: its own IM3", StringComparison.Ordinal) && n.Contains("over the residual", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Category", "Benchmark")]
    public void TheChainVerb_TunesTheCanceller_AndWritesItBack()
    {
        Directory.CreateDirectory(_ws);
        string path = Path.Combine(_ws, "Mistuned.cchain");
        ChainDesignIo.SaveToFile(path, Mistuned());
        Assert.Equal(0, CliEntry.Run(["chain", path, "--tune-canceller", "--apply"]));
        var c = CancellerSpec.Read(ChainDesignIo.LoadFromFile(path).Stages[0]);
        Assert.InRange(c.AmpErrDb - c.AmpErrForNullDb, -0.003, 0.003);
        Assert.InRange(c.PhaseErrDeg, -0.02, 0.02);
        Assert.InRange(c.DelayErrS, -20e-12, 20e-12);
    }

    [Fact]
    public void TheChainVerb_RefusesApplyWithoutATune_AndATuneWithNoCanceller()
    {
        Directory.CreateDirectory(_ws);
        string path = Path.Combine(_ws, "Front End.cchain");
        ChainDesignIo.SaveToFile(path, FrontEnd());
        Assert.Equal(1, CliEntry.Run(["chain", path, "--apply"]));
        var none = FrontEnd();
        none.Stages.RemoveAt(0);
        ChainDesignIo.SaveToFile(path, none);
        Assert.Equal(1, CliEntry.Run(["chain", path, "--tune-canceller"]));
    }

    /// <summary>The window's Write into the chain: the three settings in one undo step, which Undo takes back whole.</summary>
    [Fact]
    public void TheWindowsWriteBack_IsOneUndoStep()
    {
        var vm = new ChainBudgetViewModel(Mistuned());
        // An undo step rebuilds the rows: read the canceller's afresh each time.
        string Get(string name) => vm.Stages[0].Parameters.First(p => p.Name == name).Value;
        vm.Tuned = new ChainCancellerTuning("CANC", 1, 10, 0.5e-9, 0.0756, 0.0009, 1.8e-12, -54.09, -121.14, -111.52, true,
                                            0.0748, "trust_region", "every enabled goal is met", 94);
        Assert.True(vm.ApplyTuneCommand.CanExecute(null));
        vm.ApplyTuneCommand.Execute(null);
        Assert.Equal(("0.0756", "0.0009", "0.0018 ns"), (Get("AmpErr"), Get("PhaseErr"), Get("DelayErr")));
        Assert.Null(vm.Tuned);
        Assert.True(CancellerSpec.Read(vm.Budget!.Design!.Stages[0]).DepthDb > 70);
        vm.UndoCommand.Execute(null);
        Assert.Equal(("1", "10", "0.5 ns"), (Get("AmpErr"), Get("PhaseErr"), Get("DelayErr")));
        Assert.False(vm.UndoCommand.CanExecute(null));
    }
}
