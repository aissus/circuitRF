namespace CircuitRF.Core.Devices;

/// <summary>
/// The ideal phase modulator: an RF carrier in, a baseband phase input, and the carrier out turned by that phase.
/// Ports <c>[in+, in−, out+, out−, ph+, ph−]</c> and the quadrature node (<see cref="CarrierBlockModel"/>).
///
/// <code>
///   φ     = Kp · v_ph
///   i_in  = v_in / Zin          i_ph = v_ph / Zph
///   i_out = ( v_out − 2·G·(cos φ·v_in − sin φ·v_q) ) / Zout
/// </code>
/// <para>Into a matched load the output carrier is <c>G·e^(jφ)</c> times the input's: <c>S21 = 10^(Gain/20)·e^(j·Kp·v_ph)</c>,
/// with the phase input read as a baseband voltage. A phase input carrying RF is multiplied into the carrier like any
/// other value; the block means a baseband one.</para>
/// </summary>
public sealed class PhaseModulatorModel : CarrierBlockModel
{
    private readonly double _gi, _go, _gp, _g, _kp;

    /// <param name="kpRadPerVolt">Phase per volt at the phase input, rad/V.</param>
    /// <param name="gainDb">The carrier's gain through the block, dB.</param>
    public PhaseModulatorModel(double kpRadPerVolt, double gainDb, double zIn, double zOut, double zPh) : base(3, zIn)
    {
        _gi = Conductance(zIn);
        _go = Conductance(zOut);
        _gp = Conductance(zPh);
        _g = Math.Pow(10.0, gainDb / 20.0);
        _kp = kpRadPerVolt;
    }

    /// <summary>Phase per volt at the phase input, rad/V.</summary>
    public double RadiansPerVolt => _kp;

    public override string[] TerminalNames => ["in", "out", "ph", "quad"];

    /// <summary>The output's dependence on the carrier, its quadrature and the phase input.</summary>
    public override IReadOnlyList<(int P, int Q)> ControlledConductances => [(1, 0), (1, 2), (1, 3)];

    protected override void Law(double[] v, double[] i, double[] q, double[,] dg, double[,] dc)
    {
        double vi = v[0], vp = v[2], vq = v[Q];
        double phi = _kp * vp, c = Math.Cos(phi), s = Math.Sin(phi);
        i[0] = _gi * vi;
        dg[0, 0] = _gi;
        i[2] = _gp * vp;
        dg[2, 2] = _gp;
        double k = 2 * _go * _g;
        i[1] = _go * v[1] - k * (c * vi - s * vq);
        dg[1, 1] = _go;
        dg[1, 0] = -k * c;
        dg[1, Q] = k * s;
        dg[1, 2] = k * _kp * (s * vi + c * vq);
    }
}
