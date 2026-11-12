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

    [Fact]
    public void AStandIn_WarnsOnceWhenARunLeavesItsSampledRegion_AndAnOrdinaryModelDoesNot()
    {
        string cmc = Path.Combine(_root, "small.cmc");
        ModelCommands.Sample(cmc, new StandInSpec
        {
            Device = StandInFixture.SimpleFet, FrequencyHz = 1e8,
            Vgs = StandInSpec.Axis(-2, 0, 0.5), Vds = StandInSpec.Axis(0, 6, 1),
        });
        ModelCommands.Fit(cmc, "all");
        string model = CmodelExport.Export(cmc, Path.Combine(_root, "small.cmodel"), created: Created).Path;

        string Dc(string m, int n, double vgs, double vds) =>
            $"Vdc:VG g 0 V={vgs}\nComposedFET:X1 g d 0 File=\"{m}\" N={n}\nVdc:VD d 0 V={vds}\nanalysis DC1 type=dc\n";
        IReadOnlyList<string> Warnings(string cnl)
        {
            var r = CircuitEvaluation.Evaluate(PreparedCircuit.FromText(cnl, _root, _root), new CircuitEvaluationRequest { Measurements = false });
            Assert.True(r.Status == RunStatus.Success, r.StatusMessage);
            return [.. r.Warnings.Where(w => w.Contains("sampled over", StringComparison.Ordinal))];
        }
        Assert.Empty(Warnings(Dc(model, 1, -1, 3)));
        Assert.Contains("vds 0 to 6 V", Assert.Single(Warnings(Dc(model, 1, -1, 9))), StringComparison.Ordinal);

        // The same model without its record extrapolates silently, as guided extrapolation is meant to.
        string plain = Path.Combine(_root, "plain.cmodel");
        var m0 = CmodelFile.Read(model);
        CmodelFile.Write(m0 with { Provenance = m0.Provenance with { StandIn = null } }, plain);
        Assert.Empty(Warnings(Dc(plain, 1, -1, 9)));

        // One sampled instance: N other than N0 is refused, by name.
        var refused = CircuitEvaluation.Evaluate(PreparedCircuit.FromText(Dc(model, 2, -1, 3), _root, _root), new CircuitEvaluationRequest());
        Assert.NotEqual(RunStatus.Success, refused.Status);
        Assert.Contains("does not scale", refused.StatusMessage, StringComparison.Ordinal);
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
