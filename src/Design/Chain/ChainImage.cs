// The noise each mixer takes in at its image. A mixer's single-sideband NF is stated with its image band terminated at
// T0, and the budget's Friis sum takes exactly that; what really arrives there is whatever the stages before it pass and
// add at the image frequency. The difference, referred to the chain's input, is the image's share of the noise:
//
//   ΔTe_m = (T_im − T0) / Gn_m        T_im: the noise temperature delivered into mixer m at its image f_im
//                                     Gn_m: the signal path's noise gain from the input to mixer m
//
// The System Mixer is an ideal multiplier, so both sidebands convert with its ConvGain and the excess converts as the
// signal does: dividing by the gain to the mixer refers it to the input exactly. T_im is walked stage by stage from the
// source at the image's own frequency, every stage there as its block is (a Filter its own response, an SnP stage its
// file, a value of the frequency read there; an LO and a filter's band where the carrier put them):
//
//   a stage       T ← g·T + N            g = |S21|² (or the budget's noise gain at f), N the noise it delivers into a
//                                        matched load: T0·(1 − |S21|² − |S22|²) for a passive one with S data (Bosma: in a
//                                        stopband, where it reflects, almost none), g·T0·(F − 1) for every other
//   a mixer       T_out(f) = Gc·[T_in(f + Flo) + T_in(|f − Flo|)] + Gc·T0·(F_ssb − 2)
//                                        both inputs that convert to f (an earlier mixer's own image included), and its own
//                                        noise, the SSB figure less the two sidebands' T0
//   a mixer's input  + T0·|Γ|²           the bench's isolator in front of every mixer: its terminated port's T0, reflected
//                                        by the stage before it (Γ its output's reflection there)
//
// so a filter right in front of a mixer gives T_im = T0 + |S21|²·(T − T0): T0 where it rejects the image, and today's
// numbers. With mismatch (ChainMismatch), an image with no converter before its mixer is the stages' exact cascade there:
// T_im = G_T·(T0 + T0·(F − 1)) + T0·|S22|².
//
// Each term is a vector over what made it (the source, then every stage), so a stage's share of F − 1 includes the
// noise it sends through every image, as a noise run's shares do; the mixer carries its image termination's.

using RfCore.Data;

namespace CircuitRF.Design.Chain;

/// <summary>One mixer's image band, referred to the chain's input.</summary>
/// <param name="Stage">The mixer's index.</param>
/// <param name="ImageHz">The image at the mixer's input.</param>
/// <param name="TeK">ΔTe, K, by what made it: [0] the source at T0, [1 + i] stage i (the mixer's own: its image
/// termination's departure from T0).</param>
/// <param name="Counted">Whether the budget adds it: it moves the chain's NF by <see cref="ChainImage.ThresholdDb"/> or
/// more.</param>
public sealed record ChainImageTerm(int Stage, string Name, double ImageHz, IReadOnlyList<double> TeK, bool Counted)
{
    /// <summary>ΔTe, K, the whole of it.</summary>
    public double TotalK => TeK.Sum();

    /// <summary>What it adds to the chain's NF, dB, against the chain's noise temperature without it.</summary>
    public double AddedDb(double teK) => 10 * Math.Log10(1 + TotalK / (ChainBudget.T0 + teK));
}

public static class ChainImage
{
    private const double T0 = ChainBudget.T0;

    /// <summary>An image moving the NF by less than this, dB, is left out, so a chain whose filters reject its images budgets
    /// exactly as it did before image noise was counted. 0.001 dB: under every number the window shows and every
    /// tolerance a bench is read to, and above what the shipped chains' preselectors let through (the largest, the 26 GHz
    /// link's, 4.4×10⁻⁴ dB).</summary>
    public const double ThresholdDb = 0.001;

    /// <summary>
    /// Every Mixer's image term. <paramref name="raw"/> is the design as written (its values of the frequency read at each
    /// image), <paramref name="d"/> as resolved at the carrier, <paramref name="budgets"/> its stages' budgets there,
    /// <paramref name="gnTo"/>(m) the noise gain from the input to stage m's input, and <paramref name="twoPorts"/>, with
    /// mismatch, a stage's two-ports at a frequency.
    /// </summary>
    public static IReadOnlyList<ChainImageTerm> Terms(ChainDesign raw, ChainDesign d, IReadOnlyList<StageBudget> budgets,
                                                      Func<int, double> gnTo, double teK,
                                                      Func<int, double, IReadOnlyList<ChainTwoPort>?>? twoPorts = null,
                                                      List<string>? notes = null)
    {
        var walk = new Walk(raw, d);
        var inputHz = d.StageInputFrequencies();
        var terms = new List<ChainImageTerm>();
        for (int m = 0; m < d.Stages.Count; m++)
        {
            var s = d.Stages[m];
            if (s.Part != ChainParts.Mixer) continue;
            // The two inputs that convert to the output are fo + Flo and |fo − Flo|: the signal is one, the image the other.
            double flo = ChainValue.Or(s.Get("Flo"), 0), fin = inputHz[m], fo = ChainParts.OutputHz(s, fin);
            double fim = Math.Abs(fo + flo - fin) <= 1e-9 * fin ? Math.Abs(fo - flo) : fo + flo;
            if (!(fim > 0)) continue;
            bool single = !d.Stages.Take(m).Any(x => ChainParts.Converts(x.Part));
            double[]? t = twoPorts is not null && single ? Exact(m, fim, twoPorts, d.Stages.Count) : null;
            if (t is not null)
                for (int k = 0; k < m; k++) walk.Outside(k, fim);   // the exact cascade reads the same files there
            t ??= walk.Into(m, fim, isolated: true);
            t[1 + m] -= T0;   // the T0 the SSB figure already holds
            double gn = gnTo(m);
            var te = t.Select(x => x / gn).ToArray();
            terms.Add(new ChainImageTerm(m, s.Name, fim, te,
                                         Math.Abs(te.Sum()) >= (Math.Pow(10, ThresholdDb / 10) - 1) * (T0 + teK)));
        }
        // A file the walk read outside its range is taken at its nearest frequency, as the SnP block takes it: said once a
        // stage, as a blocker outside a file is.
        foreach (var (k, f) in walk.OutsideFiles)
            notes?.Add($"{d.Stages[k].Name}: a mixer's image reaches it at {ChainSnp.Hz(f)}, outside its file's range; its gain and " +
                       "noise there are taken at the file's nearest frequency, as the SnP block takes them in a simulation.");
        return terms;
    }

    /// <summary>The image of mixer <paramref name="m"/> through the exact cascade of the stages before it, which no
    /// converter moves: G_T·(T0 + T0·(F − 1)) + T0·|S22|², by source and stage. Null when a stage has no two-port there.</summary>
    private static double[]? Exact(int m, double fim, Func<int, double, IReadOnlyList<ChainTwoPort>?> twoPorts, int n)
    {
        var t = new double[n + 1];
        if (m == 0) { t[0] = T0; return t; }
        var parts = new List<IReadOnlyList<ChainTwoPort>>();
        for (int k = 0; k < m; k++)
            if (twoPorts(k, fim) is { } p) parts.Add(p);
            else return null;
        if (ChainMismatch.Cascade(parts) is not { } c) return null;
        double g = c.Gain;
        t[0] = g * T0;
        for (int k = 0; k < m; k++) t[1 + k] = g * T0 * c.Terms[k];
        var s22 = c.S[^1][1, 1];
        t[1 + m] = T0 * s22.Magnitude * s22.Magnitude;
        return t;
    }

    /// <summary>The stage-by-stage walk at any frequency, by what made each kelvin.</summary>
    private sealed class Walk(ChainDesign raw, ChainDesign d)
    {
        private readonly int _n = d.Stages.Count;
        private readonly IReadOnlyList<double> _hz = d.StageInputFrequencies();

        /// <summary>Each SnP stage first read outside its file, and the frequency.</summary>
        public readonly List<(int Stage, double Hz)> OutsideFiles = [];

        /// <summary>Notes stage <paramref name="k"/> when it is an SnP stage whose file does not reach <paramref name="f"/>.</summary>
        public void Outside(int k, double f)
        {
            if (d.Stages[k].Part == ChainParts.Snp && OutsideFiles.All(x => x.Stage != k)
                && ChainSnp.FileAt(d.Stages[k], d.BaseDirectory, f) is { Outside: true })
                OutsideFiles.Add((k, f));
        }

        /// <summary>The noise delivered into stage <paramref name="k"/> at <paramref name="f"/>; with
        /// <paramref name="isolated"/>, through the isolator a bench places in front of a mixer.</summary>
        public double[] Into(int k, double f, bool isolated)
        {
            double[] t;
            if (k == 0) { t = new double[_n + 1]; t[0] = T0; }
            else t = Out(k - 1, f);
            if (isolated && k > 0) t[1 + k] += T0 * Stage(k - 1, f).Gamma2;
            return t;
        }

        private double[] Out(int j, double f)
        {
            var s = d.Stages[j];
            var t = new double[_n + 1];
            var b = Stage(j, f);
            if (s.Part == ChainParts.Mixer)
            {
                double flo = ChainValue.Or(s.Get("Flo"), 0);
                foreach (double x in new[] { f + flo, Math.Abs(f - flo) })
                {
                    if (!(x > 0)) continue;
                    var a = Into(j, x, isolated: true);
                    for (int i = 0; i <= _n; i++) t[i] += b.G * a[i];
                }
                t[1 + j] += b.G * T0 * (b.F - 2);
                return t;
            }
            if (s.Part == ChainParts.IqMod)
            {
                // Its own image is its rejection's; only the sideband it is built to take reaches its output.
                double flo = ChainValue.Or(s.Get("Flo"), 0), x = ChainParts.IsUpper(s) ? f - flo : flo - f;
                if (x > 0) { var a = Into(j, x, isolated: false); for (int i = 0; i <= _n; i++) t[i] += b.G * a[i]; }
                t[1 + j] += b.G * T0 * (b.F - 1);
                return t;
            }
            var into = Into(j, f, isolated: false);
            for (int i = 0; i <= _n; i++) t[i] = b.G * into[i];
            t[1 + j] += b.N;
            return t;
        }

        /// <summary>Stage <paramref name="j"/> at <paramref name="f"/>: the gain noise sees, the noise it delivers into a
        /// matched load (K), its output's |Γ|², and its noise factor.</summary>
        private (double G, double N, double Gamma2, double F) Stage(int j, double f)
        {
            var key = (j, f);
            if (_cache.TryGetValue(key, out var hit)) return hit;
            // A converter is the one number its block carries for both sidebands, its carrier's; every other stage is read
            // at the frequency it sees.
            bool conv = ChainParts.Converts(d.Stages[j].Part);
            var s = conv ? d.Stages[j] : ChainScaling.At(raw.Stages[j], d.Stages[j], f, d.BaseDirectory);
            (double, double, double, double) r;
            switch (s.Part)
            {
                case ChainParts.Filter:
                {
                    var (_, s21, s22) = ChainLevels.FilterOf(s).At(2 * Math.PI * f);
                    double g = s21.Magnitude * s21.Magnitude, r2 = s22.Magnitude * s22.Magnitude;
                    r = (g, T0 * Math.Max(0, 1 - g - r2), r2, double.NaN);
                    break;
                }
                case ChainParts.Snp when ChainSnp.FileAt(s, d.BaseDirectory, f) is { } a:
                {
                    if (a.Outside) Outside(j, f);
                    double g = a.S[1, 0].Magnitude * a.S[1, 0].Magnitude, r2 = a.S[1, 1].Magnitude * a.S[1, 1].Magnitude;
                    double n = a.Noise is { } p
                        ? g * T0 * (NoiseMetrics.NoiseFactor(p, new System.Numerics.Complex((ChainSnp.Z0 - p.R) / (ChainSnp.Z0 + p.R), 0)) - 1)
                        : a.Passive ? T0 * Math.Max(0, 1 - g - r2) : 0;
                    // A stated NF above the file's own is an ideal matched amplifier after it on the benches, read at the
                    // stage's carrier: its noise is added, and the stage's output is that amplifier's, matched.
                    var read = ChainSnp.Read(d.Stages[j], new ChainStageContext(_hz[j], d.BaseDirectory), []);
                    r = read is { NeedsAmp: true } ? (g, n + T0 * (read.AmpNoiseFactor - 1), 0, double.NaN) : (g, n, r2, double.NaN);
                    break;
                }
                default:
                {
                    var b = ChainParts.Find(s.Part)!.Budget(s, new ChainStageContext(conv ? _hz[j] : f, d.BaseDirectory, Response: true));
                    double g = Math.Pow(10, b.NoiseGainDb / 10), fac = Math.Pow(10, b.NfDb / 10);
                    r = (g, g * T0 * (fac - 1), 0, fac);
                    break;
                }
            }
            _cache[key] = r;
            return r;
        }

        private readonly Dictionary<(int, double), (double G, double N, double Gamma2, double F)> _cache = [];
    }
}
