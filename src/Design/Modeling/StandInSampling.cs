// A stand-in for a device circuitRF already runs (LP-3): the Model Composer's own flow (AN-04's, from a reference kit) with
// circuitRF as the source simulator. `model sample` samples the device — a .charm's DUT, or an instance line — over a bias
// grid at DC and at one low frequency, writes the tables as a SimulatedTables set, load-pulls the device itself about the
// .charm's load into a LargeSignal set, and sets the steps for a quasi-static table model. `model fit --step all`, `model
// export` and `model validate` then run as for any document, and validation compares the stand-in with the device's own
// loadpull. The result is a device-level .cmodel: a ComposedFET that DC, S-parameters, HB, loadpull and harmonicaRF run
// in-process, which is what makes it fast where the device is slow (a compiled model behind a worker, one call at a time).
//
// A three-terminal device of another kind maps onto the FET's terminals by its line: base on g, collector on d, emitter on
// 0, so vgs is vbe and vds is vce. A fourth net named t is the device's thermal node; it is held at 0 K rise while
// sampling, so the tables are isothermal, and left to the device's own thermal network once, at the bias, to read Rth.
// Each document samples into a folder of its own, sampled/<its name>/, so two stand-ins beside each other keep their data.

using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CircuitRF.Core.Devices.Composed;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Design.Circuit;
using CircuitRF.Engine;
using CircuitRF.Harmonica;
using RfCore.Data;
using RfCore.Export;
using RfCore.Loadpull;

namespace CircuitRF.Design.Modeling;

/// <summary>
/// What <c>model sample</c> samples: the device — a <c>.charm</c> (<see cref="From"/>, its DUT, bias, fundamental and
/// terminations) or an instance on nets <c>g d 0</c> (<see cref="Device"/>, a relative <c>File=</c> relative to the
/// <c>.cmc</c>) — the bias axes, volts, the temperatures, °C, and the small-signal frequency, Hz (null: a tenth of the
/// <c>.charm</c>'s fundamental). <see cref="Disc"/> is the loadpull about the <c>.charm</c>'s load.
/// </summary>
public sealed record StandInSpec
{
    public string? From { get; init; }
    public string? Device { get; init; }
    public required IReadOnlyList<double> Vgs { get; init; }
    public required IReadOnlyList<double> Vds { get; init; }
    public IReadOnlyList<double> Temperatures { get; init; } = [25];
    public double? FrequencyHz { get; init; }
    public (double Radius, int Rings, int Spokes) Disc { get; init; } = (0.2, 1, 4);
    public BasePeriphery? Periphery { get; init; }

    /// <summary>An axis from <paramref name="start"/> to <paramref name="stop"/> in steps of <paramref name="step"/>, both
    /// ends included — <c>--vgs -5,1,0.1</c>.</summary>
    public static double[] Axis(double start, double stop, double step)
    {
        if (!(step > 0) || !(stop > start)) throw new ModelingException("Sample", $"{G(start)},{G(stop)},{G(step)} is not start,stop,step with stop above start and a positive step.");
        // A step that does not divide the span would end the axis past stop, or short of it: refused, never rounded.
        double steps = (stop - start) / step;
        if (Math.Abs(steps - Math.Round(steps)) > 1e-9)
            throw new ModelingException("Sample", $"{G(step)} V does not divide {G(start)} to {G(stop)} V into whole steps; choose a step that does.");
        int n = (int)Math.Round(steps) + 1;
        if (n < 2 || n > 2001) throw new ModelingException("Sample", $"{n} values from {G(start)} to {G(stop)} V; an axis holds 2 to 2001.");
        return [.. Enumerable.Range(0, n).Select(k => Math.Round(start + k * step, 12))];
    }

    private static string G(double v) => v.ToString("G6", CultureInfo.InvariantCulture);
}

public static partial class ModelCommands
{
    /// <summary>The folder beside the <c>.cmc</c> each document's samples go into, in a folder of its own.</summary>
    public const string SampledFolder = "sampled";

    /// <summary>Where <paramref name="cmcPath"/>'s samples go, relative to it with '/': <c>sampled/&lt;its name&gt;</c>.</summary>
    public static string SampledFolderOf(string cmcPath) => $"{SampledFolder}/{Path.GetFileNameWithoutExtension(cmcPath)}";

    /// <summary>The block in <c>Steps</c> that records what a stand-in was sampled from; export copies it into
    /// <see cref="ModelProvenance.StandIn"/>.</summary>
    public const string StandInStep = "StandIn";

    /// <summary>
    /// Samples a device into <paramref name="cmcPath"/> (a new document, or one holding no data yet — a network typed into
    /// it is locked and removed from the samples): <c>sampled/&lt;name&gt;/tables.npy</c> imported as a <c>SimulatedTables</c> set
    /// at the device plane, its terminals — with no network the whole device is the core — and with a <c>.charm</c> the
    /// device's own loadpull over a small disc about its load, <c>sampled/&lt;name&gt;/loadpull.spl</c>, imported as a
    /// <c>LargeSignal</c> set; then the steps for a stand-in (Current and Charge
    /// tables on the data's own grid, nothing held out; the network locked; Validate at the <c>.charm</c>'s harmonics and
    /// compression) and the record of what was sampled. The DC and small-signal solves run in parallel only where the
    /// device can be elaborated in parallel — a compiled model behind its worker cannot, and runs one point at a time.
    /// </summary>
    public static IReadOnlyList<ModelLine> Sample(string cmcPath, StandInSpec spec, RunControl? rc = null)
    {
        cmcPath = Path.GetFullPath(cmcPath);
        string dir = Path.GetDirectoryName(cmcPath)!, name = Path.GetFileName(cmcPath);
        var device = Resolve(spec, dir);
        double f = spec.FrequencyHz ?? (device.Charm is { } c0 ? c0.Settings.FrequencyHz / 10
            : throw new ModelingException("FrequencyHz", "with an instance line and no .charm, state the small-signal frequency the charges are read at."));
        if (!(f > 0)) throw new ModelingException("FrequencyHz", "the small-signal frequency is not above 0 Hz.");
        if (spec.Temperatures.Count == 0 || spec.Temperatures.Distinct().Count() != spec.Temperatures.Count)
            throw new ModelingException("Temperatures", "state each temperature once.");
        if (!(spec.Disc.Radius > 0 && spec.Disc.Radius < 1) || spec.Disc.Rings < 1 || spec.Disc.Spokes < 1)
            throw new ModelingException("Disc", "the loadpull disc is a radius in Γ above 0 and below 1, and one or more rings and spokes.");

        var lines = new List<ModelLine>();
        if (File.Exists(cmcPath))
        {
            var existing = CmcFile.Read(cmcPath);
            if (existing.Kind != CmcKind.Active) throw new ModelingException(name, "a stand-in is an Active document.");
            if (existing.DataSets.Count > 0) throw new ModelingException(name, "it already holds data; sample into a new .cmc, or one holding only a network.");
            if (spec.Periphery is { } p && existing.BasePeriphery is { } q && p != q)
                throw new ModelingException("BasePeriphery", "the document already states N0 and W0.");
        }
        else lines.AddRange(New(cmcPath, CmcKind.Active, periphery: spec.Periphery ?? new BasePeriphery(1, "1 um")));
        var doc = CmcFile.Read(cmcPath);
        if (doc.BasePeriphery is null) CmcFile.Write(cmcPath, doc = doc with { BasePeriphery = spec.Periphery ?? new BasePeriphery(1, "1 um") });
        bool network = CmcExtrinsic.Network(doc).Schematic is not null;

        // ── 1. DC and S over the grid ────────────────────────────────────────────
        var settings = new AnalysisSettings { NonlinearAbsTol = 1e-12 };
        string Bench(double t, double vgs, double vds) =>
            ValidationBench.SParams("sampled", device.Held, t, vgs, vds, [f], 50);
        var (probeLib, probeTb) = new CnlReader().Read(Bench(spec.Temperatures[0], spec.Vgs[0], spec.Vds[0]));
        bool parallel;
        using (var probe = Elaborate(probeLib, probeTb, dir)) parallel = SParameterEngine.CanElaborateInParallel(probe);

        int nG = spec.Vgs.Count, nD = spec.Vds.Count, nT = spec.Temperatures.Count, n = nG * nD * nT;
        var id = new double[n];
        var ig = new double[n];
        var s = new Complex[n * 4];
        int failed = 0, loose = 0;
        var started = System.Diagnostics.Stopwatch.StartNew();
        rc?.BeginStage($"Sampling {device.Label}", n);
        void Point(int k)
        {
            // Row-major vgs × vds × T, the layout the array import reads as the axes of a grid.
            int t = k % nT, d = k / nT % nD, g = k / (nT * nD);
            var (lib, tb) = new CnlReader().Read(Bench(spec.Temperatures[t], spec.Vgs[g], spec.Vds[d]));
            using var nl = Elaborate(lib, tb, dir);
            // 1e-12 A resolves a pinched-off or reverse-biased junction; where a device's own round-off and limiting keep
            // an ampere-scale point above it, the run's default 1e-6 A is the answer rather than a hole in the grid.
            var used = settings;
            var dc = NonlinearDcEngine.Run(nl, used);
            if (!dc.Converged)
            {
                dc = NonlinearDcEngine.Run(nl, used = AnalysisSettings.Default);
                if (dc.Converged) Interlocked.Increment(ref loose);
            }
            if (!dc.Converged) { (id[k], ig[k]) = (double.NaN, double.NaN); Interlocked.Increment(ref failed); }
            else
            {
                (ig[k], id[k]) = (dc.ProbeCurrents["IPG"], dc.ProbeCurrents["IPD"]);
                var cube = SParameterEngine.Run(nl, [f], used)["S"];
                for (int i = 0; i < 2; i++)
                    for (int j = 0; j < 2; j++) s[k * 4 + i * 2 + j] = (Complex)cube[0, i, j];
            }
            rc?.TickStage();
        }
        if (parallel)
            Parallel.For(0, n, new ParallelOptions { CancellationToken = rc?.Token ?? CancellationToken.None,
                                                     MaxDegreeOfParallelism = Environment.ProcessorCount }, Point);
        else
            for (int k = 0; k < n; k++) { rc?.ThrowIfCancellationRequested(); Point(k); }
        rc?.ThrowIfCancellationRequested();
        if (failed == n) throw new ModelingException(device.Label, "no bias point of the grid converged at DC; check the device on its own first.");
        lines.Add(new ModelLine("sample", device.Label,
            $"{device.Label}: {nG} × {nD}{(nT > 1 ? $" × {nT}" : "")} point(s), DC and S at {G(f / 1e9)} GHz, " +
            $"{(parallel ? "in parallel" : "one at a time (the device cannot be elaborated in parallel)")}, {G(started.Elapsed.TotalSeconds)} s" +
            (loose > 0 ? $"; {loose} point(s) solved to 1e-6 A, not 1e-12 A" : "") +
            (failed > 0 ? $"; {failed} point(s) did not converge and are left out, so the tables are re-gridded: narrow the grid to where the device converges." : ".")));
        if (device.Thermal) lines.Add(new ModelLine("sample", device.Label, $"{device.Label}: thermal node t held at 0 K rise, so the tables are isothermal."));

        // ── 2. The tables, and 3. the SimulatedTables sets ──────────────────────────
        string rel = SampledFolderOf(cmcPath), folder = Path.Combine(dir, rel);
        Directory.CreateDirectory(folder);
        var vgsAx = new Axis("vgs", [.. spec.Vgs], "V");
        var vdsAx = new Axis("vds", [.. spec.Vds], "V");
        var fAx = new Axis("freq", [f], "Hz");
        var nan = new Complex(double.NaN, double.NaN);
        // One set at the temperatures ts: its own .npy, imported at the device plane. With no network that is the core's own,
        // and validation places the stand-in as the ComposedFET a circuit places (an intrinsic set validates a bare core).
        IReadOnlyList<ModelLine> Tables(string set, string file, int[] ts)
        {
            int m = ts.Length;
            var tAx = new Axis("T", [.. ts.Select(t => spec.Temperatures[t])], "°C");
            Axis[] bias = m > 1 ? [vgsAx, vdsAx, tAx] : [vgsAx, vdsAx];
            var idT = new double[nG * nD * m];
            var igT = new double[idT.Length];
            var sT = new Complex[idT.Length * 4];
            for (int p = 0; p < nG * nD; p++)
                for (int t = 0; t < m; t++)
                {
                    int from = p * nT + ts[t], to = p * m + t;
                    (idT[to], igT[to]) = (id[from], ig[from]);
                    for (int e = 0; e < 4; e++) sT[to * 4 + e] = double.IsFinite(id[from]) ? s[from * 4 + e] : nan;
                }
            var ds = new DataSet();
            ds.Add("vgs", new DataCube([vgsAx], [.. spec.Vgs]));
            ds.Add("vds", new DataCube([vdsAx], [.. spec.Vds]));
            if (m > 1) ds.Add("T", new DataCube([tAx], [.. tAx.Values]));
            ds.Add("freq", new DataCube([fAx], [f]));
            ds.Add("id", new DataCube(bias, idT));
            ds.Add("ig", new DataCube(bias, igT));
            ds.Add("S", new DataCube([.. bias, fAx, new Axis("i", [1, 2], "port"), new Axis("j", [1, 2], "port")], sT));
            DataSetExporter.Export(ds, Path.Combine(folder, file), ExportFormat.Npy);
            var arrays = new Dictionary<string, string>(StringComparer.Ordinal)
                { ["vgs"] = "vgs", ["vds"] = "vds", ["id"] = "id", ["ig"] = "ig", ["freq"] = "freq", ["S"] = "S" };
            if (m > 1) arrays["T"] = "T";
            return Import(cmcPath, new CmcDataSet
            {
                Name = set,
                Kind = DataSetKind.SimulatedTables,
                Sources = [$"{rel}/{file}"],
                Conditions = new DataConditions
                {
                    ReferencePlane = ReferencePlane.Device,
                    AmbientC = m == 1 ? spec.Temperatures[ts[0]] : null,
                    Note = $"sampled by circuitRF from {device.Label}",
                },
                Import = new ImportSettings { Format = ImportSettings.Npy, Arrays = arrays, Z0 = 50 },
            }, rc);
        }
        lines.AddRange(Tables("sampled", "tables.npy", [.. Enumerable.Range(0, nT)]));
        // A table charge takes no T, so over several temperatures the charges are the first one's, a set of their own: an ANN
        // in T would fit the same points in minutes rather than seconds.
        if (nT > 1) lines.AddRange(Tables("charges", "charges.npy", [0]));

        // ── 4. The device's own loadpull ─────────────────────────────────────────
        ValidationSettings? validation = null;
        if (device.Charm is { } charm)
        {
            var (set, line, v) = DeviceLoadpull(dir, rel, device, charm, spec, rc);
            lines.Add(line);
            lines.AddRange(Import(cmcPath, set, rc));
            validation = v;
        }
        else lines.Add(new ModelLine("note", "loadpull", "no .charm, so no loadpull of the device to validate against; --from names one."));

        // ── 5. The steps, and 6. the record ──────────────────────────────────────
        doc = CmcFile.Read(cmcPath);
        var current = CurrentFit.Settings(doc);
        doc = CurrentFit.WithSettings(doc, current with
        {
            DataSets = nT > 1 ? ["sampled"] : current.DataSets,
            Id = current.Id with { Method = CurrentMethod.Table },
            Ig = current.Ig with { Method = CurrentMethod.Table },
            HeldOutFraction = 0,
        });
        var charge = ChargeFit.Settings(doc);
        doc = ChargeFit.WithSettings(doc, charge with
        {
            DataSets = nT > 1 ? ["charges"] : charge.DataSets,
            Qg = charge.Qg with { Method = ChargeMethod.Table },
            Qd = charge.Qd with { Method = ChargeMethod.Table },
            HeldOutFraction = 0,
        });
        if (network)
        {
            var net = CmcExtrinsic.Network(doc);
            var vars = new Dictionary<string, NetworkVariableSettings>(net.Variables, StringComparer.Ordinal);
            foreach (var variable in SixPinNetwork.Load(DataImport.ResolveSource(dir, net.Schematic!)).Variables)
                vars[variable.Name] = (vars.GetValueOrDefault(variable.Name) ?? new NetworkVariableSettings()) with { Lock = true };
            doc = CmcExtrinsic.With(doc, net with { Variables = vars });
            lines.Add(new ModelLine("network", net.Schematic!, $"network: {net.Schematic}, every value locked; the samples are at the device plane."));
        }
        else lines.Add(new ModelLine("network", "", "network: none — the whole device is the core, sampled at its terminals."));
        // Rth at the .charm's bias, or where the samples dissipate most at the first temperature: the largest rise to read.
        (double Vgs, double Vds)? hot = device.Charm is { } cb ? (cb.Bias.Vgs!.Value, cb.Bias.Vds) : null;
        if (device.Thermal && hot is null)
        {
            var first = Enumerable.Range(0, n).Where(k => k % nT == 0 && double.IsFinite(id[k])).ToList();
            if (first.Count == 0)
                throw new ModelingException(device.Label, $"no point at {G(spec.Temperatures[0])} °C converged, so the thermal node's Rth cannot be read; check the device there.");
            int k0 = first.MaxBy(k => spec.Vgs[k / (nT * nD)] * ig[k] + spec.Vds[k / nT % nD] * id[k]);
            hot = (spec.Vgs[k0 / (nT * nD)], spec.Vds[k0 / nT % nD]);
        }
        if (device.Thermal && Rth(device, dir, settings, spec.Temperatures[0], hot!.Value.Vgs, hot.Value.Vds) is { } rth)
        {
            if (nT > 1)
            {
                doc = ThermalFit.WithSettings(doc, ThermalFit.Settings(doc) with { SelfHeating = true, Rth = rth });
                lines.Add(new ModelLine("thermal", "", $"thermal: Rth {G(rth)} K/W read from the device's thermal node at the bias, typed; type Cth (or τth) on the Thermal step."));
            }
            else lines.Add(new ModelLine("thermal", "", $"thermal: the device reads Rth {G(rth)} K/W; self-heating needs two or more temperatures, so the stand-in is isothermal."));
        }
        if (validation is not null) doc = ModelValidation.WithSettings(doc, validation);

        var record = new StandInProvenance
        {
            Device = device.Recorded,
            TextSha256 = device.TextSha256,
            FileSha256 = device.FileSha256,
            Vgs = [spec.Vgs[0], spec.Vgs[^1]],
            Vds = [spec.Vds[0], spec.Vds[^1]],
            Grid = [nG, nD],
            FrequencyHz = f,
            Temperatures = [.. spec.Temperatures],
            ThermalNodeHeld = device.Thermal ? true : null,
            DcFallbacks = loose,
        };
        var steps = doc.Steps is JsonObject so ? (JsonObject)JsonNode.Parse(so.ToJsonString())! : new JsonObject();
        steps[StandInStep] = JsonSerializer.SerializeToNode(record);
        CmcFile.Write(cmcPath, doc with { Steps = steps });
        lines.Add(new ModelLine("steps", "", "steps: Current and Charge as tables on the data's own grid, nothing held out" +
                                             (nT > 1 ? $"; the charges at {G(spec.Temperatures[0])} °C, a table charge taking no T" : "") +
                                             "; next model fit --step all, model export, model validate."));
        return lines;
    }

    /// <summary>What a <c>.cmc</c> records of its stand-in, or null when it is not one.</summary>
    public static StandInProvenance? StandIn(CmcDocument doc) =>
        doc.Steps is JsonObject s && s[StandInStep] is { } n ? n.Deserialize<StandInProvenance>() : null;

    // ── The device ───────────────────────────────────────────────────────────────

    /// <param name="Held">The text the samples run: the device, and a source holding its thermal node at 0 K rise.</param>
    /// <param name="Recorded">The text as the provenance records it: every model file by its name only, line ends <c>\n</c>.</param>
    private sealed record SampledDevice(string Text, string Held, string Label, string Recorded, string TextSha256, string? FileSha256,
                                        bool Thermal, CircuitModel? Charm, TerminationSet? Terminations);

    /// <summary>A model file a line names, quoted or not, and a worker provider's name, which carries its model file when it
    /// is a compiled model's (<see cref="Core.Devices.External.VerilogAFileResolver"/>).</summary>
    private static readonly Regex FileKey = new(@"\bFile=(?:""([^""]+)""|(\S+))", RegexOptions.Compiled);
    private static readonly Regex ProviderKey = new(@"\bProvider=(?:""([^""]+)""|(\S+))", RegexOptions.Compiled);

    /// <summary>The bench components <see cref="ValidationBench.Dc"/> puts around the device.</summary>
    private static readonly string[] BenchParts = ["VG", "IPG", "IPD", "VD"];

    private static SampledDevice Resolve(StandInSpec spec, string dir)
    {
        if ((spec.From is null) == (spec.Device is null))
            throw new ModelingException("Device", "name the device once: a .charm (--from) or an instance on g d 0 (--device).");
        if (spec.Vgs.Count < 2 || spec.Vds.Count < 2) throw new ModelingException("Sample", "each bias axis needs two or more values.");
        string text, label;
        CircuitModel? charm = null;
        TerminationSet? terminations = null;
        if (spec.From is { } from)
        {
            var contents = CharmIo.ReadAllFile(from);
            if (contents.Unresolved.Count > 0) throw new ModelingException(Path.GetFileName(from), contents.Unresolved[0].Message);
            charm = contents.Model;
            terminations = contents.Terminations;
            if (charm.Bias.Vgs is null) throw new ModelingException("Bias", "the .charm biases its DUT by a drain current; state its Vgs.");
            text = HarmonicaNetlist.DutText(charm, "g", "d", "0");
            label = $"{Path.GetFileName(from)} DUT ({charm.Dut.Kind})";
        }
        else
        {
            text = spec.Device!.Trim() + "\n";
            label = text.Split(' ', 2)[0];
        }

        // What was sampled is the text and the model it names. The record keeps every model file by its name, never a path,
        // and two digests: of that text with \n line ends, which anyone can recompute from it, and of the files' bytes, in
        // the order the text names them. A file that is not there is refused: a digest that left it out would vouch for it.
        var keys = FileKey.Matches(text).Select(m => (m.Index, Key: "File", m))
            .Concat(ProviderKey.Matches(text).Select(m => (m.Index, Key: "Provider", m))).OrderBy(x => x.Index).ToList();
        using var bytes = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        int files = 0;
        foreach (var (_, key, m) in keys)
        {
            string value = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
            string? file = key == "File" ? value : Core.Devices.External.VerilogAFileResolver.ModelFileIn(value);
            if (file is null)
                throw new ModelingException("Device", $"Provider={value} names no model file, so what was sampled cannot be recorded by its bytes; " +
                                                      "place the compiled model itself (VerilogA File=…).");
            string full = Path.IsPathRooted(file) ? file : Path.Combine(dir, file);
            if (!File.Exists(full)) throw new ModelingException("Device", $"'{file}' does not exist (a relative path is relative to the .cmc).");
            bytes.AppendData(File.ReadAllBytes(full));
            files++;
        }
        string recorded = ProviderKey.Replace(FileKey.Replace(text, m => $"File=\"{Path.GetFileName(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value)}\""),
            m => Core.Devices.External.VerilogAFileResolver.ModelFileIn(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value) is { } f
                ? $"Provider=\"{Core.Devices.External.VerilogAFileResolver.Prefix}{Path.GetFileName(f)}\"" : m.Value)
            .ReplaceLineEndings("\n").TrimEnd();
        string textSha = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(recorded)));
        string? fileSha = files > 0 ? Convert.ToHexStringLower(bytes.GetHashAndReset()) : null;

        // The device must elaborate on g and d. A net named t is its thermal node: found in the elaborated bench, not guessed.
        bool thermal;
        try
        {
            var (lib, tb) = new CnlReader().Read(ValidationBench.Dc("probe", text, spec.Temperatures.FirstOrDefault(25), spec.Vgs[0], spec.Vds[0]));
            using var nl = Elaborate(lib, tb, dir);
            bool Touched(string net) => nl.Nodes.TryGetIndex(net, out int i) &&
                                        nl.Components.Any(c => !BenchParts.Contains(c.InstancePath) && c.Nodes.Contains(i));
            if (!Touched("g") || !Touched("d"))
                throw new ModelingException("Device", $"'{label}' connects to {(Touched("g") ? "no net d" : "no net g")}; the device sits on the nets g, d " +
                                                      "and 0 (lower case), the gate or base on g and the drain or collector on d.");
            thermal = nl.Nodes.TryGetIndex("t", out _);
        }
        catch (Exception ex) when (ex is not ModelingException and not OperationCanceledException)
        {
            throw new ModelingException("Device", $"'{label}' does not elaborate: {ex.Message}");
        }
        return new SampledDevice(text, thermal ? text + "Vdc:VTH t 0 V=0\n" : text, label, recorded, textSha, fileSha,
                                 thermal, charm, terminations);
    }

    private static ElaboratedNetlist Elaborate(Core.Design.Library lib, Core.Design.TestBench tb, string dir) =>
        new Elaborator(lib) { BaseDirectory = dir }.Elaborate(tb);

    /// <summary>The device's thermal resistance at the bias: ΔT on its thermal node over the power it dissipates, the node
    /// left to the device's own thermal network — through a resistance far above any, because a compiled model's thermal
    /// terminal left unwired is held by its host.</summary>
    private static double? Rth(SampledDevice device, string dir, AnalysisSettings settings, double ambientC, double vgs, double vds)
    {
        var (lib, tb) = new CnlReader().Read(ValidationBench.Dc("thermal", device.Text + "R:RTH t 0 R=1e12\n", ambientC, vgs, vds));
        using var nl = Elaborate(lib, tb, dir);
        var dc = NonlinearDcEngine.Run(nl, settings);
        if (!dc.Converged) return null;
        double p = vgs * dc.ProbeCurrents["IPG"] + vds * dc.ProbeCurrents["IPD"];
        double dT = dc.NodeVoltages[nl.Nodes.IndexOf("t") - 1];
        return p > 0 && dT > 0 ? dT / p : null;
    }

    // ── The device's own loadpull ────────────────────────────────────────────────

    /// <summary>
    /// The device load-pulled over a disc about the <c>.charm</c>'s load (<see cref="ModelValidationReport.Disc"/>), through
    /// the loadpull engine and the bench validation generates, each load driven a decibel past the compression validation
    /// compares at; written as <c>sampled/&lt;name&gt;/loadpull.spl</c> (its bench beside it) and returned as the set that imports it.
    /// </summary>
    private static (CmcDataSet Set, ModelLine Line, ValidationSettings Validation) DeviceLoadpull(
        string dir, string rel, SampledDevice device, CircuitModel charm, StandInSpec spec, RunControl? rc)
    {
        string folder = Path.Combine(dir, rel);
        var t = device.Terminations!;
        int k = charm.Settings.HarmonicCount;
        double f0 = charm.Settings.FrequencyHz, vgs = charm.Bias.Vgs!.Value, vds = charm.Bias.Vds;
        Complex Z(TerminationSide side, int band) => t.Z(side, band);
        Complex[] Harmonics(TerminationSide side) => [.. Enumerable.Range(2, Math.Max(0, k - 1)).Select(b => Z(side, b))];
        var zl = Z(TerminationSide.Load, 1);
        var grid = ModelValidationReport.Disc((zl - 50) / (zl + 50), spec.Disc.Radius, spec.Disc.Rings, spec.Disc.Spokes);
        double comp = charm.Settings.CompressionDb;

        string gam = Path.Combine(folder, "loadpull.gam");
        File.WriteAllLines(gam, ["# impedance Z0=50 re_im", .. grid.Select(g => 50 * (1 + g) / (1 - g))
                                                                   .Select(z => $"{ValidationBench.R(z.Real)} {ValidationBench.R(z.Imaginary)}")]);
        double ambient = spec.Temperatures[0];
        string cnl = ValidationBench.Loadpull("loadpull", device.Text, ambient, f0, vgs, vds, Z(TerminationSide.Source, 1),
                                              Harmonics(TerminationSide.Source), Harmonics(TerminationSide.Load), gam,
                                              charm.Settings.PinStartDbm, charm.Settings.PinStepDbm, charm.Settings.PinMaxDbm, k, comp + 1);
        File.WriteAllText(Path.Combine(folder, "loadpull.cnl"), cnl);

        rc?.BeginStage($"Load-pulling {device.Label}", 1);
        var started = System.Diagnostics.Stopwatch.StartNew();
        var r = CircuitEvaluation.Evaluate(PreparedCircuit.FromText(cnl, folder, dir),
                                           new CircuitEvaluationRequest { Measurements = false, Control = rc?.Child() });
        if (r.Status == RunStatus.Cancelled) throw new OperationCanceledException();
        if (r.Status != RunStatus.Success) throw new ModelingException("loadpull", r.StatusMessage);
        var lp = r.Results.Select(x => x.Data).FirstOrDefault(d => d.Groups.Any(g => d.CubesIn(g).ContainsKey("GammaLoad")))
                 ?? throw new ModelingException("loadpull", "the run produced no loadpull.");
        string group = lp.Groups.First(g => lp.CubesIn(g).ContainsKey("GammaLoad"));
        string spl = Path.Combine(folder, "loadpull.spl");
        SplWriter.WriteSpl(lp, spl, group);
        rc?.TickStage();

        var set = new CmcDataSet
        {
            Name = "loadpull",
            Kind = DataSetKind.LargeSignal,
            Sources = [$"{rel}/loadpull.spl"],
            Conditions = new DataConditions
            {
                AmbientC = ambient,
                LargeSignal = new LargeSignalConditions
                {
                    Measurement = LargeSignalMeasurement.Loadpull, FrequencyHz = f0, Vgs = vgs, Vds = vds,
                    SourceZ = Z(TerminationSide.Source, 1), LoadZ = zl,
                    SourceHarmonics = Harmonics(TerminationSide.Source), LoadHarmonics = Harmonics(TerminationSide.Load),
                },
            },
            Import = new ImportSettings { Format = ImportSettings.Loadpull },
        };
        var line = new ModelLine("sample", "loadpull",
            $"loadpull: {device.Label} over {grid.Count} load(s) about {ValidationBench.Z(zl)} Ω at {G(f0 / 1e9)} GHz, " +
            $"to {G(comp + 1)} dB compression, {G(started.Elapsed.TotalSeconds)} s.");
        return (set, line, ValidationSettings.Default with { MaxHarmonic = k, Compression = comp });
    }
}
