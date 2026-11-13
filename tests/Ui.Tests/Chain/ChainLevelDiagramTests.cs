// ================================================================
//  ChainLevelDiagramTests.cs — the RF Chain Budget's level diagram, and the level plots' one window rule
//
//    - the window rule: the top 10 dB over every level drawn, the bottom 20 dB under every level but the IM3, rounded out
//      to 10 dB; only finite levels count, so an absent one (NaN, −∞) never moves it;
//    - the window's Levels plots and the bench's Levels display take that window, clip an IM3 under it and say so in its
//      legend, and draw the IM3 only from the first node that has one;
//    - a filter's exact zero takes a blocker to nothing (−∞), never to a −300 dB that comes back up the chain, in the
//      budget, the window's trace and the bench's text;
//    - the level diagram frames every level it draws on the shipped chains, a 30-stage chain and a 200 km link, the IM3
//      under the frame marked at its node;
//    - the Blocks view's strip stands each node on the gap between two blocks, in an array and over two conversions, and
//      the Copy's picture carries it;
//    - circuitrf chain --level-diagram writes a .png or an .svg, makes its folder, and refuses anything else, or no file,
//      before writing anything;
//    - a transmit chain's diagram draws its output power, the P1dB and the Psat referred to each node, and keys the
//      back-off, the noise floor and the ACLR estimate, the transmit readouts' own; a receiver's carries none of it.
// ================================================================

using System;
using System.IO;
using System.Linq;
using CircuitRF.Cli;
using CircuitRF.Design.Chain;
using CircuitRF.Render.Chain;
using CircuitRF.Render.DataDisplay;
using CircuitRF.Ui.Chain;
using Xunit;

namespace CircuitRF.Ui.Tests.Chain;

public sealed class ChainLevelDiagramTests : IDisposable
{
    private readonly string _ws = Path.Combine(Path.GetTempPath(), "crf-chainlv-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose() { try { Directory.Delete(_ws, true); } catch (IOException) { } }

    private static ChainDesign Example(string chain) => ChainDesignIo.LoadFromFile(
        Path.Combine(CircuitRF.Ui.Tests.Examples.ChainBudgetExampleTests.Workspace, chain + ChainDesignIo.Extension));

    private static readonly string[] Shipped = ["Superhet", "Array", "Front End", "Dual Conversion", "26 GHz Link"];

    /// <summary>The superhet with a third blocker on its LO: the mixer puts it at 0 Hz, where the IF filter passes
    /// nothing at all. <paramref name="level"/> and a 60 dB IF amplifier: the case a −300 dB stand-in came back from.</summary>
    private static ChainDesign SuperhetWithABlockerOnTheLo(double level = -45, string ifGain = "20")
    {
        var d = Example("Superhet");
        d.Blockers.Add(new ChainBlocker(2.1e9 - d.FrequencyHz, level));
        d.Stages.First(s => s.Name == "IFA").Parameters["Gain"] = ifGain;
        return d;
    }

    /// <summary>Thirty stages, a 3 dB amplifier and a 2 dB pad in turn: an IM3 hundreds of dB under the signal.</summary>
    private static ChainDesign LongChain() => new()
    {
        Stages = [.. Enumerable.Range(0, 30).Select(i => i % 2 == 0
            ? new ChainStage { Name = $"A{i}", Part = ChainParts.Amp, Parameters = { ["Gain"] = "3", ["NF"] = "1", ["IP3"] = "30", ["IP3Ref"] = "Output" } }
            : new ChainStage { Name = $"P{i}", Part = ChainParts.Atten, Parameters = { ["Loss"] = "2" } })],
    };

    /// <summary>The 26 GHz link at 200 km: an IM3 near −370 dBm.</summary>
    private static ChainDesign FarLink()
    {
        var d = Example("26 GHz Link");
        d.BaseDirectory = CircuitRF.Ui.Tests.Examples.ChainBudgetExampleTests.Workspace;
        d.Link!.Path["Distance"] = "200 km";
        return d;
    }

    private static ChainDesign Named(string chain) => chain switch
    {
        "long chain" => LongChain(),
        "far link" => FarLink(),
        "blocker on the LO" => SuperhetWithABlockerOnTheLo(10, "60"),
        _ => Example(chain),
    };

    private static double[] Finite(params double[] v) => [.. v.Where(double.IsFinite)];

    /// <summary>What a level plot frames (all but the IM3) and the IM3 itself.</summary>
    private static (double[] Framed, double[] Im3) Dbm(IReadOnlyList<ChainNodeLevel> lv) =>
        (Finite([.. lv.SelectMany(n => new[] { n.SignalDbm, n.NoiseDbm, n.InterferenceDbm }.Concat(n.BlockerDbm))]),
         Finite([.. lv.Select(n => n.Im3Dbm)]));

    [Fact]
    public void TheLevelWindow_FramesTheBottomOnAllButTheIm3_AndOnlyOnLevels()
    {
        Assert.Equal((-130.0, 30.0), ChainBudgetDisplay.LevelWindow([-70, -107, double.NaN, double.NegativeInfinity], [-200, 12.5]));
        // An absent level is never a number: −∞ and NaN move nothing, and a real level far down is a level.
        Assert.Equal((-400.0, -40.0), ChainBudgetDisplay.LevelWindow([-374, -50]));
        Assert.Equal((-10.0, 50.0), ChainBudgetDisplay.DbWindow([0, 37, double.NaN]));
        Assert.Equal((-0.5, 6.5), ChainBudgetDisplay.NodeWindow(7));
        Assert.True(ChainBudgetDisplay.Clipped([-90, -200], (-130, 30)));
        Assert.False(ChainBudgetDisplay.Clipped([-90, double.NaN, double.NegativeInfinity], (-130, 30)));
    }

    [Theory]
    [InlineData("Superhet")]
    [InlineData("Array")]
    [InlineData("Front End")]
    [InlineData("Dual Conversion")]
    [InlineData("26 GHz Link")]
    [InlineData("long chain")]
    public void TheWindowsLevelsPlots_FrameEveryLevelButAnIm3UnderTheNoise_AndSaySo(string chain)
    {
        var d = Named(chain);
        var vm = new ChainBudgetViewModel(d);
        var lv = ChainLevels.Evaluate(d, ChainBudget.Evaluate(d));
        var (framed, im3) = Dbm(lv);

        var w = vm.LevelsPlot!.Axes.Window;
        Assert.Equal(ChainBudgetDisplay.LevelWindow(framed, im3), (w.Y, w.Y + w.Height));
        Assert.Equal(ChainBudgetDisplay.NodeWindow(lv.Count), (w.X, w.X + w.Width));
        Assert.All(framed, v => Assert.InRange(v, w.Y, w.Y + w.Height));
        // The bottom is the levels', not the IM3's.
        Assert.Equal(Math.Floor((framed.Min() - 20) / 10) * 10, w.Y);
        var label = vm.LevelsPlot.Traces.Select(t => t.Label).First(l => l?.StartsWith("IM3", StringComparison.Ordinal) == true);
        Assert.Equal(im3.Any(v => v < w.Y) ? ChainBudgetDisplay.Im3Clipped : "IM3", label);

        var dw = vm.LevelsDbPlot!.Axes.Window;
        Assert.Equal(ChainBudgetDisplay.DbWindow(lv.SelectMany(n => new[] { n.NfDb, n.SnrDb, n.MarginDb })), (dw.Y, dw.Y + dw.Height));
    }

    [Fact]
    public void AFiltersZero_TakesABlockerToNothing_InTheBudgetTheTraceAndTheBench()
    {
        var d = SuperhetWithABlockerOnTheLo();
        var r = ChainBudget.Evaluate(d);
        var lv = ChainLevels.Evaluate(d, r);
        int ifbpf = d.Stages.FindIndex(s => s.Name == "IFBPF") + 1;
        Assert.True(lv.Take(ifbpf).All(n => double.IsFinite(n.BlockerDbm[2])));
        Assert.True(lv.Skip(ifbpf).All(n => double.IsNegativeInfinity(n.BlockerDbm[2])), "the zero absorbs every gain after it");
        Assert.True(ChainLevels.ToDataSet(lv)["Blk3_dBm"].RealValues.Skip(ifbpf).All(double.IsNaN));

        // The window's trace stops where the blocker does, every point of it inside the frame.
        var vm = new ChainBudgetViewModel(d);
        var w = vm.LevelsPlot!.Axes.Window;
        var trace = vm.LevelsPlot.Traces.Single(t => t.Label == "blocker 3");
        var points = trace.Points.Where(q => float.IsFinite(q.Y)).ToList();
        Assert.Equal(ifbpf, points.Count);
        Assert.All(points, q => Assert.InRange(q.Y, w.Y, w.Y + w.Height));

        // A bench needs a number: where the budget has −∞ it writes its one stand-in, never an infinity. (The superhet's
        // own Blockers bench is left out: a blocker on the LO lands on the LO's own product. The dual-conversion receiver's
        // second conversion takes the blocker at 0 Hz, its band-pass the zero.)
        var dual = Example("Dual Conversion");
        double flo = ChainValue.Or(dual.Stages.First(s => s.Part == ChainParts.Mixer).Get("Flo"), 0);
        dual.Blockers.Add(new ChainBlocker(flo - dual.FrequencyHz, -45));
        var benches = ChainBenches.Build(dual, ChainBudget.Evaluate(dual), "D");
        Assert.All(benches, b => Assert.DoesNotContain("Infinity", b.Netlist, StringComparison.Ordinal));
        if (benches.FirstOrDefault(b => b.Kind == ChainBench.Blockers && b.Netlist.Contains("Blk3_calc", StringComparison.Ordinal)) is { } zeroed)
            Assert.Contains("-300", zeroed.Netlist, StringComparison.Ordinal);
    }

    [Fact]
    public void AStrongBlockerOnTheLo_ThroughA60dBAmplifier_NeitherComesBackNorGrowsTheFrame()
    {
        var with = SuperhetWithABlockerOnTheLo(10, "60");
        var without = SuperhetWithABlockerOnTheLo(10, "60");
        without.Blockers.RemoveAt(2);
        var lv = ChainLevels.Evaluate(with, ChainBudget.Evaluate(with));

        // Before: −300 + 60 dB came back as a blocker at −260 dBm.
        Assert.True(Finite([.. lv.Select(n => n.BlockerDbm[2])]).Min() > -100);
        var plan = ChainLevelDiagram.For(with, ChainBudget.Evaluate(with))!;
        Assert.Equal(ChainLevelDiagram.For(without, ChainBudget.Evaluate(without))!.Lo, plan.Lo);
    }

    [Fact]
    public void TheLevelsDisplay_DrawsTheIm3FromItsFirstNode_InTheSameWindow()
    {
        var d = Example("Superhet");
        var r = ChainBudget.Evaluate(d);
        var lv = ChainLevels.Evaluate(d, r);
        var cdd = ChainBudgetDisplay.Build("Superhet", ChainBench.Levels, d, r);
        var levels = cdd.Tabs[0].Plots[0];

        // The bench writes −300 at the input, where there is no IM3 yet; a line drawn to it fell out of the window.
        foreach (var cube in new[] { "IM3_calc", "IM3_sim" })
        {
            var slice = Assert.Single(levels.Traces.Single(t => t.CubeName == cube).CubeSlice);
            Assert.Equal((1, lv.Count), (slice.RangeStart, slice.RangeEndExclusive));
        }
        var (framed, im3) = Dbm(lv);
        var window = ChainBudgetDisplay.LevelWindow(framed, im3);
        Assert.Equal(window, (levels.Axes!.WindowY, levels.Axes.WindowY + levels.Axes.WindowHeight));
        Assert.Equal(ChainBudgetDisplay.Im3Clipped, levels.Traces.Single(t => t.CubeName == "IM3_calc").Label);
    }

    [Theory]
    [InlineData("Superhet")]
    [InlineData("Array")]
    [InlineData("Front End")]
    [InlineData("Dual Conversion")]
    [InlineData("26 GHz Link")]
    [InlineData("long chain")]
    [InlineData("far link")]
    [InlineData("blocker on the LO")]
    public void TheLevelDiagram_FramesWhatItDraws_AndMarksAnIm3UnderIt(string chain)
    {
        var d = Named(chain);
        var plan = ChainLevelDiagram.For(d, ChainBudget.Evaluate(d))!;

        var framed = Finite([.. plan.Nodes.SelectMany(n => new[] { n.SignalDbm, n.NoiseDbm, n.SensitivityDbm, n.InterferenceDbm }.Concat(n.BlockerDbm))]);
        var topOnly = Finite([.. plan.Nodes.SelectMany(n => new[] { n.Im3Dbm, n.CompressionDbm, n.NoiseDbm + n.SfdrDb })]);
        if (plan.Link is { } l) { framed = [.. framed, l.PrxDbm]; topOnly = [.. topOnly, l.EirpDbm, l.CeilingDbm]; }
        Assert.Equal(ChainBudgetDisplay.LevelWindow(framed, topOnly), (plan.Lo, plan.Hi));
        Assert.All(framed, v => Assert.InRange(v, plan.Lo, plan.Hi));
        Assert.All(topOnly, v => Assert.True(v <= plan.Hi));
        if (plan.Link is { } link) Assert.True(link.EirpDbm > plan.Nodes.Max(n => n.SignalDbm));

        // Every IM3 under the frame is marked at its node, so a real level never vanishes.
        var below = ChainLevelDiagram.Im3BelowFrame(plan);
        Assert.Equal([.. Enumerable.Range(0, plan.Nodes.Count).Where(k => double.IsFinite(plan.Nodes[k].Im3Dbm) && plan.Nodes[k].Im3Dbm < plan.Lo)], below);
        if (chain is "long chain" or "far link") Assert.NotEmpty(below);
        // It draws, in both themes, at its own size and under blocks.
        foreach (var theme in new[] { RenderTheme.Light, RenderTheme.Dark })
        {
            var (compose, page) = ChainLevelDiagram.Figure(plan, chain, theme);
            Assert.NotNull(PlotDocumentWriter.BuildPngBytes(compose, page, 1));
        }
    }

    [Theory]
    [InlineData("Superhet")]
    [InlineData("Array")]
    [InlineData("Dual Conversion")]
    [InlineData("26 GHz Link")]
    public void TheStripsNodes_StandOnTheGapsBetweenTheBlocks_AndTheCopyCarriesIt(string chain)
    {
        var vm = new ChainBudgetViewModel(Example(chain)) { ShowLevels = true };
        var plan = vm.LevelPlan!;
        var blocks = ChainBlocksGraphicExport.BlocksOf(vm);
        var edges = ChainBlocksGraphicExport.BlockEdges(blocks, plan);
        var xs = ChainBlocksGraphicExport.NodeXs(blocks, plan);

        // A node a stage: the input before the first block, each stage's output after its own, an array's branch included
        // once (the blocks draw it once, "× N"), every conversion's stages in line.
        Assert.Equal(vm.Stages.Count + 1, xs.Length);
        Assert.Equal(plan.Nodes.Count, xs.Length);
        Assert.True(xs[0] < edges[0].Left);
        for (int k = 1; k < edges.Count; k++)
        {
            Assert.InRange(xs[k], edges[k - 1].Right, edges[k].Left);
            Assert.Equal((edges[k - 1].Right + edges[k].Left) / 2, xs[k], 3);
        }
        Assert.True(xs[^1] > edges[^1].Right);
        // A click on a node finds that node.
        var frame = ChainLevelDiagram.Strip(plan, xs, ChainBlocksGraphicExport.StripHeight);
        for (int k = 0; k < xs.Length; k++) Assert.Equal(k, ChainLevelDiagram.NodeAt(frame, xs[k]));
        // The lineup moves right by the strip's scale (and the link's slot), so node 0 keeps its gap.
        Assert.True(edges[0].Left > ChainBlocksGraphicExport.PagePad + ChainLevelDiagram.LeftGutter(plan));

        var bare = ChainBlocksGraphicExport.PageFor(blocks);
        var page = ChainBlocksGraphicExport.PageFor(blocks, ChainBlocksGraphicExport.LevelsOf(vm));
        Assert.Equal(bare.Height + ChainBlocksGraphicExport.StripGap + ChainBlocksGraphicExport.StripHeight, page.Height, 3);
        Assert.True(page.Width > bare.Width);
        var (compose, p) = ChainBlocksGraphicExport.Composition(blocks, CircuitRF.Render.ColorVariant.Light, false, ChainBlocksGraphicExport.LevelsOf(vm));
        Assert.Equal(page, p);
        Assert.NotEmpty(PlotDocumentWriter.BuildPdfBytes(compose, p));

        vm.ShowLevels = false;
        Assert.Null(ChainBlocksGraphicExport.LevelsOf(vm));
    }

    /// <summary>The linear transmitter's diagram: its output power at every node, the P1dB and the Psat referred to each
    /// node, the back-off from both, the noise floor and the ACLR estimate in its key, each the transmit readouts' own, and
    /// no sensitivity or SFDR, which are a receiver's. Every receive chain's plan carries none of it.</summary>
    [Fact]
    public void ATransmitChainsDiagram_DrawsItsOutputPowerPsatAndBackOff_AndKeysTheAclrEstimate()
    {
        var d = Example("Linear TX");
        var r = ChainBudget.Evaluate(d);
        var plan = ChainLevelDiagram.For(d, r)!;
        var tx = ChainTransmit.Read(r)!;
        Assert.Equal(tx, plan.Tx);
        var psat = plan.PsatDbm!;
        Assert.Equal(plan.Nodes.Count, psat.Count);
        Assert.True(double.IsPositiveInfinity(psat[0]), "nothing saturates the input");
        var o = plan.Nodes[^1];
        Assert.Equal(tx.PoutDbm, o.SignalDbm, 9);
        Assert.Equal(tx.Op1dbDbm, o.CompressionDbm, 9);
        Assert.Equal(tx.PsatDbm, psat[^1], 9);
        Assert.Equal(tx.BackoffP1dbDb, o.CompressionDbm - o.SignalDbm, 9);
        Assert.Equal(tx.BackoffPsatDb, psat[^1] - o.SignalDbm, 9);
        // Referred to each node: the ceiling the stages up to it give, never above the one before plus the stage's gain.
        for (int k = 1; k < psat.Count; k++)
            Assert.True(psat[k] <= psat[k - 1] + r.Stages[k - 1].Stage.GainDb + 1e-9, $"node {k}");
        Assert.Equal(36.4, psat[^1], 9);

        // Framed on the transmitter's own levels, with the P1dB, the Psat and the IM3 at the top only.
        var framed = Finite([.. plan.Nodes.SelectMany(n => new[] { n.SignalDbm, n.NoiseDbm, n.InterferenceDbm }.Concat(n.BlockerDbm))]);
        var topOnly = Finite([.. plan.Nodes.SelectMany(n => new[] { n.Im3Dbm, n.CompressionDbm }), .. psat]);
        Assert.Equal(ChainBudgetDisplay.LevelWindow(framed, topOnly), (plan.Lo, plan.Hi));
        Assert.All(framed, v => Assert.InRange(v, plan.Lo, plan.Hi));
        Assert.All(topOnly, v => Assert.True(v <= plan.Hi));

        Directory.CreateDirectory(_ws);
        string svg = Path.Combine(_ws, "tx.svg");
        Assert.True(ChainLevelDiagram.Write(svg, plan, "Linear TX"));
        string text = File.ReadAllText(svg);
        foreach (var said in new[] { "Pout 28.0", "Psat 36.4", "P1dB 34.0", "back-off 6.0 dB from P1dB, 8.4 dB from Psat",
                                     "noise floor −117.8 dBm/Hz", "ACLR −38.1 dBc (estimate, from OIP3)" })
            Assert.Contains(said, text, StringComparison.Ordinal);
        foreach (var unsaid in new[] { "sensitivity", "SFDR", "margin to P1dB" })
            Assert.DoesNotContain(unsaid, text, StringComparison.Ordinal);

        foreach (string chain in Shipped)
        {
            var rx = Example(chain);
            var rp = ChainLevelDiagram.For(rx, ChainBudget.Evaluate(rx))!;
            Assert.Null(rp.Tx);
            Assert.Null(rp.PsatDbm);
        }
    }

    [Fact]
    public void TheVerb_WritesTheLevelDiagram_MakesItsFolder_AndRefusesBeforeWritingAnything()
    {
        Directory.CreateDirectory(_ws);
        string path = Path.Combine(_ws, "Rx.cchain"), png = Path.Combine(_ws, "levels.png"), svg = Path.Combine(_ws, "levels.svg");
        ChainDesignIo.SaveToFile(path, Example("26 GHz Link"));
        // Its path loss reads a table beside it.
        File.Copy(Path.Combine(CircuitRF.Ui.Tests.Examples.ChainBudgetExampleTests.Workspace, "Atmospheric attenuation.csv"),
                  Path.Combine(_ws, "Atmospheric attenuation.csv"));

        Assert.Equal(0, CliEntry.Run(["chain", path, "--level-diagram", png]));
        Assert.Equal(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G' }, File.ReadAllBytes(png)[..4]);
        Assert.Equal(0, CliEntry.Run(["chain", path, "--level-diagram", svg]));
        string text = File.ReadAllText(svg);
        Assert.StartsWith("<?xml", text, StringComparison.Ordinal);
        Assert.Contains("EIRP", text, StringComparison.Ordinal);
        // A folder that is not there yet is made, as render makes one.
        string nested = Path.Combine(_ws, "no", "such", "folder", "levels.png");
        Assert.Equal(0, CliEntry.Run(["chain", path, "--level-diagram", nested]));
        Assert.True(File.Exists(nested));

        // Another kind, or no file at all, is refused before the budget's own -o is written.
        string npy = Path.Combine(_ws, "budget.npy");
        Assert.Equal(1, CliEntry.Run(["chain", path, "-o", npy, "--level-diagram", Path.Combine(_ws, "levels.pdf")]));
        Assert.False(File.Exists(npy));
        Assert.False(File.Exists(Path.Combine(_ws, "levels.pdf")));
        Assert.Equal(1, CliEntry.Run(["chain", path, "-o", npy, "--level-diagram"]));
        Assert.Equal(1, CliEntry.Run(["chain", path, "--level-diagram", "-o", npy]));
        Assert.False(File.Exists(npy));
    }
}
