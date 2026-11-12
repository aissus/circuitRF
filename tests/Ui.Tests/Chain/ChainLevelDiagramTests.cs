// ================================================================
//  ChainLevelDiagramTests.cs — the RF Chain Budget's level plots, and their one window rule
//
//    - the window rule frames every finite level and drops a sentinel (the ±300 a bench writes for none, a dB of nothing);
//    - the window's Levels plots take that window (the refill used to drop a plot's stated window and autoscale), and so do
//      the bench's Levels display, which draws the IM3 only from the first node that has one.
// ================================================================

using System;
using System.IO;
using System.Linq;
using CircuitRF.Design.Chain;
using CircuitRF.Render.DataDisplay;
using CircuitRF.Ui.Chain;
using Xunit;

namespace CircuitRF.Ui.Tests.Chain;

public sealed class ChainLevelDiagramTests
{
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
}
