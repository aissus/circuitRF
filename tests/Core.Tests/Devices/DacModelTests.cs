using System.Numerics;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Expressions;
using Xunit;

namespace CircuitRF.Core.Tests.Devices;

/// <summary>
/// The DAC's laws against their closed forms: the quantisation floor of a full-scale sine, 6.02·N + 1.76 dB (Bennett,
/// "Spectra of quantized signals", 1948); the hold's droop and phase, d·sinc(d·f/Fs)·e^(−jπ·d·f/Fs), and its images at
/// k·Fs ± f, NRZ and RZ; and a sine clipped at full scale, whose fundamental is that of a clipped sine. Each tone sits on
/// a bin of a record whose length shares no factor with it, so its quantisation error is busy rather than periodic.
/// </summary>
public class DacModelTests
{
    private const int L = 8191;          // samples at Fs over the record: prime, so every tone is coprime with it
    private const double Fs = 100e6, T = L / Fs;

    private static DacModel Dac(int bits, DacPulse pulse = DacPulse.Nrz, double duty = 1, double fs = 1.0)
        => new(bits, fs, Fs, pulse, duty, gainDb: 0, zIn: 50, zOut: 50);

    private static double[] Tone(int bin, double amplitude, double phase = 0.3)
        => [.. Enumerable.Range(0, L).Select(n => amplitude * Math.Cos(2 * Math.PI * bin * n / L + phase))];

    /// <summary>The complex amplitude of bin <paramref name="m"/> of a real record: A·e^(jφ) for A·cos(…m… + φ).</summary>
    private static Complex Bin(double[] x, int m)
    {
        Complex acc = 0;
        for (int n = 0; n < x.Length; n++) acc += x[n] * Complex.FromPolarCoordinates(1, -2 * Math.PI * (long)m * n / x.Length);
        return 2.0 * acc / x.Length;
    }

    [Theory]
    [InlineData(6)]
    [InlineData(8)]
    [InlineData(10)]
    [InlineData(12)]
    [InlineData(14)]
    public void AFullScaleSine_QuantisesTo602NPlus176Decibels(int bits)
    {
        var dac = Dac(bits);
        var x = Tone(1234, dac.FullScale);
        double ps = 0, pe = 0;
        foreach (double v in x) { double e = dac.Quantise(v) - v; ps += v * v; pe += e * e; }
        double snr = 10 * Math.Log10(ps / pe);
        Assert.InRange(snr, 6.02 * bits + 1.76 - 0.3, 6.02 * bits + 1.76 + 0.3);   // 6 bits reads 0.27 dB under: Δ is coarse there
    }

    /// <summary>A signal backed off from full scale loses exactly its back-off: SNR = 6.02·N + 4.77 − 20·log10(FS/σ).</summary>
    [Fact]
    public void ABackedOffSine_LosesItsBackOff()
    {
        var dac = Dac(12);
        double a = dac.FullScale / Math.Pow(10, 12.0 / 20);          // 12 dB below full scale
        var x = Tone(1234, a);
        double ps = 0, pe = 0;
        foreach (double v in x) { double e = dac.Quantise(v) - v; ps += v * v; pe += e * e; }
        double bo = 20 * Math.Log10(dac.FullScale / (a / Math.Sqrt(2)));
        Assert.InRange(10 * Math.Log10(ps / pe), 6.02 * 12 + 4.77 - bo - 0.2, 6.02 * 12 + 4.77 - bo + 0.2);
    }

    /// <summary>
    /// The hold's response at the tone and at its first two images, out of the converted record at eight times Fs: the
    /// tone at d·sinc(d·f/Fs) and the phase −π·d·f/Fs, the images at |H(Fs ∓ f)| — for NRZ the first is f/(Fs − f) of
    /// the tone. Thirty-two bits make the quantisation 190 dB down.
    /// </summary>
    [Theory]
    [InlineData(DacPulse.Nrz, 1.0, 1000)]
    [InlineData(DacPulse.Nrz, 1.0, 3000)]
    [InlineData(DacPulse.Rz, 0.5, 1000)]
    [InlineData(DacPulse.Rz, 0.25, 3000)]
    public void TheHold_DroopsAsItsSinc_AndPutsItsImagesAtKFsPlusMinusF(DacPulse pulse, double duty, int m)
    {
        var dac = Dac(32, pulse, duty);
        var y = dac.Convert(Tone(m, 0.5, phase: 0), T, 8 * L, 4 * Fs);
        double f = m / T;
        Complex Expect(double hz) => 0.5 * dac.PulseResponse(hz);
        var tone = Bin(y, m);
        Assert.Equal(0.5 * duty * Math.Sin(Math.PI * duty * f / Fs) / (Math.PI * duty * f / Fs), tone.Magnitude, 9);
        Assert.Equal(-Math.PI * duty * f / Fs, tone.Phase, 9);
        Assert.Equal(Expect(Fs - f).Magnitude, Bin(y, L - m).Magnitude, 9);
        Assert.Equal(Expect(Fs + f).Magnitude, Bin(y, L + m).Magnitude, 9);
        if (pulse == DacPulse.Nrz)
            Assert.Equal(f / (Fs - f), Bin(y, L - m).Magnitude / tone.Magnitude, 9);
    }

    /// <summary>RZ halves the tone (d = 0.5) and raises the first image against it, as its wider sinc says.</summary>
    [Fact]
    public void ReturnToZero_TradesInBandLevelForStrongerImages()
    {
        double f = 1000 / T;
        var nrz = Dac(32);
        var rz = Dac(32, DacPulse.Rz, 0.5);
        double Image(DacModel d) => d.PulseResponse(Fs - f).Magnitude / d.PulseResponse(f).Magnitude;
        Assert.Equal(0.5, rz.PulseResponse(0).Magnitude, 12);
        Assert.True(Image(rz) > 1.5 * Image(nrz));
    }

    /// <summary>
    /// A sine of twice full scale is held at the end codes ±(FS − Δ/2), and its fundamental is a clipped sine's:
    /// (2A/π)·(asin(c/A) + (c/A)·√(1 − (c/A)²)) at the clip level c.
    /// </summary>
    [Fact]
    public void ASineBeyondFullScale_ClipsAtTheEndCodes()
    {
        var dac = Dac(20);
        double a = 2 * dac.FullScale, c = dac.FullScale - dac.Lsb / 2;
        var x = Tone(1234, a);
        var q = x.Select(dac.Quantise).ToArray();
        Assert.Equal(c, q.Max(), 12);
        Assert.Equal(-c, q.Min(), 12);
        double r = c / a;
        double a1 = 2 * a / Math.PI * (Math.Asin(r) + r * Math.Sqrt(1 - r * r));
        Assert.Equal(a1, Bin(q, 1234).Magnitude, 5);
    }

    [Fact]
    public void TheFactory_ReadsEveryParameter_AndRefusesWhatTheBlockCannotBe()
    {
        Dictionary<string, Value> P(params (string K, Value V)[] kv) => kv.ToDictionary(x => x.K, x => x.V);
        var dac = Assert.IsType<DacModel>(ComponentModelFactory.TryCreate("DAC",
            P(("Bits", new(10.0)), ("FS", new(0.5)), ("Fs", new(61.44e6)), ("Mode", new("rz")), ("Duty", new(0.25))), null, 25));
        Assert.Equal((10, 0.5, 61.44e6, DacPulse.Rz, 0.25), (dac.Bits, dac.FullScale, dac.SampleRateHz, dac.Pulse, dac.Duty));
        Assert.Equal(1.0, Assert.IsType<DacModel>(ComponentModelFactory.TryCreate("DAC", P(("Fs", new(1e8))), null, 25)).Duty);

        string Refusal(params (string, Value)[] kv) =>
            Assert.Throws<ParameterRefusalException>(() => ComponentModelFactory.TryCreate("DAC", P(kv), null, 25)).Message;
        Assert.Contains("Fs", Refusal(("Bits", new(8.0))));
        Assert.Contains("Bits", Refusal(("Fs", new(1e8)), ("Bits", new(0.0))));
        Assert.Contains("Bits", Refusal(("Fs", new(1e8)), ("Bits", new(8.5))));
        Assert.Contains("nrz or rz", Refusal(("Fs", new(1e8)), ("Mode", new("hold"))));
        Assert.Contains("Duty", Refusal(("Fs", new(1e8)), ("Mode", new("rz")), ("Duty", new(0.0))));
        Assert.Contains("FS", Refusal(("Fs", new(1e8)), ("FS", new(-1.0))));
    }

    /// <summary>A MOD_Source plays one rail at baseband with Baseband=i|q, and states one share of its signal only.</summary>
    [Fact]
    public void AModSource_PlaysARailAtBaseband()
    {
        Dictionary<string, Value> Src(string key, string word) => new()
        {
            ["Fc"] = new(1e9), ["Pavg"] = new(0.0), ["Carrier[1]"] = new("LTE;BW=1.4MHz;Off=0;P=0"), [key] = new(word),
        };
        var q = ModSourceModel.Create(Src("Baseband", "q"));
        Assert.Equal((IqRail?)IqRail.Q, q.Baseband);
        Assert.Equal(0, q.EnvelopeProduct);
        Assert.Contains("i or q", Assert.Throws<ParameterRefusalException>(() => ModSourceModel.Create(Src("Baseband", "x"))).Message);
        var both = Src("Baseband", "i");
        both["Polar"] = new("amp");
        Assert.Contains("both Polar and Baseband", Assert.Throws<ParameterRefusalException>(() => ModSourceModel.Create(both)).Message);
    }
}
