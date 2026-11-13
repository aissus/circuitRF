// The canceller's nonlinearity, and its residual through the stages after it.
//
// The canceller's main path is a directional coupler, linear; its reference path (the amplitude setting) is an
// Amplifier with the Tanh law, its intercept IP3 stated at its output, the coupler's isolated port. The interference is
// taken as two tones each at Pint, as the levels take the signal as two tones each at the input power. The reference
// path's own terms are the Tanh law's two-tone expansion to fifth order, y the output per tone over OIP3 — tanh u =
// u − u³/3 + 2u⁵/15 with u² = 4y, whose fifth order is the 0.1 to 0.2 dB a cubic misses 25 dB under the intercept:
//
//   reference output      P_ro = Pint + 10·log10((1 − c)/c) + AmpErr      (per tone, the level that cancels)
//   its compression       g = 1 − 3y + (40/3)·y²                          (each tone's gain, two equal tones)
//   residual              R(a·g, φ, τ, B), a = 10^(AmpErr/20)              (the reference's compression is an amplitude error)
//   its own IM3           3·P_ro − 2·OIP3 + 20·log10(1 − (20/3)·y) + 10·log10(c) − IL   (each product, at the output)
//
// and through every stage after it, a cubic's as the cascade's intercept sums are, OIP3' the stages' cascaded intercept
// from the canceller's output on:
//
//   the residual's IM3    3·P_r − 2·OIP3'                                  (each product)
//   with the signal       2·P_s + P_r − 2·OIP3'  at 2·f_s − f_r
//                         2·P_r + P_s − 2·OIP3' + 20·log10(1 + 2·√(P_c/P_r))   at 2·f_r − f_s
//   the signal's compression       Σ 20·log10(1 − 4·(P_r,i + P_c,i)/IIP3_i)   (two residual tones and two IM3 products)
//
// The canceller's own IM3 (P_c) is carried by the gains; the subtraction does not take it out, so it is the floor its
// settings cannot tune under. With the residual at f_b and the signal it makes a product on 2·f_a − f_s too, twice the
// amplitude of a two-tone one: taken in phase with the residual's own there, the worst case, as the intercepts sum. Its
// sources are in docs/design/chain-budget.md.

using RfCore.Data;

namespace CircuitRF.Design.Chain;

public sealed partial record CancellerSpec
{
    /// <summary>True when the reference path is linear: no IP3, or one at the Amplifier's linear sentinel.</summary>
    public bool Linear => !(Ip3Dbm < ChainParts.AmpLinearDbm);

    /// <summary>The reference path's output per tone at the coupler's isolated port, dBm: the level that cancels the
    /// interference, plus the amplitude error.</summary>
    public double RefOutDbm => PintDbm + 10 * Math.Log10((1 - C) / C) + AmpErrDb;

    /// <summary>The reference path's output 1 dB compression point, dBm: the Tanh law's, IP3 − 9.96 dB.</summary>
    public double RefOp1dbDbm => Linear ? double.PositiveInfinity : Ip3Dbm - ChainParts.TanhP1dbBelowIip3 - 1;

    /// <summary>The reference path's output per tone over its intercept, linear: the series' variable.</summary>
    private double Share => Linear ? 0 : Math.Pow(10, (RefOutDbm - Ip3Dbm) / 10);

    /// <summary>Past this share the fifth-order series no longer reads the Tanh law (its fifth order a third of its third
    /// at 0.075): 13 dB under the intercept.</summary>
    public const double SeriesReach = 0.05;

    /// <summary>The Tanh law's gain for each of two equal tones at a share <paramref name="y"/> of its intercept.</summary>
    public static double TwoToneGain(double y) => 1 - 3 * y + 40.0 / 3 * y * y;

    /// <summary>The reference path's compression at its output, dB (≤ 0).</summary>
    public double CompressionDb => 20 * Math.Log10(TwoToneGain(Share));

    /// <summary>The amplitude error the subtraction sees: the stated one and the compression.</summary>
    public double EffectiveAmpErrDb => AmpErrDb + CompressionDb;

    /// <summary>The amplitude error that takes the compression back, dB: a·g(a²·x) = 1, x the reference's share of its
    /// intercept at no error, found by bisection from 1 (no error) to 2. NaN where none does.</summary>
    public double AmpErrForNullDb
    {
        get
        {
            if (Linear) return 0;
            double x = Math.Pow(10, (RefOutDbm - AmpErrDb - Ip3Dbm) / 10);
            double F(double a) => a * TwoToneGain(a * a * x);
            double lo = 1, hi = 2;
            if (F(hi) < 1) return double.NaN;
            for (int i = 0; i < 200; i++)
            {
                double m = 0.5 * (lo + hi);
                if (F(m) < 1) lo = m; else hi = m;
            }
            return 20 * Math.Log10(0.5 * (lo + hi));
        }
    }

    /// <summary>The reference path's own IM3, each product, at the canceller's output, dBm; −∞ when linear.</summary>
    public double Im3Dbm => Linear ? double.NegativeInfinity
        : 3 * RefOutDbm - 2 * Ip3Dbm + 20 * Math.Log10(1 - 20.0 / 3 * Share) + 10 * Math.Log10(C) - IlDb;

    /// <summary>What the readout says of the reference path's nonlinearity; nothing when it is linear.</summary>
    private List<string> Readout(string name)
    {
        if (Linear) return [];
        string who = name.Length > 0 ? name + ": " : "";
        static string D(double v) => v.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
        var notes = new List<string>
        {
            $"{who}the reference path compresses {D(CompressionDb)} dB at {D(RefOutDbm)} dBm a tone (its OP1dB {D(RefOp1dbDbm)} dBm), " +
            "an amplitude error of its own" + (double.IsFinite(AmpErrForNullDb) ? $", which AmpErr = {AmpErrForNullDb:0.####} dB takes back." : "."),
        };
        double gap = ResidualDbm - Im3Dbm;
        notes.Add(gap >= 0
            ? $"{who}its own IM3, {D(Im3Dbm)} dBm each product at its output, is {D(gap)} dB under the residual of {D(ResidualDbm)} dBm: " +
              "its settings, not its intercept, set what is left."
            : $"{who}its own IM3, {D(Im3Dbm)} dBm each product at its output, is {D(-gap)} dB over the residual of {D(ResidualDbm)} dBm: " +
              "its intercept, not its settings, sets what is left, and no tuning takes it out.");
        if (Share > SeriesReach)
            notes.Add($"{who}the reference path is driven within {D(-10 * Math.Log10(Share))} dB of its intercept, past what the " +
                      "series the budget takes reads: the Canceller bench's harmonic-balance part shows what the Tanh law does there.");
        return notes;
    }
}

/// <summary>One node's canceller terms: the residual per tone, the canceller's own IM3 carried there, the residual's
/// own IM3 in the stages after the canceller, its two products with the signal (2·f_s − f_r and 2·f_r − f_s) and the
/// compression of the signal it causes (dB, ≤ 0). NaN before the canceller's output, and where a term does not exist.</summary>
public sealed record ChainCancellerNode(string Node, double ResidualDbm, double CancellerIm3Dbm, double ResidualIm3Dbm,
                                        double SignalSignalResidualDbm, double ResidualResidualSignalDbm, double DesenseDb);

public static class ChainCanceller
{
    private static double Lin(double db) => Math.Pow(10, db / 10);
    private static double Db(double x) => 10 * Math.Log10(x);

    /// <summary>The chain's (first) canceller, its stage index; −1 for none.</summary>
    public static int Index(ChainDesign d) => d.Stages.FindIndex(s => s.Part == ChainParts.Canceller);

    /// <summary>
    /// The canceller's terms at the input and after every stage (<see cref="ChainLevels"/>' nodes), from the canceller's
    /// output on; empty when the budget is refused or the chain has no canceller.
    /// </summary>
    public static IReadOnlyList<ChainCancellerNode> Levels(ChainBudgetResult r)
    {
        if (r.Totals is null || r.Design is not { } d || Index(d) is var ci && ci < 0) return [];
        var c = CancellerSpec.Read(d.Stages[ci]);
        var nodes = new List<ChainCancellerNode> { None("IN") };
        for (int k = 0; k < ci; k++) nodes.Add(None(d.Stages[k].Name));
        double pr = c.ResidualDbm, im3 = c.Im3Dbm, g = 1, inv3 = 0, desense = 0;
        for (int k = ci; k < r.Stages.Count; k++)
        {
            var b = r.Stages[k].Stage;
            if (k > ci)
            {
                // Stage k sees the residual at its input: its share of the cascade's intercept, and of the signal's
                // compression (two residual tones each at pr, a cubic).
                if (double.IsFinite(b.Iip3Dbm))
                {
                    inv3 += g / Lin(b.Iip3Dbm);
                    double f = 1 - 4 * (Lin(pr - b.Iip3Dbm) + (double.IsFinite(im3) ? Lin(im3 - b.Iip3Dbm) : 0));
                    desense = f > 0 ? desense + 20 * Math.Log10(f) : double.NaN;
                }
                g *= Lin(b.GainDb);
                pr += b.GainDb;
                im3 += b.GainDb;
            }
            double ps = r.Stages[k].PoutDbm;
            double oip3 = inv3 > 0 ? -Db(inv3) + Db(g) : double.PositiveInfinity;
            double cross = double.IsFinite(im3) ? 20 * Math.Log10(1 + 2 * Math.Pow(10, (im3 - pr) / 20)) : 0;
            nodes.Add(new ChainCancellerNode(d.Stages[k].Name, pr, Third(im3),
                Third(3 * pr - 2 * oip3), Third(2 * ps + pr - 2 * oip3), Third(2 * pr + ps - 2 * oip3 + cross), desense));
        }
        return nodes;
    }

    private static ChainCancellerNode None(string node) => new(node, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN);
    private static double Third(double v) => double.IsFinite(v) ? v : double.NaN;

    /// <summary>The canceller's numbers as scalars beside the budget's: its depth and residual, and with an IP3 its
    /// compression, the amplitude error that takes it back and its own IM3. Nothing for a chain with no canceller.</summary>
    public static void AddTo(DataSet ds, ChainBudgetResult r)
    {
        if (r.Design is not { } d || Index(d) is var ci && ci < 0) return;
        var c = CancellerSpec.Read(d.Stages[ci]);
        void One(string name, string unit, double v) => ds.Add(name, new DataCube([], [double.IsFinite(v) ? v : double.NaN]) { Unit = unit });
        One("CancDepth_dB", "dB", c.DepthDb);
        One("CancRes_dBm", "dBm", c.ResidualDbm);
        if (c.Linear) return;
        One("CancComp_dB", "dB", c.CompressionDb);
        One("CancAmpNull_dB", "dB", c.AmpErrForNullDb);
        One("CancIM3_dBm", "dBm", c.Im3Dbm);
    }

    /// <summary>The nodes' terms as cubes over the levels' <c>node</c> axis, NaN where a term does not exist.</summary>
    public static DataSet ToDataSet(IReadOnlyList<ChainCancellerNode> nodes)
    {
        var ds = new DataSet();
        if (nodes.Count == 0) return ds;
        var axis = new Axis("node", [.. Enumerable.Range(0, nodes.Count).Select(i => (double)i)], "", [.. nodes.Select(n => n.Node)]);
        void Per(string name, string unit, Func<ChainCancellerNode, double> pick)
            => ds.Add(name, new DataCube([axis], [.. nodes.Select(n => double.IsFinite(pick(n)) ? pick(n) : double.NaN)]) { Unit = unit });
        Per("Res_dBm", "dBm", n => n.ResidualDbm);
        Per("CancIM3_node_dBm", "dBm", n => n.CancellerIm3Dbm);
        Per("ResIM3_dBm", "dBm", n => n.ResidualIm3Dbm);
        Per("IMssr_dBm", "dBm", n => n.SignalSignalResidualDbm);
        Per("IMrrs_dBm", "dBm", n => n.ResidualResidualSignalDbm);
        Per("Desense_dB", "dB", n => n.DesenseDb);
        return ds;
    }
}
