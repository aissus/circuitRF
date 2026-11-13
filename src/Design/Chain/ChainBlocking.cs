// What the LO's phase noise and the blockers do to the wanted signal, node by node: the phase noise integrated over the
// channel, each blocker's reciprocal mixing and its desensitisation by compression, and the EVM they and the node's SNR
// and distortion leave. All from the budget's own levels (ChainLevels), so a node's numbers are the cascade's there.
//
//   phase noise   σ² = 2·∫ L(f)·(1 − f/B) df  from the mask's first offset to B     (each mixer's LO; the LOs add)
//                 the share of e^(jφ)·x that falls in the channel: x's flat spectrum of width B convolved with L
//   reciprocal    N_rm = P_b · ∫ L(|u|) du  over the channel, u from Δf − B/2 to Δf + B/2, at the mixer's input
//                 = P_b + L(Δf) + 10·log10(B) where L is flat across the channel; then through the stages after it
//   desense, RM   10·log10(1 + N_rm/N), N the node's noise floor in B
//   desense, comp −10·log10 Π |g_i|², g_i the stage's small-signal gain with the blocker at its input, over its own:
//                   tanh       (1/2π)·∫ sech²(u·cos θ) dθ,   u = 2·√(P_b/IIP3)    the limiter's slope over the cycle
//                   a law      F(s) + s·F′(s),  s = P_b                             Rapp's and Saleh's on the envelope
//   EVM           √(N/S + σ² + Σ N_rm/S + (4/3)·(P/IIP3)²)                         the last the cubic's in-channel part
//
// The tanh average is the trapezoid rule over a whole period, geometric in its convergence, as ChainTransmit's describing
// function is. The rest is closed form: the mask is a straight line in dB against log f between its points, so
// 10^(L/10) is a power of f on each segment and its integral, weighted or not, is one. The sources are in
// docs/design/chain-budget.md.

using System.Numerics;
using RfCore.Data;
using RfCore.Signals;

namespace CircuitRF.Design.Chain;

/// <summary>What the budget reads at the output under the LO's phase noise and the blockers.</summary>
/// <param name="PhaseNoiseDbc">The LO phase noise in the channel, dBc; −∞ when no mixer states a mask.</param>
/// <param name="SnrDb">The SNR with it and every blocker's reciprocal mixing, dB.</param>
/// <param name="DesenseRmDb">Each blocker's desensitisation by reciprocal mixing, dB.</param>
/// <param name="DesenseCompDb">Each blocker's desensitisation by compression: the signal's small-signal gain lost, dB.</param>
public sealed record ChainBlockingReadout(double PhaseNoiseDbc, double EvmFloorPct, double SnrDb, double EvmPct,
                                          IReadOnlyList<double> DesenseRmDb, IReadOnlyList<double> DesenseCompDb);

public static class ChainBlocking
{
    private static double Lin(double db) => Math.Pow(10, db / 10);
    private static double Db(double x) => 10 * Math.Log10(x);

    /// <summary>
    /// ∫ 10^(L(f)/10)·(1 − f/<paramref name="w"/>) df from <paramref name="a"/> to <paramref name="b"/>, Hz, L the mask as
    /// <see cref="PhaseNoiseMask.At"/> reads it (its first segment continued below its first point, the floor beyond its
    /// last); <paramref name="w"/> = ∞ is no weighting. Exact: on each segment 10^(L/10) = 10^(L_r/10)·(f/f_r)^s, s its
    /// slope in dB/decade over 10.
    /// </summary>
    public static double Integral(PhaseNoiseMask m, double a, double b, double w = double.PositiveInfinity)
    {
        if (!(b > a) || !(a > 0)) return 0;
        var cuts = m.Points.Select(p => p.OffsetHz).Where(f => f > a && f < b).Prepend(a).Append(b).ToList();
        double sum = 0;
        for (int i = 0; i + 1 < cuts.Count; i++)
        {
            double x0 = cuts[i], x1 = cuts[i + 1];
            var (fr, lr, s) = Piece(m, Math.Sqrt(x0 * x1));
            double c = Lin(lr) * fr;
            sum += c * PowerIntegral(s, x0 / fr, x1 / fr);
            if (double.IsFinite(w)) sum -= c * fr / w * PowerIntegral(s + 1, x0 / fr, x1 / fr);
        }
        return sum;
    }

    /// <summary>The segment of the mask that holds <paramref name="f"/>: a point on it, Hz and dBc/Hz, and its slope over 10.</summary>
    private static (double F, double L, double S) Piece(PhaseNoiseMask m, double f)
    {
        var p = m.Points;
        if (f > p[^1].OffsetHz) return (p[^1].OffsetHz, m.FloorDbc, 0);
        if (p.Count == 1) return (p[0].OffsetHz, p[0].Dbc, 0);
        int i = 0;
        while (i < p.Count - 2 && f > p[i + 1].OffsetHz) i++;
        return (p[i].OffsetHz, p[i].Dbc, (p[i + 1].Dbc - p[i].Dbc) / Math.Log10(p[i + 1].OffsetHz / p[i].OffsetHz) / 10);
    }

    /// <summary>∫ u^t du from <paramref name="u0"/> to <paramref name="u1"/>, the logarithm at t = −1 and nothing lost near it.</summary>
    private static double PowerIntegral(double t, double u0, double u1)
    {
        double e = t + 1, l = Math.Log(u1 / u0), p = Math.Pow(u0, e);
        return Math.Abs(e * l) < 1e-12 ? p * l : p * double.ExpM1(e * l) / e;
    }

    /// <summary>The LO's phase noise that falls in a channel of <paramref name="bwHz"/>, as a fraction of the signal:
    /// 2·∫ L(f)·(1 − f/B) df from the mask's first offset to B. Below its first offset a demodulator tracks the phase.</summary>
    public static double InChannel(PhaseNoiseMask m, double bwHz) => 2 * Integral(m, m.Points[0].OffsetHz, bwHz, bwHz);

    /// <summary>A blocker's own phase noise, the LO's mixed onto it, that falls in the channel, as a fraction of the
    /// blocker: ∫ L(|u|) du over the channel, <paramref name="offsetHz"/> from it, the mask taken from its first offset.</summary>
    public static double Reciprocal(PhaseNoiseMask m, double offsetHz, double bwHz)
    {
        double f1 = m.Points[0].OffsetHz, lo = offsetHz - bwHz / 2, hi = offsetHz + bwHz / 2;
        return Integral(m, Math.Max(lo, f1), hi) + (lo < -f1 ? Integral(m, f1, -lo) : 0);
    }

    /// <summary>The trapezoid rule's points over the tanh limiter's cycle (<see cref="ChainTransmit.TanhCompressionDb"/>'s).</summary>
    private const int Cycle = 4096;

    /// <summary>
    /// A weak signal's power gain through <paramref name="b"/>'s stage with a blocker of <paramref name="pbDbm"/> at its
    /// input, over its small-signal gain: |F(s) + s·F′(s)|² for a Rapp or Saleh law at s = P_b (the envelope's law,
    /// linearised about the blocker), and for every other stage that compresses, its tanh limiter's slope averaged over
    /// the blocker's cycle, ⟨sech²(u·cos θ)⟩² at u = 2·√(P_b/IIP3). 1 for a linear stage or no blocker.
    /// </summary>
    public static double GainRatio(StageBudget b, double pbDbm)
    {
        if (!double.IsFinite(pbDbm)) return 1;
        if (b.Law is { } law)
        {
            var f = law.Gain(Lin(pbDbm - 30), out var d);
            return Complex.Abs(f + d) is var g ? g * g : 1;
        }
        if (!double.IsFinite(b.Iip3Dbm)) return 1;
        double u = 2 * Math.Sqrt(Lin(pbDbm - b.Iip3Dbm)), sum = 0;
        if (u < 1e-9) return 1;
        for (int k = 0; k < Cycle; k++)
        {
            double c = Math.Cosh(u * Math.Cos(2 * Math.PI * k / Cycle));
            sum += 1 / (c * c);
        }
        return sum / Cycle * (sum / Cycle);
    }

    /// <summary>
    /// The levels <paramref name="nodes"/> (<see cref="ChainLevels.Evaluate"/>) with each node's phase noise, reciprocal
    /// mixing, desensitisation and EVM filled in. A mixer's mask enters at its output: the phase noise as a share of the
    /// signal from there on, each blocker's reciprocal mixing at the mixer's input carried by the noise gain after it.
    /// </summary>
    public static List<ChainNodeLevel> Annotate(ChainDesign d, ChainBudgetResult r, List<ChainNodeLevel> nodes)
    {
        int nb = d.Blockers.Count, n = r.Stages.Count;
        var shares = Shares(d, r, nodes, d.BandwidthHz);
        var comp = new double[nb];                     // each blocker's compression, the gain lost so far, dB
        double pin = Lin(d.InputPowerDbm - 30);
        for (int k = 0; k <= n; k++)
        {
            var node = nodes[k];
            if (k > 0)
                for (int b = 0; b < nb; b++) comp[b] -= Db(GainRatio(r.Stages[k - 1].Stage, nodes[k - 1].BlockerDbm[b]));
            var (pn, rm) = shares[k];
            double s = Lin(node.SignalDbm - 30), noise = Lin(node.NoiseDbm - 30);
            double iip3 = k == 0 ? double.PositiveInfinity : r.Stages[k - 1].Iip3Dbm;
            double dist = double.IsFinite(iip3) ? 4.0 / 3 * Math.Pow(pin / Lin(iip3 - 30), 2) : 0;
            nodes[k] = node with
            {
                PhaseNoiseDbc = pn > 0 ? Db(pn) : double.NegativeInfinity,
                ReciprocalDbm = [.. rm.Select(x => x > 0 ? Db(x) + 30 : double.NegativeInfinity)],
                DesenseRmDb = [.. rm.Select(x => Db(1 + x / noise))],
                DesenseCompDb = [.. comp],
                EvmPct = 100 * Math.Sqrt(noise / s + pn + rm.Sum() / s + dist),
            };
        }
        return nodes;
    }

    /// <summary>
    /// At every node, in a channel of <paramref name="bwHz"/>: σ², the LOs' phase noise there as a share of the signal,
    /// and each blocker's reciprocal mixing, W. A mixer's mask enters at its output; a blocker's reciprocal mixing is
    /// taken at the mixer's input, at the blocker's level and offset there, and carried by the noise gain after it.
    /// </summary>
    public static (double Pn, double[] RmW)[] Shares(ChainDesign d, ChainBudgetResult r, IReadOnlyList<ChainNodeLevel> nodes, double bwHz)
    {
        int nb = d.Blockers.Count, n = r.Stages.Count;
        var inputHz = d.StageInputFrequencies();
        // The noise gain to each node, dB: the signal's less the array's, as the cascade has them.
        double Gn(int node) => node == 0 ? 0 : r.Stages[node - 1].GainDb - r.Stages[node - 1].ArrayGainDb;
        var mixers = new List<(int Node, double[] RmW)>();   // each mixer passed: its input node, each blocker's N_rm there
        var res = new (double, double[])[n + 1];
        double pn = 0;
        for (int k = 0; k <= n; k++)
        {
            if (k > 0 && ChainParts.MaskOf(d.Stages[k - 1]) is { } mask)
            {
                int i = k - 1;
                pn += InChannel(mask, bwHz);
                mixers.Add((i, [.. Enumerable.Range(0, nb).Select(b =>
                    Lin(nodes[i].BlockerDbm[b] - 30) * Reciprocal(mask, Math.Abs(nodes[i].BlockerHz[b] - inputHz[i]), bwHz))]));
            }
            res[k] = (pn, [.. Enumerable.Range(0, nb).Select(b => mixers.Sum(m => m.RmW[b] * Lin(Gn(k) - Gn(m.Node))))]);
        }
        return res;
    }

    /// <summary>The readout at the output; null when the budget was refused.</summary>
    public static ChainBlockingReadout? Read(ChainBudgetResult r)
    {
        if (r.Totals is null || r.Design is not { } d) return null;
        var o = ChainLevels.Evaluate(d, r)[^1];
        double rest = Lin(o.NoiseDbm - o.SignalDbm) + (double.IsFinite(o.PhaseNoiseDbc) ? Lin(o.PhaseNoiseDbc) : 0)
                    + o.ReciprocalDbm.Sum(x => double.IsFinite(x) ? Lin(x - o.SignalDbm) : 0);
        return new ChainBlockingReadout(o.PhaseNoiseDbc, double.IsFinite(o.PhaseNoiseDbc) ? 100 * Math.Pow(10, o.PhaseNoiseDbc / 20) : 0,
                                        -Db(rest), o.EvmPct, o.DesenseRmDb, o.DesenseCompDb);
    }

    /// <summary>What a blocker or a mask moves by this much, dB, the budget says in its notes.</summary>
    public const double NoteDb = 0.01;

    /// <summary>The notes: the LO's phase noise in the channel, and each blocker that desensitises the chain by
    /// <see cref="NoteDb"/> or more.</summary>
    public static IEnumerable<string> Notes(ChainBudgetResult r)
    {
        if (Read(r) is not { } o) yield break;
        static string D(double v) => v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        if (double.IsFinite(o.PhaseNoiseDbc))
            yield return $"The LO's phase noise puts {D(o.PhaseNoiseDbc)} dBc in the channel, an EVM floor of {D(o.EvmFloorPct)} %.";
        for (int b = 0; b < o.DesenseRmDb.Count; b++)
        {
            var why = new List<string>();
            if (o.DesenseRmDb[b] >= NoteDb) why.Add($"{D(o.DesenseRmDb[b])} dB by reciprocal mixing");
            if (o.DesenseCompDb[b] >= NoteDb) why.Add($"{D(o.DesenseCompDb[b])} dB of gain by compression");
            if (why.Count > 0) yield return $"Blocker {b + 1} desensitises the chain: {string.Join(", and ", why)}.";
        }
    }

    /// <summary>The readout added to a budget's DataSet: the phase noise as scalars when a mixer states a mask, each
    /// blocker's desensitisation over a <c>blocker</c> axis when there are blockers, and the EVM at the output.</summary>
    public static void AddTo(DataSet ds, ChainBudgetResult r)
    {
        if (Read(r) is not { } o) return;
        static double Fin(double v) => double.IsFinite(v) ? v : double.NaN;
        void One(string name, string unit, double v) => ds.Add(name, new DataCube([], [Fin(v)]) { Unit = unit });
        if (double.IsFinite(o.PhaseNoiseDbc))
        {
            One("PN_dBc", "dBc", o.PhaseNoiseDbc);
            One("EVMpn_pct", "%", o.EvmFloorPct);
        }
        One("SNReff_dB", "dB", o.SnrDb);
        One("EVM_pct", "%", o.EvmPct);
        if (o.DesenseRmDb.Count == 0) return;
        var axis = new Axis("blocker", [.. Enumerable.Range(1, o.DesenseRmDb.Count).Select(i => (double)i)]);
        ds.Add("DesenseRM_dB", new DataCube([axis], [.. o.DesenseRmDb.Select(Fin)]) { Unit = "dB" });
        ds.Add("DesenseComp_dB", new DataCube([axis], [.. o.DesenseCompDb.Select(Fin)]) { Unit = "dB" });
    }
}
