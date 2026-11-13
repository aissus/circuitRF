using System.Numerics;
using CircuitRF.Core;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Expressions;
using Xunit;

namespace CircuitRF.Core.Tests.Devices;

/// <summary>
/// The polar-transmitter blocks' laws on one carrier, written out by hand from the parameters, and every Jacobian entry —
/// the quadrature bucket's included — against a central difference of the model's own currents. A carrier of amplitude
/// <c>a</c> and phase <c>ψ</c> is fed as its analytic pair, <c>v_in = a·cos ψ</c> and <c>v_q = a·sin ψ</c>, which is what the
/// quadrature node holds in a solve (gated end to end in Engine.Tests).
/// </summary>
public class CarrierBlockModelTests
{
    private const double Z = 50;

    /// <summary>The output port's law: a matched load reads half the open-circuit voltage, so i_out = (v_out − 2·law)/Z
    /// gives law = −Z·i_out/2 at v_out = 0.</summary>
    private static double Law(ComponentModel m, double[] v)
    {
        var w = (double[])v.Clone();
        w[1] = 0;
        return -Z * m.Evaluate(new PortVoltages(w)).I[1] / 2;
    }

    [Theory]
    [InlineData(0.3, 0.0)]
    [InlineData(0.3, 1.1)]
    [InlineData(1e-3, -2.5)]
    public void Limiter_OutputsItsLevelAtTheInputsPhase(double a, double psi)
    {
        var m = new LimiterModel(poutDbm: 10, kneeDbm: -100, Z, Z);
        double a0 = Math.Sqrt(2 * Z * 1e-2);                 // 10 dBm into 50 ohm, V peak
        double vk = Math.Sqrt(2 * Z * 1e-13);                // the knee
        double expect = a0 * a / Math.Sqrt(a * a + vk * vk);
        // the in-phase and quadrature parts of the output: one carrier at ψ, of the limited amplitude
        Assert.Equal(expect * Math.Cos(psi), Law(m, [a * Math.Cos(psi), 0, a * Math.Sin(psi)]), 12);
        Assert.Equal(expect * Math.Sin(psi), Law(m, [a * Math.Sin(psi), 0, -a * Math.Cos(psi)]), 12);
    }

    [Fact]
    public void Limiter_IsThreeDecibelsDownAtItsKnee()
    {
        var m = new LimiterModel(poutDbm: 0, kneeDbm: -20, Z, Z);
        double knee = Math.Sqrt(2 * Z * 1e-5);               // −20 dBm available, matched: the port amplitude
        Assert.Equal(m.OutputVolts / Math.Sqrt(2), Law(m, [knee, 0, 0]), 12);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.7)]
    [InlineData(-3.0)]
    public void PhaseModulator_TurnsTheCarrierByKpTimesTheVoltage(double vph)
    {
        var m = new PhaseModulatorModel(kpRadPerVolt: 2.0, gainDb: 6, Z, Z, Z);
        double g = Math.Pow(10, 6.0 / 20), a = 0.4, psi = 0.3, phi = 2.0 * vph;
        Assert.Equal(g * a * Math.Cos(psi + phi), Law(m, [a * Math.Cos(psi), 0, vph, a * Math.Sin(psi)]), 12);
    }

    [Theory]
    [InlineData(0.5, 0.0, 0.0)]
    [InlineData(2.0, 0.0, 15.0)]
    [InlineData(2.0, 1.5, -10.0)]
    [InlineData(-0.4, 0.0, 15.0)]                            // no supply below zero
    public void SupplyAmp_SetsItsOutputBySupplyNotByDrive(double vs, double vsat, double ampm)
    {
        var m = new SupplyModulatedAmpModel(ks: 3, vsat, ampm, kneeDbm: -100, Z, Z, Z);
        double u = Math.Max(vs, 0);
        double amp = vsat > 0 ? 3 * vsat * Math.Tanh(u / vsat) : 3 * u;
        double th = ampm * Math.PI / 180 * u, psi = -0.8;
        double vk = Math.Sqrt(2 * Z * 1e-13);                // the −100 dBm knee
        foreach (double a in new[] { 0.05, 0.5, 2.0 })      // the drive level does not matter, above the knee
            Assert.Equal(amp * a / Math.Sqrt(a * a + vk * vk) * Math.Cos(psi + th),
                         Law(m, [a * Math.Cos(psi), 0, vs, a * Math.Sin(psi)]), 12);
    }

    [Fact]
    public void Detector_ReadsTheAmplitude_ThroughItsPoleAtDc()
    {
        var plain = new EnvelopeDetectorModel(kd: 2.5, bandwidthHz: 0, Z, Z);
        Assert.Equal(2.5 * 0.3, Law(plain, [0.3 * Math.Cos(1), 0, 0.3 * Math.Sin(1)]), 12);

        // With a pole the detected value sits on the pole's node: at DC (no charge flowing) v_p = Kd·a, and the output
        // law is that node's voltage.
        var pole = new EnvelopeDetectorModel(kd: 2.5, bandwidthHz: 1e6, Z, Z);
        Assert.Equal(["q", "p"], pole.InternalNodes);
        var r = pole.Evaluate(new PortVoltages([0.3, 0, 0, 0.75]));
        Assert.Equal(0.0, r.I[3], 12);                      // v_p = 2.5·0.3: the pole's row is balanced
        Assert.Equal(1e9 / (2 * Math.PI * 1e6 * Z), r.Dc[3, 3] * 1e9, 9);
        Assert.Equal(0.75, Law(pole, [0.3, 0, 0, 0.75]), 12);
    }

    /// <summary>The quadrature row is g·v_q at w = 0 and g·v_in weighted by j·sign(ω): V_q = −j·V_in at a harmonic, 0 at DC.</summary>
    [Fact]
    public void TheQuadratureRow_HoldsTheHilbertTransform()
    {
        var m = new LimiterModel(0, -100, Z, Z);
        var r = m.Evaluate(new PortVoltages([0.2, 0, 0.1]));
        var term = Assert.Single(r.Terms);
        double g = 1 / Z;
        Assert.Equal(g * 0.1, r.I[2], 15);
        Assert.Equal(g * 0.2, term.Value[2], 15);
        // the row at harmonic ω: g·V_q + j·g·V_in = 0
        var vIn = new Complex(0.3, -0.2);
        var vq = -(m.Weight(term.W, 1e9) * term.Jac[2, 0] * vIn) / r.Dg[2, 2];
        Assert.Equal(-Complex.ImaginaryOne * vIn, vq);
        Assert.Equal(Complex.Zero, m.Weight(term.W, 0.0));
        Assert.Equal(Complex.Conjugate(m.Weight(term.W, 1e9)), m.Weight(term.W, -1e9));
    }

    public static TheoryData<string> Blocks() => ["detector", "detector-pole", "limiter", "phasemod", "supplyamp", "supplyamp-sat"];

    private static ComponentModel Make(string which) => which switch
    {
        "detector"      => new EnvelopeDetectorModel(1.7, 0, Z, 75),
        "detector-pole" => new EnvelopeDetectorModel(1.7, 5e6, Z, 75),
        "limiter"       => new LimiterModel(3, -30, Z, 75),
        "phasemod"      => new PhaseModulatorModel(1.3, -2, Z, 75, 100),
        "supplyamp"     => new SupplyModulatedAmpModel(2.5, 0, 20, -30, Z, 75, 100),
        _               => new SupplyModulatedAmpModel(2.5, 1.2, -12, -30, Z, 75, 100),
    };

    /// <summary>Every ∂I/∂V and ∂(bucket)/∂V entry against a central difference, at a point away from the kinks.</summary>
    [Theory]
    [MemberData(nameof(Blocks))]
    public void EveryJacobianEntry_IsTheDerivativeOfItsCurrent(string which)
    {
        var m = Make(which);
        int n = m.PortCount;
        var rng = new Random(7);
        var v = Enumerable.Range(0, n).Select(_ => 0.2 + 0.6 * rng.NextDouble()).ToArray();
        var r = m.Evaluate(new PortVoltages(v));
        const double h = 1e-6;
        for (int q = 0; q < n; q++)
        {
            var vp = (double[])v.Clone(); vp[q] += h;
            var vm = (double[])v.Clone(); vm[q] -= h;
            var rp = m.Evaluate(new PortVoltages(vp));
            var rm = m.Evaluate(new PortVoltages(vm));
            for (int p = 0; p < n; p++)
            {
                Assert.Equal((rp.I[p] - rm.I[p]) / (2 * h), r.Dg[p, q], 7);
                Assert.Equal((rp.Q[p] - rm.Q[p]) / (2 * h), r.Dc[p, q], 12);
                Assert.Equal((rp.Terms[0].Value[p] - rm.Terms[0].Value[p]) / (2 * h), r.Terms[0].Jac[p, q], 7);
            }
        }
    }

    /// <summary>The grid path, which the harmonic-balance and envelope device passes take, writes exactly what the scalar
    /// path returns at every sample, the quadrature bucket included.</summary>
    [Theory]
    [MemberData(nameof(Blocks))]
    public void TheGridPath_IsTheScalarPath(string which)
    {
        var m = Make(which);
        Assert.True(m.PrefersGridEvaluate);
        int n = m.PortCount, s = 5;
        var rng = new Random(11);
        var pv = Enumerable.Range(0, n * s).Select(_ => rng.NextDouble() - 0.3).ToArray();
        var g = new GridResult();
        m.EvaluateGrid(pv, [], s, g);
        var term = Assert.Single(g.LiveTerms.ToArray());
        for (int t = 0; t < s; t++)
        {
            var r = m.Evaluate(new PortVoltages([.. Enumerable.Range(0, n).Select(p => pv[p * s + t])]));
            Assert.Equal(r.Terms[0].W, term.W);
            for (int p = 0; p < n; p++)
            {
                Assert.Equal(r.I[p], g.I[g.PortBase(p) + t]);
                Assert.Equal(r.Q[p], g.Q[g.PortBase(p) + t]);
                Assert.Equal(r.Terms[0].Value[p], term.Value[g.PortBase(p) + t]);
                for (int q = 0; q < n; q++)
                {
                    Assert.Equal(r.Dg[p, q], g.Dg[g.JacBase(p, q) + t]);
                    Assert.Equal(r.Dc[p, q], g.Dc[g.JacBase(p, q) + t]);
                    Assert.Equal(r.Terms[0].Jac[p, q], term.Jac[g.JacBase(p, q) + t]);
                }
            }
        }
    }

    [Fact]
    public void ANonPositiveImpedance_IsRefusedByName()
    {
        var ex = Assert.Throws<ParameterRefusalException>(() => ComponentModelFactory.TryCreate(
            "Limiter", new Dictionary<string, Value> { ["Zout"] = new(0.0) }, null, 25));
        Assert.Contains("Zout", ex.Message);
    }
}
