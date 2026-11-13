// ================================================================
//  ChainArraysTests.cs — arrays of sub-arrays, uneven trees and unequal active paths in the chain budget, and the
//  array benches through a mixer.
//
//  The closed forms are held to an independent calculation (testdata/chain/arrays_reference.py, which sums the network
//  source by source where the budget follows it node by node); the benches are run as Simulate runs them.
// ================================================================

using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using CircuitRF.Design.Chain;
using CircuitRF.Design.Schematic;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.Tests.Em3d;
using RfCore.Data;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Chain;

[Collection(CellStatGlobalsCollection.Name)]
public sealed class ChainArraysTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _ws = Path.Combine(Path.GetTempPath(), "crf-arr-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose() { try { Directory.Delete(_ws, true); } catch (IOException) { } }

    private static double Db(double x) => 10 * Math.Log10(x);

    private static ChainStage S(string name, string part, params (string K, string V)[] p)
    {
        var s = new ChainStage { Name = name, Part = part };
        foreach (var (k, v) in p) s.Parameters[k] = v;
        return s;
    }

    private static JsonNode Reference() =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(PalaceBackendTests.RepoRoot(), "testdata", "chain", "arrays_reference.json")))!;

    /// <summary>A combiner on its own, between an element amplifier and nothing: its stage budget is the reference's.</summary>
    private static StageBudget Alone(params (string K, string V)[] p)
    {
        var d = new ChainDesign { Stages = [S("A", "Amp", ("Gain", "0"), ("NF", "")), S("C", "Combiner", p)] };
        var r = ChainBudget.Evaluate(d);
        Assert.Empty(r.Refusals);
        return r.Stages[1].Stage;
    }

    private static void Same(JsonNode refs, StageBudget b, double arrayGainDb)
    {
        Assert.Equal((double)refs["G"]!, b.GainDb, 9);
        Assert.Equal((double)refs["Gn"]!, b.NoiseGainDb, 9);
        Assert.Equal((double)refs["NF"]!, b.NfDb, 9);
        Assert.Equal((double)refs["A"]!, arrayGainDb, 9);
        if (refs["IIP3"] is { } ip3) Assert.Equal((double)ip3, b.Iip3Dbm, 9); else Assert.True(double.IsPositiveInfinity(b.Iip3Dbm));
        if (refs["IP1"] is { } ip1) Assert.Equal((double)ip1, b.Ip1dbDbm, 9);
    }

    /// <summary>
    /// The three combiners of the reference, each on its own: a 2:1-then-3:1 active tree of six with a taper (each level's
    /// amplifier on its own path's power), five elements in 2:1 levels with pass-throughs, and the example's sub-array
    /// (three, a 2:1 and a pass-through, then a 2:1, active and tapered). Gain, noise gain, NF, IIP3, IP1dB and array gain.
    /// </summary>
    [Fact]
    public void UnevenTreesAndUnequalActivePaths_AreTheIndependentReference()
    {
        var refs = Reference()["Combiners"]!;
        StageBudget b;
        b = Alone(("N", "6"), ("Topology", "Tree"), ("Ways", "2, 3"), ("Kind", "Active"), ("IL", "0.5, 0.3"),
                  ("PathIL", "2, 1, 0, 0, 1, 2"), ("Gain", "10, 8"), ("NF", "2, 3"), ("IIP3", "10, 16"));
        Same(refs["Mixed"]!, b, b.GainDb - b.NoiseGainDb);
        b = Alone(("N", "5"), ("Topology", "Tree"), ("IL", "0.5"));
        Same(refs["Five"]!, b, b.GainDb - b.NoiseGainDb);
        b = Alone(("N", "3"), ("Topology", "Tree"), ("Kind", "Active"), ("IL", "0.5"), ("PathIL", "1, 0, 1"),
                  ("Gain", "10"), ("NF", "2"), ("IIP3", "10"));
        Same(refs["Sub"]!, b, b.GainDb - b.NoiseGainDb);
        // A lossless tree of three keeps every element's noise and loses only its coherence: (1 + 1/√2)² of 3.
        var three = Alone(("N", "3"), ("Topology", "Tree"), ("IL", "0"));
        Assert.Equal(0, three.NoiseGainDb, 12);
        Assert.Equal(Db(Math.Pow(1 + 1 / Math.Sqrt(2), 2)), three.GainDb, 12);
    }

    /// <summary>The example's IF Array, stage by stage: its cumulative gain, NF, IIP3, IP1dB, array gain and SNR, and each
    /// stage's own, are the reference's. Two sub-arrays of three: the array gain is the product of the two combiners'.</summary>
    [Fact]
    public void TheIfArray_IsTheIndependentReference()
    {
        var d = ChainDesignIo.LoadFromFile(Path.Combine(Examples.ChainBudgetExampleTests.Workspace, "IF Array.cchain"));
        var r = ChainBudget.Evaluate(d);
        Assert.Empty(r.Refusals);
        var rows = Reference()["IfArray"]!.AsArray();
        Assert.Equal(rows.Count, r.Stages.Count);
        for (int i = 0; i < rows.Count; i++)
        {
            var (x, s) = (rows[i]!, r.Stages[i]);
            Assert.Equal((string)x["Name"]!, s.Name);
            Assert.Equal((double)x["G"]!, s.GainDb, 9);
            Assert.Equal((double)x["NF"]!, s.NfDb, 9);
            Assert.Equal((double)x["IIP3"]!, s.Iip3Dbm, 9);
            Assert.Equal((double)x["IP1"]!, s.Ip1dbDbm, 9);
            Assert.Equal((double)x["A"]!, s.ArrayGainDb, 9);
            Assert.Equal((double)x["SNR"]!, s.SnrDb, 9);
            Assert.Equal((double)x["StageG"]!, s.Stage.GainDb, 9);
            Assert.Equal((double)x["StageNF"]!, s.Stage.NfDb, 9);
        }
        Assert.Equal(6, d.Copies(-1));
        Assert.Equal(Db(2) + r.Stages.Single(s => s.Name == "SUB").ArrayGainDb, r.Totals!.ArrayGainDb, 9);
        Assert.Contains("\"FormatVersion\": 4", ChainDesignIo.Serialize(d));
    }

    /// <summary>The block view draws the IF array compactly: the element's branch once, marked × 6; each sub-array's
    /// combiner and amplifier once, marked × 2; the sub-array tree as its levels, a 2:1 and a pass-through, then a 2:1,
    /// an amplifier after each.</summary>
    [Fact]
    public void TheBlockView_DrawsEachLevelOfTheArrayOnce()
    {
        var vm = new CircuitRF.Ui.Chain.ChainBudgetViewModel(
            ChainDesignIo.LoadFromFile(Path.Combine(Examples.ChainBudgetExampleTests.Workspace, "IF Array.cchain")));
        Assert.Equal(["× 6", "× 6", "× 6", "× 6", "× 6", "× 2", "× 2", "", ""], vm.Stages.Select(s => s.TimesN));
        Assert.Equal(["1 × 2:1 + 1", "", "1 × 2:1", ""], vm.Stages[5].Glyphs.Select(g => g.Caption));
        Assert.Equal(["2:1"], vm.Stages[7].Glyphs.Select(g => g.Caption));
    }

    /// <summary>Sub-arrays at RF: two of three, each a tree with a pass-through, active and tapered, then a sub-array
    /// amplifier and a 2:1 combiner. Its S-parameter and two-tone benches, level by level, on the budget: gain, NF and
    /// each stage's, the compression point, and the intercepts and each stage's.</summary>
    internal static ChainDesign RfSubArrays() => new()
    {
        FrequencyHz = 2.45e9, BandwidthHz = 4.5e6, InputPowerDbm = -80, TemperatureK = 290,
        Stages =
        [
            S("LNA", "Amp", ("Gain", "18"), ("NF", "1.2"), ("IP3", "10")),
            S("PS", "PhaseShifter", ("Phase", "30"), ("IL", "5"), ("NF", "5"), ("IIP3", "15")),
            S("SUB", "Combiner", ("N", "3"), ("Topology", "Tree"), ("Kind", "Active"), ("IL", "0.5, 0.3"), ("PathIL", "1, 0, 1"),
              ("Gain", "10, 8"), ("NF", "2, 3"), ("IIP3", "10, 16"), ("Steer", "45")),
            S("SA", "Amp", ("Gain", "6"), ("NF", "3"), ("IP3", "25")),
            S("ARR", "Combiner", ("N", "2"), ("IL", "0.3"), ("PathIL", "0.5, 0")),
            S("A2", "Amp", ("Gain", "14"), ("NF", "2"), ("IP3", "30")),
        ],
    };

    [Fact]
    [Trait("Category", "Benchmark")]
    public void SubArrays_SimulateToTheirBudget_LevelByLevel()
    {
        var d = RfSubArrays();
        var one = Bench(d, "Sub", ChainBench.OneTone);
        Assert.InRange(M(one, "G_err"), -1e-6, 1e-6);
        Assert.InRange(M(one, "NF_err"), -1e-6, 1e-6);
        foreach (var s in d.Stages) Assert.InRange(M(one, $"NF_{s.Name}_sim") - M(one, $"NF_{s.Name}_calc"), -1e-6, 1e-6);
        Assert.InRange(M(one, "IP1dB_err"), -0.1, 0.1);
        var two = Bench(d, "Sub", ChainBench.TwoTone);
        Assert.InRange(M(two, "IIP3_err"), -1e-3, 1e-3);
        Assert.InRange(M(two, "OIP3_err"), -1e-3, 1e-3);
        foreach (var s in d.Stages)
        {
            Assert.InRange(M(two, $"G_{s.Name}_sim") - M(two, $"G_{s.Name}_calc"), -1e-3, 1e-3);
            if (two.CubesIn("measurements").ContainsKey($"OIP3_{s.Name}_calc"))
                Assert.InRange(M(two, $"OIP3_{s.Name}_sim") - M(two, $"OIP3_{s.Name}_calc"), -1e-3, 1e-3);
        }
        // Placed level by level: six elements, two sub-arrays of two levels (a 2:1 and a pass-through, then a 2:1, an
        // amplifier after each part), one SA each, and one 2:1.
        var (model, _, _) = SchematicPersistence.LoadFromFile(Csch(ChainBenches.CellName("Sub", ChainBench.OneTone)));
        Assert.Equal(6, model.Components.Count(c => c.InstanceName.StartsWith("LNA_", StringComparison.Ordinal)));
        Assert.Equal(2 * 2 + 1, model.Components.Count(c => c.Symbol == SymbolKind.Combiner));
        Assert.Equal(2 * 3, model.Components.Count(c => c.InstanceName.StartsWith("SUB_", StringComparison.Ordinal) && c.InstanceName.Contains("_A", StringComparison.Ordinal)));
        Assert.Equal(["SA_1", "SA_2"], model.Components.Where(c => c.InstanceName.StartsWith("SA_", StringComparison.Ordinal)).Select(c => c.InstanceName).Order());
    }

    /// <summary>The Steering bench of sub-arrays: the wave across all six elements, the pattern the closed-form array
    /// factor of each element's own amplitude (its taper, its tree's pass-through and its sub-array's path), and at
    /// each peak the gain, NF and array gain on the budget.</summary>
    [Fact]
    public void SubArrays_Steering_ThePatternIsTheArrayFactorOfEveryElement()
    {
        var st = Bench(RfSubArrays(), "SubB", ChainBench.Steering);
        foreach (string a in new[] { "m30", "0", "p30" })
        {
            Assert.InRange(M(st, $"G_{a}_err"), -1e-9, 1e-9);
            Assert.InRange(M(st, $"NF_{a}_err"), -1e-9, 1e-9);
            Assert.InRange(M(st, $"A_{a}_err"), -1e-9, 1e-9);
        }
        Assert.InRange(WorstPatternError(st), 0, 1e-6);
    }

    /// <summary>
    /// The array benches through a mixer whose LO is ABOVE the signal, as harmonic balance with one LO split to every
    /// element's mixer. The phase is applied at the IF, after the mixer, where the IF's phase is the RF's negated, so the
    /// phase shifters steer the other way: the pattern is still the array factor, and at each peak the gain, the NF from
    /// the wave's port and the array gain are the budget's. The Combiners bench: each variant's gain and NF. (The
    /// example's own LO, below the signal, is <c>ChainBudgetExampleTests.IfArray_SteeringAndCombiners_*</c>.)
    /// </summary>
    [Fact]
    [Trait("Category", "Benchmark")]
    public void ThroughAMixerAboveTheSignal_TheSteeringAndCombinersBenches_AreOnTheBudget()
    {
        const string flo = "2.8 GHz";
        var d = IfArray(flo);
        Assert.Contains(ChainBench.Steering, ChainBenches.BenchesFor(d));
        string chain = "If" + flo[..3].Replace(".", "", StringComparison.Ordinal);
        var st = Bench(d, chain, ChainBench.Steering);
        foreach (string a in new[] { "m30", "0", "p30" })
        {
            Assert.InRange(M(st, $"G_{a}_err"), -5e-4, 5e-4);   // the small-signal drive, 40 dB under compression
            Assert.InRange(M(st, $"NF_{a}_err"), -1e-3, 1e-3);
            Assert.InRange(M(st, $"A_{a}_err"), -1e-3, 1e-3);
        }
        Assert.InRange(WorstPatternError(st, 3 * 37), 0, 1e-3);   // 5° steps through a mixer
        var cb = Bench(d, chain, ChainBench.Combiners);
        foreach (var (_, _, code) in ChainBenches.VariantsFor(d.Stages.First(s => s.Name == "SUB")))
        {
            Assert.InRange(M(cb, $"G_{code}_err"), -5e-4, 5e-4);
            Assert.InRange(M(cb, $"NF_{code}_err"), -1e-3, 1e-3);
        }
        // One LO for every mixer: a P1Tone into a splitter of six ways on the Steering bench, twelve on the Combiners
        // bench (the sub-array's three into each of its four variants).
        foreach (var (b, ways) in new[] { (ChainBench.Steering, 6), (ChainBench.Combiners, 12) })
        {
            var (model, _, _) = SchematicPersistence.LoadFromFile(Csch(ChainBenches.CellName(chain, b)));
            Assert.Equal(["LO_MIX"], model.Components.Where(c => c.InstanceName.StartsWith("LO_", StringComparison.Ordinal)).Select(c => c.InstanceName));
            Assert.Equal(ways.ToString(System.Globalization.CultureInfo.InvariantCulture),
                         model.Components.Single(c => c.InstanceName == "LOS_MIX").Parameters.First(p => p.Name == "N").Expression);
        }
    }

    /// <summary>The IF Array with its LO at <paramref name="flo"/>: 2.1 GHz below the signal, 2.8 GHz above it.</summary>
    private static ChainDesign IfArray(string flo)
    {
        var d = ChainDesignIo.LoadFromFile(Path.Combine(Examples.ChainBudgetExampleTests.Workspace, "IF Array.cchain"));
        d.Stages.First(s => s.Name == "MIX").Parameters["Flo"] = flo;
        return d;
    }

    private static double WorstPatternError(DataSet ds, int points = 543)
    {
        var p = ds["measurements.Pattern_sim"].RealValues;
        var e = ds["measurements.Pattern_err"].RealValues;
        Assert.Equal(points, p.Length);
        return Enumerable.Range(0, p.Length).Where(i => p[i] > -40).Max(i => Math.Abs(e[i]));
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private string Csch(string cell) => Path.Combine(_ws, cell, "schematic", cell + ".csch");

    private static double M(DataSet ds, string name) => ds["measurements." + name].RealValues[0];

    /// <summary>Transfers <paramref name="d"/> and runs one of its benches as Simulate runs it.</summary>
    private DataSet Bench(ChainDesign d, string chain, ChainBench which)
    {
        Directory.CreateDirectory(_ws);
        if (!File.Exists(Path.Combine(_ws, ".cws")))
            File.WriteAllText(Path.Combine(_ws, ".cws"), "{\"FormatVersion\": 2, \"LibraryRefs\": [], \"KnownFiles\": []}");
        if (!Directory.Exists(Path.Combine(_ws, ChainBenches.CellName(chain, which))))
        {
            var w = ChainBenches.Write(d, _ws, chain);
            Assert.True(w.SchematicPaths.Count == ChainBenches.BenchesFor(d).Count, string.Join("\n", w.Refusals));
        }
        string cell = ChainBenches.CellName(chain, which);
        var (model, _, _) = SchematicPersistence.LoadFromFile(Csch(cell));
        string netlist = Path.Combine(_ws, "netlist.cnl");
        File.WriteAllText(netlist, SchematicCircuit.CnlTextOf(model, cell));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var run = SchematicRunService.RunNetlist(netlist, _ws);
        output.WriteLine($"{cell}: {sw.Elapsed.TotalSeconds:0.0} s");
        Assert.True(run.Status == RunStatus.Success, $"{cell}: {run.StatusMessage}");
        Assert.Empty(run.Errors);
        Assert.DoesNotContain(run.Warnings, x => x.Contains("failed to evaluate", StringComparison.Ordinal));
        var ds = run.GroupedResults!;
        foreach (var (name, cube) in ds.CubesIn("measurements").Where(kv => kv.Key.EndsWith("_err", StringComparison.Ordinal)))
            output.WriteLine($"{cell}: {name} = {cube.RealValues[0]:G6}");
        return ds;
    }
}
