using System.Globalization;
using CircuitRF.Diagnostics;

namespace CircuitRF.Core.Devices;

/// <summary>
/// The refusals of the polar-transmitter blocks (<see cref="CarrierBlockModel"/>) and of a polar pair of MOD_Sources
/// (<see cref="ModSourceModel.Polar"/>), as coded diagnostics rather than bare sentences
/// (tests/Firewall.Tests/user-facing-text-allowlist.txt says why).
/// </summary>
public static class CarrierBlockDiagnostics
{
    private static string G6(double v) => v.ToString("G6", CultureInfo.InvariantCulture);

    /// <summary>A port impedance that is zero or negative: the block divides by it.</summary>
    public static Diagnostic ImpedanceNotPositive(string type, string parameter, double ohms) => Diagnostic.Create(
        "carrier.z-not-positive", DiagnosticSeverity.Error,
        "{type}: {parameter} must be a positive impedance (got {ohms} ohms).",
        ("type", type), ("parameter", parameter), ("ohms", G6(ohms)));

    /// <summary>A Polar word that is neither half.</summary>
    public static Diagnostic PolarWord(string source, string word) => Diagnostic.Create(
        "modsource.polar.word", DiagnosticSeverity.Error,
        "MOD_Source '{source}': Polar must be amp or phase (got '{word}').",
        ("source", source), ("word", word));

    /// <summary>Two polar halves that play different signals.</summary>
    public static Diagnostic PolarMismatch(string source, string half, string other, string otherHalf) => Diagnostic.Create(
        "modsource.polar.mismatch", DiagnosticSeverity.Error,
        "MOD_Source '{source}' (Polar={half}) plays a different signal from '{other}' (Polar={otherHalf}): the halves of a " +
        "polar pair must state the same Fc and the same carriers.",
        ("source", source), ("half", half), ("other", other), ("otherHalf", otherHalf));

    /// <summary>A phase half with no amplitude half to measure the pair through.</summary>
    public static Diagnostic PolarPhaseAlone(string source) => Diagnostic.Create(
        "modsource.polar.phase-alone", DiagnosticSeverity.Error,
        "MOD_Source '{source}' is a phase half (Polar=phase) with no amplitude half (Polar=amp) beside it: the pair is " +
        "measured through its amplitude half.",
        ("source", source));

    /// <summary>A polar half in the multi-fundamental envelope, which has no one baseband to drive.</summary>
    public static Diagnostic PolarMultiFundamental(string source, string half) => Diagnostic.Create(
        "modsource.polar.multi-fundamental", DiagnosticSeverity.Error,
        "MOD_Source '{source}' is a polar half (Polar={half}), which drives the baseband of a single envelope: the " +
        "multi-fundamental envelope has no one baseband to drive. Set Envelope=single.",
        ("source", source), ("half", half));
}
