// ================================================================
//  ChainCancellerTests.cs — a canceller's nonlinearity and its residual through the stages after it
//
//    - with no IP3 a canceller reads as it did before it had one: linear, its depth the settings' alone, nothing said;
//    - the example's front end is an independent calculation's (testdata/chain/canceller_reference.py: the Tanh law's
//      two-tone coefficients by quadrature, the cascade by hand), and the budget's fifth-order series is that law's;
//    - the level diagram draws the canceller's own IM3 from its output on, and nothing for a linear one.
// ================================================================

using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using CircuitRF.Design.Chain;
using CircuitRF.Ui.Tests.Em3d;
using CircuitRF.Ui.Tests.Examples;
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
    /// residual, own IM3 and the null's amplitude, and at every stage after it the residual, the
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
}
