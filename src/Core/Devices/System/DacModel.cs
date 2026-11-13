using System.Numerics;
using RfCore.Signals;

namespace CircuitRF.Core.Devices;

/// <summary>The DAC's output pulse: held for the whole sample period (NRZ), or for <c>Duty</c> of it and then zero (RZ).</summary>
public enum DacPulse
{
    Nrz,
    Rz,
}

/// <summary>
/// The digital-to-analog converter: a digital word stream in, its analog reconstruction out at baseband. Ports
/// <c>[in+, in−, out+, out−]</c>; the single-ended tile ties both returns to ground at extraction.
///
/// <code>
///   c[n]   = Q(v_in(n/Fs))      Q(v) = Δ·(clamp(⌊v/Δ⌋, −2^(N−1), 2^(N−1) − 1) + ½),   Δ = 2·FS / 2^N
///   out(t) = G · Σ_n c[n] · p(t − n/Fs)        p = 1 on [0, d/Fs), 0 after          (d = 1 NRZ, Duty RZ)
///   H(f)   = d · sinc(d·f/Fs) · e^(−jπ·d·f/Fs)                                        the pulse's response
/// </code>
/// <para><b>The quantiser</b> is the symmetric mid-rise one: 2^N levels at odd multiples of Δ/2 across ±FS, so an input
/// beyond ±FS is held at the end level ±(FS − Δ/2) (clipping), and over a busy signal the error is uniform in ±Δ/2,
/// Δ²/12 of power spread evenly from −Fs/2 to Fs/2 (Bennett, "Spectra of quantized signals", 1948). For a sine whose
/// peak is FS that is SNR = 6.02·N + 1.76 dB; for any signal of RMS σ it is 6.02·N + 4.77 − 20·log10(FS/σ) dB.</para>
///
/// <para><b>The hold</b>: the output spectrum is the stream's, periodic in Fs, times <c>H(f)</c>. In the Nyquist band the
/// signal droops by <c>|H(f)/H(0)|</c>, and every image <c>k·Fs ± f</c> is there at <c>|H(k·Fs ± f)|</c> — for NRZ the first
/// at <c>f/(Fs − f)</c> of the signal; RZ trades d of in-band level for images that fall more slowly.</para>
///
/// <para><b>Where it runs.</b> In the circuit envelope its input is a <see cref="ModSourceModel.Baseband"/> rail of the
/// Signal Designer's record and nothing else, and <see cref="Convert"/> turns that record into the output exactly, band
/// by band. Every other analysis sees the matched, unilateral through path, <c>S21 = G</c>: the S-parameter view; harmonic
/// balance has no stream to sample and refuses the block by name.</para>
/// </summary>
public sealed class DacModel : IdealSBlockModel
{
    private readonly Complex _gain;

    /// <param name="bits">Resolution N, 1 to 32.</param>
    /// <param name="fullScale">FS, volts peak at the input: the codes span ±FS.</param>
    /// <param name="sampleRateHz">Fs.</param>
    /// <param name="pulse">NRZ (zero-order hold) or RZ.</param>
    /// <param name="duty">The RZ pulse's share of the period; ignored for NRZ.</param>
    /// <param name="gainDb">G, from input volts to output volts into a matched load.</param>
    public DacModel(int bits, double fullScale, double sampleRateHz, DacPulse pulse, double duty, double gainDb,
                    double zIn, double zOut) : base([zIn, zOut])
    {
        Bits = bits;
        FullScale = fullScale;
        SampleRateHz = sampleRateHz;
        Pulse = pulse;
        Duty = pulse == DacPulse.Rz ? duty : 1.0;
        _gain = new Complex(AmplitudeFromDb(-gainDb), 0);
    }

    public int Bits { get; }
    public double FullScale { get; }
    public double SampleRateHz { get; }
    public DacPulse Pulse { get; }

    /// <summary>The pulse's share of the period, d: 1 for NRZ.</summary>
    public double Duty { get; }

    /// <summary>The input's resistance, ohms: what the stream's source drives.</summary>
    public double Zin => Z0Of(0).Real;

    /// <summary>Δ = 2·FS / 2^N, volts.</summary>
    public double Lsb => 2 * FullScale / Math.Pow(2, Bits);

    /// <summary>The level an input of <paramref name="v"/> volts is held at.</summary>
    public double Quantise(double v)
    {
        double top = Math.Pow(2, Bits - 1), lsb = Lsb;
        return lsb * (Math.Clamp(Math.Floor(v / lsb), -top, top - 1) + 0.5);
    }

    /// <summary>The pulse's response <c>H(f) = d·sinc(d·f/Fs)·e^(−jπ·d·f/Fs)</c>.</summary>
    public Complex PulseResponse(double fHz)
    {
        double x = Math.PI * Duty * fHz / SampleRateHz;
        double sinc = x == 0 ? 1 : Math.Sin(x) / x;
        return Complex.FromPolarCoordinates(Duty * sinc, -x);
    }

    /// <summary>
    /// The output a periodic stream makes: <paramref name="input"/> (volts at the input at n/Fs, one record of
    /// <paramref name="recordSeconds"/>) quantised and held, sampled at <paramref name="outputSamples"/> points over the same
    /// record and band-limited to |f| &lt; <paramref name="bandHz"/>. Exact on the periodic record: every bin of the output
    /// is the stream's own bin (its image, past ±Fs/2) times <c>H(f)</c>. Volts at the input's scale; a matched load reads
    /// G times this.
    /// </summary>
    public double[] Convert(ReadOnlySpan<double> input, double recordSeconds, int outputSamples, double bandHz)
    {
        int n = input.Length;
        var c = new Complex[n];
        for (int i = 0; i < n; i++) c[i] = Quantise(input[i]);
        var spectrum = Fft.Forward(c);
        var y = new Complex[outputSamples];
        double scale = (double)outputSamples / n;
        int half = (outputSamples - 1) / 2;                  // an even record's Nyquist bin is left out: it has no sign
        for (int m = -half; m <= half; m++)
        {
            double f = m / recordSeconds;
            if (Math.Abs(f) >= bandHz) continue;
            y[(m + outputSamples) % outputSamples] = scale * spectrum[((m % n) + n) % n] * PulseResponse(f);
        }
        var t = Fft.Inverse(y);
        var r = new double[outputSamples];
        for (int i = 0; i < outputSamples; i++) r[i] = t[i].Real;
        return r;
    }

    /// <summary>A converter: its output is a law of a stream, not a passive network.</summary>
    public override Activity Activity => Activity.ActiveExact;

    /// <inheritdoc/>
    public override string? PassivationNote => "forward gain → 0; matched terminations kept";

    /// <summary>Forward gain → 0, as the amplifier's: two matched terminations, nothing through.</summary>
    protected override void PassivateS(Complex[,] s) => s[1, 0] = Complex.Zero;

    public override string[] TerminalNames => ["in", "out"];

    /// <summary>Matched, unilateral, G through: S21 = G and nothing else.</summary>
    protected override void FillS(double omega, Complex[,] s) => s[1, 0] = _gain;
}
