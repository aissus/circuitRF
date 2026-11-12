using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using CircuitRF.Core.Devices.Composed;
using CircuitRF.Core.Devices.External;
using CircuitRF.Design.Circuit;
using CircuitRF.Design.Modeling;
using CircuitRF.Harmonica;
using RfCore.Export;
using RfCore.Loadpull;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.ModelComposer;

/// <summary>
/// LP-3's opt-in tier. Hero-2's SDD FET on a finer grid against the Hero-3 loads, frozen in <c>testdata/StandInComposed</c>
/// (the sampled tables, the sampled document and the comparison at compression; <c>CRF_WRITE_STANDIN_GOLDENS=1</c> writes
/// them), and the round trip a compiled model takes: the shipped GaN <c>.cmodel</c> as Verilog-A, compiled, sampled back
/// into a stand-in, against the <c>ComposedFET</c> it came from.
/// </summary>
[Collection(ExternalDeviceRegistryCollection.Name)]
public sealed class StandInBenchmarkTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("crf-standin-bench-").FullName;
    private readonly string _previousTools = DeviceWorkerManifest.ToolsDirectory;

    public void Dispose()
    {
        ExternalDeviceRegistry.ResetResolved();
        DeviceWorkerManifest.ToolsDirectory = _previousTools;
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    private static string Golden(string name) => Path.Combine(ModelCliFixture.RepoRoot(), "testdata", "StandInComposed", name);
    private static bool Write => Environment.GetEnvironmentVariable("CRF_WRITE_STANDIN_GOLDENS") == "1";
    private static string R(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>The compared figures, one row per Hero-3 load: the SDD's and the stand-in's at 3 dB compression.</summary>
    private static readonly string[] Figures = ["Pout_dBm", "Gt_dB", "PAE", "AMPM_deg"];

    [Fact]
    [Trait("Category", "Benchmark")]
    public void TheHero2Sdd_OnAFinerGrid_AgainstTheHero3Loads_IsTheFrozenResult()
    {
        string charm = StandInFixture.Hero2Charm(_root);
        string cmc = Path.Combine(_root, "hero2.cmc");
        var clock = Stopwatch.StartNew();
        foreach (var l in ModelCommands.Sample(cmc, StandInSamplingTests.Coarse(charm) with
                 { Vgs = StandInSpec.Axis(-20, 12, 0.5), Vds = StandInSpec.Axis(-4, 116, 2) }))
            output.WriteLine(l.Text);
        string sampled = File.ReadAllText(cmc);

        // The SDD's own loadpull over the Hero-3 loads, through the bench `model sample` writes for its disc.
        var model = CharmIo.ReadAllFile(charm);
        var disc = CmcFile.Read(cmc).DataSets.Single(s => s.Name == "loadpull");
        var c = disc.Conditions.LargeSignal!;
        string cnl = ValidationBench.Loadpull("hero3", HarmonicaNetlist.DutText(model.Model, "g", "d", "0"), 25, c.FrequencyHz!.Value,
                                              c.Vgs!.Value, c.Vds!.Value, c.SourceZ!.Value, c.SourceHarmonics, c.LoadHarmonics,
                                              Path.Combine(ModelCliFixture.RepoRoot(), "testdata", "Hero3", "hero3_load.gam"),
                                              -10, 1, 50, 4, 4);
        var device = Stopwatch.StartNew();
        var r = CircuitEvaluation.Evaluate(PreparedCircuit.FromText(cnl, _root, _root), new CircuitEvaluationRequest { Measurements = false });
        Assert.True(r.Status == RunStatus.Success, r.StatusMessage);
        double deviceSeconds = device.Elapsed.TotalSeconds;
        var lp = r.Results.Select(x => x.Data).First(d => d.Contains("GammaLoad"));
        SplWriter.WriteSpl(lp, Path.Combine(_root, "hero3.spl"));
        ModelCommands.Import(cmc, disc with { Name = "hero3", Sources = ["hero3.spl"] });

        foreach (var l in ModelCommands.Fit(cmc, "all")) output.WriteLine(l.Text);
        string cmodel = CmodelExport.Export(cmc, Path.Combine(_root, "hero2.cmodel"), created: "2026-11-12T00:00:00Z").Path;
        var standIn = Stopwatch.StartNew();
        var result = ModelValidation.RunFile(cmc, cmodel, ModelValidation.Settings(CmcFile.Read(cmc)) with { DataSets = ["hero3"] });
        double standInSeconds = standIn.Elapsed.TotalSeconds;
        foreach (string l in result.Lines) output.WriteLine(l);
        output.WriteLine($"SDD loadpull {deviceSeconds:F2} s, the stand-in's {standInSeconds:F2} s; {clock.Elapsed.TotalSeconds:F1} s in all");

        var item = Assert.Single(result.Items);
        Assert.True(item.Passed, item.Line);
        Assert.Empty(result.Notes);

        // The comparison at compression, every load either side reaches it at.
        var at = item.Data;
        int n = at["GammaLoad"].ComplexValues.Length;
        var rows = new List<string> { "load,gamma_re,gamma_im," + string.Join(",", Figures.SelectMany(f => new[] { $"{f}.sdd", $"{f}.standin" })) };
        for (int g = 0; g < n; g++)
        {
            var gamma = at["GammaLoad"].ComplexValues[g];
            rows.Add($"{g},{R(gamma.Real)},{R(gamma.Imaginary)}," + string.Join(",", Figures.SelectMany(f => new[]
                { R(at[$"{f}@compression.meas"].RealValues[g]), R(at[$"{f}@compression.model"].RealValues[g]) })));
        }
        string folder = ModelCommands.SampledFolderOf(cmc), tables = Path.Combine(_root, folder, "tables.npy");
        // The frozen document names the frozen tables beside it; its loadpull set keeps the path sample wrote, a validation
        // file this test does not read (the device's loadpull over the Hero-3 loads is run again above).
        string Frozen(string text) => text.Replace($"{folder}/tables.npy", "hero2_sampled.npy");
        if (Write)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Golden("x"))!);
            File.Copy(tables, Golden("hero2_sampled.npy"), overwrite: true);
            File.WriteAllText(Golden("hero2.cmc"), Frozen(sampled));
            File.WriteAllLines(Golden("hero2_hero3_loads.csv"), rows);
            return;
        }

        // The samples are the frozen ones, and so is the document that names them.
        var now = DataSetImporter.Import(tables).DataSet;
        var then = DataSetImporter.Import(Golden("hero2_sampled.npy")).DataSet;
        foreach (string name in new[] { "id", "ig" })
            Assert.All(now[name].RealValues.Zip(then[name].RealValues), p => Assert.True(Same(p.First, p.Second), $"{name}: {p.First:R} against {p.Second:R}"));
        Assert.All(now["S"].ComplexValues.Zip(then["S"].ComplexValues), p => Assert.True(Complex.Abs(p.First - p.Second) <= 1e-9, $"S: {p.First} against {p.Second}"));
        Assert.Equal(File.ReadAllText(Golden("hero2.cmc")), Frozen(sampled));

        // The comparison: each figure within a hundredth of the frozen one (a dB, a point of PAE, a degree).
        var frozen = File.ReadAllLines(Golden("hero2_hero3_loads.csv"));
        Assert.Equal(frozen.Length, rows.Count);
        for (int k = 1; k < rows.Count; k++)
        {
            var a = rows[k].Split(',').Skip(3).Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray();
            var b = frozen[k].Split(',').Skip(3).Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray();
            for (int i = 0; i < a.Length; i++)
                Assert.True(double.IsNaN(a[i]) ? double.IsNaN(b[i]) : Math.Abs(a[i] - b[i]) <= 0.01, $"load {k - 1}, column {i}: {a[i]:R} against {b[i]:R}");
        }

        static bool Same(double x, double y) => double.IsNaN(x) ? double.IsNaN(y) : Math.Abs(x - y) <= 1e-9 * Math.Max(Math.Abs(y), 1e-6);
    }

    /// <summary>
    /// The shipped synthetic GaN through Verilog-A and back: its <c>.va</c> (traps at their DC values, so it is quasi-static,
    /// and its thermal pin wired to <c>t</c>, so it is sampled isothermally at three temperatures and its Rth read from the
    /// pin), compiled and sampled into a stand-in with the Cth the model states typed; the stand-in against the
    /// <c>ComposedFET</c> with the same switches, at DC with self-heating on and at 2 GHz, between the sampled grid lines.
    /// </summary>
    [CompilerFact]
    [Trait("Category", "Benchmark")]
    public void TheGanModel_ThroughVerilogA_SampledBack_IsTheComposedFetItCameFrom()
    {
        if (CompilerAvailability.WorkerDirectory() is { } dir) DeviceWorkerManifest.ToolsDirectory = dir;
        string models = Path.Combine(ModelCliFixture.RepoRoot(), "examples", "Model Composer", "synthetic-gan");
        string va = Path.Combine(models, "gan_4x125.va"), original = Path.Combine(models, "gan_4x125.cmodel");
        const string Quasi = "GateLag=0 DrainLag=0";
        string cmc = Path.Combine(_root, "gan.cmc");
        var clock = Stopwatch.StartNew();
        foreach (var l in ModelCommands.Sample(cmc, new StandInSpec
                 {
                     Device = $"VerilogA:X1 g d 0 t File=\"{va}\" N=4 {Quasi}",
                     Vgs = StandInSpec.Axis(-5, 1, 0.1), Vds = StandInSpec.Axis(0, 20, 1),
                     Temperatures = [25, 75, 125], FrequencyHz = 2e8,
                 }))
            output.WriteLine(l.Text);
        double sampling = clock.Elapsed.TotalSeconds;
        var thermal = ThermalFit.Settings(CmcFile.Read(cmc));
        Assert.True(thermal.SelfHeating);
        // The model's 25 K/W heats with the core's own power; read on the terminals' it is less by what the network's
        // resistances dissipate — which is the Rth a stand-in whose core is the whole device must carry.
        Assert.InRange(thermal.Rth!.Value, 20, 25);
        ModelCommands.Save(cmc, ThermalFit.WithSettings(CmcFile.Read(cmc), thermal with { Cth = 4e-7 }));
        foreach (var l in ModelCommands.Fit(cmc, "all")) output.WriteLine(l.Text);
        // The module's network is inside the sampled device, so its Y is not exactly a charge function of the terminal
        // voltages (the Charge step reports paths A and B some per cent apart): the ANN fits the closest one that is.
        foreach (var l in ModelCommands.Fit(cmc, "charge", new FitOptions(Method: "ANN"))) output.WriteLine(l.Text);
        string standIn = CmodelExport.Export(cmc, Path.Combine(_root, "gan.cmodel"), created: "2026-11-12T00:00:00Z").Path;
        output.WriteLine($"sampled in {sampling:F1} s, {clock.Elapsed.TotalSeconds:F1} s in all");

        (double Id, Complex S21) Run(string dut, double vgs, double vds)
        {
            var (lib, tb) = new CircuitRF.Core.Netlist.CnlReader().Read(ValidationBench.SParams("check", dut + "\n", 25, vgs, vds, [2e9], 50));
            using var nl = new CircuitRF.Core.Elaboration.Elaborator(lib) { BaseDirectory = _root }.Elaborate(tb);
            var dc = CircuitRF.Engine.NonlinearDcEngine.Run(nl);
            Assert.True(dc.Converged, $"{dut} at {vgs}, {vds}");
            return (dc.ProbeCurrents["IPD"], (Complex)CircuitRF.Engine.SParameterEngine.Run(nl, [2e9])["S"][0, 1, 0]);
        }
        string twin = $"ComposedFET:X1 g d 0 File=\"{original}\" N=4 GateLag=false DrainLag=false";
        string stand = $"ComposedFET:X1 g d 0 File=\"{standIn}\"";
        double worstId = 0, worstS = 0;
        foreach (var (vgs, vds) in new[] { (-2.6, 5.5), (-1.85, 10.5), (-1.1, 15.5), (-0.35, 3.5), (0.15, 8.5) })
        {
            var (ia, sa) = Run(twin, vgs, vds);
            var (ib, sb) = Run(stand, vgs, vds);
            // |ΔS21| over max(|S21|, 1): relative where the device has gain, absolute near pinch-off, where |S21| is a few
            // hundredths and a relative error says little; the plain relative error is printed beside it.
            output.WriteLine($"vgs {vgs} vds {vds}: Id {ia:G5} / {ib:G5} A, S21 {sa:F4} / {sb:F4}, " +
                             $"|ΔS21|/|S21| {100 * Complex.Abs(sa - sb) / Complex.Abs(sa):F1} %");
            worstId = Math.Max(worstId, Math.Abs(ia - ib) / Math.Max(Math.Abs(ia), 1e-3));
            worstS = Math.Max(worstS, Complex.Abs(sa - sb) / Math.Max(Complex.Abs(sa), 1));
        }
        output.WriteLine($"worst Id {100 * worstId:F2} %, worst |ΔS21|/max(|S21|, 1) {100 * worstS:F2} %");
        Assert.True(worstId < 0.01, $"Id differs by {100 * worstId:F2} %");
        Assert.True(worstS < 0.03, $"S21 differs by {100 * worstS:F2} %");
    }
}
