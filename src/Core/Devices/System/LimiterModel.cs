namespace CircuitRF.Core.Devices;

/// <summary>
/// The ideal limiter: a constant-envelope stage. The output carrier has a fixed amplitude and the input's phase.
/// Ports <c>[in+, in−, out+, out−]</c> and the quadrature node (<see cref="CarrierBlockModel"/>).
///
/// <code>
///   i_in  = v_in / Zin
///   i_out = ( v_out − 2·A·v_in / √(v_in² + v_q² + Vk²) ) / Zout
/// </code>
/// <para>For an input carrier of amplitude a the output into a matched load is <c>A·a/√(a² + Vk²)</c> at the input's
/// phase: <c>A</c> (from <c>Pout</c>) well above the knee, and a linear stage of gain <c>A/Vk</c> below it. <c>Vk</c>
/// is the knee, from <c>Knee</c>: the available input power at which the output is 3 dB below <c>Pout</c>. It is the
/// block's one smoothness parameter, and what keeps a zero input finite.</para>
/// </summary>
public sealed class LimiterModel : CarrierBlockModel
{
    private readonly double _gi, _go, _a, _vk2;

    /// <param name="poutDbm">Output power into a matched load, dBm.</param>
    /// <param name="kneeDbm">Available input power at which the output is 3 dB down, dBm.</param>
    public LimiterModel(double poutDbm, double kneeDbm, double zIn, double zOut) : base(2, zIn)
    {
        _gi = Conductance(zIn);
        _go = Conductance(zOut);
        _a = PeakVolts(poutDbm, zOut);
        double vk = PeakVolts(kneeDbm, zIn);
        _vk2 = vk * vk;
    }

    /// <summary>The output amplitude into a matched load, V peak.</summary>
    public double OutputVolts => _a;

    /// <summary>The knee, V peak at the input port.</summary>
    public double KneeVolts => Math.Sqrt(_vk2);

    public override string[] TerminalNames => ["in", "out", "quad"];

    /// <summary>The output's dependence on the input and its quadrature.</summary>
    public override IReadOnlyList<(int P, int Q)> ControlledConductances => [(1, 0), (1, 2)];

    protected override void Law(double[] v, double[] i, double[] q, double[,] dg, double[,] dc)
    {
        double vi = v[0], vq = v[Q];
        double r2 = vi * vi + vq * vq + _vk2, r = Math.Sqrt(r2), r3 = r2 * r;
        i[0] = _gi * vi;
        dg[0, 0] = _gi;
        i[1] = _go * (v[1] - 2 * _a * vi / r);
        dg[1, 1] = _go;
        dg[1, 0] = -2 * _go * _a * (vq * vq + _vk2) / r3;
        dg[1, Q] = 2 * _go * _a * vi * vq / r3;
    }
}
