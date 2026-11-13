namespace CircuitRF.Core.Devices;

/// <summary>
/// The supply-modulated amplifier: a saturated stage whose output amplitude is set by a baseband supply (the AM path of a
/// polar transmitter) and whose phase follows its RF input, with AM-AM and AM-PM against the supply.
/// Ports <c>[in+, in−, out+, out−, vs+, vs−]</c> and the quadrature node (<see cref="CarrierBlockModel"/>).
///
/// <code>
///   u = max(v_s, 0)
///   A(v_s) = Ks · u                       (Vsat = 0)
///   A(v_s) = Ks · Vsat · tanh(u / Vsat)    (Vsat > 0: AM-AM compression)
///   θ(v_s) = AMPM · u                      (AM-PM, degrees per volt)
///   i_in  = v_in / Zin          i_s = v_s / Zs
///   i_out = ( v_out − 2·A·(cos θ·v_in − sin θ·v_q) / √(v_in² + v_q² + Vk²) ) / Zout
/// </code>
/// <para>Into a matched load the output carrier is <c>A(v_s)·e^(jθ(v_s))</c> at the input's phase, whatever the input's
/// amplitude above the knee <c>Vk</c> (<c>Knee</c>, as the <see cref="LimiterModel"/>'s): the drive saturates the stage,
/// and the supply alone sets how much comes out. A supply below zero is no supply.</para>
/// </summary>
public sealed class SupplyModulatedAmpModel : CarrierBlockModel
{
    private readonly double _gi, _go, _gs, _ks, _vsat, _pm, _vk2;

    /// <param name="ks">Output peak volts into a matched load per supply volt, V/V.</param>
    /// <param name="vsat">The AM-AM compression scale, V; 0 is a linear supply law.</param>
    /// <param name="ampmDegPerVolt">The output phase's slope with the supply, degrees per volt.</param>
    /// <param name="kneeDbm">Available RF input power at which the output is 3 dB down, dBm.</param>
    public SupplyModulatedAmpModel(double ks, double vsat, double ampmDegPerVolt, double kneeDbm,
                                   double zIn, double zOut, double zSupply) : base(3, zIn)
    {
        _gi = Conductance(zIn);
        _go = Conductance(zOut);
        _gs = Conductance(zSupply);
        _ks = ks;
        _vsat = vsat;
        _pm = ampmDegPerVolt * Math.PI / 180.0;
        double vk = PeakVolts(kneeDbm, zIn);
        _vk2 = vk * vk;
    }

    /// <summary>The output amplitude (V peak, matched load) and phase (rad) at a supply of <paramref name="vs"/> volts:
    /// the AM-AM and AM-PM laws, with their slopes.</summary>
    public (double A, double dA, double Theta, double dTheta) SupplyLaw(double vs)
    {
        if (vs <= 0) return (0, 0, 0, 0);
        double a, da;
        if (_vsat > 0)
        {
            double t = Math.Tanh(vs / _vsat);
            a = _ks * _vsat * t;
            da = _ks * (1 - t * t);
        }
        else { a = _ks * vs; da = _ks; }
        return (a, da, _pm * vs, _pm);
    }

    public override string[] TerminalNames => ["in", "out", "vs", "quad"];

    /// <summary>The output's dependence on the carrier, its quadrature and the supply.</summary>
    public override IReadOnlyList<(int P, int Q)> ControlledConductances => [(1, 0), (1, 2), (1, 3)];

    protected override void Law(double[] v, double[] i, double[] q, double[,] dg, double[,] dc)
    {
        double vi = v[0], vs = v[2], vq = v[Q];
        i[0] = _gi * vi;
        dg[0, 0] = _gi;
        i[2] = _gs * vs;
        dg[2, 2] = _gs;
        i[1] = _go * v[1];
        dg[1, 1] = _go;

        var (a, da, th, dth) = SupplyLaw(vs);
        double r2 = vi * vi + vq * vq + _vk2, r = Math.Sqrt(r2), r3 = r2 * r;
        double c = Math.Cos(th), s = Math.Sin(th), n = c * vi - s * vq;
        double k = 2 * _go;
        i[1] -= k * a * n / r;
        dg[1, 0] = -k * a * (c / r - n * vi / r3);
        dg[1, Q] = -k * a * (-s / r - n * vq / r3);
        dg[1, 2] = -k * (da * n / r + a * dth * (-s * vi - c * vq) / r);
    }
}
