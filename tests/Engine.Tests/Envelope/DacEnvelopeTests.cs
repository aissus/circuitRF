using System.Globalization;
using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine.Envelope;
using CircuitRF.Engine.HarmonicBalance;
using RfCore.Data;
using RfCore.Signals;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Envelope;

/// <summary>
/// The DAC in the circuit envelope: a MOD_Source rail (Baseband=i|q) played through it is its conversion of the record
/// sample for sample; an I/Q pair through two DACs and an I/Q modulator reads the source's own EVM when the DACs are
/// ideal, and the quantisation floor's closed form below that; and harmonic balance refuses it by name.
/// </summary>
public sealed class DacEnvelopeTests(ITestOutputHelper output)
{
    private static string N(double x) => x.ToString("R", CultureInfo.InvariantCulture);

    // 1.4 MHz: the narrowest LTE carrier, so a whole 0.5 ms record is ~3,800 draft steps.
    private const string Lte = "Carrier[1]=\"LTE;BW=1.4MHz;Mod=64QAM;Off=0;P=0;Seed=3\" Record=0.5e-3";

    /// <summary>The record's rails peak at 3.524 of its RMS envelope; at −10 dBm a matched input reads 0.1 V of envelope,
    /// so FS = 0.36 V holds every sample without clipping, 20·log10(0.36/0.1) = 11.1 dB above it.</summary>
    private const double Fs0 = 0.36;

    /// <summary>An I/Q transmitter: two rails, two DACs, two mixers on a quadrature LO, summed into a matched load.</summary>
    private static string IqTx(int bits, double fs) => $"""
        MOD_Source:SI bi 0 Fc=1e9 Pavg=-10 Z=50 Baseband=i {Lte}
        MOD_Source:SQ bq 0 Fc=1e9 Pavg=-10 Z=50 Baseband=q {Lte}
        DAC:DI bi 0 ai 0 Bits={bits} FS={N(Fs0)} Fs={N(fs)}
        DAC:DQ bq 0 aq 0 Bits={bits} FS={N(Fs0)} Fs={N(fs)}
        P1Tone:LO lo 0 Freq=1e9 Pavl=7 Z=50
        Coupler:HY lo 0 loi 0 loq 0 iso 0 Coupling=3.0103 dB Phase=90 deg Z0=50 Ohm
        R:Riso iso 0 R=50
        Mixer:MI ai 0 loq 0 rf 0 ConvGain=-7 Plo=4
        Mixer:MQ aq 0 loi 0 rf 0 ConvGain=-7 Plo=4
        IProbe:Iout rf load
        R:RL load 0 R=50
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

    private static double OwnEvm(ModulatedSignal sig)
        => SignalEvm.Measure(sig.Carriers[0].Spec, sig.Carriers[0].Native, sig.RecordSeconds).RmsPercent;

    /// <summary>
    /// A rail through a DAC into a matched load reads, at every step, the DAC's conversion of the record: the rail at Fs
    /// as its input reads it (half the open-circuit drive), quantised, held and band-limited to the run's signal band,
    /// times the gain. At Fs = 3.84 MHz its first image (from 3.3 MHz) is inside the band, so the run carries it; the note
    /// says where the band ends.
    /// </summary>
    [Fact]
    public void ARailThroughADac_IsItsConversion_StepForStep()
    {
        const double fs = 3.84e6;
        var (lib, tb) = new CnlReader().Read($"""
            MOD_Source:SI bi 0 Fc=1e9 Pavg=-10 Z=50 Baseband=i {Lte}
            DAC:D1 bi 0 o 0 Bits=6 FS={N(Fs0)} Fs={N(fs)} Gain=6
            R:RL o 0 R=50
            """);
        using var nl = new Elaborator(lib).Elaborate(tb);
        var r = new EnvelopeEngine(nl).Run(new EnvelopeParams { StopS = 40e-6, WarmupS = 1e-6, Oversample = 1, MaxHarm = 1, Probes = ["o"] });
        var src = nl.Components.Select(c => c.Model).OfType<ModSourceModel>().Single();
        var dac = nl.Components.Select(c => c.Model).OfType<DacModel>().Single();

        int nRec = (int)Math.Round(src.Signal.RecordSeconds / r.StepS);
        var x = src.Signal.Composite(fs);
        var y = dac.Convert([.. x.Select(v => 0.5 * src.VsPeak * v.Real)], src.Signal.RecordSeconds, nRec, 0.5 * r.SignalRateHz);
        double g = Math.Pow(10, 6.0 / 20), worst = 0, peak = 0;
        int io = Array.IndexOf(r.ProbeNames, "o");
        for (int n = 0; n < r.Time.Length; n++)
        {
            worst = Math.Max(worst, Math.Abs(r.Probe[n][io][0].Real - g * y[n]));
            peak = Math.Max(peak, Math.Abs(g * y[n]));
        }
        output.WriteLine($"{r.Time.Length} steps of {r.StepS * 1e9:F1} ns: worst departure {worst / peak:E2} of the peak");
        Assert.True(worst < 1e-9 * peak);
        Assert.Contains(nl.Notes, m => m.Contains("DAC 'D1'") && m.Contains("±3.84 MHz"));
    }

    /// <summary>
    /// Ideal DACs (24 bits at 245.76 MHz: quantisation 140 dB down, a droop of 0.002 % at the channel's edge) read the
    /// source's own EVM through the I/Q modulator, and the pair's signal is measured once, through its I rail, at the
    /// source's available power.
    /// </summary>
    [Fact]
    public void IdealDacs_ReadTheSourcesOwnEvm()
    {
        var (ds, sig) = RunPlan(IqTx(24, 245.76e6));
        double evm = ds["EVM_pct"].RealValues[0], own = OwnEvm(sig);
        output.WriteLine($"through ideal DACs {evm:F5} %, the source's own {own:F5} %; Pin {ds["Pin_avg_dBm"].RealValues[0]:F4} dBm");
        Assert.InRange(evm, own - 2e-3, own + 2e-3);
        Assert.Single(ds["EVM_pct"].RealValues);
        Assert.InRange(ds["Pin_avg_dBm"].RealValues[0], -10.001, -9.999);
    }

    /// <summary>
    /// EVM against resolution. Each rail's quantisation error is Δ²/12 spread from −Fs/2 to Fs/2 (Δ = 2·FS/2^N), so a
    /// signal of RMS envelope x_rms has SQNR = 6.02·N + 1.76 − 20·log10(FS/x_rms) over the Nyquist band, and the share of
    /// it in the occupied band B, which is what the demodulator reads, is B/Fs: EVM_q = 10^(−(SQNR + 10·log10(Fs/B))/20).
    /// The source's own EVM is taken out in quadrature (at 15.36 MHz the hold's droop adds nothing measurable to it: ideal
    /// DACs there read 0.4578 % against its 0.4624 %); what is left is within 10 % of the closed form. One run per case.
    /// </summary>
    [Theory]
    [InlineData(6)]
    [InlineData(8)]
    public void EvmAgainstBits_FollowsTheQuantisationClosedForm(int bits)
    {
        const double fs = 15.36e6;
        var (ds, sig) = RunPlan(IqTx(bits, fs));
        double evm = ds["EVM_pct"].RealValues[0], own = OwnEvm(sig);
        double xRms = 0.1;                                         // −10 dBm: |Vs|/2 of RMS envelope at a matched input
        var nm = OfdmGenerator.Numerology(sig.Carriers[0].Spec);
        double b = nm.NSc * nm.ScsHz;
        double sqnr = 6.02 * bits + 1.76 - 20 * Math.Log10(Fs0 / xRms);
        double closed = 100 * Math.Pow(10, -(sqnr + 10 * Math.Log10(fs / b)) / 20);
        double measured = Math.Sqrt(evm * evm - own * own);
        output.WriteLine($"{bits} bits: EVM {evm:F4} % (the source's own {own:F4} %), quantisation {measured:F4} %, closed form {closed:F4} % " +
                         $"(SQNR {sqnr:F2} dB + {10 * Math.Log10(fs / b):F2} dB of oversampling); ACPR1 {ds["ACLR"].RealValues[1]:F2} dBc");
        Assert.InRange(measured, 0.9 * closed, 1.1 * closed);
    }

    /// <summary>The rails of one signal are measured once, through the first I rail; beside an RF source of it, not at all. A
    /// second I rail or a second amplitude half of the same signal is the same signal, and is not measured again (counted,
    /// it read Pin 6 dB high and listed every EVM row twice).</summary>
    [Fact]
    public void ARailPair_IsMeasuredOnce_ThroughItsIRail()
    {
        ModSourceModel Src(string? key, string? word)
        {
            var p = new Dictionary<string, Core.Expressions.Value>
            {
                ["Fc"] = new(1e9), ["Pavg"] = new(0.0), ["Carrier[1]"] = new("LTE;BW=1.4MHz;Off=0;P=0"),
            };
            if (key is not null) p[key] = new(word!);
            return ModSourceModel.Create(p);
        }
        ModSourceModel i = Src("Baseband", "i"), q = Src("Baseband", "q"), rf = Src(null, null), amp = Src("Polar", "amp");
        Assert.Equal([0], EnvelopeEngine.Measured([i, q]));
        Assert.Equal([1], EnvelopeEngine.Measured([q, i, amp]));
        Assert.Equal([0], EnvelopeEngine.Measured([rf, i, q]));
        Assert.Equal([0], EnvelopeEngine.Measured([i, q, Src("Baseband", "i")]));
        Assert.Equal([1], EnvelopeEngine.Measured([Src("Polar", "phase"), amp, Src("Polar", "amp")]));
        Assert.Contains("no I rail", Assert.Throws<InvalidOperationException>(() => EnvelopeEngine.BasebandPair([q])).Message);
    }

    /// <summary>A DAC converts a rail's record and nothing else: anything else on its input net is refused by name.</summary>
    [Theory]
    [InlineData("MOD_Source:SI bi 0 Fc=1e9 Pavg=-10 Z=50 " + Lte)]
    [InlineData("MOD_Source:SI bi 0 Fc=1e9 Pavg=-10 Z=50 Baseband=i " + Lte + "\nR:Rx bi 0 R=50")]
    public void ADacWhoseInputIsNotARail_IsRefusedByName(string input)
    {
        var (lib, tb) = new CnlReader().Read($"""
            {input}
            DAC:D7 bi 0 o 0 Fs=3.84e6
            R:RL o 0 R=50
            """);
        using var nl = new Elaborator(lib).Elaborate(tb);
        var ex = Assert.ThrowsAny<InvalidOperationException>(() => new EnvelopeEngine(nl).Run(
            new EnvelopeParams { StopS = 5e-6, Oversample = 1, MaxHarm = 1 }));
        Assert.Contains("DAC 'D7'", ex.Message);
        Assert.Contains("Baseband=i or q", ex.Message);
    }

    /// <summary>Harmonic balance has no stream to sample, at any tone count and through its single-point entry (loadpull
    /// and the oscillator analysis): a DAC is refused there by name.</summary>
    [Theory]
    [InlineData("Tone=1e6", false)]
    [InlineData("NumFreqs=2 Tone[1]=1e6 Tone[2]=1.1e6 MaxMixOrder=3", false)]
    [InlineData("NumFreqs=3 Tone[1]=1e6 Tone[2]=1.1e6 Tone[3]=1.3e6 MaxMixOrder=3", false)]
    [InlineData("Tone=1e6", true)]
    public void HarmonicBalance_RefusesADacByName(string tones, bool singlePoint)
    {
        var (lib, tb) = new CnlReader().Read($"""
            P1Tone:P1 a 0 Freq=1e6 Pavl=-10 Z=50
            DAC:D9 a 0 b 0 Fs=100e6
            R:RL b 0 R=50
            analysis HB1 type=hb {tones} MaxHarm=3
            """);
        var nl = new Elaborator(lib).Elaborate(tb);
        var hba = tb.Analyses.OfType<HarmonicBalanceAnalysis>().First();
        var hb = new HbEngine(nl, tb);
        var p = HbEngine.Resolve(hba, nl.ResolvedGlobals);
        var ex = Assert.ThrowsAny<InvalidOperationException>(() => { if (singlePoint) hb.RunSinglePoint(p); else hb.Run(p); });
        Assert.Contains("DAC 'D9'", ex.Message);
        Assert.Contains("harmonic balance", ex.Message);
    }

    /// <summary>What a DAC's stream cannot be, refused by name: an Fs that does not put whole samples in the record (named
    /// to nine digits, so 3.8401 MHz is not read as 3.84), an Fs below what the rail carries, and a phase-noise mask on it.</summary>
    [Theory]
    [InlineData("", "3.8401e6", "3.8401 MHz does not put a whole number of samples")]
    [InlineData("", "1e6", "below the 1.08 MHz")]
    [InlineData("PhaseNoise=\"10kHz:-80,1MHz:-120\"", "3.84e6", "PhaseNoise")]
    public void AStreamTheDacCannotConvert_IsRefusedByName(string extra, string fs, string expected)
    {
        var (lib, tb) = new CnlReader().Read($"""
            MOD_Source:SI bi 0 Fc=1e9 Pavg=-10 Z=50 Baseband=i {extra} {Lte}
            DAC:D5 bi 0 o 0 Fs={fs}
            R:RL o 0 R=50
            """);
        using var nl = new Elaborator(lib).Elaborate(tb);
        var ex = Assert.ThrowsAny<InvalidOperationException>(() => new EnvelopeEngine(nl).Run(
            new EnvelopeParams { StopS = 5e-6, Oversample = 1, MaxHarm = 1 }));
        Assert.Contains("DAC 'D5'", ex.Message);
        Assert.Contains(expected, ex.Message);
    }

    /// <summary>The multi-fundamental envelope (a second carrier at 2 GHz) has no one baseband: it refuses a rail, and a DAC
    /// with no rail, by name.</summary>
    [Theory]
    [InlineData("MOD_Source:SI bi 0 Fc=1e9 Pavg=-10 Z=50 Baseband=i " + Lte + "\nR:R1 bi 0 R=50", "MOD_Source 'SI' is a baseband rail")]
    [InlineData("MOD_Source:S1 a 0 Fc=1e9 Pavg=-10 Z=50 " + Lte + "\nDAC:D3 a 0 b 0 Fs=3.84e6\nR:R1 b 0 R=50", "DAC 'D3'")]
    public void TheMultiFundamentalEnvelope_RefusesRailsAndDacsByName(string body, string expected)
    {
        var ex = Assert.ThrowsAny<InvalidOperationException>(() => RunPlan($"""
            {body}
            MOD_Source:SX x 0 Fc=2e9 Pavg=-10 Z=50 {Lte}
            R:RX x 0 R=50
            analysis ENV1 type=env Accuracy=draft MaxHarm=1 Segments=1 Warmup=1e-6 Stop=5e-6
            """));
        Assert.Contains(expected, ex.Message);
    }

    /// <summary>
    /// A rail plays its signal about its own Fc, whatever source is listed first and sets the run's fundamental: a CW rail
    /// at +1 MHz, alone and behind an RF source at 1.004 GHz, reads the same 1 MHz line on its own net and through its
    /// DAC (it used to read 3 MHz behind the RF source), and no line at 3 MHz. Both runs at the same step.
    /// </summary>
    [Fact]
    public void ARailsWaveform_DoesNotDependOnAnotherSourcesCarrier()
    {
        // a 1.4 MHz LTE carrier 200 dB down only gives the record a modulated carrier's rate
        const string cw = "Carrier[1]=\"CW;Off=1MHz;P=0\" Carrier[2]=\"LTE;BW=1.4MHz;Mod=QPSK;Off=0;P=-200;Seed=1\" Record=1e-4";
        Complex[] Lines(string first)
        {
            var (lib, tb) = new CnlReader().Read(first + $"""
                MOD_Source:SI bi 0 Fc=1e9 Pavg=0 Z=50 Baseband=i {cw}
                R:RL bi 0 R=50
                MOD_Source:SJ di 0 Fc=1e9 Pavg=0 Z=50 Baseband=i {cw}
                DAC:D1 di 0 o 0 Bits=24 FS=2 Fs=15.36e6
                R:RO o 0 R=50
                """);
            using var nl = new Elaborator(lib).Elaborate(tb);
            var r = new EnvelopeEngine(nl).Run(new EnvelopeParams { StopS = 1e-4, StepS = 1 / 61.44e6, MaxHarm = 1, Probes = ["bi", "o"] });
            int n = r.Time.Length - 1;
            Complex Line(string node, double hz)
            {
                int k = Array.IndexOf(r.ProbeNames, node);
                Complex acc = 0;
                for (int t = 0; t < n; t++) acc += r.Probe[t][k][0].Real * Complex.FromPolarCoordinates(1, -2 * Math.PI * hz * r.Time[t]);
                return 2.0 * acc / n;
            }
            return [Line("bi", 1e6), Line("o", 1e6), Line("bi", 3e6), Line("o", 3e6)];
        }
        var alone = Lines("");
        var behind = Lines("MOD_Source:SX x 0 Fc=1.004e9 Pavg=-60 Z=50 Carrier[1]=\"CW;Off=0;P=0\" Record=1e-4\nR:RX x 0 R=50\n");
        output.WriteLine($"1 MHz on bi: {alone[0].Magnitude:F6} / {behind[0].Magnitude:F6} V, through the DAC {alone[1].Magnitude:F6} / {behind[1].Magnitude:F6} V; 3 MHz {behind[2].Magnitude:E1} / {behind[3].Magnitude:E1} V");
        for (int i = 0; i < 2; i++)
        {
            Assert.True(alone[i].Magnitude > 0.1);
            Assert.True((alone[i] - behind[i]).Magnitude < 1e-9 * alone[i].Magnitude);
            Assert.True(behind[i + 2].Magnitude < 1e-6 * alone[i].Magnitude);    // 24 bits' quantisation noise, no line
        }
    }
}
