using System.Numerics;

namespace CircuitRF.Core.Devices;

/// <summary>Which compression law an ideal amplifier follows (<c>Law=</c>).</summary>
public enum AmplifierLawKind
{
    /// <summary>The soft limiter on the instantaneous input, <see cref="ThirdOrderLimiter"/>: one intercept sets IM3,
    /// compression and saturation together. The default, and every amplifier written before the others existed.</summary>
    Tanh,

    /// <summary>Rapp's solid-state amplifier: a saturated output and a smoothness p, AM-AM only.</summary>
    Rapp,

    /// <summary>Saleh's travelling-wave tube: an AM-AM that peaks at the saturated output, and an AM-PM.</summary>
    Saleh,
}

/// <summary>
/// A bandpass compression law and its closed forms, shared by the amplifier that simulates it
/// (<see cref="AmplifierModel"/>) and the chain budget that predicts it, so the two cannot disagree.
///
/// <para><b>A bandpass law acts on the envelope, not on the instantaneous voltage.</b> Rapp's and Saleh's laws are
/// stated for a narrowband signal as an output amplitude A(r) and phase Φ(r) against the input envelope r. Written as a
/// complex gain on the analytic signal x + j·x̂, x̂ the Hilbert transform of the input,</para>
/// <code>
///   u = Re{ F(s)·(x + j·x̂) },     s = (x² + x̂²)·κ          (κ: volts² to available input power)
///   F(s) = A(r)·e^(jΦ(r)) / r,     normalised so that F(0) = 1, the small-signal gain being the amplifier's own
/// </code>
/// <para>a single tone has a constant envelope, so its output is the law at that drive exactly and nothing else: no
/// harmonics, which a bandpass law does not define. Two tones beat, and the products are the law's own intermodulation.
/// The amplifier reads x̂ from a third port that a linear companion holds at the input's Hilbert transform
/// (<see cref="AmplifierQuadratureModel"/>), so every analysis sees it at every frequency it solves at.</para>
///
/// <para><b>The laws</b>, s the input's available power and v = s/s_sat:</para>
/// <code>
///   Rapp    F = (1 + (s/P0)^p)^(−1/(2p))              P0 = Psat/G, the input power the output saturates from
///   Saleh   F = e^(jΦ(v)) / (1 + v),  Φ = a·v/(1 + b·v)   s_sat = 4·Psat/G, where the output peaks at Psat
/// </code>
/// <para>Rapp's is the AM-AM of C. Rapp, "Effects of HPA-nonlinearity on 4-DPSK/OFDM-signal for a digital sound
/// broadcasting system", Proc. 2nd European Conf. on Satellite Communications, 1991. Saleh's are the amplitude and phase
/// laws of A. A. M. Saleh, "Frequency-independent and frequency-dependent nonlinear models of TWT amplifiers", IEEE
/// Trans. Communications, vol. 29, no. 11, 1981: A(r) = α_a·r/(1 + β_a·r²), Φ(r) = α_φ·r²/(1 + β_φ·r²), here with
/// α_a the amplifier's gain, β_a from Psat, and the phase stated by its value at saturation and its limit.</para>
///
/// <para><b>The closed forms</b> (input-referred, dBm unless said):</para>
/// <code>
///   Rapp    IP1dB = Psat − G + (10/p)·log10(10^(p/10) − 1)        Psat at the output, its asymptote
///   Saleh   IP1dB = Psat − G + 6.0206 + 10·log10(10^(1/20) − 1)    Psat at the output, the AM-AM's peak
///           IIP3  = Psat − G + 6.0206 − 5·log10(1 + a²)             the third-order term, AM-PM included (a in rad)
///   Rapp    IIP3  = IP1dB + 10·log10(1/(1 − 10^(−1/20)))           the cubic with the same 1 dB point, IP1dB + 9.64 dB
/// </code>
/// <para>Rapp has no third-order term at p ≠ 1 — its IM3 rises 2p + 1 dB per dB at small drive — so the intercept it is
/// given is the cubic's that compresses where it does. At p = 1 its own third-order term is 0.76 dB below that.</para>
/// </summary>
public sealed class AmplifierLaw
{
    /// <summary>At or above this saturated output power (dBm) the law is off and the amplifier exactly linear, as the
    /// tanh law is at an intercept of 190 dBm.</summary>
    public const double OffDbm = 190.0;

    /// <summary>The tanh limiter's input 1 dB compression point below its input intercept, dB
    /// (<see cref="ThirdOrderLimiter"/>).</summary>
    public const double TanhP1dbBelowIip3Db = 8.9625;

    /// <summary>The cubic's input intercept above its input 1 dB compression point, dB: 10·log10(1/(1 − 10^(−1/20))).</summary>
    public static readonly double CubicIip3AboveP1dbDb = -10 * Math.Log10(1 - Math.Pow(10, -1.0 / 20));

    public AmplifierLawKind Kind { get; }

    /// <summary>The input power the law is scaled by, W: Rapp's P0 = Psat/G, Saleh's s_sat = 4·Psat/G.</summary>
    public double SatInW { get; }

    /// <summary>Rapp's smoothness p.</summary>
    public double P { get; }

    /// <summary>Saleh's AM-PM, Φ(v) = A·v/(1 + B·v), v = s/s_sat, A in radians.</summary>
    public double A { get; }
    public double B { get; }

    private AmplifierLaw(AmplifierLawKind kind, double satInW, double p, double a, double b)
        => (Kind, SatInW, P, A, B) = (kind, satInW, p, a, b);

    private static double W(double dbm) => 1e-3 * Math.Pow(10, dbm / 10);

    /// <summary>Rapp with an output saturation of <paramref name="psatDbm"/> at a gain of <paramref name="gainDb"/>.</summary>
    public static AmplifierLaw Rapp(double psatDbm, double gainDb, double p)
    {
        if (!(p > 0) || !double.IsFinite(p))
            throw new InvalidOperationException($"Amp: a Rapp law's smoothness P must be above 0; it is {p}.");
        return new(AmplifierLawKind.Rapp, W(psatDbm - gainDb), p, 0, 0);
    }

    /// <summary>
    /// Saleh with its AM-AM peaking at <paramref name="psatDbm"/> out, and its AM-PM <paramref name="phiSatDeg"/> at that
    /// drive and <paramref name="phiMaxDeg"/> in the limit. Both zero: no AM-PM. Otherwise the limit is beyond the value
    /// at saturation, on the same side of zero, which is what Saleh's α_φ·r²/(1 + β_φ·r²) can be.
    /// </summary>
    public static AmplifierLaw Saleh(double psatDbm, double gainDb, double phiSatDeg, double phiMaxDeg)
    {
        if (SalehPhaseRefusal(phiSatDeg, phiMaxDeg) is { } why) throw new InvalidOperationException("Amp: " + why);
        double b = phiSatDeg == 0 ? 0 : phiSatDeg / (phiMaxDeg - phiSatDeg);
        double a = phiSatDeg == 0 ? 0 : phiMaxDeg * b * Math.PI / 180;
        return new(AmplifierLawKind.Saleh, 4 * W(psatDbm - gainDb), 0, a, b);
    }

    /// <summary>Why a Saleh AM-PM cannot be written in Saleh's form, or null.</summary>
    public static string? SalehPhaseRefusal(double phiSatDeg, double phiMaxDeg)
    {
        if (!double.IsFinite(phiSatDeg) || !double.IsFinite(phiMaxDeg)) return "PhiSat and PhiMax are numbers of degrees.";
        if (phiSatDeg == 0) return phiMaxDeg == 0 ? null : "PhiSat is 0, so there is no AM-PM, and PhiMax must be 0 too.";
        return Math.Sign(phiMaxDeg) == Math.Sign(phiSatDeg) && Math.Abs(phiMaxDeg) > Math.Abs(phiSatDeg)
            ? null
            : $"Saleh's AM-PM rises toward its limit PhiMax, so PhiMax is beyond PhiSat on the same side of 0; PhiSat is {phiSatDeg}° and PhiMax {phiMaxDeg}°.";
    }

    /// <summary>
    /// The complex gain relative to small signal, F(s), at an available input power of <paramref name="s"/> W, and
    /// D = s·dF/ds, which stays finite at s = 0 for every p &gt; 0 where dF/ds itself need not.
    /// </summary>
    public Complex Gain(double s, out Complex d)
    {
        if (!(s > 0)) { d = Complex.Zero; return Complex.One; }
        if (Kind == AmplifierLawKind.Rapp)
        {
            double q = Math.Pow(s / SatInW, P);
            double f = Math.Pow(1 + q, -1 / (2 * P));
            d = new Complex(-0.5 * q * f / (1 + q), 0);
            return new Complex(f, 0);
        }
        double v = s / SatInW, den = 1 + B * v;
        double phi = A * v / den, dphi = A / (den * den);       // dΦ/dv
        var rot = Complex.FromPolarCoordinates(1, phi);
        var g = rot / (1 + v);
        d = v * rot * (new Complex(0, dphi) / (1 + v) - 1 / ((1 + v) * (1 + v)));
        return g;
    }

    // ── the closed forms the budget uses ──────────────────────────────────────

    private static double Db(double x) => 10 * Math.Log10(x);
    private double SatInDbm => Db(SatInW) + 30;

    /// <summary>The input 1 dB compression point, dBm.</summary>
    public double Ip1dbDbm => Kind == AmplifierLawKind.Rapp
        ? SatInDbm + 10 / P * Math.Log10(Math.Pow(10, P / 10) - 1)
        : SatInDbm + Db(Math.Pow(10, 1.0 / 20) - 1);

    /// <summary>The input third-order intercept the budget uses, dBm: Saleh's own third-order term with its AM-PM,
    /// Rapp's cubic equivalent.</summary>
    public double Iip3Dbm => Kind == AmplifierLawKind.Rapp
        ? Ip1dbDbm + CubicIip3AboveP1dbDb
        : SatInDbm - 5 * Math.Log10(1 + A * A);

    /// <summary>The saturated output at a small-signal gain of <paramref name="gainDb"/>, dBm: Rapp's asymptote, Saleh's
    /// peak, a quarter of s_sat·G.</summary>
    public double PsatDbm(double gainDb) => SatInDbm + gainDb - (Kind == AmplifierLawKind.Saleh ? Db(4) : 0);

    /// <summary>The CW output at an input of <paramref name="pinDbm"/>, dBm, at a small-signal gain of
    /// <paramref name="gainDb"/>: G·s·|F(s)|².</summary>
    public double OutputDbm(double pinDbm, double gainDb)
    {
        double s = W(pinDbm);
        return pinDbm + gainDb + 2 * 10 * Math.Log10(Gain(s, out _).Magnitude);
    }

    /// <summary>The AM-PM at an input of <paramref name="pinDbm"/>, degrees.</summary>
    public double AmPmDeg(double pinDbm) => Gain(W(pinDbm), out _).Phase * 180 / Math.PI;
}
