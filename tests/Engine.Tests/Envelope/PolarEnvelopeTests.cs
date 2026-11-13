using System.Globalization;
using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine.Envelope;
using RfCore.Data;
using RfCore.Signals;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Envelope;

/// <summary>
/// A polar transmitter in the circuit envelope: a polar pair of MOD_Sources driving the baseband (k = 0) product beside
/// the carrier (k = 1), the System Delay stepped as its delay at both, the detector's pole, and the recombination's EVM
/// against the source's own and against the AM/PM delay mismatch.
/// </summary>
public sealed class PolarEnvelopeTests(ITestOutputHelper output)
{
    private static string N(double x) => x.ToString("R", CultureInfo.InvariantCulture);

    // 1.4 MHz: the narrowest LTE carrier, so a whole 0.5 ms record is ~3,800 draft steps and each run stays routine.
    private const string Lte = "Carrier[1]=\"LTE;BW=1.4MHz;Mod=64QAM;Off=0;P=0;Seed=3\" Record=0.5e-3";

    private static string Pair => $"""
        MOD_Source:SA a 0 Fc=1e9 Pavg=-10 Z=50 Polar=amp {Lte}
        MOD_Source:SP p 0 Fc=1e9 Pavg=-10 Z=50 Polar=phase {Lte}
        """;

    /// <summary>The chain: the amplitude half through a Delay into the supply; a carrier, limited, turned by the phase half
    /// into the RF input. Ks = 1 and matched ports put the source's own RF envelope across the load.</summary>
    private static string PolarTx(double tAm) => $"""
        {Pair}
        Delay:DA a 0 b 0 T={N(tAm)}
        P1Tone:LO c 0 Pavl=0 Z=50 Freq=1e9
        Limiter:L1 c 0 d 0 Pout=0
        PhaseMod:PM1 d 0 e 0 p 0 Kp=1
        SupplyAmp:PA e 0 f 0 b 0 Ks=1
        IProbe:Iout f g
        R:RL g 0 R=50 Ohm
        analysis ENV1 type=env Accuracy=draft MaxHarm=1 Segments=1 Warmup=1e-6 Stop=0.5e-3
        """;

    private static (DataSet ds, ModulatedSignal sig) RunPlan(string cnl)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        using var nl = new Elaborator(lib).Elaborate(tb);
        var plan = EnvelopeRunner.Resolve(tb.Analyses.OfType<EnvelopeAnalysis>().Single(), nl.ResolvedGlobals);
        bool used = false;
        ElaboratedNetlist Elab() { if (used) return new Elaborator(lib).Elaborate(tb); used = true; return nl; }
        var ds = EnvelopeRunner.Run(plan, Elab);
        return (ds, nl.Components.Select(c => c.Model).OfType<ModSourceModel>().First().Signal);
    }

    private static EnvelopeRunResult RunEngine(string cnl, EnvelopeParams p)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        using var nl = new Elaborator(lib).Elaborate(tb);
        return new EnvelopeEngine(nl).Run(p);
    }

    /// <summary>The automatic step at draft for the pair's carrier: what a delay of whole steps is counted in.</summary>
    private static double Step()
    {
        var (lib, tb) = new CnlReader().Read(Pair);
        using var nl = new Elaborator(lib).Elaborate(tb);
        var src = nl.Components.Select(c => c.Model).OfType<ModSourceModel>().ToArray();
        return EnvelopeRunner.StepSeconds(src, new EnvelopeParams { Oversample = 1 }, 1);
    }

    /// <summary>
    /// The System Delay is stepped as its delay at the baseband and at the carrier: a whole number of steps comes out
    /// exactly, the carrier turned by e^(−jω0·T). A polar amplitude half drives the baseband path, an RF source the other.
    /// </summary>
    [Fact]
    public void ADelay_IsExactAtTheBasebandAndAtTheCarrier()
    {
        double h = Step(), t = 3 * h;
        var r = RunEngine($"""
            MOD_Source:SA a 0 Fc=1e9 Pavg=-10 Z=50 Polar=amp {Lte}
            Delay:D1 a 0 b 0 T={N(t)}
            R:R1 b 0 R=50
            MOD_Source:SR x 0 Fc=1e9 Pavg=-10 Z=50 {Lte}
            Delay:D2 x 0 y 0 T={N(t)}
            R:R2 y 0 R=50
            """, new EnvelopeParams { StopS = 20e-6, WarmupS = 1e-6, Oversample = 1, MaxHarm = 1, Probes = ["a", "b", "x", "y"] });
        Assert.Equal(h, r.StepS, 15);
        int ia = Array.IndexOf(r.ProbeNames, "a"), ib = Array.IndexOf(r.ProbeNames, "b");
        int ix = Array.IndexOf(r.ProbeNames, "x"), iy = Array.IndexOf(r.ProbeNames, "y");
        var rot = Complex.FromPolarCoordinates(1, -2 * Math.PI * 1e9 * t);
        double worstBb = 0, worstRf = 0, scale = 0;
        for (int n = 3; n < r.Time.Length; n++)
        {
            scale = Math.Max(scale, r.Probe[n][ia][0].Magnitude);
            worstBb = Math.Max(worstBb, (r.Probe[n][ib][0] - r.Probe[n - 3][ia][0]).Magnitude);
            worstRf = Math.Max(worstRf, (r.Probe[n][iy][1] - rot * r.Probe[n - 3][ix][1]).Magnitude);
        }
        output.WriteLine($"step {h * 1e9:F3} ns, T = 3 steps: worst baseband error {worstBb / scale:E2}, carrier {worstRf / scale:E2} of the peak");
        Assert.True(worstBb < 1e-9 * scale);
        Assert.True(worstRf < 1e-9 * scale);
    }

    /// <summary>
    /// The detector's pole: a carrier with a small sideband ε at fm is amplitude-modulated by ε·cos(2π·fm·t), and the
    /// detected ripple is ε·H(fm) of its mean, H(f) = 1/(1 + jf/BW) — at fm = BW, 1/√2 at −45°. To O(ε²) and the Gear-2
    /// step's warp at fm·h ≈ 0.013 (fine), both under 0.2 %.
    /// </summary>
    [Fact]
    public void TheDetectorsPole_IsOnePoleAtBw()
    {
        const double fm = 200e3, eps = 0.01;
        var r = RunEngine("""
            MOD_Source:S1 a 0 Fc=1e9 Pavg=0 Z=50 Record=0.1e-3 Carrier[1]="CW;BW=0.2MHz;Off=0;P=0" Carrier[2]="CW;BW=0.2MHz;Off=200kHz;P=-40"
            EnvDetector:D1 a 0 b 0 Kd=1 BW=200e3
            R:RL b 0 R=50
            """, new EnvelopeParams { StopS = 50e-6, WarmupS = 10e-6, Oversample = 8, MaxHarm = 1, Probes = ["a", "b"] });
        int ia = Array.IndexOf(r.ProbeNames, "a"), ib = Array.IndexOf(r.ProbeNames, "b");
        int n = (int)Math.Round(10 / fm / r.StepS);                 // ten whole periods
        Complex Tone(Func<int, double> v)
        {
            Complex acc = 0;
            for (int i = 0; i < n; i++) acc += v(i) * Complex.FromPolarCoordinates(1, -2 * Math.PI * fm * r.Time[i]);
            return 2.0 * acc / n;
        }
        double Mean(Func<int, double> v) => Enumerable.Range(0, n).Average(v);
        Func<int, double> envIn = i => r.Probe[i][ia][1].Magnitude, det = i => r.Probe[i][ib][0].Real;
        var hIn = Tone(envIn) / Mean(envIn);
        var hOut = Tone(det) / Mean(det);
        var h = hOut / hIn;
        output.WriteLine($"input ripple {hIn.Magnitude:F6} (eps {eps}), H(fm) {h.Magnitude:F5} at {h.Phase * 180 / Math.PI:F3} deg");
        Assert.Equal(eps, hIn.Magnitude, 4);
        Assert.InRange(h.Magnitude * Math.Sqrt(2), 0.998, 1.002);
        Assert.InRange(h.Phase * 180 / Math.PI, -45.1, -44.9);
    }

    /// <summary>
    /// The ideal polar transmitter recombines its input sample for sample: the output envelope at the carrier is the
    /// pair's own signal, x(t) = V(a)·e^(j·V(p)) read off the two halves' baseband nets, times one constant (Ks = 1,
    /// matched: the carrier's own phase), to the solve's tolerance. The routine form of the EVM gate below, on 20 µs.
    /// </summary>
    [Fact]
    public void AnIdealPolarPath_RecombinesItsInputSampleForSample()
    {
        var r = RunEngine(PolarTx(0).Replace("analysis ENV1", "; analysis ENV1"),
                          new EnvelopeParams { StopS = 20e-6, WarmupS = 1e-6, Oversample = 1, MaxHarm = 1, Tol = 1e-12, Probes = ["a", "p", "g"] });
        int ia = Array.IndexOf(r.ProbeNames, "a"), ip = Array.IndexOf(r.ProbeNames, "p"), io = Array.IndexOf(r.ProbeNames, "g");
        Complex ratio = 0;
        double worst = 0, peak = 0;
        for (int n = 0; n < r.Time.Length; n++)
        {
            var x = Complex.FromPolarCoordinates(r.Probe[n][ia][0].Real, r.Probe[n][ip][0].Real);
            var y = r.Probe[n][io][1];
            if (n == 0) ratio = y / x;
            worst = Math.Max(worst, (y - ratio * x).Magnitude);
            peak = Math.Max(peak, y.Magnitude);
        }
        output.WriteLine($"{r.Time.Length} steps: |out/x| {ratio.Magnitude:F9}, worst departure {worst / peak:E2} of the peak");
        Assert.Equal(1.0, ratio.Magnitude, 6);                     // Ks = 1 into a matched load: |out| = V(a)
        Assert.True(worst < 1e-8 * peak);                          // at Tol = 1e-12 A; the default holds 1e-4 of the drive
    }

    /// <summary>
    /// The ideal polar transmitter — no mismatch, nothing limiting the AM path — recombines the source's own signal: its
    /// EVM is the Signal Designer's for that record, to the solve's tolerance (what it adds in quadrature is under 0.03 %,
    /// −70 dB), and the pair's signal is measured once, at the source's available power.
    /// </summary>
    [Fact]
    [Trait("Category", "Benchmark")]      // 2-3 s alone, 8-20 s inside the parallel suite (a whole 0.5 ms record)
    public void AnIdealPolarPath_ReadsTheSourcesOwnEvm()
    {
        var (ds, sig) = RunPlan(PolarTx(0));
        var own = SignalEvm.Measure(sig.Carriers[0].Spec, sig.Carriers[0].Native, sig.RecordSeconds);
        double evm = ds["EVM_pct"].RealValues[0];
        output.WriteLine($"polar recombination {evm:F6} %, the source's own {own.RmsPercent:F6} %; Pin {ds["Pin_avg_dBm"].RealValues[0]:F4} dBm");
        Assert.InRange(own.RmsPercent, 0.1, 10);
        // what the recombination adds, in quadrature: the solve's own relative tolerance (1e-4), 70 dB under the signal
        double added = Math.Sqrt(Math.Max(0, evm * evm - own.RmsPercent * own.RmsPercent));
        output.WriteLine($"added by the recombination: {added:F5} %");
        Assert.InRange(evm, own.RmsPercent - 1e-4, own.RmsPercent + 1e-3);
        Assert.True(added < 0.03, $"the recombination adds {added:F4} % of EVM");
        Assert.InRange(ds["Pin_avg_dBm"].RealValues[0], -10.001, -9.999);   // the pair's signal, measured once
        Assert.Single(ds["EVM_pct"].RealValues);
    }

    /// <summary>
    /// EVM against the AM/PM delay mismatch Δτ. The recombined output is a(t − Δτ)·e^(jφ(t)), so its error against the
    /// signal is e(t) = [a(t − Δτ) − a(t)]·e^(jφ(t)) ≈ −Δτ·ȧ(t)·e^(jφ(t)): it grows in proportion to Δτ and to the envelope's
    /// bandwidth — the trend Raab derived for the Kahn transmitter ("Intermodulation distortion in Kahn-technique
    /// transmitters", 1996). Here e is formed from the very record the run plays (Δτ a whole number of steps), one gain
    /// fitted as the EVM measurement fits one, and only its share inside the occupied band counted, as the demodulator
    /// counts it; added in quadrature to the source's own EVM, that is the prediction, to ±15 %.
    /// </summary>
    [Theory]
    [Trait("Category", "Benchmark")]      // as above: a whole record each
    [InlineData(2)]
    [InlineData(6)]
    public void EvmAgainstDelayMismatch_FollowsTheRecombinationError(int steps)
    {
        double h = Step(), tau = steps * h;
        var (ds, sig) = RunPlan(PolarTx(tau));
        var own = SignalEvm.Measure(sig.Carriers[0].Spec, sig.Carriers[0].Native, sig.RecordSeconds).RmsPercent / 100;

        double delayEvm = DelayMismatchEvm(sig, h, steps, out double firstOrder);
        double predict = 100 * Math.Sqrt(own * own + delayEvm * delayEvm);
        double evm = ds["EVM_pct"].RealValues[0];
        output.WriteLine($"Δτ = {steps} steps ({tau * 1e9:F2} ns): EVM {evm:F4} %, predicted {predict:F4} % (delay part {100 * delayEvm:F4} %, " +
                         $"first order {100 * firstOrder:F4} %, own {100 * own:F4} %); ACPR1 {ds["ACLR"].RealValues[1]:F2} dBc");
        Assert.InRange(evm, 0.85 * predict, 1.15 * predict);
        Assert.True(evm > 1.5 * 100 * own, "the mismatch must be visible over the source's own EVM");
    }

    /// <summary>
    /// The delay part of the EVM, from the record the run plays: the output y = a(t − Δτ)·e^(jφ(t)) against x, inside the
    /// occupied band, after the demodulator's own fits (SignalEvm.Evaluate) — one delay, the peak of the in-band
    /// cross-correlation, then one complex gain. <paramref name="firstOrder"/> is its first-order form: with the timing
    /// fit taking out the part of −Δτ·ȧ·e^(jφ) along ẋ, EVM ≈ Δτ·√(⟨ȧ²⟩·⟨a²φ̇²⟩ / (⟨|ẋ|²⟩·⟨a²⟩)) over the whole band.
    /// </summary>
    private static double DelayMismatchEvm(ModulatedSignal sig, double h, int steps, out double firstOrder)
    {
        int n = (int)Math.Round(sig.RecordSeconds / h);
        var x = sig.CompositeWindow(1 / h, 0, 0, n);
        var y = new Complex[n];
        for (int i = 0; i < n; i++) y[i] = x[i].Magnitude > 0 ? x[(i - steps + n) % n].Magnitude * x[i] / x[i].Magnitude : 0;
        var nm = OfdmGenerator.Numerology(sig.Carriers[0].Spec);
        double half = 0.5 * nm.NSc * nm.ScsHz;
        var fx = RfCore.Signals.Fft.Forward(x);
        var fy = RfCore.Signals.Fft.Forward(y);
        var bins = Enumerable.Range(0, n).Select(k => (k, hz: (k <= n / 2 ? k : k - n) / (n * h))).Where(b => Math.Abs(b.hz) <= half).ToArray();
        Complex Corr(double t)
        {
            Complex acc = 0;
            foreach (var (k, hz) in bins) acc += fy[k] * Complex.Conjugate(fx[k]) * Complex.FromPolarCoordinates(1, 2 * Math.PI * hz * t);
            return acc;
        }
        // the peak of |Corr| between no delay and the whole mismatch, by golden section
        double lo = -h, hi = (steps + 1) * h, r = (Math.Sqrt(5) - 1) / 2;
        for (int it = 0; it < 80; it++)
        {
            double c1 = hi - r * (hi - lo), c2 = lo + r * (hi - lo);
            if (Corr(c1).Magnitude > Corr(c2).Magnitude) hi = c2; else lo = c1;
        }
        double tau = 0.5 * (lo + hi);
        double px = bins.Sum(b => fx[b.k].Real * fx[b.k].Real + fx[b.k].Imaginary * fx[b.k].Imaginary);
        var g = Corr(tau) / px;
        double err = 0;
        foreach (var (k, hz) in bins)
        {
            double d = (fy[k] * Complex.FromPolarCoordinates(1, 2 * Math.PI * hz * tau) / g - fx[k]).Magnitude;
            err += d * d;
        }

        // first order, over the whole band: ȧ and a·φ̇ by central differences on the record
        double pa = 0, pphi = 0, px0 = 0;
        for (int i = 0; i < n; i++)
        {
            var xp = x[(i + 1) % n]; var xm = x[(i - 1 + n) % n];
            var dx = (xp - xm) / (2 * h);
            double a = x[i].Magnitude;
            double da = a > 0 ? (dx * Complex.Conjugate(x[i])).Real / a : 0, adphi = a > 0 ? (dx * Complex.Conjugate(x[i])).Imaginary / a : 0;
            pa += da * da; pphi += adphi * adphi; px0 += a * a;
        }
        firstOrder = steps * h * Math.Sqrt(pa * pphi / ((pa + pphi) * px0));
        return Math.Sqrt(err / px);
    }

    [Fact]
    public void APolarPair_PlayingTwoSignals_IsRefusedByName()
    {
        var ex = Assert.ThrowsAny<InvalidOperationException>(() => RunEngine($"""
            MOD_Source:SA a 0 Fc=1e9 Pavg=-10 Z=50 Polar=amp {Lte}
            MOD_Source:SP p 0 Fc=1e9 Pavg=-10 Z=50 Polar=phase Carrier[1]="LTE;BW=1.4MHz;Mod=64QAM;Off=0;P=0;Seed=4" Record=0.5e-3
            R:RA a 0 R=50
            R:RP p 0 R=50
            """, new EnvelopeParams { StopS = 1e-6, Oversample = 1, MaxHarm = 1 }));
        Assert.Contains("'SP'", ex.Message);
        Assert.Contains("same carriers", ex.Message);

        ex = Assert.ThrowsAny<InvalidOperationException>(() => RunEngine($"""
            MOD_Source:SP p 0 Fc=1e9 Pavg=-10 Z=50 Polar=phase {Lte}
            R:RP p 0 R=50
            """, new EnvelopeParams { StopS = 1e-6, Oversample = 1, MaxHarm = 1 }));
        Assert.Contains("no amplitude half", ex.Message);
    }
}
