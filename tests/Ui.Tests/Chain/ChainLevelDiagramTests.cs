// ================================================================
//  ChainLevelDiagramTests.cs — the RF Chain Budget's level diagram, and the level plots' one window rule
//
//    - the window rule frames every finite level and drops a sentinel (the ±300 a bench writes for none, a dB of nothing);
//    - the window's Levels plots take that window (the refill used to drop a plot's stated window and autoscale), and so do
//      the bench's Levels display, which draws the IM3 only from the first node that has one;
//    - the level diagram frames every level it draws, the link's EIRP and ceiling included, and no blocker a filter's zero
//      has taken away;
//    - circuitrf chain --level-diagram writes the figure as a .png or an .svg, and refuses any other kind.
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

    /// <summary>The superhet with a third blocker on its LO: the mixer puts it at 0 Hz, where the IF filter passes nothing,
    /// so from there on it is the −300 dB a zero reads as.</summary>
    private static ChainDesign SuperhetWithAnAbsentBlocker()
    {
        var d = Example("Superhet");
        d.Blockers.Add(new ChainBlocker(2.1e9 - d.FrequencyHz, -45));
        return d;
    }

    private static double[] Finite(params double[] v) => [.. v.Where(x => double.IsFinite(x) && Math.Abs(x) < ChainBudgetDisplay.Sentinel)];
    [Fact]
    public void TheLevelWindow_HoldsEveryFiniteLevel_AndNoSentinel()
    {
        var (lo, hi) = ChainBudgetDisplay.LevelWindow([-70, double.NaN, double.PositiveInfinity, -300, -3000, 300, 12.5]);
        Assert.Equal((-80.0, 30.0), (lo, hi));
        Assert.Equal((-0.5, 6.5), ChainBudgetDisplay.NodeWindow(7));
        Assert.Equal((-10.0, 10.0), ChainBudgetDisplay.LevelWindow([double.NaN]));
    }

    [Fact]
    public void TheWindowsLevelsPlots_FrameEveryFiniteLevel_AndNoSentinel()
    {
        var d = SuperhetWithAnAbsentBlocker();
        var vm = new ChainBudgetViewModel(d);
        var lv = ChainLevels.Evaluate(d, ChainBudget.Evaluate(d));
        Assert.Contains(lv, n => n.BlockerDbm[2] <= -ChainBudgetDisplay.Sentinel);   // the sentinel is there to be dropped

        var dbm = Finite([.. lv.SelectMany(n => new[] { n.SignalDbm, n.NoiseDbm, n.Im3Dbm, n.InterferenceDbm }.Concat(n.BlockerDbm))]);
        var db = Finite([.. lv.SelectMany(n => new[] { n.NfDb, n.SnrDb, n.MarginDb })]);
        foreach (var (plot, levels) in new[] { (vm.LevelsPlot!, dbm), (vm.LevelsDbPlot!, db) })
        {
            var w = plot.Axes.Window;
            Assert.All(levels, v => Assert.InRange(v, w.Y, w.Y + w.Height));
            Assert.True(w.Y > -ChainBudgetDisplay.Sentinel, $"the window reaches {w.Y}");
            // The stated window, not an autoscale: 10 dB beyond the levels, rounded out to 10, and half a node beyond the
            // input and the output.
            Assert.Equal(ChainBudgetDisplay.LevelWindow(levels), (w.Y, w.Y + w.Height));
            Assert.Equal(ChainBudgetDisplay.NodeWindow(lv.Count), (w.X, w.X + w.Width));
        }
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
        var dbm = Finite([.. lv.SelectMany(n => new[] { n.SignalDbm, n.NoiseDbm, n.Im3Dbm, n.InterferenceDbm }.Concat(n.BlockerDbm))]);
        Assert.Equal(ChainBudgetDisplay.LevelWindow(dbm), (levels.Axes!.WindowY, levels.Axes.WindowY + levels.Axes.WindowHeight));
    }

    [Theory]
    [InlineData("Superhet")]
    [InlineData("26 GHz Link")]
    public void TheLevelDiagram_FramesEveryLevelItDraws_AndNoSentinel(string chain)
    {
        var d = chain == "Superhet" ? SuperhetWithAnAbsentBlocker() : Example(chain);
        var plan = ChainLevelDiagram.For(d, ChainBudget.Evaluate(d))!;

        var drawn = Finite([.. plan.Nodes.SelectMany(n => new[] { n.SignalDbm, n.NoiseDbm, n.SensitivityDbm, n.Im3Dbm, n.InterferenceDbm,
                                                                  n.CompressionDbm, n.NoiseDbm + n.SfdrDb }.Concat(n.BlockerDbm))]);
        if (plan.Link is { } l) drawn = [.. drawn, l.EirpDbm, l.PrxDbm, l.CeilingDbm];
        Assert.All(drawn, v => Assert.InRange(v, plan.Lo, plan.Hi));
        Assert.True(plan.Lo > -ChainBudgetDisplay.Sentinel);
        Assert.Equal(ChainBudgetDisplay.LevelWindow(drawn), (plan.Lo, plan.Hi));
        // The link stands in front: its EIRP is the figure's top.
        if (chain != "Superhet") Assert.True(plan.Link!.EirpDbm > plan.Nodes.Max(n => n.SignalDbm));
    }

    [Fact]
    public void TheVerb_WritesTheLevelDiagram_AsAPngOrAnSvg()
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
        Assert.Equal(1, CliEntry.Run(["chain", path, "--level-diagram", Path.Combine(_ws, "levels.pdf")]));
    }
}
