// Tune canceller: the canceller's amplitude, phase and delay found by circuitRF's own optimizer, on the Canceller bench's
// harmonic-balance part. The bench is the one Transfer writes (ChainBenches), built here in memory, and it carries its
// own tune, goal and optimize lines, so the same run is `circuitrf opt` on the transferred bench or the Optimizer window
// on it. Nothing here searches: OptimizationRun does, as it does for any schematic.
//
//   tune      amp_hb, phase_hb, delay_hb (the part's own settings), each over a range about 0 holding the stage's value
//   goal      the residual at the canceller's output, the mean of its two tones' powers, at most the target
//   target    20 dB under the floor the settings cannot tune under: the canceller's own IM3, or the noise in the
//             interference's bandwidth there, whichever is higher. 20 dB under it adds 0.04 dB to it.
//
// The closed-form optimum is the null: no phase error, no delay mismatch, and the amplitude error that takes the
// reference path's compression back (CancellerSpec.AmpErrForNullDb).

using System.Globalization;
using CircuitRF.Design.Circuit;
using CircuitRF.Design.Optimization;

namespace CircuitRF.Design.Chain;

/// <summary>What a tune found: the canceller's settings before and after, the residual (the goal's value) at each, the
/// target, and the closed form's optimum beside them.</summary>
public sealed record ChainCancellerTuning(
    string Stage, double StartAmpErrDb, double StartPhaseErrDeg, double StartDelayErrS,
    double AmpErrDb, double PhaseErrDeg, double DelayErrS, double ResidualBeforeDbm, double ResidualAfterDbm,
    double TargetDbm, bool Met, double OptimumAmpErrDb, string Algorithm, string FinishReason, long Evaluations);

public static class ChainCancellerTune
{
    /// <summary>The Canceller bench's harmonic-balance part's settings, the variables the tune moves: its own, since the
    /// envelope runs sweep amp_err, phase_err and delay_err, and the optimizer moves no value a sweep moves.</summary>
    public const string AmpVar = "amp_hb", PhaseVar = "phase_hb", DelayVar = "delay_hb";

    /// <summary>The goal's name on the bench.</summary>
    public const string Goal = "Residual";

    /// <summary>How far under the floor the goal asks the residual to go, dB.</summary>
    public const double BelowFloorDb = 20;

    /// <summary>
    /// The level the canceller's settings cannot usefully tune the residual under, dBm at its output: its own IM3 at the
    /// null's amplitude, or the noise there in the interference's bandwidth, whichever is higher. Neither depends on where
    /// the settings start.
    /// </summary>
    public static double FloorDbm(ChainBudgetResult r)
    {
        var d = r.Design!;
        int ci = ChainCanceller.Index(d);
        var c = CancellerSpec.Read(d.Stages[ci]);
        double noise = r.Stages[ci].NoiseOutDbm + 10 * Math.Log10(c.BintHz / d.BandwidthHz);
        var at = double.IsFinite(c.AmpErrForNullDb) ? c with { AmpErrDb = c.AmpErrForNullDb } : c;
        return Math.Max(noise, at.Im3Dbm);
    }

    /// <summary>The goal's limit: <see cref="BelowFloorDb"/> under <see cref="FloorDbm"/>.</summary>
    public static double TargetDbm(ChainBudgetResult r) => FloorDbm(r) - BelowFloorDb;

    /// <summary>The goal's expression: the mean of the residual's two tones' powers at <paramref name="node"/>, dBm, on
    /// <paramref name="analysis"/>.</summary>
    public static string GoalExpression(string analysis, string node) =>
        $"10*log10((10^((dB({analysis}.V(\"{node}\",\"(0,1,0)\"))+10)/10)+10^((dB({analysis}.V(\"{node}\",\"(0,0,1)\"))+10)/10))/2)";

    /// <summary>
    /// The bench's tuning lines: each setting over a range about 0 that holds the stage's own value (3 dB, 20° and 1 ns, or
    /// twice the value), the goal on the residual at <paramref name="node"/>, and the trust-region optimizer, which needs
    /// no derivatives and takes the delay's weak pull as it takes the others'.
    /// </summary>
    internal static IEnumerable<string> Lines(CancellerSpec c, double targetDbm, string analysis, string node)
    {
        static string N(double v) => v.ToString("G6", CultureInfo.InvariantCulture);
        double amp = Math.Max(3, 2 * Math.Abs(c.AmpErrDb)), ph = Math.Max(20, 2 * Math.Abs(c.PhaseErrDeg));
        double dl = Math.Max(1e-9, 2 * Math.Abs(c.DelayErrS));
        yield return $"tune {AmpVar} min={N(-amp)} max={N(amp)} opt=1";
        yield return $"tune {PhaseVar} min={N(-ph)} max={N(ph)} opt=1";
        yield return $"tune {DelayVar} min={N(-dl)} max={N(dl)} opt=1";
        yield return $"goal {Goal} = {GoalExpression(analysis, node)} analysis={analysis} le {ChainValue.Format(targetDbm)}";
        yield return "optimize algorithm=trust_region maxiter=300 seed=1";
    }

    /// <summary>
    /// Runs the optimizer on the chain's Canceller bench, built as Transfer builds it, from the stage's own settings.
    /// Null with <paramref name="refusal"/> when the chain has no canceller, its budget is refused or the run cannot
    /// start. <paramref name="parallel"/> overrides the bench's evaluations at once (null: the optimizer's own choice).
    /// </summary>
    public static ChainCancellerTuning? Run(ChainDesign d, out string? refusal, int? parallel = null,
                                            Action<OptimizationProgress>? progress = null, CancellationToken ct = default)
    {
        refusal = null;
        var r = ChainBudget.Evaluate(d);
        if (r.Totals is null) { refusal = string.Join(" ", r.Refusals); return null; }
        var rd = r.Design!;
        int ci = ChainCanceller.Index(rd);
        if (ci < 0) { refusal = "The chain has no canceller to tune."; return null; }
        var bench = ChainBenches.Build(rd, r, "chain").FirstOrDefault(b => b.Kind == ChainBench.Canceller);
        if (bench is null) { refusal = "The chain's Canceller bench cannot be written, so there is nothing to tune on."; return null; }
        var circuit = PreparedCircuit.FromText(bench.Netlist, rd.BaseDirectory, rd.BaseDirectory);
        if (circuit.ReadError is { } read || circuit.Tb?.Tuning is not { } tuning) { refusal = circuit.ReadError ?? "The bench has no tuning setup."; return null; }

        var setup = tuning.Clone();
        if (parallel is { } p && setup.Optimizer is { } o) o.Parallelism = p;
        var run = OptimizationRun.Create(circuit, new OptimizationOptions { Setup = setup, Progress = progress, Cancellation = ct });
        if (run.Refusal is { } why) { refusal = why.Render(); return null; }

        var c = CancellerSpec.Read(rd.Stages[ci]);
        static string T(double v) => v.ToString("R", CultureInfo.InvariantCulture);
        var start = new Dictionary<string, string>
        {
            [AmpVar] = T(c.AmpErrDb), [PhaseVar] = T(c.PhaseErrDeg), [DelayVar] = T(c.DelayErrS),
        };
        double before = run.EvaluateValues([start], keepData: false, ct)[0] is { Status: PointStatus.Evaluated } e
            ? e.Goals[0].WorstValue : double.NaN;
        var res = run.Run();
        if (res.Outcome is OptimizationOutcome.Refused or OptimizationOutcome.Cancelled || res.BestValues.Count == 0)
        {
            refusal = res.Refusal?.Render() ?? res.FinishReason;
            return null;
        }
        double Best(string key) => double.Parse(res.BestValues[key], NumberStyles.Float, CultureInfo.InvariantCulture);
        return new ChainCancellerTuning(rd.Stages[ci].Name, c.AmpErrDb, c.PhaseErrDeg, c.DelayErrS,
            Best(AmpVar), Best(PhaseVar), Best(DelayVar), before, res.Goals[0].WorstValue, TargetDbm(r), res.Goals[0].Met,
            c.AmpErrForNullDb, res.Algorithm, res.FinishReason, res.Evaluations);
    }

    /// <summary>The tuned settings as a stage writes them: AmpErr in dB, PhaseErr in degrees, DelayErr in ns.</summary>
    public static IReadOnlyList<(string Name, string Value)> Values(ChainCancellerTuning t) =>
    [
        ("AmpErr", ChainValue.Format(t.AmpErrDb)), ("PhaseErr", ChainValue.Format(t.PhaseErrDeg)),
        ("DelayErr", ChainValue.Format(t.DelayErrS * 1e9) + " ns"),
    ];

    /// <summary>A copy of <paramref name="d"/> with the tuned settings written into its canceller stage.</summary>
    public static ChainDesign Apply(ChainDesign d, ChainCancellerTuning t)
    {
        var c = d.Clone();
        var s = c.Stages.First(x => x.Part == ChainParts.Canceller && x.Name == t.Stage);
        foreach (var (name, value) in Values(t)) s.Parameters[name] = value;
        return c;
    }
}
