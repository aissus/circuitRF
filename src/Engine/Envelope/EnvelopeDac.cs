// ================================================================
//  EnvelopeDac.cs  —  a DAC in the circuit envelope, exactly
//
//  A DAC (DacModel) converts a digital stream, and the only one a design
//  has is a Signal Designer record: a MOD_Source rail (Baseband=i|q) on
//  the DAC's input net, with nothing else on it.  The record is
//  periodic, so the whole conversion is known before the first step:
//  the rail at the DAC's own Fs, quantised and held, is the output
//  spectrum C(f)·H(f) at every bin of the envelope's record, images
//  included, band-limited to the run's signal band (DacModel.Convert).
//  The envelope's step never has to resolve the hold in time, and need
//  not divide 1/Fs.
//
//  The rail's source then plays that conversion, in volts at the DAC's
//  input (its drive scaled up by the divider into Zin), and the DAC
//  carries it to its output with its gain: exact, because nothing else
//  is on the net.  The run says what it leaves out — the images and the
//  quantisation noise beyond the band — in a note.
// ================================================================

using System.Numerics;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Elaboration;

namespace CircuitRF.Engine.Envelope;

internal static class EnvelopeDac
{
    /// <summary>
    /// Replaces the drive of every rail a DAC converts by the DAC's conversion of it, over the window
    /// <c>[i0, i0 + count)</c> of the envelope's record of <paramref name="recordSamples"/> samples (a function of the absolute
    /// sample alone, as every drive is). <paramref name="bandHz"/> is the run's signal band. Refuses a DAC whose input is not
    /// one rail's own net, by name.
    /// </summary>
    public static void Apply(ElaboratedNetlist nl, ElaboratedComponent[] srcEcs, Complex[][] drive, double f0,
                             int recordSamples, double bandHz, long i0)
    {
        foreach (var ec in nl.Components)
        {
            if (ec.Model is not DacModel dac) continue;
            int s = RailOf(nl, ec, srcEcs);
            var src = (ModSourceModel)srcEcs[s].Model;
            if (EnvelopePhaseNoise.Mask(srcEcs[s]) is not null)
                throw new InvalidOperationException(DacDiagnostics.PhaseNoiseOnStream(ec.InstancePath, srcEcs[s].InstancePath).Render());
            var sig = src.Signal;
            double fs = dac.SampleRateHz, centre = f0 - src.FcHz;
            int n;
            try { n = sig.LengthAt(fs); }
            catch (ArgumentException) { throw new InvalidOperationException(DacDiagnostics.RecordNotWhole(ec.InstancePath, fs, sig.RecordSeconds).Render()); }
            double reach = sig.Carriers.Max(c => Math.Abs(c.OffsetHz - centre) + 0.5 * c.OccupiedBwHz);
            if (fs < 2 * reach)
                throw new InvalidOperationException(DacDiagnostics.RateBelowSignal(ec.InstancePath, fs, 2 * reach).Render());

            // What the DAC's input reads of the rail: its open-circuit drive through the divider into Zin.
            double divider = dac.Zin / (src.Z.Real + dac.Zin);
            var x = sig.Composite(fs, centre);
            var v = new double[n];
            for (int i = 0; i < n; i++) v[i] = divider * src.VsPeak * (src.Baseband == IqRail.I ? x[i].Real : x[i].Imaginary);
            var y = dac.Convert(v, sig.RecordSeconds, recordSamples, bandHz);
            var d = drive[s];
            for (int m = 0; m < d.Length; m++)
            {
                long g = (i0 + m) % recordSamples;
                d[m] = new Complex(y[g < 0 ? g + recordSamples : g] / divider, 0);
            }

            // The first image the band leaves out, and how far below the signal any of them is: |H(f)| ≤ d·Fs/(π·d·f) past f,
            // against the signal's own |H| at its widest offset.
            int k = 1;
            while (k * fs + reach <= bandHz) k++;
            double first = Math.Max(k * fs - reach, bandHz);
            double edge = dac.PulseResponse(reach).Magnitude / dac.Duty;
            double bound = Math.Min(1, fs / (Math.PI * dac.Duty * first)) / edge;
            nl.AddNoteOnce("dac-band:" + ec.InstancePath,
                DacDiagnostics.Band(ec.InstancePath, bandHz, first, -20 * Math.Log10(bound)).Render());
        }
    }

    /// <summary>The source whose rail is <paramref name="dac"/>'s input: on that net alone, with nothing else on it.</summary>
    private static int RailOf(ElaboratedNetlist nl, ElaboratedComponent dac, ElaboratedComponent[] srcEcs)
    {
        int inP = dac.Nodes[0], inN = dac.Nodes[1];
        int s = Array.FindIndex(srcEcs, c => c.Nodes[0] == inP && c.Nodes[1] == inN && ((ModSourceModel)c.Model).Baseband is not null);
        if (inP == 0 || s < 0 || nl.Components.Any(c => c != dac && c != srcEcs[s] && c.Nodes.Contains(inP)))
            throw new InvalidOperationException(DacDiagnostics.InputNotARail(dac.InstancePath).Render());
        return s;
    }
}
