// What a transmit chain's budget reads as: its output. The cascade is the receive chain's, the same rows; these are read
// from it at the output.
//
//   Pout        Pin + G                                        the linear budget's output, as every row has it
//   Pout, CW    the carrier through every stage's AM-AM        a bandpass law's own (AmplifierLaw); every other stage that
//                                                              compresses as the tanh limiter of its intercept does, its
//                                                              describing function N(u) = (2/πu)·∫₀^π tanh(u·cos θ)·cos θ dθ,
//                                                              u = 2·√(P/IIP3)
//   OP1dB       the cascade's, IP1dB + G − 1                    back-off from it: OP1dB − Pout
//   Psat        the output's ceiling: P_k = min(P_(k−1) + g_k, Psat_k) through the stages, Psat_k a law's own or a tanh
//               stage's OIP3 − 10·log10(π²/4)                   back-off from it: Psat − Pout
//   ACLR        2·(Pout − OIP3) − 10·log10(3)                  dBc, the adjacent channel's power over the channel's
//   noise       10·log10(k·(Ts + Te)·Gn) + 30                  dBm/Hz at the output, in band
//   RX band     the same sum at the band's frequency, each stage's gain there (an SnP file's |S21|), T ← (T + T0·(F − 1))·g;
//               a filter its own response, passing |S21|² and adding T0·(1 − |S21|² − |S22|²) (Bosma: in its stopband it
//               reflects, and adds almost nothing); then 10·log10(k·T·B) + 30
//   image, LO   an IQ modulator's (IqModSpec), each carried to the output at its own frequency, dBc
//
// The ACLR relation: a memoryless bandpass nonlinearity to third order, y = a1·x + a3·x·|x|², driven by a complex
// Gaussian signal x of power P and a flat spectrum of width B. Its third-order output is correlated with x in part
// (a gain change, 2·a3·P·x, by E|x|⁴ = 2P²) and the rest, of power |a3|²·(E|x|⁶ − 4P³) = 2·|a3|²·P³ (E|x|⁶ = 6P³, by
// Isserlis' theorem, Reed's for a complex Gaussian), spreads as the threefold convolution of the flat spectrum over 3B: 2/3 of it in the channel and
// 1/6 in each neighbour of the same width, centred B away. The two-tone intercept of the same nonlinearity is
// IIP3 = |a1/a3| per tone. So the adjacent channel holds |a3|²·P³/3 against |a1|²·P in the channel:
// ACLR = (P/IIP3)²/3 = 2·(Pout − OIP3) − 4.77 dB. It assumes third order dominates (a back-off well under compression),
// a noise-like signal (OFDM is one, by the central limit theorem) and every stage's IM3 adding in phase, as the
// cascade's intercept does; a Rapp stage's intercept is the cubic's with its 1 dB point, which is optimistic for a
// carrier whose peaks reach past that point (Rapp clips harder than the cubic there). It is an estimate, and says so.

using RfCore.Data;

namespace CircuitRF.Design.Chain;

/// <summary>A transmit chain's output readouts; dBm, dB, dBc and dBm/Hz.</summary>
public sealed record ChainTxReadout(
    double PoutDbm, double PoutCwDbm, double CompressionDb,
    double Op1dbDbm, double BackoffP1dbDb, double PsatDbm, double BackoffPsatDb,
    double Oip3Dbm, double AclrDbc, double NoisePsdDbmHz,
    double RxFrequencyHz, double RxNoisePsdDbmHz, double RxNoiseDbm,
    double ImageDbc, double LoLeakDbc)
{
    /// <summary>Whether the intercept the ACLR is estimated from takes a Rapp stage's as the cubic's at its 1 dB point.</summary>
    public bool CubicEquivalent { get; init; }
}

public static class ChainTransmit
{
    private static double Lin(double db) => Math.Pow(10, db / 10);
    private static double Db(double x) => 10 * Math.Log10(x);

    /// <summary>10·log10(3): the ACLR relation's constant, the adjacent channel holding 1/6 of 2·|a3|²·P³.</summary>
    public static readonly double AclrConstantDb = 10 * Math.Log10(3);

    /// <summary>The tanh limiter's saturated output below its output intercept, dB: 10·log10(π²/4).</summary>
    public static readonly double TanhPsatBelowOip3Db = 10 * Math.Log10(Math.PI * Math.PI / 4);

    /// <summary>
    /// The tanh limiter's gain change at an input of <paramref name="pinDbm"/> against its input intercept
    /// <paramref name="iip3Dbm"/>, dB: 20·log10 N(u), its describing function at u = 2·√(P/IIP3). The integrand is
    /// periodic and analytic, so the trapezoid rule over a whole period converges geometrically; 4096 points hold it to
    /// round-off up to 40 dB past the intercept.
    /// </summary>
    public static double TanhCompressionDb(double pinDbm, double iip3Dbm)
    {
        if (!double.IsFinite(iip3Dbm)) return 0;
        double u = 2 * Math.Sqrt(Lin(pinDbm - iip3Dbm));
        if (u < 1e-6) return 0;
        const int m = 4096;
        double sum = 0;
        for (int k = 0; k < m; k++)
        {
            double th = 2 * Math.PI * k / m, c = Math.Cos(th);
            sum += Math.Tanh(u * c) * c;
        }
        return 20 * Math.Log10(2 * sum / m / u);
    }

    /// <summary>A stage's CW output for <paramref name="pinDbm"/> at its input, dBm: its law's, or its tanh limiter's.</summary>
    public static double StageCwDbm(StageBudget b, double pinDbm)
        => b.Law is { } law ? law.OutputDbm(pinDbm, b.GainDb) : pinDbm + b.GainDb + TanhCompressionDb(pinDbm, b.Iip3Dbm);

    /// <summary>A stage's own saturated output, dBm: its law's Psat, a tanh stage's OIP3 − 10·log10(π²/4); +∞ for a
    /// linear one.</summary>
    public static double StagePsatDbm(StageBudget b) =>
        b.Law?.PsatDbm(b.GainDb) ?? (double.IsFinite(b.Iip3Dbm) ? b.Iip3Dbm + b.GainDb - TanhPsatBelowOip3Db : double.PositiveInfinity);

    /// <summary>The readouts of a transmit chain's budget; null for a receive chain or a refused budget.</summary>
    public static ChainTxReadout? Read(ChainBudgetResult r)
    {
        if (r.Totals is not { } t || r.Design is not { IsTransmit: true } d) return null;
        double pin = d.InputPowerDbm, cw = pin, psat = double.PositiveInfinity;
        foreach (var row in r.Stages)
        {
            cw = StageCwDbm(row.Stage, cw);
            psat = Math.Min(psat + row.Stage.GainDb, StagePsatDbm(row.Stage));
        }
        double pout = t.PoutDbm;
        double aclr = double.IsFinite(t.Oip3Dbm) ? 2 * (pout - t.Oip3Dbm) - AclrConstantDb : double.NegativeInfinity;
        double psd = t.NoiseOutDbm - Db(d.BandwidthHz);

        double rxHz = double.NaN, rxPsd = double.NaN, rxN = double.NaN;
        if (d.RxBandOffsetHz is { } off)
        {
            (rxHz, double tout) = NoiseAt(d, r, off);
            rxPsd = Db(ChainBudget.K * tout) + 30;
            rxN = rxPsd + Db(d.RxBandHz);
        }
        var (image, lo) = Spurs(d, r);
        return new ChainTxReadout(pout, cw, pout - cw, t.Op1dbDbm, t.Op1dbDbm - pout, psat, psat - pout,
                                  t.Oip3Dbm, aclr, psd, rxHz, rxPsd, rxN, image - pout, lo - pout)
        {
            CubicEquivalent = r.Stages.Any(x => x.Stage.Law?.Kind == Core.Devices.AmplifierLawKind.Rapp),
        };
    }

    /// <summary>The input frequency whose output is <paramref name="offsetHz"/> from the carrier's, and the output noise
    /// temperature there, K: the Friis sum walked with each stage's gain at its own frequency.</summary>
    private static (double OutputHz, double TK) NoiseAt(ChainDesign d, ChainBudgetResult r, double offsetHz)
    {
        // The output frequency is the input's moved by ±1 through each converter; the band's input frequency is the
        // carrier's moved the same way.
        double Out(double f) { foreach (var s in d.Stages) f = ChainParts.OutputHz(s, f); return f; }
        double slope = Math.Sign(Out(d.FrequencyHz + 1) - Out(d.FrequencyHz));
        double f = d.FrequencyHz + slope * offsetHz;
        double tk = d.TemperatureK;
        for (int i = 0; i < d.Stages.Count; i++)
        {
            var st = d.Stages[i];
            var b = r.Stages[i].Stage;
            if (st.Part == ChainParts.Filter)
            {
                // A filter between matched stages passes |S21|² of what arrives and adds its own thermal noise, which by
                // Bosma's theorem is T0·(1 − |S21|² − |S22|²): in its passband T0·(1 − g), its loss; in its stopband, where
                // it reflects, nearly none.
                var (_, s21, s22) = ChainLevels.FilterOf(st).At(2 * Math.PI * f);
                double g21 = s21.Magnitude * s21.Magnitude, g22 = s22.Magnitude * s22.Magnitude;
                tk = tk * g21 + ChainBudget.T0 * Math.Max(0, 1 - g21 - g22);
                continue;
            }
            // The gain noise sees: a Combiner's coherent signal gain is not its noise's.
            double g = Lin(ChainLevels.GainAt(st, b, ref f, d.BaseDirectory) - b.GainDb + b.NoiseGainDb);
            // Every other passive stage is matched at every frequency: its noise figure is its loss there.
            double fac = Math.Abs(b.NfDb + b.GainDb) < 1e-9 && b.GainDb <= 0 ? 1 / Math.Max(g, 1e-300) : Lin(b.NfDb);
            tk = (tk + ChainBudget.T0 * (fac - 1)) * g;
        }
        return (f, tk);
    }

    /// <summary>The first IQ modulator's image and LO at the output, dBm, each carried through the stages after it at
    /// its own frequency; −∞ for a chain without one, or a balanced, isolated one.</summary>
    private static (double ImageDbm, double LoDbm) Spurs(ChainDesign d, ChainBudgetResult r)
    {
        int iq = d.Stages.FindIndex(s => s.Part == ChainParts.IqMod);
        if (iq < 0) return (double.NegativeInfinity, double.NegativeInfinity);
        var st = d.Stages[iq];
        var spec = IqModSpec.Read(st);
        double fin = d.StageInputFrequencies()[iq], flo = ChainValue.Or(st.Get("Flo"), 0);
        double pin = iq > 0 ? r.Stages[iq - 1].PoutDbm : d.InputPowerDbm;
        double conv = ChainValue.Or(st.Get("ConvGain"), 0);
        double image = pin + conv + spec.ImageDb, lo = spec.LoLeakDbm;
        double fi = ChainParts.IsUpper(st) ? Math.Abs(flo - fin) : flo + fin, fl = flo;
        for (int i = iq + 1; i < d.Stages.Count; i++)
        {
            image += ChainLevels.GainAt(d.Stages[i], r.Stages[i].Stage, ref fi, d.BaseDirectory);
            lo += ChainLevels.GainAt(d.Stages[i], r.Stages[i].Stage, ref fl, d.BaseDirectory);
        }
        return (image, lo);
    }

    /// <summary>What a transmit budget says about its output: a drive past the chain's 1 dB compression point, and how
    /// far the carrier comes out under the linear budget there.</summary>
    public static IEnumerable<string> Notes(ChainBudgetResult r)
    {
        if (Read(r) is not { } x || !(x.BackoffP1dbDb < 0)) yield break;
        static string D(double v) => v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        yield return $"The output, {D(x.PoutDbm)} dBm by the linear budget, is past the chain's output P1dB of {D(x.Op1dbDbm)} dBm: " +
                     $"through each stage's compression a carrier comes out at {D(x.PoutCwDbm)} dBm, {D(x.CompressionDb)} dB under it.";
    }

    /// <summary>The readouts added to a budget's DataSet as scalars.</summary>
    public static void AddTo(DataSet ds, ChainBudgetResult r)
    {
        if (Read(r) is not { } x) return;
        void One(string name, string unit, double v) => ds.Add(name, new DataCube([], [double.IsFinite(v) ? v : double.NaN]) { Unit = unit });
        One("PoutCW_dBm", "dBm", x.PoutCwDbm);
        One("Compression_dB", "dB", x.CompressionDb);
        One("BackoffP1dB_dB", "dB", x.BackoffP1dbDb);
        One("Psat_dBm", "dBm", x.PsatDbm);
        One("BackoffPsat_dB", "dB", x.BackoffPsatDb);
        One("ACLR_dBc", "dBc", x.AclrDbc);
        One("NoisePSD_dBmHz", "dBm/Hz", x.NoisePsdDbmHz);
        One("RxFreq_Hz", "Hz", x.RxFrequencyHz);
        One("RxNoisePSD_dBmHz", "dBm/Hz", x.RxNoisePsdDbmHz);
        One("RxNoise_dBm", "dBm", x.RxNoiseDbm);
        One("Image_dBc", "dBc", x.ImageDbc);
        One("LoLeak_dBc", "dBc", x.LoLeakDbc);
    }
}
