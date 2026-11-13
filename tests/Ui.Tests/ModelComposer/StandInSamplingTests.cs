using System.Diagnostics;
using System.Numerics;
using CircuitRF.Core.Devices.Composed;
using CircuitRF.Design.Circuit;
using CircuitRF.Design.Modeling;
using CircuitRF.Harmonica;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.ModelComposer;

/// <summary>
/// LP-3: <c>model sample</c> turns a device circuitRF already runs into a quasi-static stand-in through the Model Composer's
/// own flow. The routine gate: the Hero-2 SDD FET, with its own capacitances, from a <c>.charm</c>, sampled on a coarse
/// grid, fitted, exported and validated against the SDD's own loadpull on five loads; the stand-in warns once when a run
/// leaves its sampled region, refuses to scale, and the verb writes the in-process call's bytes.
/// </summary>
[Collection(CircuitRF.Ui.Tests.Lvs.LvsCliConsoleCollection.Name)]
public sealed class StandInSamplingTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("crf-standin-").FullName;

    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    private const string Created = "2026-11-12T00:00:00Z";

    /// <summary>The coarse grid the routine gate samples on, 33 × 25 points: wider than the RF swing at Hero-2's bias and
    /// compression, whose gate (a resistor, with no diode to clamp it) swings from about −15 to +8 V.</summary>
    internal static StandInSpec Coarse(string? charm) => new()
    {
        From = charm,
        Vgs = StandInSpec.Axis(-20, 12, 1),
        Vds = StandInSpec.Axis(-4, 116, 5),
    };

    [Fact]
    public void TheHero2Sdd_SampledFittedAndExported_MatchesItsOwnLoadpullAtCompression()
    {
        string charm = StandInFixture.Hero2Charm(_root);
        string cmc = Path.Combine(_root, "hero2.cmc");
        var clock = Stopwatch.StartNew();
        foreach (var l in ModelCommands.Sample(cmc, Coarse(charm))) output.WriteLine(l.Text);
        foreach (var l in ModelCommands.Fit(cmc, "all")) output.WriteLine(l.Text);
        var export = CmodelExport.Export(cmc, Path.Combine(_root, "hero2.cmodel"), created: Created);
        var result = ModelValidation.RunFile(cmc, export.Path);
        foreach (string l in result.Lines) output.WriteLine(l);
        output.WriteLine($"{clock.Elapsed.TotalSeconds:F1} s");

        var standIn = CmodelFile.Read(export.Path).Provenance.StandIn;
        Assert.NotNull(standIn);
        Assert.Equal([33, 25], standIn.Grid);
        Assert.StartsWith("Sv = ", standIn.Device, StringComparison.Ordinal);

        var lp = Assert.Single(result.Items, i => i.Kind == ValidationKind.Loadpull);
        Assert.Equal(5, lp.Count);
        Assert.True(lp.Passed, lp.Line);
        Assert.True(Assert.Single(result.Items, i => i.Kind == ValidationKind.DcIv).Passed);
        Assert.DoesNotContain(result.Notes, n => n.Contains(ComposedCoreModel.OutsideSentence, StringComparison.Ordinal));
    }

    /// <summary>The same device sampled over Hero-2's DC range alone: its gate swings past the table at compression,
    /// validation says so beside the loadpull it missed, and the miss is the swing's — the wide grid above passes.</summary>
    [Fact]
    public void AGridNarrowerThanTheSwing_IsNamedBesideTheLoadpullItMisses()
    {
        string charm = StandInFixture.Hero2Charm(_root);
        string cmc = Path.Combine(_root, "narrow.cmc");
        ModelCommands.Sample(cmc, Coarse(charm) with { Vgs = StandInSpec.Axis(-8, 2, 1) });
        ModelCommands.Fit(cmc, "all");
        var result = ModelValidation.RunFile(cmc, CmodelExport.Export(cmc, Path.Combine(_root, "narrow.cmodel"), created: Created).Path);
        foreach (string l in result.Lines) output.WriteLine(l);
        Assert.Contains(result.Notes, n => n.StartsWith("loadpull: ", StringComparison.Ordinal)
                                           && n.Contains(ComposedCoreModel.OutsideSentence, StringComparison.Ordinal));
        Assert.False(Assert.Single(result.Items, i => i.Kind == ValidationKind.Loadpull).Passed);
    }

    /// <summary>The small closed-form FET sampled over vgs −2…0 V and vds 0…6 V, fitted and exported: <c>small.cmodel</c>.</summary>
    private string SmallStandIn(string name = "small")
    {
        string cmc = Path.Combine(_root, name + ".cmc");
        ModelCommands.Sample(cmc, new StandInSpec
        {
            Device = StandInFixture.SimpleFet, FrequencyHz = 1e8,
            Vgs = StandInSpec.Axis(-2, 0, 0.5), Vds = StandInSpec.Axis(0, 6, 1),
        });
        ModelCommands.Fit(cmc, "all");
        return CmodelExport.Export(cmc, Path.Combine(_root, name + ".cmodel"), created: Created).Path;
    }

    private static string Dc(string model, string placement, double vgs, double vds) =>
        $"Vdc:VG g 0 V={vgs}\n{placement}:X1 g d 0 File=\"{model}\"\nVdc:VD d 0 V={vds}\nanalysis DC1 type=dc\n";

    private RunResult Run(string cnl) =>
        CircuitEvaluation.Evaluate(PreparedCircuit.FromText(cnl, _root, _root), new CircuitEvaluationRequest { Measurements = false });

    private IReadOnlyList<string> Outside(string cnl)
    {
        var r = Run(cnl);
        Assert.True(r.Status == RunStatus.Success, r.StatusMessage);
        return [.. r.Warnings.Where(w => w.Contains(ComposedCoreModel.OutsideSentence, StringComparison.Ordinal))];
    }

    [Fact]
    public void AStandIn_WarnsOnceWhenARunLeavesItsSampledRegion_AndAnOrdinaryModelDoesNot()
    {
        string model = SmallStandIn();
        Assert.Empty(Outside(Dc(model, "ComposedFET", -1, 3)));
        string warning = Assert.Single(Outside(Dc(model, "ComposedFET", -1, 9)));
        Assert.Contains("vds 0 to 6 V", warning, StringComparison.Ordinal);
        // Operating points against operating points: the outside ones are a share of all of them, never more.
        var m = System.Text.RegularExpressions.Regex.Match(warning, @"at (\d+) of (\d+) operating point");
        Assert.True(m.Success, warning);
        Assert.InRange(long.Parse(m.Groups[1].Value), 1, long.Parse(m.Groups[2].Value));

        // The same model without its record extrapolates silently, as guided extrapolation is meant to.
        string plain = Path.Combine(_root, "plain.cmodel");
        var m0 = CmodelFile.Read(model);
        CmodelFile.Write(m0 with { Provenance = m0.Provenance with { StandIn = null } }, plain);
        Assert.Empty(Outside(Dc(plain, "ComposedFET", -1, 9)));
    }

    /// <summary>The CLI's run verbs print what the run's models reported once the run is over, and a sweep folds in the
    /// warning of each point it ran on a netlist of its own — before, both lost it.</summary>
    [Fact]
    public void TheWarning_ReachesTheCliAndASweep()
    {
        string model = SmallStandIn();
        File.WriteAllText(Path.Combine(_root, "out.cnl"), Dc(model, "ComposedFET", -1, 9));
        File.WriteAllText(Path.Combine(_root, "in.cnl"), Dc(model, "ComposedFET", -1, 3));
        var outside = ModelCliFixture.Run(_root, "dc", "out.cnl");
        output.WriteLine(outside.StdErr);
        Assert.Equal(0, outside.ExitCode);
        Assert.Contains(ComposedCoreModel.OutsideSentence, outside.StdErr, StringComparison.Ordinal);
        Assert.DoesNotContain(ComposedCoreModel.OutsideSentence, ModelCliFixture.Run(_root, "dc", "in.cnl").StdErr, StringComparison.Ordinal);

        // A sweep whose every point is outside the region.
        string sweep = $"vd = 3\nVdc:VG g 0 V=-1\nComposedFET:X1 g d 0 File=\"{model}\"\nVdc:VD d 0 V=vd\n" +
                       "analysis DC1 type=dc\nanalysis SW1 type=parametric_sweep Var=vd Start=7 Stop=9 Npts=3 Inner=DC1\n";
        Assert.Single(Outside(sweep));
    }

    /// <summary>A loadpull's grid on one, two and three workers: each load of a parallel grid runs on a worker's own copy of
    /// the netlist, whose counts join the caller's stand-in once the load is done, so <c>circuitrf lp</c> prints one warning
    /// with the grid's totals, word for word the serial run's, however the loads were shared out (without the hand-over the
    /// parallel grid printed none). The gate swing at the top rungs leaves the sampled vgs −2 to 0 V.</summary>
    [Fact]
    public void TheWarning_SurvivesAParallelLoadpullGrid_Once()
    {
        string model = SmallStandIn();
        File.WriteAllLines(Path.Combine(_root, "grid.gam"), ["# gamma Z0=50 mag_ang", "0 0", "0.2 0", "0.2 90", "0.2 180", "0.2 270", "0.4 45"]);
        string Warning(int workers)
        {
            File.WriteAllText(Path.Combine(_root, "lp.cnl"), $"""
                ComposedFET:X1 g d 0 File="{model}"
                Tuner:Src g 0 Zdefault=50 Z0=50 BiasTee=on Vbias=-1 Z[1]=50
                Tuner:Load d 0 Zdefault=50 Z0=50 BiasTee=on Vbias=3 Z[1]=50
                analysis LP1 type=loadpull Tone=1e8 MaxHarm=3 LoadTuner=Load SourceTuner=Src Grid="grid.gam" Sweep=Load TuneHarm=1 Compression=3 GainType=Gt PinStart=-10 PinStep=5 PinMax=10 MaxIter=100 Workers={workers}
                """);
            var run = ModelCliFixture.Run(_root, "lp", "lp.cnl");
            Assert.Equal(0, run.ExitCode);
            string warning = Assert.Single(run.StdErr.Split('\n'), l => l.Contains(ComposedCoreModel.OutsideSentence, StringComparison.Ordinal));
            output.WriteLine($"Workers={workers}: {warning}");
            return warning;
        }
        string serial = Warning(1);
        Assert.Equal(serial, Warning(2));
        Assert.Equal(serial, Warning(3));
    }

    /// <summary>One sampled instance: N other than N0 is refused whether the stand-in is placed as a ComposedFET or as its bare
    /// core; the simulator's multiplier M still multiplies whole devices.</summary>
    [Fact]
    public void AStandIn_RefusesN_AsAComposedFetAndAsABareCore_AndTakesM()
    {
        string model = SmallStandIn();
        foreach (string placement in new[] { "ComposedFET", "ComposedCore" })
        {
            var refused = Run(Dc(model, placement, -1, 3).Replace("\"\n", "\" N=2\n", StringComparison.Ordinal));
            Assert.NotEqual(RunStatus.Success, refused.Status);
            Assert.Contains("does not scale", refused.StatusMessage, StringComparison.Ordinal);
            var multiplied = Run(Dc(model, placement, -1, 3).Replace("\"\n", "\" m=2\n", StringComparison.Ordinal));
            Assert.True(multiplied.Status == RunStatus.Success, $"{placement} with m=2: {multiplied.StatusMessage}");
        }
    }

    /// <summary>Two documents in one folder each sample into a folder of their own: the second does not overwrite the first's
    /// tables while the first's record still claims its own grid.</summary>
    [Fact]
    public void TwoDocumentsInOneFolder_KeepTheirOwnSamples()
    {
        StandInSpec Spec(double vdsMax) => new()
        {
            Device = StandInFixture.SimpleFet, FrequencyHz = 1e8,
            Vgs = StandInSpec.Axis(-2, 0, 1), Vds = StandInSpec.Axis(0, vdsMax, 1),
        };
        string a = Path.Combine(_root, "a10.cmc"), b = Path.Combine(_root, "a11.cmc");
        ModelCommands.Sample(a, Spec(6));
        ModelCommands.Sample(b, Spec(4));
        foreach (var (cmc, n) in new[] { (a, 7), (b, 5) })
        {
            var set = CmcFile.Read(cmc).DataSets.Single(s => s.Name == "sampled");
            Assert.Equal($"{ModelCommands.SampledFolderOf(cmc)}/tables.npy", Assert.Single(set.Sources));
            var tables = RfCore.Export.DataSetImporter.Import(Path.Combine(_root, set.Sources[0])).DataSet;
            Assert.Equal(n, tables["vds"].RealValues.Length);
            Assert.Equal([3, n], ModelCommands.StandIn(CmcFile.Read(cmc))!.Grid);
        }
    }

    /// <summary>What is not a device on g and d is refused by name, never sampled into an empty stand-in, and a line that does
    /// not parse is a refusal rather than a crash.</summary>
    [Theory]
    [InlineData("garbage line")]
    [InlineData("SDD:M1 G 0 D 0 I[1,0]=1e-3*_v1 I[2,0]=1e-3*_v2")]
    [InlineData("SDD:M1 g 0 d 0 I[1,0]=_v1/(1+ I[2,0]=_v2")]
    public void ADeviceThatIsNotOneOnGAndD_IsRefused(string line)
    {
        var ex = Assert.Throws<ModelingException>(() => ModelCommands.Sample(Path.Combine(_root, "bad.cmc"), new StandInSpec
        {
            Device = line, FrequencyHz = 1e8, Vgs = StandInSpec.Axis(-1, 0, 1), Vds = StandInSpec.Axis(0, 1, 1),
        }));
        output.WriteLine(ex.Message);
        Assert.Equal("Device", ex.Input);
    }

    [Fact]
    public void AnAxisWhoseStepDoesNotDivideItsSpan_IsRefused_AndADiscIsChecked()
    {
        Assert.Throws<ModelingException>(() => StandInSpec.Axis(0, 1, 0.35));
        Assert.Equal(11, StandInSpec.Axis(0, 1, 0.1).Length);
        Assert.Throws<ModelingException>(() => ModelCommands.Sample(Path.Combine(_root, "disc.cmc"), new StandInSpec
        {
            Device = StandInFixture.SimpleFet, FrequencyHz = 1e8, Disc = (1.2, 1, 4),
            Vgs = StandInSpec.Axis(-1, 0, 1), Vds = StandInSpec.Axis(0, 1, 1),
        }));
        Assert.False(File.Exists(Path.Combine(_root, "disc.cmc")));
        var run = ModelCliFixture.Run(_root, "model", "sample", "disc.cmc", "--device", StandInFixture.SimpleFet,
                                      "--vgs", "-1,0,1", "--vds", "0,1,1", "--freq", "100MHz", "--disc", "0.2,1.5,4");
        Assert.Equal(1, run.ExitCode);
        Assert.Contains("--disc", run.StdErr + run.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public void TheVerb_WritesTheInProcessCallsBytes()
    {
        string a = Path.Combine(_root, "cli"), b = Path.Combine(_root, "inproc");
        Directory.CreateDirectory(a);
        Directory.CreateDirectory(b);
        var run = ModelCliFixture.Run(a, "model", "sample", "fet.cmc", "--device", StandInFixture.SimpleFet,
                                      "--vgs", "-2,0,0.5", "--vds", "0,6,1", "--freq", "100MHz", "--temps", "25,85");
        output.WriteLine(run.StdOut + run.StdErr);
        Assert.Equal(0, run.ExitCode);
        var lines = ModelCommands.Sample(Path.Combine(b, "fet.cmc"), new StandInSpec
        {
            Device = StandInFixture.SimpleFet, FrequencyHz = 1e8, Temperatures = [25, 85],
            Vgs = StandInSpec.Axis(-2, 0, 0.5), Vds = StandInSpec.Axis(0, 6, 1),
        });
        // Every line but the one that states how long the sampling took (the second: the first is the new document's).
        var stdout = run.StdOut.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToList();
        Assert.Equal(lines.Count, stdout.Count);
        Assert.Equal(lines.Select(l => l.Text).Where((_, i) => i != 1), stdout.Where((_, i) => i != 1));
        var files = ModelCliFixture.Files(a);
        Assert.Equal(files, ModelCliFixture.Files(b));
        foreach (string f in files)
            Assert.True(File.ReadAllBytes(Path.Combine(a, f)).AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(b, f))),
                        $"{f} differs between the CLI and the in-process call.");
    }
}

/// <summary>
/// What a stand-in records of the device it was sampled from: the text with every model file by its name, never a path,
/// quoted or not; a digest of that text anyone can recompute from it, whatever the line ends; a digest of the model file's
/// bytes. Two folders holding the same bytes give the same record; a model file that is not there, or a provider that names
/// none, is refused. Through the repository's own compiled fake (<c>tools/fake-osdi-model</c>), so the worker must be built.
/// </summary>
[Collection(ExternalDeviceRegistryCollection.Name)]
public sealed class StandInProvenanceTests : IDisposable
{
    private const string Fake = "tools/fake-osdi-model/fake_osdi.osdi";
    private const string HowTo = "run tools/osdi-worker/build.sh (needs a C compiler)";
    private readonly string _root = Directory.CreateTempSubdirectory("crf-standin-prov-").FullName;
    private readonly string _previousTools = Core.Devices.External.DeviceWorkerManifest.ToolsDirectory;

    public StandInProvenanceTests()
    {
        if (CompilerAvailability.WorkerDirectory() is { } dir) Core.Devices.External.DeviceWorkerManifest.ToolsDirectory = dir;
    }

    public void Dispose()
    {
        Core.Devices.External.ExternalDeviceRegistry.ResetResolved();
        Core.Devices.External.DeviceWorkerManifest.ToolsDirectory = _previousTools;
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    private const string Fet = "Model=crf_fet Pins=3 beta=0.06 vth=-2.5 lambda=0.02 alpha=1.5 delta=0.2 cgs=2e-12 cgd=2e-13 ggs=1e-6";

    private StandInProvenance Sample(string folder, string device)
    {
        Directory.CreateDirectory(Path.Combine(_root, folder));
        string cmc = Path.Combine(_root, folder, "fet.cmc");
        ModelCommands.Sample(cmc, new StandInSpec
        {
            Device = device, FrequencyHz = 1e8, Vgs = StandInSpec.Axis(-2, 0, 1), Vds = StandInSpec.Axis(0, 2, 1),
        });
        return ModelCommands.StandIn(CmcFile.Read(cmc))!;
    }

    private string Copy(string folder)
    {
        string to = Path.Combine(_root, folder, "m.osdi");
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        File.Copy(FixturePaths.Find(Fake)!, to);
        return to;
    }

    [FixtureFact(Fake, HowTo)]
    public void TheSameBytesInTwoFolders_RecordTheSame_WithNoPath_WhateverTheLineEnds()
    {
        Copy("A");
        Copy("B");
        var a = Sample("A", $"VerilogA:X1 g d 0 File=\"m.osdi\" {Fet}\nC:CX g 0 C=1e-15");
        // B names its model by an unquoted absolute path and ends its lines CRLF: the record is A's.
        var b = Sample("B", $"VerilogA:X1 g d 0 File={Path.Combine(_root, "B", "m.osdi")} {Fet}\r\nC:CX g 0 C=1e-15");
        Assert.Equal(a.Device, b.Device);
        Assert.DoesNotContain(_root, b.Device, StringComparison.Ordinal);
        Assert.Contains("File=\"m.osdi\"", b.Device, StringComparison.Ordinal);
        Assert.Equal(a.TextSha256, b.TextSha256);
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(a.Device))), a.TextSha256);
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(FixturePaths.Find(Fake)!))), a.FileSha256);
        Assert.Equal(a.FileSha256, b.FileSha256);
        Assert.Null(a.ThermalNodeHeld);
    }

    [Fact]
    public void AModelFileThatIsNotThere_OrAProviderNamingNone_IsRefused()
    {
        var missing = Assert.Throws<ModelingException>(() => Sample("C", $"VerilogA:X1 g d 0 File=\"nope.osdi\" {Fet}"));
        Assert.Contains("nope.osdi", missing.Message, StringComparison.Ordinal);
        var kit = Assert.Throws<ModelingException>(() => Sample("D", "ExtDevice:X1 g d 0 Provider=SomeKit Type=fet"));
        Assert.Contains("names no model file", kit.Message, StringComparison.Ordinal);
    }
}

/// <summary>The devices the stand-in tests sample.</summary>
internal static class StandInFixture
{
    /// <summary>A small closed-form FET on g d 0: a smooth square law with a gate leak and linear capacitances.</summary>
    public const string SimpleFet =
        "SDD:M1 g 0 d 0 I[1,0]=1e-6*_v1 I[2,0]=0.05*(0.5*((_v1+2.5)+sqrt((_v1+2.5)^2+0.04)))^2*tanh(1.5*_v2)*(1+0.02*_v2) " +
        "I[1,1]=1e-12*_v1 I[2,1]=2e-13*_v2";

    /// <summary>
    /// Hero-2's SDD FET (testdata/Hero2) as a harmonicaRF document: its equations and variables, Cgs, Cdg and Cds of its
    /// own (a gate capacitance that varies with vgs), at Hero-2's bias and fundamental, 25 Ω source, 80 + j10 Ω load and a
    /// 1 Ω 2f0 load, four harmonics.
    /// </summary>
    public static string Hero2Charm(string dir)
    {
        var dut = new DutSpec
        {
            Kind = DutKind.Sdd,
            Parameters = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Sv"] = "-0.837", ["Sc"] = "0.71", ["TV0"] = "4.268", ["TC"] = "1.507", ["th"] = "0.001",
                ["a"] = "0.176", ["g"] = "0.089", ["lam"] = "0.0012", ["B"] = "1130",
                ["I[1,0]"] = "_v1/50",
                ["I[2,0]"] = "(B*TC*tanh(_v2*a*(tanh(g*(TV0 - _v1 + _v2*th + Sc*ln(exp(-(Sv - _v1)/Sc) + 1))) + 1))*" +
                             "ln(exp(-(2*TV0 - 2*_v1 + 2*_v2*th + 2*Sc*ln(exp(-(Sv - _v1)/Sc) + 1))/TC) + 1)*(_v2*lam + 1))/2",
            },
            Capacitances = new DutCapacitances
            {
                Cgs = new DutCapacitance { Coefficients = [2e-12, 1e-13] },
                Cdg = new DutCapacitance { Farads = 2e-13 },
                Cds = new DutCapacitance { Farads = 5e-13 },
            },
        };
        var model = new CircuitModel
        {
            Dut = dut,
            Bias = new BiasSpec { Vgs = -3.05, Vds = 48 },
            Settings = new HarmonicaSettings { HarmonicCount = 4, FrequencyHz = 2e9 },
        };
        var t = new TerminationSet(4);
        t.Set(TerminationSide.Source, 1, new Complex(25, 0));
        t.Set(TerminationSide.Load, 1, new Complex(80, 10));
        t.Set(TerminationSide.Load, 2, new Complex(1, 0));
        string path = Path.Combine(dir, "hero2.charm");
        CharmIo.WriteFile(path, model, t);
        return path;
    }
}
