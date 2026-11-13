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

    private static (EnvelopeRunResult r, List<string> notes, List<string> warnings) RunEngineNotes(string cnl, EnvelopeParams p)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        using var nl = new Elaborator(lib).Elaborate(tb);
        var r = new EnvelopeEngine(nl).Run(p);
        return (r, [.. nl.Notes], [.. nl.Warnings]);
    }

    /// <summary>
    /// Envelope tracking: an RF source and the amplitude half of the same signal, the half on a supply-modulated
    /// amplifier's supply. The RF source is the signal, so it is measured once: Pin is the source's −10 dBm, Gt is 0 dB
    /// (Ks = 1, matched), and the carrier is listed once — not twice, with the drive counted twice (6 dB).
    /// </summary>
    [Fact]
    public void EnvelopeTracking_MeasuresTheSignalOnce()
    {
        var (ds, _) = RunPlan($"""
            MOD_Source:SR r 0 Fc=1e9 Pavg=-10 Z=50 {Lte}
            MOD_Source:SA a 0 Fc=1e9 Pavg=-10 Z=50 Polar=amp {Lte}
            SupplyAmp:PA r 0 f 0 a 0 Ks=1
            IProbe:Iout f g
            R:RL g 0 R=50 Ohm
            analysis ENV1 type=env Accuracy=draft MaxHarm=1 Segments=1 Warmup=1e-6 Stop=0.2e-3
            """);
        output.WriteLine($"Pin {ds["Pin_avg_dBm"].RealValues[0]:F4} dBm, Gt {ds["Gt_dB"].RealValues[0]:F4} dB, " +
                         $"{ds["EVM_pct"].RealValues.Length} EVM value(s)");
        Assert.InRange(ds["Pin_avg_dBm"].RealValues[0], -10.05, -9.95);   // 0.2 ms of a 0.5 ms record: not quite its average
        Assert.InRange(ds["Gt_dB"].RealValues[0], -0.01, 0.01);
        Assert.Single(ds["EVM_pct"].RealValues);
        Assert.Equal(1, ds["ACLR"].Axes[1].Length);
    }

    /// <summary>
    /// The phase half plays the phase UNWRAPPED over the record: continuous (no step over π), twice the signal's own
    /// phase to within whole turns, and a function of the absolute sample alone — a window starting anywhere, before the
    /// record or a period later, reads the same values, a period adding the record's own winding.
    /// </summary>
    [Fact]
    public void ThePhaseHalf_PlaysTheUnwrappedPhase_TheSameInEveryWindow()
    {
        var (lib, tb) = new CnlReader().Read(Pair);
        using var nl = new Elaborator(lib).Elaborate(tb);
        var sp = nl.Components.Select(c => c.Model).OfType<ModSourceModel>().Single(m => m.Polar == PolarHalf.Phase);
        double h = Step();
        int n = (int)Math.Round(sp.Signal.RecordSeconds / h);
        var whole = EnvelopeEngine.UnwrappedPhase(sp.Signal, sp, null, 1 / h, 0, n, 0, 2 * n);
        var x = sp.Signal.CompositeWindow(1 / h, 0, 0, n);
        double jump = 0, off = 0;
        for (int i = 1; i < 2 * n; i++) jump = Math.Max(jump, Math.Abs(whole[i].Real - whole[i - 1].Real) / 2);
        for (int i = 0; i < n; i++) off = Math.Max(off, Math.Abs(Math.IEEERemainder(whole[i].Real / 2 - x[i].Phase, 2 * Math.PI)));
        double winding = (whole[n].Real - whole[0].Real) / 2;
        var early = EnvelopeEngine.UnwrappedPhase(sp.Signal, sp, null, 1 / h, 0, n, -7, 20);
        double shift = 0;
        for (int i = 7; i < 20; i++) shift = Math.Max(shift, Math.Abs(early[i].Real - whole[i - 7].Real));
        for (int i = 0; i < 7; i++) shift = Math.Max(shift, Math.Abs(early[i].Real - (whole[n - 7 + i].Real - 2 * winding)));
        output.WriteLine($"{n} samples a record: largest step {jump:F3} rad, off arg x by {off:E1}, winding {winding / (2 * Math.PI):F3} turns, window mismatch {shift:E1}");
        Assert.True(jump <= Math.PI + 1e-12);
        Assert.True(off < 1e-9);
        Assert.Equal(0.0, Math.IEEERemainder(winding, 2 * Math.PI), 9);
        Assert.True(shift < 1e-9);
    }

    /// <summary>
    /// The phase path is a path like the amplitude one: half a step of delay on the phase reads as half a step on the
    /// amplitude (the demodulator's delay fit takes out the common part, so delaying φ by τ is advancing a by τ). With the
    /// wrapped phase it read four times the EVM, the delay interpolating across every 2π wrap.
    /// </summary>
    [Fact]
    [Trait("Category", "Benchmark")]      // two whole records
    public void AHalfStepDelayOnThePhasePath_ReadsAsOneOnTheAmplitudePath()
    {
        double half = 0.5 * Step();
        string Bench(double tAm, double tPh) => PolarTx(tAm).Replace("PhaseMod:PM1 d 0 e 0 p 0 Kp=1",
            $"Delay:DP p 0 pd 0 T={N(tPh)}\nPhaseMod:PM1 d 0 e 0 pd 0 Kp=1");
        double am = RunPlan(Bench(half, 0)).ds["EVM_pct"].RealValues[0];
        double ph = RunPlan(Bench(0, half)).ds["EVM_pct"].RealValues[0];
        output.WriteLine($"half a step ({half * 1e9:F1} ns): on the amplitude path {am:F4} %, on the phase path {ph:F4} %");
        Assert.InRange(ph, 0.8 * am, 1.25 * am);
    }

    /// <summary>
    /// A Delay of a fraction of a step is the quadratic Lagrange interpolation of the delayed wave on the step grid:
    /// a CW carrier offset fm from the envelope's centre comes out turned by Σ_j L_j·e^(−j·2π·fm·h·j), with
    /// L = (3/8, 3/4, −1/8) at half a step, and the carrier's own e^(−jω0·T) — to 1e-9. The run's note now states that
    /// interpolation's distance from the line across the band beside the line's own (0 % here), which it used to omit.
    /// </summary>
    [Fact]
    public void AFractionalDelay_IsTheLagrangeInterpolation_AndItsNoteSaysSo()
    {
        const double fm = 300e3;
        string Cw(double t) => $"""
            MOD_Source:S1 x 0 Fc=1e9 Pavg=-10 Z=50 Record=0.1e-3 Carrier[1]="CW;BW=0.2MHz;Off=300kHz;P=0"
            Delay:D1 x 0 y 0 T={N(t)}
            R:R1 y 0 R=50
            """;
        var probe = new EnvelopeParams { StopS = 10e-6, WarmupS = 2e-6, Oversample = 1, MaxHarm = 1, Probes = ["x", "y"] };
        double h = RunEngine(Cw(0), probe).StepS, t = 0.5 * h;
        var (r, notes, warnings) = RunEngineNotes(Cw(t), probe);
        Assert.Equal(h, r.StepS, 15);
        int ix = Array.IndexOf(r.ProbeNames, "x"), iy = Array.IndexOf(r.ProbeNames, "y");
        double th = 2 * Math.PI * fm * h;
        var lagrange = 0.375 + 0.75 * Complex.FromPolarCoordinates(1, -th) - 0.125 * Complex.FromPolarCoordinates(1, -2 * th);
        var expect = Complex.FromPolarCoordinates(1, -2 * Math.PI * 1e9 * t) * lagrange;
        double worst = 0;
        for (int n = 3; n < r.Time.Length; n++)
            worst = Math.Max(worst, (r.Probe[n][iy][1] / r.Probe[n - 0][ix][1] - expect).Magnitude);
        var note = notes.Single(x => x.Contains("'D1'") && x.Contains("stepped as its delay"));
        output.WriteLine($"h {h * 1e9:F2} ns, T = h/2: out/in vs the Lagrange closed form, worst {worst:E2}; " +
                         $"|L·e^(jθ/2)| = {(lagrange * Complex.FromPolarCoordinates(1, th / 2)).Magnitude:F6}; note: {note}");
        Assert.True(worst < 1e-9);
        var pct = System.Text.RegularExpressions.Regex.Match(note, @"; 0 % \(RMS\).*, ([0-9.]+) % with the delay interpolated between steps");
        Assert.True(pct.Success && double.Parse(pct.Groups[1].Value, CultureInfo.InvariantCulture) > 1, note);
        Assert.DoesNotContain(warnings, w => w.Contains("'D1'"));                 // the warning reads the line's own error
        Assert.Contains(RunEngineNotes(Cw(3 * h), probe).notes, x => x.Contains("'D1'") && x.Contains(", 0 % with the delay interpolated"));
    }

    /// <summary>A Delay that is no causal line in a ground-referenced system — a negative T, a complex Z0, returns that are
    /// not ground — keeps the path it had: it is not stepped as a delay, and the run completes.</summary>
    [Theory]
    [InlineData("Delay:D1 x 0 y 0 T=-1ns", "")]
    [InlineData("Delay:D1 x 0 y 0 T=1ns Z0=50+j10", "")]
    [InlineData("Delay:D1 x rx y ry T=1ns", "R:RX rx 0 R=1\nR:RY ry 0 R=1")]
    public void ADelayThatIsNoLine_KeepsItsOldPath(string delay, string extra)
    {
        var (r, notes, warnings) = RunEngineNotes($"""
            MOD_Source:S1 x 0 Fc=1e9 Pavg=-10 Z=50 {Lte}
            {delay}
            {extra}
            R:R1 y 0 R=50
            """, new EnvelopeParams { StopS = 5e-6, WarmupS = 1e-6, Oversample = 1, MaxHarm = 1, Probes = ["y"] });
        output.WriteLine(string.Join(" | ", notes.Concat(warnings).Where(x => x.Contains("D1"))));
        Assert.DoesNotContain(notes, x => x.Contains("'D1'") && x.Contains("stepped as its delay"));
        Assert.True(r.Time.Length > 10);
    }

    /// <summary>
    /// The multi-fundamental envelope steps a Delay as its delay too: two carriers 200 MHz apart, each on its own
    /// fundamental, through a Delay of whole steps, come out sample for sample as they went in three steps earlier, each
    /// product turned by e^(−j2π·f·T) at its own frequency — to 1e-9. On main the same Delay was a rational fit that
    /// missed, and two NR carriers through 5 ns read 1.2 % of EVM against 0.15 % here.
    /// </summary>
    [Fact]
    public void TheMultiFundamentalEnvelope_StepsADelayAsItsDelay()
    {
        const string two = "Carrier[1]=\"LTE;BW=1.4MHz;Mod=64QAM;Off=-100MHz;P=0;Seed=3\" " +
                           "Carrier[2]=\"LTE;BW=1.4MHz;Mod=64QAM;Off=100MHz;P=0;Seed=4\" Record=0.5e-3";
        var (lib0, tb0) = new CnlReader().Read($"MOD_Source:S1 x 0 Fc=1e9 Pavg=-10 Z=50 {two}");
        double h;
        using (var nl0 = new Elaborator(lib0).Elaborate(tb0))
            h = EnvelopeRunner.StepSeconds([.. nl0.Components.Select(c => c.Model).OfType<ModSourceModel>()], new EnvelopeParams { Oversample = 1 }, 2);
        var (lib, tb) = new CnlReader().Read($"""
            MOD_Source:S1 x 0 Fc=1e9 Pavg=-10 Z=50 {two}
            Delay:D1 x 0 y 0 T={N(3 * h)}
            R:R1 y 0 R=50
            """);
        using var nl = new Elaborator(lib).Elaborate(tb);
        var r = new EnvelopeEngineMt(nl).Run(new EnvelopeParams { StopS = 20e-6, WarmupS = 2e-6, Oversample = 1, MaxHarm = 1, Probes = ["x", "y"] }, 3);
        Assert.Equal(h, r.StepS, 15);
        Assert.Contains(nl.Notes, n => n.Contains("'D1'") && n.Contains("stepped as its delay"));
        int ix = Array.IndexOf(r.ProbeNames, "x"), iy = Array.IndexOf(r.ProbeNames, "y");
        foreach (int m in r.CarrierMix.Distinct())
        {
            var rot = Complex.FromPolarCoordinates(1, -2 * Math.PI * r.MixFreqHz![m] * 3 * h);
            double worst = 0, peak = 0;
            for (int n = 3; n < r.Time.Length; n++)
            {
                peak = Math.Max(peak, r.Probe[n][ix][m].Magnitude);
                worst = Math.Max(worst, (r.Probe[n][iy][m] - rot * r.Probe[n - 3][ix][m]).Magnitude);
            }
            output.WriteLine($"product {r.MixLabels![m]} at {r.MixFreqHz[m] / 1e9:F3} GHz: worst {worst / peak:E2} of the peak");
            Assert.True(worst < 1e-9 * peak);
        }
    }

    /// <summary>The multi-fundamental envelope drops weighted (w ≥ 2) terms, so it refuses a carrier block, and it has no
    /// one baseband for a polar half to drive, so it refuses that too — each by name.</summary>
    [Theory]
    [InlineData("Limiter:LIM7 x 0 y 0 Pout=0", "", "quadrature")]
    [InlineData("R:R0 x y R=1", "MOD_Source:SA7 a 0 Fc=1e9 Pavg=-10 Z=50 Polar=amp " + Lte + "\nR:RA a 0 R=50", "polar half")]
    public void TheMultiFundamentalEnvelope_RefusesWhatItCannotRun(string part, string extra, string why)
    {
        var (lib, tb) = new CnlReader().Read($"""
            MOD_Source:S1 x 0 Fc=1e9 Pavg=-10 Z=50 Carrier[1]="LTE;BW=1.4MHz;Off=-100MHz;P=0;Seed=3" Carrier[2]="LTE;BW=1.4MHz;Off=100MHz;P=0;Seed=4" Record=0.5e-3
            {part}
            {extra}
            R:R1 y 0 R=50
            """);
        using var nl = new Elaborator(lib).Elaborate(tb);
        var ex = Assert.ThrowsAny<InvalidOperationException>(() => new EnvelopeEngineMt(nl).Run(new EnvelopeParams { StopS = 1e-6 }, 3));
        Assert.Contains(why, ex.Message);
        Assert.Contains(part.Contains("LIM7") ? "LIM7" : "SA7", ex.Message);
    }

    /// <summary>
    /// The warm-up estimate holds a Delay as the line it is stepped as, and the Delay declares its own memory, T: an
    /// amplifier, 5 ns of Delay and an amplifier warm up on 5 µs and run in parallel. Before, the Delay's stamp between
    /// two nonlinear ports read as a pole of tens of seconds and the run went to one segment.
    /// </summary>
    [Fact]
    public void ADelayBetweenTwoAmplifiers_IsFiveNanosecondsOfMemory_NotSeconds()
    {
        var (lib, tb) = new CnlReader().Read($"""
            MOD_Source:S1 a 0 Fc=1e9 Pavg=-20 Z=50 {Lte}
            Amp:A1 a 0 b 0 Gain=10 IP3=20 IP3Ref=Input
            Delay:D1 b 0 c 0 T=5ns
            Amp:A2 c 0 d 0 Gain=10 IP3=30 IP3Ref=Input
            R:RL d 0 R=50
            analysis ENV1 type=env Accuracy=draft MaxHarm=1 Stop=100e-6
            """);
        using var nl = new Elaborator(lib).Elaborate(tb);
        var declared = EnvelopeMemory.Declared(nl);
        Assert.NotNull(declared);
        Assert.Equal(5e-9, declared!.Seconds, 15);
        using var own = new Elaborator(lib).Elaborate(tb);
        var pole = EnvelopeMemory.LinearPole(own, new AnalysisSettings(), Math.PI * 7e6, 100e-6);
        output.WriteLine($"declared {declared.What} {declared.Seconds * 1e9:F1} ns; the linear network's slowest pole: {(pole is null ? "none" : $"{pole.Seconds:E2} s at {pole.What}")}");
        Assert.True(pole is null || pole.Seconds < 1e-6);
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
