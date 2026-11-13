namespace CircuitRF.Core.Devices;

/// <summary>
/// The ideal envelope detector: an RF carrier in, its amplitude out at baseband, through an optional one-pole bandwidth.
/// Ports <c>[in+, in−, out+, out−]</c>, the quadrature node and, with a bandwidth, the pole's node
/// (<see cref="CarrierBlockModel"/>).
///
/// <code>
///   a     = √(v_in² + v_q²)                      the carrier's amplitude
///   i_in  = v_in / Zin
///   i_p   = ( v_p − Kd·a ) / Zout + C·dv_p/dt    C = 1 / (2π·BW·Zout)    (BW &gt; 0)
///   i_out = ( v_out − 2·v_p ) / Zout             (v_p = Kd·a when BW = 0)
/// </code>
/// <para>Into a matched load the output is <c>Kd·a</c> volts, through <c>1/(1 + jf/BW)</c>. The pole sits on a node of
/// its own, so the load does not move it. A DC component at the input is part of what it reads.</para>
/// </summary>
public sealed class EnvelopeDetectorModel : CarrierBlockModel
{
    private readonly double _gi, _go, _kd, _cp;

    /// <param name="kd">Output volts into a matched load per volt of carrier amplitude, V/V.</param>
    /// <param name="bandwidthHz">The one-pole bandwidth, Hz; 0 is none.</param>
    public EnvelopeDetectorModel(double kd, double bandwidthHz, double zIn, double zOut) : base(2, zIn)
    {
        _gi = Conductance(zIn);
        _go = Conductance(zOut);
        _kd = kd;
        BandwidthHz = bandwidthHz > 0 ? bandwidthHz : 0;
        _cp = BandwidthHz > 0 ? _go / (2 * Math.PI * BandwidthHz) : 0;
    }

    /// <summary>The one-pole bandwidth, Hz; 0 when there is none.</summary>
    public double BandwidthHz { get; }

    private bool HasPole => BandwidthHz > 0;
    private int P => Q + 1;

    public override IReadOnlyList<string> InternalNodes => HasPole ? ["q", "p"] : ["q"];

    public override string[] TerminalNames => HasPole ? ["in", "out", "quad", "pole"] : ["in", "out", "quad"];

    /// <summary>The detected amplitude's dependence on the carrier and its quadrature, and the output's on the pole.</summary>
    public override IReadOnlyList<(int P, int Q)> ControlledConductances
        => HasPole ? [(P, 0), (P, Q), (1, P)] : [(1, 0), (1, Q)];

    /// <summary>The pole remembers 1/(2π·BW): an envelope's parallel segments warm up past it.</summary>
    public override (double Seconds, string State)? SlowestTimeConstant()
        => HasPole ? (1.0 / (2 * Math.PI * BandwidthHz), "envelope detector bandwidth") : null;

    protected override void Law(double[] v, double[] i, double[] q, double[,] dg, double[,] dc)
    {
        double vi = v[0], vq = v[Q];
        double a = Math.Sqrt(vi * vi + vq * vq);
        // The amplitude's gradient is the unit vector (v_in, v_q)/a; at a = 0 it has none, and 0 is the subgradient.
        double ui = a > 0 ? vi / a : 0, uq = a > 0 ? vq / a : 0;
        i[0] = _gi * vi;
        dg[0, 0] = _gi;
        i[1] = _go * v[1];
        dg[1, 1] = _go;
        if (!HasPole)
        {
            i[1] -= 2 * _go * _kd * a;
            dg[1, 0] = -2 * _go * _kd * ui;
            dg[1, Q] = -2 * _go * _kd * uq;
            return;
        }
        double vp = v[P];
        i[P] = _go * (vp - _kd * a);
        dg[P, P] = _go;
        dg[P, 0] = -_go * _kd * ui;
        dg[P, Q] = -_go * _kd * uq;
        q[P] = _cp * vp;
        dc[P, P] = _cp;
        i[1] -= 2 * _go * vp;
        dg[1, P] = -2 * _go;
    }
}
