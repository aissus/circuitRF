// The cascade with mismatch: every stage a two-port, its S at 50 Ω and its noise as the chain correlation matrix C_A
// (RfCore's NoiseMetrics), cascaded exactly, so the gain and the noise figure are those between real ports, every
// reflection between neighbours included.
//
//   ABCD      each stage's from its S at Z0 = 50 Ω; the cascade's is their product, A = A1·A2·…·An
//   noise     C_A = C1 + A1·C2·A1ᴴ + (A1·A2)·C3·(A1·A2)ᴴ + …          (Hillbrand & Russer 1976)
//   NF        F = 1 + (C11·|Ys|² + 2·Re(Ys·C12) + C22) / (4kT0·Re Ys),  Ys = 1/50 Ω
//   gain      G_T = |S21|² of the cascade between 50 Ω terminations, S21 = 2/(A + B/Z0 + C·Z0 + D)
//
// Term k of the sum is stage k's share of F − 1, which is what a noise run's NFc reports. Each stage's two-port:
//
//   Filter    its own network (FilterNetwork, the block's model), reciprocal, its noise Bosma's: Cs = kT0·(I − S·Sᴴ)
//   SnP       the file between In and Out at 50 Ω, the other ports terminated; its noise rows' C_A, or Bosma's when it
//             is passive and has none; a stated NF above the file's an ideal matched 0 dB amplifier after it, as the
//             benches place it
//   Atten, Switch, Coupler, Splitter   matched and reciprocal, S12 = S21 = 10^(G/20), Bosma
//   Circulator                         matched, an isolator: S12 = 0, Bosma (its third port's termination comes out of
//                                      its input)
//   every other stage                  matched and unilateral, S21 = 10^(Gn/20) (its noise gain: a Combiner's coherent
//                                      gain is the array's, not the two-port's), and one noise wave of T0·(F − 1) into
//                                      its input: the System Amplifier's own NF model (Fmin = F, Zopt = 50 Ω,
//                                      Rn = (F − 1)·50/4)
//
// A bench places an isolator in front of every mixer (its image termination), so the cascade does too: S = [[0,0],[1,0]],
// its terminated port's kT0 coming out of its input, where the stage before it reflects it into the mixer.
//
// A stage with no S data is its budget's matched two-port, so a chain of them cascades to the matched budget exactly;
// only a Filter's or an SnP stage's reflections, and what its neighbours do with them, move a number.

using System.Numerics;
using NumFlat;
using RfCore.Data;

namespace CircuitRF.Design.Chain;

/// <summary>A stage as a two-port at one frequency: its S at 50 Ω and its chain correlation matrix C_A.</summary>
public sealed record ChainTwoPort(Mat<Complex> S, Mat<Complex> Ca);

/// <summary>A cascade of parts, each a list of two-ports: after each part, the cascade so far, its S between 50 Ω
/// terminations and its noise factor from a 50 Ω source; and each part's share of the whole one's F − 1.</summary>
public sealed record ChainCascade(IReadOnlyList<Mat<Complex>> S, IReadOnlyList<double> NoiseFactor, IReadOnlyList<double> Terms)
{
    /// <summary>The transducer gain of the parts up to and including <paramref name="k"/>, |S21|² between 50 Ω.</summary>
    public double GainAt(int k) => S[k][1, 0].Magnitude * S[k][1, 0].Magnitude;

    /// <summary>The whole cascade's transducer gain.</summary>
    public double Gain => GainAt(S.Count - 1);
}

public static class ChainMismatch
{
    private const double Z0 = ChainSnp.Z0;
    private static double KT0 => ChainBudget.K * ChainBudget.T0;

    /// <summary>
    /// The two-ports of a resolved stage read at <paramref name="fHz"/> (its input frequency) with budget
    /// <paramref name="b"/> there: one, or two for an SnP stage whose stated NF or intercept the benches place as an ideal
    /// matched amplifier of 0 dB after the file (<paramref name="snp"/>, the stage read at its carrier), which also stops
    /// what comes back from the stage after it. Null when the stage has no transmission there.
    /// </summary>
    public static IReadOnlyList<ChainTwoPort>? Of(ChainStage s, StageBudget b, double fHz, string? baseDirectory, SnpStageRead? snp = null)
    {
        switch (s.Part)
        {
            case ChainParts.Filter:
            {
                var (s11, s21, s22) = ChainLevels.FilterOf(s).At(2 * Math.PI * fHz);
                return One(Bosma(S(s11, s21, s21, s22)));
            }
            case ChainParts.Snp:
            {
                if (ChainSnp.FileAt(s, baseDirectory, fHz) is not { } a) return null;
                ChainTwoPort? file = a.Noise is { } p ? new(a.S, NoiseMetrics.ChainCorrelation(p))
                                   : a.Passive ? Bosma(a.S) : new(a.S, new Mat<Complex>(2, 2));
                if (file is null) return null;
                return snp is { NeedsAmp: true } ? [file, Unilateral(1, snp.AmpNoiseFactor)] : [file];
            }
            case ChainParts.Atten or ChainParts.Switch or ChainParts.Coupler or ChainParts.Splitter:
            {
                double t = Math.Pow(10, b.GainDb / 20);
                return One(Bosma(S(0, t, t, 0)));
            }
            case ChainParts.Circulator:
                return One(Bosma(S(0, 0, Math.Pow(10, b.GainDb / 20), 0)));
            default:
                return double.IsFinite(b.NoiseGainDb) && double.IsFinite(b.NfDb)
                    ? [Unilateral(Math.Pow(10, b.NoiseGainDb / 20), Math.Pow(10, b.NfDb / 10))] : null;
        }
    }

    /// <summary>The isolator a bench places in front of a mixer: lossless, matched, its terminated port's noise coming
    /// out of its input.</summary>
    public static ChainTwoPort Isolator { get; } = Bosma(S(0, 0, 1, 0))!;

    private static IReadOnlyList<ChainTwoPort>? One(ChainTwoPort? t) => t is null ? null : [t];

    private static Mat<Complex> S(Complex s11, Complex s12, Complex s21, Complex s22)
    {
        var m = new Mat<Complex>(2, 2);
        m[0, 0] = s11; m[0, 1] = s12; m[1, 0] = s21; m[1, 1] = s22;
        return m;
    }

    /// <summary>A matched unilateral two-port of voltage gain <paramref name="t"/> whose noise is one wave of
    /// T0·(F − 1) into its input: Fmin = F at Zopt = 50 Ω, Rn = (F − 1)·50/4.</summary>
    private static ChainTwoPort Unilateral(double t, double f)
        => new(S(0, 0, t, 0), f > 1 ? NoiseMetrics.ChainCorrelation(new TwoPortNoise(f, 0, (f - 1) * Z0 / 4, Z0)) : new Mat<Complex>(2, 2));

    /// <summary>A passive two-port at T0: its noise waves Cs = kT0·(I − S·Sᴴ) (Bosma), as C_A through its admittance.
    /// None (a lossless one) is a zero C_A; null when it has no admittance matrix to carry noise through.</summary>
    private static ChainTwoPort? Bosma(Mat<Complex> s)
    {
        var ssh = s * s.ConjugateTranspose();
        var cs = S(KT0 * (1 - ssh[0, 0]), -KT0 * ssh[0, 1], -KT0 * ssh[1, 0], KT0 * (1 - ssh[1, 1]));
        if (Enumerable.Range(0, 4).All(k => cs[k / 2, k % 2].Magnitude <= 1e-15 * KT0)) return new(s, new Mat<Complex>(2, 2));
        Complex[] z0 = [Z0, Z0];
        var y = NoiseMetrics.PowerWaveAdmittance(s, z0);
        var ca = NoiseMetrics.ChainFromAdmittance(NoiseMetrics.AdmittanceCorrelation(s, cs, z0), y);
        return Enumerable.Range(0, 4).All(k => double.IsFinite(ca[k / 2, k % 2].Real) && double.IsFinite(ca[k / 2, k % 2].Imaginary))
            ? new(s, ca) : null;
    }

    /// <summary>ABCD from S at a real Z0 (Pozar, Table 4.2).</summary>
    private static Mat<Complex> Abcd(Mat<Complex> s)
    {
        Complex s11 = s[0, 0], s12 = s[0, 1], s21 = s[1, 0], s22 = s[1, 1], d = 2 * s21;
        var m = new Mat<Complex>(2, 2);
        m[0, 0] = ((1 + s11) * (1 - s22) + s12 * s21) / d;
        m[0, 1] = Z0 * ((1 + s11) * (1 + s22) - s12 * s21) / d;
        m[1, 0] = ((1 - s11) * (1 - s22) - s12 * s21) / (d * Z0);
        m[1, 1] = ((1 - s11) * (1 + s22) + s12 * s21) / d;
        return m;
    }

    /// <summary>S at a real Z0 from ABCD (Pozar, Table 4.2).</summary>
    private static Mat<Complex> FromAbcd(Mat<Complex> m)
    {
        Complex a = m[0, 0], b = m[0, 1] / Z0, c = m[1, 0] * Z0, d = m[1, 1], den = a + b + c + d;
        return S((a + b - c - d) / den, 2 * (m[0, 0] * m[1, 1] - m[0, 1] * m[1, 0]) / den, 2 / den, (-a + b - c + d) / den);
    }

    /// <summary>F − 1 of a chain correlation matrix from a 50 Ω source.</summary>
    private static double Excess(Mat<Complex> ca)
    {
        const double ys = 1 / Z0;
        return (ca[0, 0].Real * ys * ys + 2 * (ys * ca[0, 1]).Real + ca[1, 1].Real) / (4 * KT0 * ys);
    }

    /// <summary>The cascade of <paramref name="parts"/>, each a list of two-ports in order (a stage's), its noise from a
    /// 50 Ω source and each part's share of F − 1. Null when a two-port has no transmission.</summary>
    public static ChainCascade? Cascade(IEnumerable<IReadOnlyList<ChainTwoPort>> parts)
    {
        var acc = S(1, 0, 0, 1);
        var ca = new Mat<Complex>(2, 2);
        var terms = new List<double>();
        var ss = new List<Mat<Complex>>();
        var fs = new List<double>();
        foreach (var part in parts)
        {
            double term = 0;
            foreach (var t in part)
            {
                if (!(t.S[1, 0].Magnitude > 0)) return null;
                var c = acc * t.Ca * acc.ConjugateTranspose();
                term += Excess(c);
                ca += c;
                acc *= Abcd(t.S);
            }
            terms.Add(term);
            ss.Add(FromAbcd(acc));
            fs.Add(1 + Excess(ca));
        }
        return new ChainCascade(ss, fs, terms);
    }
}
