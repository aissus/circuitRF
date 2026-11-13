// ================================================================
//  ChainBudgetExampleTests.cs — the "RF Chain Budget" example workspace
//
//  The four chains (a 2.45 GHz superhet, a four-element steered array, a front end with a canceller and a dual-conversion
//  receiver) and their transferred benches, run as Simulate runs them (SchematicRunService.RunNetlist on the schematic's
//  own netlist), in a temporary copy of the workspace:
//
//    - the shipped benches and displays are what Transfer to Schematic writes from the shipped .cchain today, to the
//      last bit of a computed number (which the platform's maths library decides);
//    - the workspace checks clean, and every display column is a measurement its bench computes;
//    - each bench's simulated gain, NF, intercepts, compression and SNR sit on the budget within the tolerances the
//      README quotes, stage by stage;
//    - the array's Steering bench draws the closed-form array factor at every steer angle, its NF and array gain staying
//      put at each beam's peak, and its Combiners bench sets the four combiners each on its own budget;
//    - the dual-conversion receiver's benches, each conversion's on its own budget, and the two conversions' simulated
//      gain and NF cascading to the whole chain's budget.
//
//  The 2-Tone and Envelope runs take 7 to 110 s each, so those are Category=Benchmark.
// ================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using CircuitRF.Cli;
using CircuitRF.Design.Chain;
using CircuitRF.Design.Schematic;
using CircuitRF.Render.DataDisplay;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.Tests.Em3d;
using RfCore.Data;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Examples;

[Collection(CellStatGlobalsCollection.Name)]
public sealed class ChainBudgetExampleTests(ITestOutputHelper output) : IDisposable
{
    public const string Folder = "RF Chain Budget";
    public static string Workspace => Path.Combine(PalaceBackendTests.RepoRoot(), "examples", Folder);

    private static readonly string[] Chains = ["Superhet", "Array", "Front End", "Dual Conversion", "26 GHz Link", "Linear TX", "Wideband"];

    /// <summary>The benches Transfer writes for a shipped chain: a dual-conversion chain's mostly once per conversion.</summary>
    private static IReadOnlyList<ChainBenchText> Benches(string root, string chain)
    {
        var d = ChainDesignIo.LoadFromFile(Path.Combine(root, chain + ".cchain"));
        return ChainBenches.Build(d, ChainBudget.Evaluate(d), chain);
    }

    private readonly string _ws = Path.Combine(Path.GetTempPath(), "crf-chainex-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose() { try { Directory.Delete(_ws, true); } catch (IOException) { } }

    /// <summary>The 26 GHz link's eight-element benches take most of the transfer's time; their own test is a benchmark
    /// (<see cref="TheShippedLinkBenches_AreWhatTransferWritesToday"/>), and this one still counts their folders.</summary>
    private const string LinkChain = "26 GHz Link";

    /// <summary>One shipped chain's benches and displays, transferred in a copy and compared, a case a chain so each stays
    /// short; the link's is a benchmark (<see cref="TheShippedLinkBenches_AreWhatTransferWritesToday"/>).</summary>
    [Theory]
    [InlineData("Superhet")]
    [InlineData("Array")]
    [InlineData("Front End")]
    [InlineData("Dual Conversion")]
    [InlineData("Linear TX")]
    [InlineData("Wideband")]
    public void TheShippedBenches_AreWhatTransferWritesToday(string chain)
    {
        CopyDirectory(Workspace, _ws);
        Assert.NotEmpty(TransferAndCompare(chain));
    }

    /// <summary>And nothing shipped that Transfer no longer writes: every bench folder and display is one of the shipped
    /// chains' benches, the link's included (the README's pictures folder aside).</summary>
    [Fact]
    public void EveryShippedBench_IsOneTransferWrites()
    {
        var written = Chains.SelectMany(c => Benches(Workspace, c).Select(b => b.CellName)).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(written.Order(), Directory.GetDirectories(Workspace).Select(x => Path.GetFileName(x)).Where(x => x != "pictures").Order());
        Assert.Equal(written.Order(), Directory.GetFiles(Workspace, "*.cdd").Select(x => Path.GetFileNameWithoutExtension(x)).Order());
    }

    [Fact]
    [Trait("Category", "Benchmark")]
    public void TheShippedLinkBenches_AreWhatTransferWritesToday()
    {
        CopyDirectory(Workspace, _ws);
        Assert.NotEmpty(TransferAndCompare(LinkChain));
    }

    /// <summary>Transfers a shipped chain in the copy and compares each bench and display with the shipped one; returns
    /// the cells written.</summary>
    private IEnumerable<string> TransferAndCompare(string chain)
    {
        var d = ChainDesignIo.LoadFromFile(Path.Combine(_ws, chain + ".cchain"));
        var w = ChainBenches.Write(d, _ws, chain, replace: true);
        Assert.Empty(w.Refusals);
        ChainBudgetDisplay.WriteAll(_ws, chain, d, ChainBudget.Evaluate(d));
        var cells = Benches(_ws, chain).Select(b => b.CellName).ToList();
        foreach (string cell in cells)
        {
            SameUpToTheLastBit(File.ReadAllText(Csch(cell, Workspace)), File.ReadAllText(Csch(cell, _ws)), cell + ".csch");
            SameUpToTheLastBit(File.ReadAllText(Path.Combine(Workspace, cell + ".cdd")), File.ReadAllText(Path.Combine(_ws, cell + ".cdd")), cell + ".cdd");
        }
        return cells;
    }

    /// <summary>
    /// The same document, but for the last bit of a computed number. Transfer writes the budget's numbers round-trip,
    /// and the budget is computed with the platform's maths library, whose log10 and pow can differ by an ulp between
    /// operating systems: the shipped superhet bench read "1.4972226032470457" where macOS writes "…455". So every
    /// character outside a number must match exactly, and every number to a relative 1e-12 — a thousand ulps, and a
    /// million times finer than any tolerance a bench reads its numbers to. (Rounding the written numbers instead does
    /// not cure it: 10·log10(4) = 6.020599913279625 sits on a rounding edge at 15 digits, and any digit count has such
    /// values.)
    /// </summary>
    private static void SameUpToTheLastBit(string expected, string actual, string what)
    {
        var number = new System.Text.RegularExpressions.Regex(@"-?\d+(?:\.\d+)?(?:[eE][-+]?\d+)?");
        Assert.Equal(number.Replace(expected, "#"), number.Replace(actual, "#"));
        var e = number.Matches(expected);
        var a = number.Matches(actual);
        for (int i = 0; i < e.Count; i++)
        {
            if (e[i].Value == a[i].Value) continue;
            double x = double.Parse(e[i].Value, System.Globalization.CultureInfo.InvariantCulture);
            double y = double.Parse(a[i].Value, System.Globalization.CultureInfo.InvariantCulture);
            Assert.True(Math.Abs(x - y) <= 1e-12 * Math.Max(Math.Abs(x), Math.Abs(y)),
                        $"{what}: {e[i].Value} written as {a[i].Value}");
        }
    }

    [Fact]
    public void TheWorkspaceChecksClean_AndEveryDisplayColumnIsAMeasurementOfItsBench()
    {
        Assert.Equal(0, CliEntry.Run(["check", Workspace]));
        foreach (string chain in Chains)
        {
            var benches = Benches(Workspace, chain);
            // Each bench's MEAS rows, by the result file a display trace names: a display reads its own bench, and the
            // Levels display also the 1-Tone and Blockers benches of its chain, or of its conversion.
            var rowsOf = benches.ToDictionary(b => b.CellName + ".npy", b =>
            {
                var (model, _, _) = SchematicPersistence.LoadFromFile(Csch(b.CellName, Workspace));
                return model.Components.Where(c => c.Symbol == SymbolKind.Meas)
                            .SelectMany(c => c.Parameters.Select(p => p.Name)).ToHashSet(StringComparer.Ordinal);
            });
            foreach (var bench in benches)
            {
                var b = bench.Kind;
                string cell = bench.CellName;
                var cdd = JsonNode.Parse(File.ReadAllText(Path.Combine(Workspace, cell + ".cdd")))!;
                foreach (var trace in cdd["Tabs"]![0]!["Plots"]!.AsArray().SelectMany(p => p!["Traces"]!.AsArray()))
                {
                    string src = (string?)trace!["SourcePath"] ?? "";
                    Assert.True(rowsOf.ContainsKey(src), $"{cell}.cdd reads '{src}', which is not a bench of {chain}");
                    if (b != ChainBench.Levels) Assert.Equal(cell + ".npy", src);
                    else Assert.StartsWith(bench.Chain + " ", src);
                    if ((string?)trace["CubeName"] is { Length: > 0 } cube)
                        Assert.True(rowsOf[src].Contains(cube), $"{cell}.cdd shows '{cube}', which {src}'s MEAS block does not compute");
                }
            }
        }
    }

    // ── 1-Tone: noise and compression, seconds ───────────────────────────────

    [Fact]
    public void Superhet_1Tone_NfAndGainOnTheBudget_WithTheLoRunning()
    {
        var ds = Simulate("Superhet 1-Tone");
        Assert.InRange(M(ds, "G_err"), -5e-4, 5e-4);
        Assert.InRange(M(ds, "NF_err"), -2e-4, 2e-4);
        foreach (string s in new[] { "LNA", "RFBPF", "MIX", "IFBPF", "IFA", "ATT" })
            Assert.InRange(M(ds, $"NF_{s}_sim") - M(ds, $"NF_{s}_calc"), -2e-4, 2e-4);
        Assert.Equal(2.5488, M(ds, "NF_calc"), 4);
        Assert.InRange(M(ds, "IP1dB_err"), 0, 0.1);
    }

    [Fact]
    public void Array_1Tone_OneElementsNf_AndTheCoherentGain()
    {
        var ds = Simulate("Array 1-Tone");
        Assert.InRange(M(ds, "G_err"), -5e-4, 5e-4);
        Assert.InRange(M(ds, "NF_err"), -2e-4, 2e-4);
        foreach (string s in new[] { "LNA", "PS", "CMB", "RFBPF", "MIX", "IFBPF", "IFA", "ATT" })
            Assert.InRange(M(ds, $"NF_{s}_sim") - M(ds, $"NF_{s}_calc"), -2e-4, 2e-4);
        Assert.Equal(26.0206, M(ds, "G_calc"), 4);
        Assert.InRange(M(ds, "IP1dB_err"), 0, 0.1);
    }

    // ── the array's Steering and Combiners benches: an S-parameter run each, seconds ──

    [Fact]
    public void Array_Steering_ThePatternIsTheArrayFactor_AndTheNfStaysPut()
    {
        var ds = Simulate("Array Steering");
        foreach (string a in new[] { "m30", "0", "p30" })
        {
            Assert.InRange(M(ds, $"G_{a}_err"), -1e-9, 1e-9);
            Assert.InRange(M(ds, $"NF_{a}_err"), -1e-9, 1e-9);
            Assert.InRange(M(ds, $"A_{a}_err"), -1e-9, 1e-9);
        }
        Assert.Equal(6.0206, M(ds, "A_calc"), 4);
        var p = ds["measurements.Pattern_sim"].RealValues;
        var e = ds["measurements.Pattern_err"].RealValues;
        double worst = Enumerable.Range(0, p.Length).Where(i => p[i] > -40).Max(i => Math.Abs(e[i]));
        output.WriteLine($"Array Steering: worst |pattern - array factor| above -40 dB = {worst:G3} dB");
        Assert.InRange(worst, 0, 1e-6);
    }

    [Fact]
    public void Array_Combiners_TheFourVariantsEachOnItsBudget()
    {
        var ds = Simulate("Array Combiners");
        foreach (var (_, _, code) in ChainBenches.CombinerVariants)
        {
            Assert.InRange(M(ds, $"G_{code}_err"), -1e-9, 1e-9);
            Assert.InRange(M(ds, $"NF_{code}_err"), -1e-9, 1e-9);
        }
        // The README's table: a tree's loss accumulates per level, an active combiner's amplifiers cascade.
        Assert.Equal(18.5206, M(ds, "G_DP_calc"), 4);
        Assert.Equal(18.0206, M(ds, "G_TP_calc"), 4);
        Assert.Equal(28.5206, M(ds, "G_DA_calc"), 4);
        Assert.Equal(38.0206, M(ds, "G_TA_calc"), 4);
    }

    /// <summary>The signal and its IM3 at every node; every blocker at every node, its own and the others' compression
    /// under it; and the signal's gain the blockers take, the dynamic-range check, on the budget's desensitisation.</summary>
    [Fact]
    public void Superhet_LevelsAndBlockers_EveryNodeOnTheBudget()
    {
        var lev = Simulate("Superhet Levels");
        var blk = Simulate("Superhet Blockers");
        for (int k = 0; k <= 6; k++)
        {
            Assert.InRange(M(lev, $"Psig_{k}_err"), -1e-4, 1e-4);
            if (k > 0) Assert.InRange(M(lev, $"IM3_{k}_err"), -1e-3, 1e-3);
            for (int b = 1; b <= 3; b++) Assert.InRange(M(blk, $"Blk{b}_{k}_err"), -0.01, 1e-4);
            Assert.InRange(M(blk, $"Des_{k}_err"), -1e-4, 1e-4);
        }
        Assert.True(M(blk, "Des_6_calc") > 0.005, "the blockers take a measurable gain from the signal");
    }

    /// <summary>The superhet's SFDR bench: two tones swept to where their IM3 at the output meets the noise floor, the
    /// SFDR there on the budget's within tanh's fifth-order term.</summary>
    [Fact]
    [Trait("Category", "Benchmark")]
    public void Superhet_Sfdr_TheIm3MeetsTheFloorAtTheBudgetsSfdr()
    {
        var ds = Simulate("Superhet SFDR");
        Assert.InRange(M(ds, "SFDR_err"), -0.05, 0.05);
        Assert.Equal(65.0, M(ds, "SFDR_calc"), 2);
    }

    [Fact]
    public void FrontEnd_1Tone_TheCancellersNoiseOnTheBudget()
    {
        var ds = Simulate("Front End 1-Tone");
        Assert.InRange(M(ds, "G_err"), -1e-6, 1e-6);
        Assert.InRange(M(ds, "NF_err"), -1e-6, 1e-6);
        Assert.Equal(2.6685, M(ds, "NF_calc"), 4);
    }

    [Fact]
    [Trait("Category", "Benchmark")]
    public void FrontEnd_Canceller_DepthOnTheClosedForm_AndEvmWithAndWithout()
    {
        var ds = Simulate("Front End Canceller");
        double worst = 0;
        string at = "";
        foreach (string sw in new[] { "P", "A", "D" })
            for (int k = 1; k <= ChainBenches.CancInterference.Length; k++)
            {
                var sim = ds[$"measurements.Depth{sw}_I{k}_sim"].RealValues;
                var calc = ds[$"measurements.Depth{sw}_I{k}_calc"].RealValues;
                for (int i = 0; i < sim.Length; i++)
                    if (Math.Abs(sim[i] - calc[i]) / Math.Max(calc[i], 1) > worst) { worst = Math.Abs(sim[i] - calc[i]) / Math.Max(calc[i], 1); at = $"{sw}{k}[{i}]: {calc[i]:G4} dB deep, simulated {sim[i]:G4} dB"; }
            }
        output.WriteLine($"Front End Canceller: worst |depth - closed form| = {100 * worst:G3} % of the depth ({at}); EVM 64-QAM {M(ds, "EVM_QAM64_off"):G3} -> {M(ds, "EVM_QAM64_on"):G3} %, QPSK {M(ds, "EVM_QPSK_off"):G3} -> {M(ds, "EVM_QPSK_on"):G3} %");
        Assert.InRange(worst, 0, 0.02);
        Assert.True(M(ds, "EVM_QAM64_on") < M(ds, "EVM_QAM64_off") / 3);
    }

    [Fact]
    [Trait("Category", "Benchmark")]
    public void Superhet_Demo_TheEvmIsTheBudgetsWhereNoiseDominates()
    {
        var ds = Simulate("Superhet Demo");
        var err = ds["measurements.SNR_QAM_err"].RealValues;
        var noise = ds["measurements.EVMnoise_QAM"].RealValues;
        var calc = ds["measurements.EVM_QAM_calc"].RealValues;
        output.WriteLine($"Superhet Demo: SNR err {string.Join(", ", err.Select(v => v.ToString("F3")))} dB; " +
                         $"EVM noise {string.Join(", ", noise.Select(v => v.ToString("F3")))} % against {string.Join(", ", calc.Select(v => v.ToString("F3")))} %");
        Assert.InRange(err[0], -0.5, 0.5);
        Assert.InRange(noise[0] / calc[0], 0.9, 1.1);
        // With its LO's own mask the budget has the phase noise: on the SNR up to where compression takes over, and on the
        // EVM's noise part node by node at the middle power, the step at the mixer included.
        for (int i = 1; i <= ChainBenches.DemoMidPower; i++) Assert.InRange(err[i], -0.3, 0.3);
        for (int k = 0; k <= 6; k++)
        {
            output.WriteLine($"node {k}: EVM noise {M(ds, $"EVMn_{k}_sim"):F4} % against {M(ds, $"EVMn_{k}_calc"):F4} %, " +
                             $"EVM {M(ds, $"EVM_{k}_sim"):F4} % against {M(ds, $"EVM_{k}_calc"):F4} %");
            Assert.InRange(M(ds, $"EVMn_{k}_sim") / M(ds, $"EVMn_{k}_calc"), 0.95, 1.05);
        }
    }

    // ── the 26 GHz link ──────────────────────────────────────────────────────

    /// <summary>The README's numbers for the link, worked out by hand there from its coefficients at 26 GHz.</summary>
    [Fact]
    public void Link_TheBudget_IsWhatTheReadmeWorksOut()
    {
        var d = ChainDesignIo.LoadFromFile(Path.Combine(Workspace, "26 GHz Link.cchain"));
        var r = ChainBudget.Evaluate(d);
        Assert.Empty(r.Refusals);
        var ds = ChainBudget.ToDataSet(r);
        Assert.Equal(44.3806, M0(ds, "EIRP_dBm"), 4);
        Assert.Equal(106.7679, M0(ds, "Lfs_dB"), 4);
        Assert.Equal(-59.4053, M0(ds, "Pin_dBm"), 4);
        Assert.Equal(-93.4041, M0(ds, "MDS_dBm"), 4);
        Assert.Equal(34.00, M0(ds, "SNRtot_dB"), 2);
        Assert.Equal(9.0358, M0(ds, "Rate_Gbps"), 4);
        Assert.Equal(8.7523, M0(ds, "Pdctot_W"), 4);
        Assert.Equal(2, ds["Nstg"].RealValues[1]);
        Assert.Contains(r.Notes, x => x.StartsWith("MIX sets the receiver's compression", StringComparison.Ordinal));
        // Its three sweeps, each the whole chain again at every point, none refused.
        var notes = new List<string>();
        ChainBudget.AddSweeps(ds, d, notes);
        Assert.Empty(notes);
        Assert.All(ds["Frequency.Rate_Gbps"].RealValues, v => Assert.True(double.IsFinite(v)));
        Assert.Equal(12, ds["RxElements.SNR_dB"].RealValues.Length);
    }

    [Fact]
    [Trait("Category", "Benchmark")]
    public void Link_1Tone_TheReceiverOnTheBudget_AtThePowerTheLinkDelivers()
    {
        var ds = Simulate("26 GHz Link 1-Tone");
        Assert.InRange(M(ds, "G_err"), -5e-4, 5e-4);
        Assert.InRange(M(ds, "NF_err"), -5e-4, 5e-4);
        foreach (string s in new[] { "RXSW", "LNA", "PS", "CMB", "DRV", "BPF", "MIX" })
            Assert.InRange(M(ds, $"NF_{s}_sim") - M(ds, $"NF_{s}_calc"), -5e-4, 5e-4);
        Assert.Equal(3.5814, M(ds, "NF_calc"), 4);
    }

    [Fact]
    public void Link_SteeringAndCombiners_EightElementsOnTheirBudgets()
    {
        var st = Simulate("26 GHz Link Steering");
        foreach (string a in new[] { "m30", "0", "p30" })
        {
            Assert.InRange(M(st, $"G_{a}_err"), -1e-9, 1e-9);
            Assert.InRange(M(st, $"NF_{a}_err"), -1e-9, 1e-9);
        }
        Assert.Equal(9.0309, M(st, "A_calc"), 4);
        var cmb = Simulate("26 GHz Link Combiners");
        foreach (var (_, _, code) in ChainBenches.CombinerVariants)
        {
            Assert.InRange(M(cmb, $"G_{code}_err"), -1e-9, 1e-9);
            Assert.InRange(M(cmb, $"NF_{code}_err"), -1e-9, 1e-9);
        }
    }

    [Fact]
    [Trait("Category", "Benchmark")]
    public void Link_TX_WhatTheBackoffBuys()
    {
        var ds = Simulate("26 GHz Link TX");
        output.WriteLine($"26 GHz Link TX: at 8 dB, Pout {M(ds, "Pant_at_err"):0.###} dB from the budget, EVM {M(ds, "EVM_at"):0.##} %, " +
                         $"ACPR {M(ds, "ACPR_L_at"):0.#} / {M(ds, "ACPR_U_at"):0.#} dBc");
        Assert.InRange(M(ds, "Pant_at_err"), -1.0, -0.8);
        Assert.InRange(M(ds, "EVM_at"), 5.5, 7);
        Assert.InRange(M(ds, "Psat_err"), -1, 0);
    }

    private static double M0(DataSet ds, string name) => ds[name].RealValues[0];

    // ── the linear transmitter ───────────────────────────────────────────────

    /// <summary>The transmitter's readouts are an independent calculation's (testdata/chain/tx_reference.py, every number
    /// from its textbook form, each law's 1 dB point and saturation by root-finding its own AM-AM), to 10⁻⁶ dB.</summary>
    [Fact]
    public void LinearTx_TheBudget_IsTheIndependentReference()
    {
        var refs = JsonNode.Parse(File.ReadAllText(Path.Combine(PalaceBackendTests.RepoRoot(), "testdata", "chain", "tx_reference.json")))!;
        double R(string key) => (double)refs[key]!;
        var d = ChainDesignIo.LoadFromFile(Path.Combine(Workspace, "Linear TX.cchain"));
        var r = ChainBudget.Evaluate(d);
        var x = ChainTransmit.Read(r)!;
        var iq = IqModSpec.Read(d.Stages[0]);
        (double Budget, string Key)[] pairs =
        [
            (r.Totals!.GainDb, "gain_dB"), (r.Totals.NfDb, "nf_dB"), (x.PoutDbm, "pout_dBm"), (x.PoutCwDbm, "pout_cw_dBm"),
            (x.Op1dbDbm, "op1db_dBm"), (x.BackoffP1dbDb, "backoff_op1db_dB"), (x.PsatDbm, "psat_dBm"), (x.BackoffPsatDb, "backoff_psat_dB"),
            (x.Oip3Dbm, "oip3_dBm"), (x.AclrDbc, "aclr_dBc"), (x.NoisePsdDbmHz, "noise_psd_dBmHz"),
            (x.RxNoisePsdDbmHz, "rx_noise_psd_dBmHz"), (x.RxNoiseDbm, "rx_noise_dBm"), (x.ImageDbc, "image_dBc"), (x.LoLeakDbc, "lo_dBc"),
            (iq.ImageRejectionDb, "image_rejection_dB"), (iq.LoLeakDbm - (d.InputPowerDbm + r.Stages[0].Stage.GainDb), "lo_at_modulator_dBc"),
            (CircuitRF.Core.Devices.AmplifierLaw.Rapp(37, 30, 2).Ip1dbDbm, "rapp_ip1db_root_dBm"),
            (CircuitRF.Core.Devices.AmplifierLaw.Rapp(37, 30, 2).PsatDbm(30), "rapp_psat_at_80dBm_in_dBm"),
            (CircuitRF.Core.Devices.AmplifierLaw.Saleh(37, 30, 10, 20).Ip1dbDbm, "saleh_ip1db_root_dBm"),
            (CircuitRF.Core.Devices.AmplifierLaw.Saleh(37, 30, 10, 20).PsatDbm(30), "saleh_psat_peak_dBm"),
            (CircuitRF.Core.Devices.AmplifierLaw.Saleh(37, 30, 10, 20).Iip3Dbm, "saleh_iip3_closed_dBm"),
        ];
        foreach (var (budget, key) in pairs)
        {
            output.WriteLine($"{key}: budget {budget:F9}, reference {R(key):F9}");
            Assert.InRange(budget - R(key), -1e-6, 1e-6);
        }
    }

    /// <summary>The PA bench: Rapp's AM-AM over the drive to round-off, its 1 dB point on the closed form to the
    /// three-point interpolation's 0.01 dB, its efficiency the power it adds over its 10 W; and the NR carrier's ACLR
    /// through the PA alone 0.3 to 1.5 dB worse than the relation's estimate for it: the estimate is optimistic at a Rapp
    /// PA, whose law clips harder than the cubic past the 1 dB point the carrier's peaks reach.</summary>
    [Fact]
    [Trait("Category", "Benchmark")]
    public void LinearTx_PA_CompressesOnTheLaw()
    {
        var ds = Simulate("Linear TX PA");
        Assert.InRange(ds["measurements.AMAM_err"].RealValues.Max(Math.Abs), 0, 1e-6);
        Assert.InRange(ds["measurements.AMPM_err"].RealValues.Max(Math.Abs), 0, 1e-6);
        Assert.InRange(M(ds, "IP1dB_err"), -0.02, 0.02);
        Assert.InRange(M(ds, "Pout_op_err"), -1e-6, 1e-6);
        output.WriteLine($"Linear TX PA: NR carrier {M(ds, "Penv_sim"):0.##} dBm out, ACLR {M(ds, "ACLR_L_sim"):0.##} / " +
                         $"{M(ds, "ACLR_U_sim"):0.##} dBc, EVM {M(ds, "EVM_sim"):0.##} %, PAE {M(ds, "PAE_env_sim"):0.##} %");
        // The relation is optimistic at the PA: the NR carrier's peaks reach its 1 dB point, past the cubic's reach.
        output.WriteLine($"Linear TX PA: ACLR estimate {M(ds, "ACLR_calc"):0.##} dBc");
        Assert.InRange(M(ds, "ACLR_L_sim") - M(ds, "ACLR_calc"), 0.3, 1.5);
        Assert.InRange(M(ds, "ACLR_U_sim") - M(ds, "ACLR_calc"), 0.3, 1.5);
        Assert.InRange(M(ds, "PAE_env_sim"), 6, 8);
    }

    /// <summary>The 1-Tone bench: the output over the drive on the budget's CW output through each stage's compression,
    /// from 20 dB back to 18 dB over the operating point; the 1 dB point the cascade formula's; the IQ modulator's image
    /// and LO at its own output on their closed forms; and the noise in band and in the receive band 0.13 to 0.15 dB
    /// under the budget, half of it amplitude noise, which the PA passes 0.2245 dB under its gain at its operating point
    /// (0.134 dB expected).</summary>
    [Fact]
    [Trait("Category", "Benchmark")]
    public void LinearTx_1Tone_TheOutputAndItsSpurs_OnTheBudget()
    {
        var ds = Simulate("Linear TX 1-Tone");
        Assert.InRange(ds["measurements.Pout_err"].RealValues.Max(Math.Abs), 0, 1e-3);
        Assert.InRange(M(ds, "Ptop_err"), -1e-3, 1e-3);
        Assert.InRange(M(ds, "IP1dB_err"), 0, 0.15);
        Assert.InRange(M(ds, "ImageIq_sim") - M(ds, "ImageIq_calc"), -1e-3, 1e-3);
        Assert.InRange(M(ds, "LoIq_sim") - M(ds, "LoIq_calc"), -0.01, 0.01);
        Assert.InRange(M(ds, "LoLeak_sim") - M(ds, "LoLeak_calc"), -0.2, 0.2);
        Assert.InRange(M(ds, "Noise_err"), -0.25, 0);
        Assert.InRange(M(ds, "RxNoise_err"), -0.25, 0);
    }

    /// <summary>The 2-Tone bench: at the operating point the IM3 at or under the budget's, the cubic with the PA's 1 dB
    /// point being the pessimistic one for two tones and a Rapp law of p = 2; 20 dB down the PA's IM3 falls at five times
    /// the drive, and the intercept read there is the other stages' third order with the PA's residual fifth in phase.</summary>
    [Fact]
    [Trait("Category", "Benchmark")]
    public void LinearTx_2Tone_TheIm3AtOrUnderTheEstimate()
    {
        var ds = Simulate("Linear TX 2-Tone");
        output.WriteLine($"Linear TX 2-Tone: IM3 at the operating point {M(ds, "IM3_op_sim"):0.##} dBc against {M(ds, "IM3_op_calc"):0.##}; " +
                         $"OIP3 20 dB down {M(ds, "OIP3_lo_sim"):0.##} dBm against {M(ds, "OIP3_calc"):0.##}");
        Assert.InRange(M(ds, "IM3_op_err"), -6, 0);
        Assert.True(M(ds, "OIP3_lo_sim") > M(ds, "OIP3_calc"));
    }

    /// <summary>The Envelope bench: the NR carrier's ACLR at the operating point beside the budget's estimate from the
    /// intercept, 1 dB under it because the band filter after the PA removes 1.43 dB of the regrowth; its output under the
    /// linear budget by the compression its peaks see.</summary>
    [Fact]
    [Trait("Category", "Benchmark")]
    public void LinearTx_Envelope_TheAclrBesideTheEstimate()
    {
        var ds = Simulate("Linear TX Envelope");
        output.WriteLine($"Linear TX Envelope: ACLR {M(ds, "ACLR_op_sim"):0.##} dBc against {M(ds, "ACLR_op_calc"):0.##}, " +
                         $"Pout {M(ds, "Pout_op_err"):0.###} dB from the budget, EVM {M(ds, "EVM_op_sim"):0.##} %");
        Assert.InRange(M(ds, "ACLR_op_err"), -2, 2);
        Assert.InRange(M(ds, "Pout_op_err"), -0.5, 0);
    }

    /// <summary>The verb's report heads the cascade with the input the budget used: with a link, the power it delivers,
    /// the same number the link line ends with, not the stated input.</summary>
    [Fact]
    public void TheVerbsReport_HeadsTheCascadeWithTheLinksInput()
    {
        var (code, stdout, stderr) = CircuitRF.Ui.Tests.Em3d.CliProcess.Run(PalaceBackendTests.RepoRoot(), [], "chain",
            Path.Combine(Workspace, "26 GHz Link.cchain"));
        Assert.True(code == 0, stderr);
        string head = stdout.Split('\n').First(l => l.StartsWith("Chain budget:", StringComparison.Ordinal));
        string link = stdout.Split('\n').First(l => l.TrimStart().StartsWith("link:", StringComparison.Ordinal));
        Assert.Contains("-59.41 dBm in", head, StringComparison.Ordinal);
        Assert.EndsWith("-59.41 dBm in", link.TrimEnd(), StringComparison.Ordinal);
        Assert.DoesNotContain("-70.00 dBm in", head, StringComparison.Ordinal);
    }

    /// <summary>Every shipped chain, opened and saved again, is the file as shipped, to the byte: nothing the reader
    /// keeps is lost, and nothing it derives (the link's element count) is written as a key of its own.</summary>
    [Fact]
    public void EveryShippedChain_SavesBackToItsOwnBytes()
    {
        foreach (string chain in Chains)
        {
            string path = Path.Combine(Workspace, chain + ChainDesignIo.Extension);
            Assert.Equal(File.ReadAllText(path), ChainDesignIo.Serialize(ChainDesignIo.LoadFromFile(path)));
        }
    }

    // ── 2-Tone and Envelope ──────────────────────────────────────────────────

    [Theory]
    [Trait("Category", "Benchmark")]
    [InlineData("Superhet")]
    [InlineData("Array")]
    public void TwoTone_TheInterceptsOnTheBudget_StageByStage(string chain)
    {
        var d = ChainDesignIo.LoadFromFile(Path.Combine(Workspace, chain + ".cchain"));
        var ds = Simulate(chain + " 2-Tone");
        Assert.InRange(M(ds, "IIP3_err"), -5e-4, 5e-4);
        Assert.InRange(M(ds, "OIP3_err"), -5e-4, 5e-4);
        foreach (var s in d.Stages)
        {
            Assert.InRange(M(ds, $"G_{s.Name}_sim") - M(ds, $"G_{s.Name}_calc"), -5e-4, 5e-4);
            if (ds.CubesIn("measurements").ContainsKey($"OIP3_{s.Name}_calc"))
                Assert.InRange(M(ds, $"OIP3_{s.Name}_sim") - M(ds, $"OIP3_{s.Name}_calc"), -5e-4, 5e-4);
        }
    }

    [Theory]
    [Trait("Category", "Benchmark")]
    [InlineData("Superhet")]
    [InlineData("Array")]
    public void Envelope_TheSnrOnTheBudget_WithinOneRecordsStatistics(string chain)
    {
        var ds = Simulate(chain + " Envelope");
        Assert.InRange(M(ds, "SNR_err"), -0.5, 0.5);
        Assert.InRange(M(ds, "Pout_err"), -0.1, 0.1);
        Assert.InRange(M(ds, "EVM_sim") / M(ds, "EVM_calc"), 0.9, 1.1);
    }

    /// <summary>
    /// The superhet's Envelope bench, its LO's mask on and its third blocker 12 MHz below the carrier beside it, four
    /// records averaged: the SNR on the budget's with the LO's phase noise and that blocker's reciprocal mixing in it,
    /// 2.36 dB under the thermal SNR, within three of the records' own spread of their mean.
    /// </summary>
    [Fact]
    [Trait("Category", "Benchmark")]
    public void Superhet_Envelope_ThePhaseNoiseAndTheReciprocalMixing_OnTheBudget()
    {
        var ds = Simulate("Superhet Envelope");
        double spread = M(ds, "SNR_spread");
        output.WriteLine($"Superhet Envelope: SNR {M(ds, "SNR_sim"):F3} dB simulated, {M(ds, "SNR_calc"):F3} dB budget " +
                         $"({M(ds, "SNRth_calc"):F3} thermal, PN {M(ds, "PN_calc"):F3} dBc, RM {M(ds, "RM_calc"):F3} dBc), " +
                         $"spread of the mean {spread:F3} dB; Pout {M(ds, "Pout_err"):F4} dB off");
        Assert.InRange(M(ds, "SNR_err"), -Math.Max(3 * spread, 0.1), Math.Max(3 * spread, 0.1));
        Assert.True(M(ds, "SNRth_calc") - M(ds, "SNR_calc") > 2, "the mask and the blocker cost the SNR");
        Assert.InRange(M(ds, "Pout_err"), -0.1, 0.1);
    }

    // ── the dual-conversion receiver: per conversion, and the conversions cascaded ──

    /// <summary>Each conversion's 1-Tone bench on its own budget, stage by stage; and the two together, their simulated
    /// gains added and their simulated noise factors cascaded by Friis, on the whole chain's budget.</summary>
    [Fact]
    public void DualConversion_1Tone_EachConversionOnItsBudget_AndTogetherTheWholeChains()
    {
        var d = ChainDesignIo.LoadFromFile(Path.Combine(Workspace, "Dual Conversion.cchain"));
        var t = ChainBudget.Evaluate(d).Totals!;
        var one = Simulate("Dual Conversion IF1 1-Tone");
        var two = Simulate("Dual Conversion IF2 1-Tone");
        foreach (var (ds, stages) in new[] { (one, new[] { "LNA", "RFBPF", "MIX1" }), (two, new[] { "IF1BPF", "IF1A", "IMGBPF", "MIX2", "IF2BPF", "IF2A" }) })
        {
            Assert.InRange(M(ds, "G_err"), -5e-4, 5e-4);
            Assert.InRange(M(ds, "NF_err"), -2e-4, 2e-4);
            foreach (string s in stages) Assert.InRange(M(ds, $"NF_{s}_sim") - M(ds, $"NF_{s}_calc"), -2e-4, 2e-4);
            Assert.InRange(M(ds, "IP1dB_err"), 0, 0.1);
        }
        static double Lin(double db) => Math.Pow(10, db / 10);
        double g = M(one, "G_sim") + M(two, "G_sim");
        double nf = 10 * Math.Log10(Lin(M(one, "NF_sim")) + (Lin(M(two, "NF_sim")) - 1) / Lin(M(one, "G_sim")));
        output.WriteLine($"Dual Conversion: the conversions cascaded, gain {g:F5} dB against {t.GainDb:F5}, NF {nf:F5} dB against {t.NfDb:F5}");
        Assert.InRange(g - t.GainDb, -1e-3, 1e-3);
        Assert.InRange(nf - t.NfDb, -2e-4, 2e-4);
    }

    /// <summary>Each conversion's levels and blockers on the budget node by node, the two image blockers among them. The
    /// second conversion's input reads the second blocker 0.002 dB high: the roofing filter there reflects a little at
    /// 308 MHz, and the input node is the one with no isolator in front of it.</summary>
    [Fact]
    [Trait("Category", "Benchmark")]
    public void DualConversion_LevelsAndBlockers_EveryNodeOnTheBudget()
    {
        foreach (var (conv, nodes) in new[] { ("IF1", 3), ("IF2", 6) })
        {
            var lev = Simulate($"Dual Conversion {conv} Levels");
            var blk = Simulate($"Dual Conversion {conv} Blockers");
            for (int k = 0; k <= nodes; k++)
            {
                Assert.InRange(M(lev, $"Psig_{k}_err"), -1e-4, 1e-4);
                // A node before anything nonlinear has no IM3 to read.
                if (lev.CubesIn("measurements").ContainsKey($"IM3_{k}_err")) Assert.InRange(M(lev, $"IM3_{k}_err"), -1e-3, 1e-3);
                Assert.InRange(M(blk, $"Blk1_{k}_err"), -0.01, 5e-3);
                Assert.InRange(M(blk, $"Blk2_{k}_err"), -0.01, 5e-3);
            }
        }
    }

    /// <summary>The whole chain's two tones, on four tones (the two, and both LOs): the 600-product cap allows MaxMixOrder 5,
    /// the least that carries the output's IM3, and there the intercepts read 3×10⁻³ dB high, against 2×10⁻⁴ dB at the
    /// order a single mixer's converge at.</summary>
    [Fact]
    [Trait("Category", "Benchmark")]
    public void DualConversion_2Tone_TheWholeChainsIntercept_AtTheOrderTheCapAllows()
    {
        var d = ChainDesignIo.LoadFromFile(Path.Combine(Workspace, "Dual Conversion.cchain"));
        var ds = Simulate("Dual Conversion 2-Tone");
        Assert.InRange(M(ds, "G_err"), -1e-3, 1e-3);
        Assert.InRange(M(ds, "IIP3_err"), -5e-3, 5e-3);
        Assert.InRange(M(ds, "OIP3_err"), -5e-3, 5e-3);
        foreach (var s in d.Stages)
        {
            Assert.InRange(M(ds, $"G_{s.Name}_sim") - M(ds, $"G_{s.Name}_calc"), -1e-3, 1e-3);
            if (ds.CubesIn("measurements").ContainsKey($"OIP3_{s.Name}_calc"))
                Assert.InRange(M(ds, $"OIP3_{s.Name}_sim") - M(ds, $"OIP3_{s.Name}_calc"), -5e-3, 5e-3);
        }
    }

    /// <summary>
    /// Each conversion's envelope SNR on the budget; the second's is the whole chain's, its source carrying the noise the
    /// first delivers. The load's own noise is in the simulated SNR and not in the budget's: kT0 against the (Ts + Te)·G
    /// a conversion delivers into it, 0.52 dB at the first conversion's output (MIX1's, 7.5 dB of gain) and nothing to
    /// speak of after 32.5 dB.
    /// </summary>
    [Theory]
    [Trait("Category", "Benchmark")]
    [InlineData("IF1")]
    [InlineData("IF2")]
    public void DualConversion_Envelope_EachConversionsSnrOnTheBudget(string conv)
    {
        double load = LoadNoiseDb(conv);
        var ds = Simulate($"Dual Conversion {conv} Envelope");
        output.WriteLine($"Dual Conversion {conv} Envelope: the load's own noise {load:G3} dB of SNR");
        Assert.InRange(M(ds, "SNR_err") + load, -0.35, 0.35);
        Assert.InRange(M(ds, "Pout_err"), -0.1, 0.1);
    }

    [Theory]
    [Trait("Category", "Benchmark")]
    [InlineData("IF1")]
    [InlineData("IF2")]
    public void DualConversion_Demo_EachConversionsEvmIsItsBudgetsWhereNoiseDominates(string conv)
    {
        double load = LoadNoiseDb(conv);
        var ds = Simulate($"Dual Conversion {conv} Demo");
        var err = ds["measurements.SNR_QAM_err"].RealValues;
        output.WriteLine($"Dual Conversion {conv} Demo: SNR err at the lowest power {err[0]:G3} dB, the load's own noise {load:G3} dB");
        Assert.InRange(err[0] + load, -0.35, 0.35);
    }

    /// <summary>The SNR a conversion's load costs at its output, dB: 10·log10(1 + T0 / ((Ts + Te)·G)).</summary>
    private static double LoadNoiseDb(string conv)
    {
        var d = ChainDesignIo.LoadFromFile(Path.Combine(Workspace, "Dual Conversion.cchain"));
        var c = ChainBenches.Conversions(d, ChainBudget.Evaluate(d), "Dual Conversion")!.Single(x => x.Chain == $"Dual Conversion {conv}");
        var t = c.Budget.Totals!;
        return 10 * Math.Log10(1 + ChainBudget.T0 / ((c.Design.TemperatureK + t.TeK) * Math.Pow(10, t.GainDb / 10)));
    }

    // ── the wideband receiver: an image passed, two modules' mismatch, a response ──

    /// <summary>
    /// The wideband receiver's 1-Tone bench, harmonic-balance noise with its LO running: the preselector passes the mixer's
    /// image, the preselector's and the LNA's files reflect into each other, and the budget, with both counted, is the run's
    /// NF and gain, stage by stage.
    /// </summary>
    [Fact]
    public void Wideband_1Tone_TheImageAndTheMismatchOnTheBudget()
    {
        var r = ChainBudget.Evaluate(ChainDesignIo.LoadFromFile(Path.Combine(Workspace, "Wideband.cchain")));
        Assert.True(Assert.Single(r.Images).Counted);
        var ds = Simulate("Wideband 1-Tone");
        Assert.InRange(M(ds, "G_err"), -5e-4, 5e-4);
        Assert.InRange(M(ds, "NF_err"), -2e-4, 2e-4);
        foreach (string s in new[] { "PRE", "LNA", "MIX", "IFBPF", "IFA" })
        {
            output.WriteLine($"NF at {s}: {M(ds, $"NF_{s}_sim"):F6} simulated, {M(ds, $"NF_{s}_calc"):F6} budget");
            Assert.InRange(M(ds, $"NF_{s}_sim") - M(ds, $"NF_{s}_calc"), -2e-4, 2e-4);
        }
        output.WriteLine($"Wideband: G {M(ds, "G_calc"):F4} dB, NF {M(ds, "NF_calc"):F4} dB, the image {r.ImageNfDb:F4} dB; IP1dB_err {M(ds, "IP1dB_err"):G4}");
    }

    /// <summary>
    /// The wideband receiver's levels and intercepts. From the LNA's output on, every node's signal and IM3 sit on the
    /// budget; the input and the node between the preselector and the LNA read their voltage, which holds the wave the
    /// next file reflects as well as the one going forward, so they read off the budget's forward level by that
    /// reflection (−0.29 and +2.44 dB), which the README says.
    /// </summary>
    [Fact]
    [Trait("Category", "Benchmark")]
    public void Wideband_LevelsAnd2Tone_OnTheBudget()
    {
        var lev = Simulate("Wideband Levels");
        for (int k = 2; k <= 5; k++)
        {
            Assert.InRange(M(lev, $"Psig_{k}_err"), -2e-3, 2e-3);
            Assert.InRange(M(lev, $"IM3_{k}_err"), -3e-3, 3e-3);
        }
        Assert.InRange(M(lev, "Psig_1_err"), 2, 3);
        var two = Simulate("Wideband 2-Tone");
        Assert.InRange(M(two, "IIP3_err"), -1e-3, 1e-3);
        Assert.InRange(M(two, "OIP3_err"), -1e-3, 1e-3);
    }

    [Fact]
    [Trait("Category", "Benchmark")]
    public void Wideband_Envelope_TheSnrOnTheBudget()
    {
        var ds = Simulate("Wideband Envelope");
        Assert.InRange(M(ds, "SNR_err"), -0.35, 0.35);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static string Csch(string cell, string root) => Path.Combine(root, cell, "schematic", cell + ".csch");

    /// <summary>Simulate: the bench's netlist at the workspace root, run, every measurement row evaluated.</summary>
    private DataSet Simulate(string cell)
    {
        if (!Directory.Exists(_ws)) CopyDirectory(Workspace, _ws);
        var (model, _, _) = SchematicPersistence.LoadFromFile(Csch(cell, _ws));
        string netlist = Path.Combine(_ws, "netlist.cnl");
        File.WriteAllText(netlist, SchematicCircuit.CnlTextOf(model, cell));
        var run = SchematicRunService.RunNetlist(netlist, _ws);
        Assert.True(run.Status == RunStatus.Success, $"{cell}: {run.StatusMessage}");
        Assert.Empty(run.Errors);
        Assert.DoesNotContain(run.Warnings, w => w.Contains("failed to evaluate", StringComparison.Ordinal));
        var ds = run.GroupedResults!;
        foreach (var (name, cube) in ds.CubesIn("measurements").Where(kv => kv.Key.EndsWith("_err", StringComparison.Ordinal)))
            output.WriteLine($"{cell}: {name} = {cube.RealValues[0]:G6}");
        return ds;
    }

    private static double M(DataSet ds, string name) => ds["measurements." + name].RealValues[0];

    private static void CopyDirectory(string from, string to)
    {
        foreach (var dir in Directory.GetDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(dir.Replace(from, to, StringComparison.Ordinal));
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
            File.Copy(file, file.Replace(from, to, StringComparison.Ordinal));
    }
}
