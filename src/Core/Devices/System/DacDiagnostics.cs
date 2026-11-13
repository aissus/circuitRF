using System.Globalization;
using CircuitRF.Diagnostics;

namespace CircuitRF.Core.Devices;

/// <summary>
/// The refusals and notes of the DAC (<see cref="DacModel"/>) and of a MOD_Source's baseband rails
/// (<see cref="ModSourceModel.Baseband"/>), as coded diagnostics (tests/Firewall.Tests/user-facing-text-allowlist.txt says why).
/// </summary>
public static class DacDiagnostics
{
    private static string G6(double v) => v.ToString("G6", CultureInfo.InvariantCulture);
    private static string Mhz(double hz) => (hz / 1e6).ToString("0.###", CultureInfo.InvariantCulture) + " MHz";

    /// <summary>A DAC parameter outside what the block can be.</summary>
    public static Diagnostic Parameter(string parameter, string rule, double value) => Diagnostic.Create(
        "dac.parameter", DiagnosticSeverity.Error,
        "DAC: {parameter} must be {rule} (got {value}).",
        ("parameter", parameter), ("rule", rule), ("value", G6(value)));

    /// <summary>No Fs: a DAC's sample rate is what it is, and none is assumed.</summary>
    public static Diagnostic SampleRateMissing() => Diagnostic.Create(
        "dac.fs-missing", DiagnosticSeverity.Error, "DAC needs Fs, its sample rate.");

    /// <summary>A Mode word that is neither pulse.</summary>
    public static Diagnostic ModeWord(string word) => Diagnostic.Create(
        "dac.mode", DiagnosticSeverity.Error, "DAC: Mode must be nrz or rz (got '{word}').", ("word", word));

    /// <summary>A DAC in harmonic balance, which has no stream to sample.</summary>
    public static Diagnostic NotInHarmonicBalance(string instance) => Diagnostic.Create(
        "dac.harmonic-balance", DiagnosticSeverity.Error,
        "DAC '{instance}' converts a digital stream sampled at Fs, and harmonic balance has none: its tones are " +
        "harmonics of one frequency, and a held tone's images k·Fs ± f are not. Run it in the circuit envelope, its " +
        "input a MOD_Source with Baseband=i or q.",
        ("instance", instance));

    /// <summary>A DAC in the multi-fundamental envelope, which has no one baseband for it to drive.</summary>
    public static Diagnostic NotInMultiFundamental(string instance) => Diagnostic.Create(
        "dac.multi-fundamental", DiagnosticSeverity.Error,
        "DAC '{instance}' drives the baseband of a single envelope, which the multi-fundamental envelope does not have. " +
        "Set Envelope=single.",
        ("instance", instance));

    /// <summary>A DAC whose input is not one baseband rail's own net.</summary>
    public static Diagnostic InputNotARail(string instance) => Diagnostic.Create(
        "dac.input", DiagnosticSeverity.Error,
        "DAC '{instance}': its input must be the net of one MOD_Source with Baseband=i or q, and nothing else: that " +
        "rail of the Signal Designer's record is the stream it converts.",
        ("instance", instance));

    /// <summary>A phase-noise mask on the rail a DAC converts.</summary>
    public static Diagnostic PhaseNoiseOnStream(string instance, string source) => Diagnostic.Create(
        "dac.phase-noise", DiagnosticSeverity.Error,
        "DAC '{instance}': its stream '{source}' states a PhaseNoise mask, which a digital stream does not have. Put " +
        "it on the LO.",
        ("instance", instance), ("source", source));

    /// <summary>An Fs that does not put whole samples in the record: the stream would not be periodic.</summary>
    public static Diagnostic RecordNotWhole(string instance, double fsHz, double recordSeconds) => Diagnostic.Create(
        "dac.record", DiagnosticSeverity.Error,
        "DAC '{instance}': Fs = {fs} MHz does not put a whole number of samples in the {record} ms record.",
        ("instance", instance), ("fs", (fsHz / 1e6).ToString("G9", CultureInfo.InvariantCulture)), ("record", G6(recordSeconds * 1e3)));

    /// <summary>An Fs below what the rail carries: the stream could not hold the signal.</summary>
    public static Diagnostic RateBelowSignal(string instance, double fsHz, double needHz) => Diagnostic.Create(
        "dac.rate-below-signal", DiagnosticSeverity.Error,
        "DAC '{instance}': Fs = {fs} is below the {need} its stream needs to carry the signal (twice its widest " +
        "offset from the carrier).",
        ("instance", instance), ("fs", Mhz(fsHz)), ("need", Mhz(needHz)));

    /// <summary>What a run carries of a DAC's output, and what it leaves out.</summary>
    public static Diagnostic Band(string instance, double bandHz, double firstImageHz, double imageDb) => Diagnostic.Create(
        "dac.band", DiagnosticSeverity.Info,
        "DAC '{instance}': its output is carried within ±{band}, the run's signal band. What it makes beyond is left " +
        "out: its images from {first} up, each at least {level} dB below the signal at its output before any filter, " +
        "and its quantisation noise above the band.",
        ("instance", instance), ("band", Mhz(bandHz)), ("first", Mhz(firstImageHz)),
        ("level", imageDb.ToString("0.#", CultureInfo.InvariantCulture)));

    /// <summary>A Baseband word that is neither rail.</summary>
    public static Diagnostic BasebandWord(string source, string word) => Diagnostic.Create(
        "modsource.baseband.word", DiagnosticSeverity.Error,
        "MOD_Source '{source}': Baseband must be i or q (got '{word}').", ("source", source), ("word", word));

    /// <summary>A source that states both a polar half and a baseband rail.</summary>
    public static Diagnostic BasebandAndPolar(string source) => Diagnostic.Create(
        "modsource.baseband.and-polar", DiagnosticSeverity.Error,
        "MOD_Source '{source}' states both Polar and Baseband: it plays one share of its signal, so give it one.",
        ("source", source));

    /// <summary>Two rails that play different signals.</summary>
    public static Diagnostic BasebandMismatch(string source, string other) => Diagnostic.Create(
        "modsource.baseband.mismatch", DiagnosticSeverity.Error,
        "MOD_Source '{source}' (Baseband) plays a different signal from '{other}': the I and Q rails of one signal " +
        "must state the same Fc and the same carriers.",
        ("source", source), ("other", other));

    /// <summary>A Q rail with no I rail to measure the signal through.</summary>
    public static Diagnostic QAlone(string source) => Diagnostic.Create(
        "modsource.baseband.q-alone", DiagnosticSeverity.Error,
        "MOD_Source '{source}' is a Q rail (Baseband=q) with no I rail (Baseband=i) beside it: the signal is measured " +
        "through its I rail.",
        ("source", source));

    /// <summary>A baseband rail in the multi-fundamental envelope, which has no one baseband to drive.</summary>
    public static Diagnostic BasebandMultiFundamental(string source) => Diagnostic.Create(
        "modsource.baseband.multi-fundamental", DiagnosticSeverity.Error,
        "MOD_Source '{source}' is a baseband rail (Baseband=), which drives the baseband of a single envelope: the " +
        "multi-fundamental envelope has no one baseband to drive. Set Envelope=single.",
        ("source", source));
}
