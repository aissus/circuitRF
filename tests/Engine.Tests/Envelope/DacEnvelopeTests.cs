using System.Globalization;
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

    /// <summary>The rails of one signal are measured once, through the I rail; beside an RF source of it, not at all.</summary>
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

    /// <summary>Harmonic balance has no stream to sample, at any tone count: a DAC is refused there by name.</summary>
    [Fact]
    public void HarmonicBalance_RefusesADacByName()
    {
        var (lib, tb) = new CnlReader().Read("""
            P1Tone:P1 a 0 Freq=1e6 Pavl=-10 Z=50
            DAC:D9 a 0 b 0 Fs=100e6
            R:RL b 0 R=50
            analysis HB1 type=hb Tone=1e6 MaxHarm=5
            """);
        var nl = new Elaborator(lib).Elaborate(tb);
        var hba = tb.Analyses.OfType<HarmonicBalanceAnalysis>().First();
        var ex = Assert.ThrowsAny<InvalidOperationException>(() => new HbEngine(nl, tb).Run(HbEngine.Resolve(hba, nl.ResolvedGlobals)));
        Assert.Contains("DAC 'D9'", ex.Message);
        Assert.Contains("harmonic balance", ex.Message);
    }
}
