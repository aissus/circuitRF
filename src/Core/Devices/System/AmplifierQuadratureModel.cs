using System.Numerics;
using CircuitRF.Core.Elaboration;

namespace CircuitRF.Core.Devices;

/// <summary>
/// The quadrature an amplifier with a bandpass law reads (<see cref="AmplifierLaw"/>): a linear companion the Elaborator
/// places beside it, holding an internal net at the Hilbert transform of the amplifier's input,
/// <c>V_q = −j·sign(ω)·V_in</c>. Two ports, four nets, <c>[in+, in−, q, 0]</c>: it senses the input and draws nothing from
/// it, and drives the net through a conductance of its own, <c>i_q = G·(v_q + j·sign(ω)·v_in)</c>, the amplifier's third
/// port drawing nothing from it either.
///
/// <para><b>Linear, so every analysis carries it at every frequency it solves at</b> — single- and multi-tone harmonic
/// balance and the envelope alike stamp the linear network per frequency, which no weighted term on the amplifier
/// itself would reach. It is a frequency-flat −90° held at every frequency, as the 90° hybrid is: an idealisation with no
/// causal realisation, and exactly what a bandpass law means.</para>
///
/// <para><b>None below <see cref="FloorHz"/></b>: at DC and at the envelope's baseband product the quadrature is zero,
/// so the net carries nothing there, and nothing at those frequencies reaches an amplifier's input in an RF bench.</para>
/// </summary>
public sealed class AmplifierQuadratureModel : ComponentModel
{
    /// <summary>The frequency below which the quadrature is zero, Hz.</summary>
    public const double FloorHz = 1e3;

    /// <summary>The conductance the net is driven through, S: one 50 Ω port's, so the row is scaled as its neighbours.</summary>
    private const double G = 1.0 / 50;

    /// <summary>The amplifier it belongs to, for its noise row.</summary>
    public string Amplifier { get; }

    public AmplifierQuadratureModel(string amplifier) => Amplifier = amplifier;

    public override int PortCount => 2;
    public override ModelKind Kind => ModelKind.Linear;

    /// <summary>A dependent source, removed exactly by dropping the sensed term (<see cref="StampPassive"/>).</summary>
    public override Activity Activity => Activity.ActiveExact;

    public override string? PassivationNote => "the sensed input → 0; the net's own conductance kept";

    public override void Stamp(IMnaContext mna, ElaboratedComponent c, double omega)
    {
        int ip = c.Nodes[0], im = c.Nodes[1], q = c.Nodes[2];
        mna.AddBlockAdmittance(q, q, new Complex(G, 0));
        if (Math.Abs(omega) < 2 * Math.PI * FloorHz) return;
        var y = new Complex(0, G * Math.Sign(omega));
        mna.AddBlockAdmittance(q, ip, y);
        mna.AddBlockAdmittance(q, im, -y);
    }

    public override void StampPassive(IMnaContext mna, ElaboratedComponent c, double omega)
        => mna.AddBlockAdmittance(c.Nodes[2], c.Nodes[2], new Complex(G, 0));
}
