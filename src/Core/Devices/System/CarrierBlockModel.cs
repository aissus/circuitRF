using System.Numerics;
using CircuitRF.Core.Elaboration;

namespace CircuitRF.Core.Devices;

/// <summary>
/// The polar-transmitter blocks — <see cref="EnvelopeDetectorModel"/>, <see cref="LimiterModel"/>,
/// <see cref="PhaseModulatorModel"/> and <see cref="SupplyModulatedAmpModel"/>: memoryless laws of a carrier's
/// AMPLITUDE and PHASE rather than of its instantaneous voltage.
///
/// <para><b>Why a quadrature node.</b> Harmonic balance and the envelope evaluate a nonlinear model on samples of one
/// carrier period, and no function of v(t) alone reads the amplitude of <c>A·cos(ωt + φ)</c> or turns its phase. Each
/// block therefore carries one internal node, <c>q</c>, held at the Hilbert transform of its RF input: the input's
/// quadrature, <c>A·sin(ωt + φ)</c>. The pair is the carrier's analytic signal, so at every sample
/// <c>√(v² + v_q²) = A</c> and <c>cos θ·v − sin θ·v_q = A·cos(ωt + φ + θ)</c>, and each law below is exact on the
/// grid: no harmonics, nothing to filter off.</para>
///
/// <para>The node's row is <c>g·v_q + H₂(ω)·g·v_in = 0</c> with the weight <c>H₂(ω) = j·sign(ω)</c> — the quadrature bucket
/// <see cref="IdealSBlockModel"/>'s passive intermod already uses — so at harmonic k ≥ 1 <c>V_q = −j·V_in</c>, and at DC
/// <c>V_q = 0</c>. The weight is flat about each harmonic, so the envelope applies it exactly across the signal band.
/// Every harmonic-balance solver (single-tone, two-tone and multi-tone) and both envelope engines carry such a bucket,
/// so the blocks run in each of them.</para>
///
/// <para><b>Ports.</b> Every external port is a signal net and a reference net, in the System family's ± pair order
/// (port 0 is always the RF input); the internal ports follow, each an internal node against ground, which the
/// elaborator mints (<see cref="InternalNodes"/>). An output port is a source behind its own impedance, its open-circuit
/// voltage twice the law's value, so a matched load reads the law: the S-parameter convention every System block keeps.</para>
/// </summary>
public abstract class CarrierBlockModel : ComponentModel
{
    /// <summary>The weighting index of the quadrature bucket: the first above the built-in <c>H[0] = 1</c> and <c>H[1] = jω</c>.</summary>
    private const int QuadratureW = 2;

    private readonly double _gq;

    /// <param name="externalPorts">The ports the instance line binds, two nets each.</param>
    /// <param name="zIn">The RF input's impedance, Ω; its conductance also scales the quadrature row.</param>
    protected CarrierBlockModel(int externalPorts, double zIn)
    {
        ExternalPortCount = externalPorts;
        _gq = Conductance(zIn);
    }

    /// <summary>The ports the instance line binds (2 nets each). The internal ports follow them.</summary>
    public int ExternalPortCount { get; }

    /// <summary>The internal nodes, by suffix, in port order after the external ports; the first is always the quadrature
    /// node <c>q</c>. The elaborator mints <c>__carrier_&lt;instance&gt;_&lt;suffix&gt;</c> for each.</summary>
    public virtual IReadOnlyList<string> InternalNodes => ["q"];

    /// <summary>The quadrature node's port index.</summary>
    protected int Q => ExternalPortCount;

    public override int PortCount => ExternalPortCount + InternalNodes.Count;
    public override ModelKind Kind => ModelKind.Nonlinear;

    /// <summary>Each block is a dependent source: its output is a law of its inputs.</summary>
    public override Activity Activity => Activity.ActiveExact;

    // Nonlinear: the linear engines call StampLinearized (the base, which includes the quadrature bucket).
    public override void Stamp(IMnaContext mna, ElaboratedComponent c, double omega) { }

    /// <summary><c>H₂(ω) = j·sign(ω)</c>: zero at DC, so a DC solve drops the bucket.</summary>
    public override Complex Weight(int w, double omega)
        => w == QuadratureW ? new Complex(0.0, Math.Sign(omega)) : base.Weight(w, omega);

    /// <summary>The block's own law: every port's current and charge and their Jacobians, except the quadrature row,
    /// which <see cref="Evaluate"/> writes. The arrays arrive zeroed.</summary>
    protected abstract void Law(double[] v, double[] i, double[] q, double[,] dg, double[,] dc);

    public override NonlinearResult Evaluate(in PortVoltages v)
    {
        int n = PortCount;
        var i = new double[n];
        var q = new double[n];
        var dg = new double[n, n];
        var dc = new double[n, n];
        Law(v.Voltages, i, q, dg, dc);

        // The quadrature row: g·v_q at w = 0, and g·v_in weighted by j·sign(ω), so V_q = −j·V_in at every harmonic.
        i[Q] = _gq * v[Q];
        dg[Q, Q] = _gq;
        var value = new double[n];
        var jac = new double[n, n];
        value[Q] = _gq * v[0];
        jac[Q, 0] = _gq;
        return new NonlinearResult(i, q, dg, dc, [new WeightedTerm(QuadratureW, value, jac)]);
    }

    /// <summary>The whole grid at once into the caller's buffers, the quadrature bucket included: one set of scratch arrays
    /// per call rather than six allocations a sample, which is most of an envelope step's cost.</summary>
    public override bool PrefersGridEvaluate => true;

    /// <inheritdoc/>
    public override void EvaluateGrid(ReadOnlySpan<double> portVoltages, ReadOnlySpan<double> controlCurrents,
                                      int sampleCount, GridResult into)
    {
        int n = PortCount;
        into.EnsureShape(n, 0, sampleCount);
        into.ClearBlocks();
        into.ResetTerms();
        var term = into.AddTerm(QuadratureW);
        var v = new double[n];
        var i = new double[n];
        var q = new double[n];
        var dg = new double[n, n];
        var dc = new double[n, n];
        for (int t = 0; t < sampleCount; t++)
        {
            for (int p = 0; p < n; p++) v[p] = portVoltages[p * sampleCount + t];
            Array.Clear(i); Array.Clear(q); Array.Clear(dg); Array.Clear(dc);
            Law(v, i, q, dg, dc);
            i[Q] = _gq * v[Q];
            dg[Q, Q] = _gq;
            for (int p = 0; p < n; p++)
            {
                into.I[into.PortBase(p) + t] = i[p];
                into.Q[into.PortBase(p) + t] = q[p];
                for (int r = 0; r < n; r++)
                {
                    into.Dg[into.JacBase(p, r) + t] = dg[p, r];
                    into.Dc[into.JacBase(p, r) + t] = dc[p, r];
                }
            }
            term.Value[into.PortBase(Q) + t] = _gq * v[0];
            term.Jac[into.JacBase(Q, 0) + t] = _gq;
        }
    }

    /// <summary>1/Z for a port impedance; a zero or negative one is refused by the factory before it gets here.</summary>
    protected static double Conductance(double z) => 1.0 / z;

    /// <summary>The peak voltage a power in dBm puts across a matched <paramref name="z"/>: √(2·Z·P).</summary>
    public static double PeakVolts(double dbm, double z) => Math.Sqrt(2.0 * z * 1e-3 * Math.Pow(10.0, dbm / 10.0));
}
