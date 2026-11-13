// ================================================================
//  ChainFidelityTests.cs — the budget off its carrier and between real ports: the in-band response of one design, each
//  mixer's image noise, and the cascade with mismatch; each against a noise run of the benches Transfer writes, and the
//  closed forms against testdata/chain/fidelity_reference.py, an independent standard-library calculation.
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
public sealed class ChainFidelityTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _ws = Path.Combine(Path.GetTempPath(), "crf-chainfid-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose() { try { Directory.Delete(_ws, true); } catch (IOException) { } }

    private static double Lin(double db) => Math.Pow(10, db / 10);
    private static double Db(double x) => 10 * Math.Log10(x);
    private const double T0 = 290;

    private static ChainStage S(string name, string part, params (string K, string V)[] p)
    {
        var s = new ChainStage { Name = name, Part = part };
        foreach (var (k, v) in p) s.Parameters[k] = v;
        return s;
    }

    private static string Root => CircuitRF.Ui.Tests.Em3d.PalaceBackendTests.RepoRoot();

    /// <summary>The superhet whose RF filter is wide enough to pass the mixer's image, 1.75 GHz, and still centred on
    /// the carrier (1.6 to 3.6 GHz: 2.4 GHz at its centre, 6×10⁻⁸ dB down at 2.45 GHz).</summary>
    private static ChainDesign WideSuperhet()
    {
        var d = ChainBudgetTests.Superhet();
        d.Stages[1].Parameters["F1"] = "1.6 GHz";
        d.Stages[1].Parameters["F2"] = "3.6 GHz";
        return d;
    }

    // ── the image ────────────────────────────────────────────────────────────

    /// <summary>
    /// Every shipped chain's preselectors keep its mixers' images under the threshold, so none is counted and every number
    /// it budgets is the one it budgeted before (its benches, to the last bit: ChainBudgetExampleTests).
    /// </summary>
    [Fact]
    public void TheShippedChains_ImagesUnderTheThreshold_BudgetAsBefore()
    {
        foreach (var f in Directory.GetFiles(Path.Combine(Root, "examples", "RF Chain Budget"), "*.cchain"))
        {
            var r = ChainBudget.Evaluate(ChainDesignIo.LoadFromFile(f));
            Assert.NotNull(r.Totals);
            foreach (var im in r.Images)
                output.WriteLine($"{Path.GetFileNameWithoutExtension(f)}: {im.Name} image at {im.ImageHz / 1e9:0.####} GHz would add " +
                                 $"{im.AddedDb(r.Totals!.TeK - (im.Counted ? im.TotalK : 0)):E2} dB{(im.Counted ? " (counted)" : "")}");
            if (Path.GetFileNameWithoutExtension(f) == "Wideband") continue;
            Assert.All(r.Images, im => Assert.False(im.Counted));
            Assert.All(r.Images, im => Assert.True(Math.Abs(im.AddedDb(r.Totals!.TeK)) < ChainImage.ThresholdDb));
        }
    }

    /// <summary>
    /// The superhet with an RF filter that passes the image: the LNA's amplified noise reaches the mixer at 1.75 GHz as well,
    /// T_im = T0 + |S21(f_im)|²·(G·(T0 + Te) − T0), referred to the input by the gain to the mixer. By hand, with the
    /// filter's own |S21| at the image.
    /// </summary>
    [Fact]
    public void AnImagePassingFilter_AddsTheNoiseOfTheStagesBeforeIt_ByHand()
    {
        var d = WideSuperhet();
        var r = ChainBudget.Evaluate(d);
        var im = Assert.Single(r.Images);
        Assert.True(im.Counted);
        Assert.Equal(1.75e9, im.ImageHz, 3);
        double g21 = Math.Pow(ChainLevels.FilterOf(r.Design!.Stages[1]).At(2 * Math.PI * 1.75e9).S21.Magnitude, 2);
        double tLna = Lin(16) * (T0 + T0 * (Lin(1) - 1));
        double tIm = T0 + g21 * (tLna - T0);
        double gTo = Lin(16) * Lin(-1.5);
        Assert.Equal((tIm - T0) / gTo, im.TotalK, 9);
        double friis = T0 * (Lin(1) - 1) + T0 * (Lin(1.5) - 1) / Lin(16) + T0 * (Lin(7) - 1) / gTo
                     + T0 * (Lin(1) - 1) / (gTo * Lin(-7)) + T0 * (Lin(4) - 1) / (gTo * Lin(-8)) + T0 * (Lin(3) - 1) / (gTo * Lin(12));
        Assert.Equal(Db(1 + (friis + im.TotalK) / T0), r.Totals!.NfDb, 9);
        Assert.Contains(r.Notes, n => n.StartsWith("MIX: its image at 1.75 GHz adds", StringComparison.Ordinal));
        // Each stage's share holds the noise it sends through the image, as a noise run's shares do; the source's own noise
        // there is no stage's, and the shares sum to the rest.
        Assert.Equal(100 * (1 - im.TeK[0] / r.Totals.TeK), r.Stages.Sum(s => s.NoiseSharePct), 9);
        Assert.True(Db(1 + im.TotalK / (T0 + friis)) > 1.5, $"the image added {r.ImageNfDb} dB");
    }

    /// <summary>The image band, simulated: the superhet with its wide filter, harmonic-balance noise with the LO running,
    /// the image sideband in the run, and the budget's NF with the image in it.</summary>
    [Fact]
    public void AnImagePassingFilter_SimulatesToTheBudgetWithItsImage()
    {
        var d = WideSuperhet();
        var one = Bench(d, "Img", ChainBench.OneTone);
        Assert.InRange(Measure(one, "G_err"), -1e-3, 1e-3);
        Assert.InRange(Measure(one, "NF_err"), -1e-3, 1e-3);
        foreach (var s in d.Stages) Assert.InRange(Measure(one, $"NF_{s.Name}_sim") - Measure(one, $"NF_{s.Name}_calc"), -1e-3, 1e-3);
    }

    /// <summary>An LNA straight into a mixer, nothing in between: its noise at the image doubles what the mixer sees,
    /// about 3 dB on the NF, which a noise run reads too.</summary>
    [Fact]
    public void AnLnaStraightIntoAMixer_ReadsItsImage_AsTheNoiseRunDoes()
    {
        var d = ChainBudgetTests.Superhet();
        d.Stages.RemoveAt(1);
        var r = ChainBudget.Evaluate(d);
        var one = Bench(d, "Lna", ChainBench.OneTone);
        Assert.InRange(Measure(one, "NF_err"), -1e-3, 1e-3);
        Assert.InRange(r.ImageNfDb, 2, 3.01);
    }

    // ── the cascade with mismatch ────────────────────────────────────────────

    /// <summary>Stages with no S data are their budgets' matched two-ports: the superhet with mismatch is the superhet.</summary>
    [Fact]
    public void WithMismatch_StagesWithNoSData_CascadeToTheMatchedBudget()
    {
        var a = ChainBudget.Evaluate(ChainBudgetTests.Superhet());
        var d = ChainBudgetTests.Superhet();
        d.Mismatch = true;
        var b = ChainBudget.Evaluate(d);
        for (int i = 0; i < a.Stages.Count; i++)
        {
            Assert.Equal(a.Stages[i].GainDb, b.Stages[i].GainDb, 6);
            Assert.Equal(a.Stages[i].NfDb, b.Stages[i].NfDb, 6);
            Assert.Equal(a.Stages[i].Iip3Dbm, b.Stages[i].Iip3Dbm, 6);
            Assert.Equal(a.Stages[i].NoiseSharePct, b.Stages[i].NoiseSharePct, 4);
        }
    }

    /// <summary>
    /// Two amplifiers from files whose ports reflect, and a passive network between them: with mismatch, the budget is the
    /// S-parameter noise run of the bench Transfer writes to 10⁻⁹ dB in gain and NF, stage by stage; matched, it is not.
    /// </summary>
    [Fact]
    public void WithMismatch_AMismatchedSnpChain_IsTheSParameterNoiseBench()
    {
        var d = MismatchedChain();
        var matched = ChainBudget.Evaluate(d);
        d.Mismatch = true;
        var r = ChainBudget.Evaluate(d);
        Assert.Empty(r.Refusals);
        output.WriteLine($"matched G {matched.Totals!.GainDb:F4} NF {matched.Totals.NfDb:F4}; with mismatch G {r.Totals!.GainDb:F4} NF {r.Totals.NfDb:F4}");
        Assert.True(Math.Abs(matched.Totals.GainDb - r.Totals.GainDb) > 0.1);
        var one = Bench(d, "Mis", ChainBench.OneTone);
        Assert.InRange(Measure(one, "G_err"), -1e-9, 1e-9);
        Assert.InRange(Measure(one, "NF_err"), -1e-9, 1e-9);
        foreach (var s in d.Stages) Assert.InRange(Measure(one, $"NF_{s.Name}_sim") - Measure(one, $"NF_{s.Name}_calc"), -1e-9, 1e-9);
    }

    /// <summary>A two-port amplifier file of S (mag, deg) and noise rows, flat over 1 to 4 GHz.</summary>
    private string Amp2(string name, (double M, double A) s11, (double M, double A) s21, (double M, double A) s12, (double M, double A) s22,
                        double nfMin, double gMag, double gDeg, double rn)
    {
        Directory.CreateDirectory(_ws);
        var sb = new System.Text.StringBuilder("# GHz S MA R 50\n");
        foreach (double f in new[] { 1.0, 2.0, 3.0, 4.0 })
            sb.AppendLine(FormattableString.Invariant($"{f} {s11.M} {s11.A} {s21.M} {s21.A} {s12.M} {s12.A} {s22.M} {s22.A}"));
        foreach (double f in new[] { 1.0, 4.0 })
            sb.AppendLine(FormattableString.Invariant($"{f} {nfMin} {gMag} {gDeg} {rn}"));
        File.WriteAllText(Path.Combine(_ws, name), sb.ToString());
        return name;
    }

    private ChainDesign MismatchedChain()
    {
        Amp2("a1.s2p", (0.35, -60), (6.0, 110), (0.05, 20), (0.4, -40), 0.9, 0.35, 45, 0.25);
        Amp2("a2.s2p", (0.3, 150), (4.0, -70), (0.03, 10), (0.25, 80), 2.0, 0.2, -30, 0.4);
        return new ChainDesign
        {
            FrequencyHz = 2.45e9, BandwidthHz = 1e6, InputPowerDbm = -60, BaseDirectory = _ws,
            Stages =
            [
                S("A1", "SnP", ("File", "a1.s2p")),
                S("BPF", "Filter", ("Response", "Chebyshev"), ("Order", "3"), ("F1", "2.4 GHz"), ("F2", "2.6 GHz"), ("Ripple", "0.5"), ("IL", "0.5")),
                S("A2", "SnP", ("File", "a2.s2p"), ("IIP3", "5")),
                S("ATT", "Atten", ("Loss", "2")),
            ],
        };
    }

    // ── the response ─────────────────────────────────────────────────────────

    /// <summary>
    /// The superhet across ±60 MHz with its LO fixed: the IF moves with the input, the IF filter rolls off, and at the
    /// carrier the response is the budget. By hand from each filter's own |S21| at the point.
    /// </summary>
    [Fact]
    public void TheResponse_IsOneDesignAcrossItsBand_ItsFiltersRollingOff()
    {
        var d = ChainBudgetTests.Superhet();
        d.Response = new ChainResponse(-60e6, 60e6, 7);
        var r = ChainBudget.Evaluate(d);
        var ds = ChainBudget.Response(d);
        double[] off = ds["Gain_dB"].Axes[0].Values;
        Assert.Equal("offset", ds["Gain_dB"].Axes[0].Name);
        Assert.Equal([-60e6, -40e6, -20e6, 0, 20e6, 40e6, 60e6], off);
        var at = r.Design!;
        for (int k = 0; k < off.Length; k++)
        {
            double f = 2.45e9 + off[k], fif = f - 2.1e9;
            double rf = Math.Pow(ChainLevels.FilterOf(at.Stages[1]).At(2 * Math.PI * f).S21.Magnitude, 2);
            double iff = Math.Pow(ChainLevels.FilterOf(at.Stages[3]).At(2 * Math.PI * fif).S21.Magnitude, 2);
            double g = 16 + Db(rf) - 7 + Db(iff) + 20 - 3;
            Assert.Equal(g, ds["Gain_dB"].RealValues[k], 9);
            Assert.Equal(fif, ds["Fout_Hz"].RealValues[k], 3);
            Assert.Equal(-70 + g, ds["Pout_dBm"].RealValues[k], 9);
        }
        Assert.Equal(r.Totals!.GainDb, ds["Gain_dB"].RealValues[3], 6);
        Assert.Equal(r.Totals.NfDb, ds["NF_dB"].RealValues[3], 6);
        Assert.True(ds["Gain_dB"].RealValues[0] < r.Totals.GainDb - 10, "the IF filter's roll-off 60 MHz out");
    }

    /// <summary>A value of the frequency is read at each point, and a frequency parameter (an LO, a filter's band) stays
    /// where the carrier put it; absolute frequencies are an axis of their own.</summary>
    [Fact]
    public void TheResponse_ReadsValuesOfTheFrequencyThere_AndKeepsTheLoAndTheBandPut()
    {
        var d = ChainBudgetTests.Superhet();
        d.Stages[0].Parameters["Gain"] = "16 + (f_GHz - 2.45)*10";
        d.Stages[1].Parameters["F1"] = "freq - 100MHz";
        d.Stages[1].Parameters["F2"] = "freq + 100MHz";
        d.Stages[2].Parameters["Flo"] = "freq - 350MHz";
        d.Response = new ChainResponse(2.44e9, 2.46e9, 3, Offset: false);
        var ds = ChainBudget.Response(d);
        Assert.Equal("freq", ds["Gain_dB"].Axes[0].Name);
        // The LO stays at 2.1 GHz: the IF moves with the input.
        Assert.Equal([340e6, 350e6, 360e6], ds["Fout_Hz"].RealValues.Select(x => Math.Round(x)).ToArray());
        var at = ChainBudget.Evaluate(d).Design!;
        Assert.Equal("2350000000", at.Stages[1].Parameters["F1"]);
        double G(double f) => 16 + (f / 1e9 - 2.45) * 10 + Db(Math.Pow(ChainLevels.FilterOf(at.Stages[1]).At(2 * Math.PI * f).S21.Magnitude, 2))
                            - 7 + Db(Math.Pow(ChainLevels.FilterOf(at.Stages[3]).At(2 * Math.PI * (f - 2.1e9)).S21.Magnitude, 2)) + 17;
        Assert.Equal(G(2.44e9), ds["Gain_dB"].RealValues[0], 9);
        Assert.Equal(G(2.46e9), ds["Gain_dB"].RealValues[2], 9);
    }

    [Fact]
    public void TheResponseAndTheMismatch_RoundTripAsVersion4_AndAPlainChainKeepsItsVersion()
    {
        var d = ChainBudgetTests.Superhet();
        Assert.Contains("\"FormatVersion\": 1", ChainDesignIo.Serialize(d));
        d.Response = new ChainResponse(-1e8, 1e8, 41);
        d.Mismatch = true;
        string json = ChainDesignIo.Serialize(d);
        Assert.Contains("\"FormatVersion\": 4", json);
        var back = ChainDesignIo.Deserialize(json);
        Assert.Equal(d.Response, back.Response);
        Assert.True(back.Mismatch);
        Assert.Equal(json, ChainDesignIo.Serialize(back));
        d.Response = new ChainResponse(-3e9, 1e8, 41);
        Assert.Contains(ChainBudget.Evaluate(d).Refusals, x => x.Contains("not a frequency above 0 Hz", StringComparison.Ordinal));
        d.Response = new ChainResponse(1e8, -1e8, 41);
        Assert.Contains(ChainBudget.Evaluate(d).Refusals, x => x.StartsWith("The response runs from", StringComparison.Ordinal));
    }

    // ── the window, the verb and the diagram ─────────────────────────────────

    /// <summary>The window reads a response like a sweep, a row of its own, and shows its tab; the mismatch is a box, a
    /// step of its own to undo; the level diagram's key gives a counted image.</summary>
    [Fact]
    public void TheWindow_TakesTheResponseAndTheMismatch_AndTheDiagramSaysTheImage()
    {
        var d = WideSuperhet();
        var vm = new CircuitRF.Ui.Chain.ChainBudgetViewModel(d);
        Assert.False(vm.HasResponse);
        vm.ResponseInputs.Single(p => p.Name == "Band").Value = "-60, 60, 7";
        Assert.True(vm.HasResponse);
        Assert.Equal(new ChainResponse(-60e6, 60e6, 7), vm.ToDesign().Response);
        Assert.All(vm.ResponsePlots, p => Assert.Contains(vm.PlotHost.Plots, c => ReferenceEquals(c.PlotVM.Plot, p)));
        // A pick the budget refuses is not made: offsets of ±60 MHz are no input frequencies.
        vm.ResponseInputs.Single(p => p.Name == "Of").Value = "Frequency";
        Assert.Contains("not a frequency above 0 Hz", vm.Refusals, StringComparison.Ordinal);
        Assert.True(vm.ToDesign().Response!.Offset);
        vm.ResponseInputs.Single(p => p.Name == "Band").Value = "2.4 GHz, 2.5 GHz, 3";
        vm.ResponseInputs.Single(p => p.Name == "Of").Value = "Frequency";
        Assert.Equal(new ChainResponse(2.4e9, 2.5e9, 3, Offset: false), vm.ToDesign().Response);

        Assert.False(vm.Mismatch);
        vm.Mismatch = true;
        Assert.True(vm.ToDesign().Mismatch);
        vm.UndoCommand.Execute(null);
        Assert.False(vm.Mismatch);

        Assert.Contains("image noise +", vm.LevelPlan!.Image, StringComparison.Ordinal);
        Assert.Null(CircuitRF.Render.Chain.ChainLevelDiagram.For(ChainBudgetTests.Superhet(), ChainBudget.Evaluate(ChainBudgetTests.Superhet()))!.Image);
    }

    /// <summary>The verb says the mismatch on its first line, gives the response's span, and writes it as a group.</summary>
    [Fact]
    public void TheVerb_WritesTheResponseAsAGroup_AndSaysTheMismatch()
    {
        Directory.CreateDirectory(_ws);
        var d = WideSuperhet();
        d.Mismatch = true;
        d.Response = new ChainResponse(-60e6, 60e6, 7);
        string path = Path.Combine(_ws, "Wide.cchain"), npy = Path.Combine(_ws, "wide.npy");
        ChainDesignIo.SaveToFile(path, d);
        var stdout = new StringWriter();
        var old = Console.Out;
        Console.SetOut(stdout);
        try { Assert.Equal(0, CircuitRF.Cli.CliEntry.Run(["chain", path, "-o", npy])); }
        finally { Console.SetOut(old); }
        string text = stdout.ToString();
        Assert.Contains("cascaded with mismatch", text, StringComparison.Ordinal);
        Assert.Contains("response: 2.39 GHz to 2.51 GHz in, 7 points, LOs fixed", text, StringComparison.Ordinal);
        var ds = CircuitRF.Render.DataDisplay.PlotSourceFile.LoadWithNotes(npy).Item1!;
        Assert.Equal(7, ds["Response.NF_dB"].RealValues.Length);
        Assert.True(ds.Contains("NFimage_dB"));
    }

    // ── the independent reference ────────────────────────────────────────────

    /// <summary>
    /// testdata/chain/fidelity_reference.py works out, with its own standard-library arithmetic and the textbook forms, a
    /// superhet whose RF filter passes the image (its response across ±60 MHz with the LO fixed, and its NF with the image
    /// at the carrier) and two amplifiers whose ports reflect, cascaded with mismatch (Friis on available gains, each
    /// stage's noise factor at the source it really sees). The budget agrees to 10⁻⁹ dB.
    /// </summary>
    [Fact]
    public void TheResponseTheImageAndTheMismatch_AreTheIndependentReference()
    {
        string dir = Path.Combine(Root, "testdata", "chain");
        var root = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "fidelity_reference.json")))!;
        int n = 0;
        foreach (var c in root["cases"]!.AsArray())
        {
            var d = ChainDesignIo.Deserialize(c!["design"]!.ToJsonString());
            d.BaseDirectory = dir;
            var r = ChainBudget.Evaluate(d);
            Assert.Empty(r.Refusals);
            var ds = ChainBudget.ToDataSet(r);
            ChainBudget.AddSweeps(ds, d);
            void Check(string key, System.Text.Json.Nodes.JsonNode v)
            {
                double[] want = v is System.Text.Json.Nodes.JsonArray a ? [.. a.Select(x => (double)x!)] : [(double)v];
                double[] got = ds[key].RealValues;
                Assert.Equal(want.Length, got.Length);
                for (int i = 0; i < want.Length; i++, n++)
                {
                    double tol = 1e-9 * (key.EndsWith("Hz", StringComparison.Ordinal) ? Math.Abs(want[i]) : 1);
                    Assert.True(Math.Abs(got[i] - want[i]) <= tol, $"{c["name"]}: {key}[{i}] = {got[i]:R}, the reference {want[i]:R}");
                }
            }
            foreach (var (key, v) in c["expected"]!.AsObject())
                if (v is System.Text.Json.Nodes.JsonObject group)
                    foreach (var (k2, v2) in group) Check($"{key}.{k2}", v2!);
                else Check(key, v!);
        }
        output.WriteLine($"{n} numbers against the reference");
        Assert.True(n > 60);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private string Csch(string cell) => Path.Combine(_ws, cell, "schematic", cell + ".csch");

    /// <summary>Transfers <paramref name="d"/> and runs one of its benches as Simulate runs it.</summary>
    private DataSet Bench(ChainDesign d, string chain, ChainBench which)
    {
        Directory.CreateDirectory(_ws);
        if (!File.Exists(Path.Combine(_ws, ".cws")))
            File.WriteAllText(Path.Combine(_ws, ".cws"), "{\"FormatVersion\": 2, \"LibraryRefs\": [], \"KnownFiles\": []}");
        if (!Directory.Exists(Path.Combine(_ws, ChainBenches.CellName(chain, which))))
        {
            var w = ChainBenches.Write(d, _ws, chain);
            Assert.True(w.SchematicPaths.Count > 0, string.Join("\n", w.Refusals));
        }
        string cell = ChainBenches.CellName(chain, which);
        var (model, _, _) = SchematicPersistence.LoadFromFile(Csch(cell));
        string netlist = Path.Combine(_ws, "netlist.cnl");
        File.WriteAllText(netlist, SchematicCircuit.CnlTextOf(model, cell));
        var run = SchematicRunService.RunNetlist(netlist, _ws);
        Assert.True(run.Status == RunStatus.Success, $"{cell}: {run.StatusMessage}");
        foreach (var x in run.Warnings) output.WriteLine($"{cell}: warning: {x}");
        Assert.Empty(run.Errors);
        var ds = run.GroupedResults!;
        foreach (var (name, cube) in ds.CubesIn("measurements").Where(kv => kv.Key.EndsWith("_err", StringComparison.Ordinal)))
            output.WriteLine($"{cell}: {name} = {cube.RealValues[0]:G6}");
        foreach (var s in d.Stages.Where(s => ds.Contains($"measurements.NF_{s.Name}_sim")))
            output.WriteLine($"{cell}: NF at {s.Name} {Measure(ds, $"NF_{s.Name}_sim"):F6} simulated, {Measure(ds, $"NF_{s.Name}_calc"):F6} budget");
        return ds;
    }

    private static double Measure(DataSet ds, string name) => ds["measurements." + name].RealValues[0];
}
