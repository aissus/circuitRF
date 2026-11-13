// ================================================================
//  ChainBlockingTests.cs — what an LO's phase noise and the blockers do to a receiver: the phase noise in the channel,
//  each blocker's reciprocal mixing and its desensitisation by compression under every amplifier law, the EVM at every
//  node and the SFDR; the closed forms against testdata/chain/blocking_reference.py, an independent standard-library
//  calculation, and the benches Transfer writes against their budget: the Blockers bench's desensitisation and the SFDR
//  bench's crossing, each a harmonic-balance run of a second or so. The example's envelope benches are its own tests.
// ================================================================

using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Chain;
using CircuitRF.Design.Schematic;
using CircuitRF.Ui.Schematic;
using RfCore.Data;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Chain;

[Collection(CellStatGlobalsCollection.Name)]
public sealed class ChainBlockingTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _ws = Path.Combine(Path.GetTempPath(), "crf-chainblk-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose() { try { Directory.Delete(_ws, true); } catch (IOException) { } }

    private static string Root => CircuitRF.Ui.Tests.Em3d.PalaceBackendTests.RepoRoot();

    private static JsonNode Reference() =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(Root, "testdata", "chain", "blocking_reference.json")))!;

    private static ChainDesign Design(JsonNode n) => ChainDesignIo.Deserialize(n.ToJsonString());

    /// <summary>A chain of one stage, its blocker <paramref name="offsetHz"/> off the carrier at <paramref name="levelDbm"/>.</summary>
    private static ChainDesign OneStage(JsonNode stage, double levelDbm, double offsetHz = 20e6) => new()
    {
        FrequencyHz = 2.45e9, BandwidthHz = 4.5e6, InputPowerDbm = -70,
        Stages = [ChainDesignIo.Deserialize($"{{\"Stages\": [{stage.ToJsonString()}]}}").Stages[0]],
        Blockers = [new ChainBlocker(offsetHz, levelDbm)],
    };

    // ── the closed forms against an independent calculation ───────────────────

    /// <summary>
    /// The example's superhet, its LO's mask and its three blockers, worked out by quadrature and by projecting a weak tone
    /// through each nonlinearity rather than by the budget's closed forms: the phase noise in the channel at every node,
    /// each blocker's reciprocal mixing and compression, the EVM, the SNR with them all, and the SFDR.
    /// </summary>
    [Fact]
    public void TheBlockingBudget_IsTheIndependentReference()
    {
        var x = Reference()["superhet"]!;
        var d = Design(x["design"]!);
        // The hand calculation is the shipped example's own chain.
        var shipped = ChainDesignIo.LoadFromFile(Path.Combine(Root, "examples", "RF Chain Budget", "Superhet.cchain"));
        Assert.Equal(ChainDesignIo.Serialize(shipped), ChainDesignIo.Serialize(d));
        var r = ChainBudget.Evaluate(d);
        Assert.Empty(r.Refusals);
        var lv = ChainLevels.Evaluate(d, r);
        int n = lv.Count;
        for (int k = 0; k < n; k++)
        {
            var pn = x["PN_dBc"]![k];
            if (pn is null) Assert.False(double.IsFinite(lv[k].PhaseNoiseDbc), $"node {k}: no phase noise before the mixer");
            else Assert.Equal((double)pn, lv[k].PhaseNoiseDbc, 6);
            Assert.Equal((double)x["EVM_pct"]![k]!, lv[k].EvmPct, 6);
            for (int b = 0; b < 3; b++)
            {
                Assert.Equal((double)x["DesenseRM_dB"]![k]![b]!, lv[k].DesenseRmDb[b], 6);
                Assert.Equal((double)x["DesenseComp_dB"]![k]![b]!, lv[k].DesenseCompDb[b], 6);
            }
        }
        var o = ChainBlocking.Read(r)!;
        Assert.Equal((double)x["SNReff_dB"]!, o.SnrDb, 6);
        Assert.Equal((double)x["EVMpn_pct"]!, o.EvmFloorPct, 6);
        Assert.Equal((double)x["SFDR_dB"]!, r.Totals!.SfdrDb, 9);
        output.WriteLine($"superhet: PN {o.PhaseNoiseDbc:F4} dBc, SNR {o.SnrDb:F4} dB, desense " +
                         string.Join(", ", o.DesenseRmDb.Select((v, b) => $"{v:F4}/{o.DesenseCompDb[b]:F6} dB")));
    }

    /// <summary>Each amplifier law's small-signal gain beside a blocker, at five levels up to 5 dB below the saturated
    /// input: the budget's closed form (tanh's slope averaged over the cycle, a law's F + s·F′) against the weak tone the
    /// reference passes through the law itself.</summary>
    [Fact]
    public void EachLawsDesensitisation_IsTheIndependentReference()
    {
        foreach (var c in Reference()["laws"]!.AsArray())
        {
            var levels = c!["levels"]!.AsArray().Select(v => (double)v!).ToArray();
            for (int i = 0; i < levels.Length; i++)
            {
                var d = OneStage(c["stage"]!, levels[i]);
                var r = ChainBudget.Evaluate(d);
                Assert.Empty(r.Refusals);
                double des = ChainLevels.Evaluate(d, r)[^1].DesenseCompDb[0];
                output.WriteLine($"{c["law"]} at {levels[i]} dBm: {des:F6} dB, reference {(double)c["DesenseComp_dB"]![i]!:F6}");
                Assert.Equal((double)c["DesenseComp_dB"]![i]!, des, 6);
            }
        }
    }

    // ── the benches ───────────────────────────────────────────────────────────

    /// <summary>
    /// A blocker at −10 dBm into an amplifier of each law, with the dynamic-range checks: the Blockers bench's run reads the
    /// signal's gain beside it, and the gain it loses is the budget's desensitisation by compression, within 10⁻³ dB.
    /// </summary>
    [Theory]
    [InlineData("Tanh")]
    [InlineData("Rapp")]
    [InlineData("Saleh")]
    public void EachLaw_DesensitisesOnTheBlockersBench(string law)
    {
        var c = Reference()["laws"]!.AsArray().First(l => (string)l!["law"]! == law)!;
        var d = OneStage(c["stage"]!, -10);
        d.DynamicRange = true;
        var ds = Bench(d, "Law", ChainBench.Blockers);
        double sim = M(ds, "Des_1_sim"), calc = M(ds, "Des_1_calc");
        output.WriteLine($"{law}: desensitised {sim:F6} dB simulated, {calc:F6} dB budget");
        Assert.True(calc > 0.05, $"{law}: the blocker desensitises the amplifier ({calc} dB)");
        Assert.InRange(sim - calc, -1e-3, 1e-3);
        Assert.InRange(M(ds, "Des_0_sim"), -1e-6, 1e-6);   // the input, ahead of every stage
    }

    /// <summary>
    /// The SFDR bench on an amplifier and a pad: two tones swept about where the budget puts their IM3 on the noise floor,
    /// the crossing read off the run, and the SFDR there on the budget's within tanh's fifth-order term (0.03 dB, 32 dB
    /// under the intercept).
    /// </summary>
    [Fact]
    public void TheSfdrBench_FindsTheIm3OnTheNoiseFloor_AtTheBudgetsSfdr()
    {
        var d = new ChainDesign
        {
            FrequencyHz = 2.45e9, BandwidthHz = 4.5e6, InputPowerDbm = -70, DynamicRange = true,
            Stages =
            [
                new ChainStage { Name = "LNA", Part = "Amp", Parameters = new() { ["Gain"] = "16", ["NF"] = "1", ["IP3"] = "15", ["IP3Ref"] = "Output" } },
                new ChainStage { Name = "ATT", Part = "Atten", Parameters = new() { ["Loss"] = "3" } },
            ],
        };
        var ds = Bench(d, "Range", ChainBench.Sfdr);
        output.WriteLine($"SFDR {M(ds, "SFDR_sim"):F4} dB simulated, {M(ds, "SFDR_calc"):F4} dB budget; crossing at {M(ds, "Pcross_sim"):F4} dBm");
        Assert.InRange(M(ds, "SFDR_err"), -0.05, 0.05);
        Assert.InRange(M(ds, "Pcross_err"), -0.05, 0.05);
    }

    // ── the file and the window's inputs ─────────────────────────────────────

    /// <summary>A mask, the dynamic-range checks and the records are version 5, read back as written, and a chain using none
    /// of them is written as before; a mask that does not read, or records out of range, are refused by name.</summary>
    [Fact]
    public void TheNewInputs_AreVersionFive_AndRefusedWhenTheyDoNotRead()
    {
        var d = ChainBudgetTests.Superhet();
        Assert.DoesNotContain("\"FormatVersion\": 5", ChainDesignIo.Serialize(d), StringComparison.Ordinal);
        d.Stages[2].Parameters["PhaseNoise"] = "10kHz:-90, 1MHz:-120";
        d.Records = 3;
        d.DynamicRange = true;
        string text = ChainDesignIo.Serialize(d);
        Assert.Contains("\"FormatVersion\": 5", text, StringComparison.Ordinal);
        var back = ChainDesignIo.Deserialize(text);
        Assert.Equal(3, back.Records);
        Assert.True(back.DynamicRange);
        Assert.Equal(text, ChainDesignIo.Serialize(back));
        // On the benches the mask is the LO's, never the mixer's line.
        var r = ChainBudget.Evaluate(back);
        Assert.Empty(r.Refusals);
        var env = ChainBenches.Build(back, r, "S").First(b => b.Kind == ChainBench.Envelope).Netlist;
        Assert.Contains("PhaseNoise=\"10kHz:-90, 1MHz:-120\"", env, StringComparison.Ordinal);
        Assert.DoesNotContain(env.Split('\n'), l => l.StartsWith("Mixer:", StringComparison.Ordinal) && l.Contains("PhaseNoise", StringComparison.Ordinal));
        Assert.Contains("SeedPerPoint=yes", env, StringComparison.Ordinal);
        Assert.Contains("Values=1,2,3 Inner=ENV1", env, StringComparison.Ordinal);

        back.Stages[2].Parameters["PhaseNoise"] = "10kHz -90";
        back.Records = 0;
        var no = ChainBudget.Evaluate(back).Refusals;
        Assert.Contains(no, x => x.StartsWith("Stage 'MIX': PhaseNoise = '10kHz -90'", StringComparison.Ordinal));
        Assert.Contains(no, x => x.StartsWith("Records is 1 to 64", StringComparison.Ordinal));
    }

    /// <summary>Transfers <paramref name="d"/> and runs one of its benches as Simulate runs it.</summary>
    private DataSet Bench(ChainDesign d, string chain, ChainBench which)
    {
        Directory.CreateDirectory(_ws);
        if (!File.Exists(Path.Combine(_ws, ".cws")))
            File.WriteAllText(Path.Combine(_ws, ".cws"), "{\"FormatVersion\": 2, \"LibraryRefs\": [], \"KnownFiles\": []}");
        var w = ChainBenches.Write(d, _ws, chain, replace: true);
        Assert.True(w.SchematicPaths.Count > 0, string.Join("\n", w.Refusals));
        string cell = ChainBenches.CellName(chain, which);
        var (model, _, _) = SchematicPersistence.LoadFromFile(Path.Combine(_ws, cell, CellFolder.SchematicSubFolder, cell + ".csch"));
        string netlist = Path.Combine(_ws, "netlist.cnl");
        File.WriteAllText(netlist, SchematicCircuit.CnlTextOf(model, cell));
        var run = SchematicRunService.RunNetlist(netlist, _ws);
        Assert.True(run.Status == RunStatus.Success, $"{cell}: {run.StatusMessage}");
        Assert.Empty(run.Errors);
        Assert.DoesNotContain(run.Warnings, x => x.Contains("failed to evaluate", StringComparison.Ordinal));
        return run.GroupedResults!;
    }

    private static double M(DataSet ds, string name) => ds["measurements." + name].RealValues[0];
}
