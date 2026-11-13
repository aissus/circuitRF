// The closed-form cascade of a chain: per stage, the cumulative value at that stage's output, referred to the chain's
// input — and, in an array, to ONE element's input.
//
//   Gain                 G_k = Π g_i                                     (signal gain; a Combiner's g is N/L)
//   Noise (Friis)        Te_k = Σ Te_i / Gn_(i−1),  F = 1 + Te/T0        (noise gain Gn: a Combiner's is 1/L)
//   Intercept            1/IIP3_k = Σ G_(i−1) / IIP3_i ,  OIP3 = IIP3·G  (in-phase IM3 addition, the worst case)
//   Compression          1/IP1dB_k = Σ G_(i−1) / IP1dB_i ,  OP1dB = IP1dB + G − 1 dB   (an approximation, as for IP3)
//   MDS                  10·log10(k·(Ts + Te)·B) + 30 − 10·log10(A)       (A: the array gain, N for equal paths)
//   SNR                  Pin − MDS ;  sensitivity = MDS + SNR_req ;  SFDR = (2/3)(IIP3 − MDS)
//
// The array: N branches whose signals add in phase and whose noise is uncorrelated, so the SNR rises by N while the
// array's noise temperature is one branch's Friis sum. The sources are in docs/design/chain-budget.md.

using RfCore.Data;

namespace CircuitRF.Design.Chain;

/// <summary>The cumulative budget at one stage's output.</summary>
public sealed record ChainStageResult(
    string Name, string Part, StageBudget Stage, double FrequencyHz,
    double GainDb, double NfDb, double TeK, double Iip3Dbm, double Oip3Dbm, double Ip1dbDbm, double Op1dbDbm,
    double PoutDbm, double NoiseOutDbm, double SnrDb, double ArrayGainDb, double NoiseSharePct);

/// <summary>The whole chain's numbers.</summary>
public sealed record ChainTotals(
    double GainDb, double NfDb, double TeK, double Iip3Dbm, double Oip3Dbm, double Ip1dbDbm, double Op1dbDbm,
    double PoutDbm, double ThermalFloorDbm, double NoiseOutDbm, double MdsDbm, double SensitivityDbm, double SnrDb,
    double SfdrDb, double ArrayGainDb, double OutputFrequencyHz);

public sealed record ChainBudgetResult(
    IReadOnlyList<ChainStageResult> Stages, ChainTotals? Totals, IReadOnlyList<string> Refusals, IReadOnlyList<string> Notes)
{
    /// <summary>The design as it was budgeted (<see cref="ChainDesign.Resolve"/>): every expression and table its number
    /// at its stage's frequency. The levels, the diagram and the benches read this one. Null when refused.</summary>
    public ChainDesign? Design { get; init; }

    /// <summary>Every mixer's image band (<see cref="ChainImage"/>), counted or not.</summary>
    public IReadOnlyList<ChainImageTerm> Images { get; init; } = [];

    /// <summary>What the counted image bands add to the chain's NF, dB; 0 when none is counted.</summary>
    public double ImageNfDb => Totals is { } t && Images.Any(x => x.Counted)
        ? t.NfDb - 10 * Math.Log10(1 + (t.TeK - Images.Where(x => x.Counted).Sum(x => x.TotalK)) / ChainBudget.T0) : 0;
}

public static class ChainBudget
{
    /// <summary>Boltzmann's constant, exactly (the noise conventions in simulations.md).</summary>
    public const double K = 1.380649e-23;

    /// <summary>The temperature a noise figure is referred to.</summary>
    public const double T0 = 290;

    private static double Lin(double db) => Math.Pow(10, db / 10);
    private static double Db(double x) => 10 * Math.Log10(x);

    /// <summary>The budget, or the refusals that stop it. Never throws.</summary>
    public static ChainBudgetResult Evaluate(ChainDesign d) => Evaluate(d, response: false);

    /// <summary>The budget; with <paramref name="response"/>, of a point off the carrier (<see cref="Response"/>, a
    /// frequency sweep), where every Filter is its own response rather than its passband loss. <paramref name="raw"/>: the
    /// design as written when <paramref name="d"/> is a point already resolved (a response's), so that an image still
    /// reads its values of the frequency at its own frequency.</summary>
    private static ChainBudgetResult Evaluate(ChainDesign d, bool response, ChainDesign? raw = null)
    {
        // Expressions and tables first, each read at its stage's frequency; then the resolved design's own refusals.
        raw ??= d;
        var unread = new List<string>();
        d = d.Resolve(unread);
        if (unread.Count > 0) return new([], null, unread, []);
        var refusals = d.Refusals();
        if (refusals.Count > 0) return new([], null, refusals, []);

        var notes = new List<string>();
        var rows = new List<ChainStageResult>();
        var inputHz = d.StageInputFrequencies();
        var budgets = d.Stages.Select((s, i) => ChainParts.Find(s.Part)!.Budget(s, new ChainStageContext(inputHz[i], d.BaseDirectory, response))).ToList();
        foreach (var b in budgets) notes.AddRange(b.Notes);
        notes.AddRange(BlockersOutsideFiles(d));

        // The cascade to each stage's output: signal gain, noise gain, noise temperature (at T0), and each stage's share
        // of it. Matched, Friis on the stages' own gains; with mismatch, the two-ports' exact cascade (ChainMismatch).
        int n = budgets.Count;
        var gsRow = new double[n];
        var gnRow = new double[n];
        var teRow = new double[n];
        var shares = new double[n];
        double gs = 1, gn = 1, te = 0;
        for (int i = 0; i < n; i++)
        {
            var b = budgets[i];
            shares[i] = T0 * (Lin(b.NfDb) - 1) / gn;
            te += shares[i];
            gs *= Lin(b.GainDb);
            gn *= Lin(b.NoiseGainDb);
            gsRow[i] = gs; gnRow[i] = gn; teRow[i] = te;
        }
        ChainCascade? cascade = null;
        if (d.Mismatch)
        {
            cascade = Cascade(d, budgets, inputHz);
            if (cascade is null) return new([], null, ["With mismatch, a stage has no transmission at its frequency: the cascade has no gain to divide by."], []);
            double arr = 1;
            for (int i = 0; i < n; i++)
            {
                arr *= Lin(budgets[i].GainDb - budgets[i].NoiseGainDb);
                gnRow[i] = cascade.GainAt(i);
                gsRow[i] = gnRow[i] * arr;
                teRow[i] = T0 * (cascade.NoiseFactor[i] - 1);
                shares[i] = T0 * cascade.Terms[i];
            }
            te = teRow[^1];
        }

        // Each mixer's image: what the stages before it deliver there, over the T0 its SSB figure states. Counted when it
        // moves the NF by ChainImage.ThresholdDb or more; then it joins the noise from its mixer on, each kelvin on the
        // stage that made it, and the source's at Ts for the floor. The source's own noise in the image band is no stage's,
        // as it is no part's in a noise run's shares: those then sum to less than all of F − 1.
        var images = ChainImage.Terms(raw, d, budgets, m => m == 0 ? 1 : gnRow[m - 1], te,
                                      d.Mismatch ? (k, f) => TwoPortsAt(raw, d, k, f) : null, notes);
        var srcRow = new double[n];
        foreach (var im in images.Where(x => x.Counted))
        {
            for (int k = im.Stage; k < n; k++)
            {
                teRow[k] += im.TotalK;
                srcRow[k] += im.TeK[0] * (d.TemperatureK / T0 - 1);
            }
            for (int k = 0; k < n; k++) shares[k] += im.TeK[1 + k];
            notes.Add($"{im.Name}: its image at {ChainSnp.Hz(im.ImageHz)} adds {im.AddedDb(te):0.###} dB to the NF: the stages " +
                      "before it pass their noise there, which its single-sideband NF takes as T0.");
        }
        te = teRow[^1];

        double inv3 = 0, inv1 = 0, f = d.FrequencyHz;
        double a = 1;
        for (int i = 0; i < n; i++)
        {
            var b = budgets[i];
            // The intercepts sum each stage's with the gain to its nonlinearity: the stage's input, matched; with mismatch
            // an SnP stage's is the amplifier after its file, so its own gain is to there.
            bool after = cascade is not null && d.Stages[i].Part == ChainParts.Snp;
            double gTo = after ? gsRow[i] : i == 0 ? 1 : gsRow[i - 1], ref3 = after ? b.GainDb : 0;
            if (!double.IsPositiveInfinity(b.Iip3Dbm)) inv3 += gTo / Lin(b.Iip3Dbm + ref3);
            if (!double.IsPositiveInfinity(b.Ip1dbDbm)) inv1 += gTo / Lin(b.Ip1dbDbm + ref3);
            a *= Lin(b.GainDb - b.NoiseGainDb);   // the array gain: signal over noise gain, N for equal paths
            f = ChainParts.OutputHz(d.Stages[i], f);
            gs = gsRow[i]; gn = gnRow[i]; te = teRow[i];
            double ts = te + srcRow[i];

            double g = Db(gs);
            double iip3 = inv3 > 0 ? -Db(inv3) : double.PositiveInfinity;
            double ip1 = inv1 > 0 ? -Db(inv1) : double.PositiveInfinity;
            double mds = Db(K * (d.TemperatureK + ts) * d.BandwidthHz) + 30 - Db(a);
            rows.Add(new ChainStageResult(
                d.Stages[i].Name, d.Stages[i].Part, b, f,
                g, Db(1 + te / T0), te, iip3, iip3 + g, ip1, ip1 + g - 1,
                d.InputPowerDbm + g, Db(K * (d.TemperatureK + ts) * d.BandwidthHz * gn) + 30,
                d.InputPowerDbm - mds, Db(a), 0));
        }
        double tsT = te + srcRow[^1];

        // Each stage's share of F − 1 (which is what an S-parameter or harmonic-balance noise run's NFpct reports).
        for (int i = 0; i < rows.Count; i++)
            rows[i] = rows[i] with { NoiseSharePct = te > 0 ? 100 * shares[i] / te : 0 };
        // A link delivering more than the receiver takes is held at its ceiling, on the cascade's own input P1dB.
        d = ChainLink.Cap(d, rows, notes);

        if (budgets.Any(b => b.ArrayN > 1)
            && d.Stages.Any(s => s.Part == ChainParts.Combiner && ChainValue.Or(s.Get("Steer"), 0) != 0)
            && !d.Stages.TakeWhile(s => s.Part != ChainParts.Combiner).Any(s => s.Part == ChainParts.PhaseShifter))
            notes.Add("The combiner's Steer is not 0 but no PhaseShifter precedes it: the branches arrive out of phase, " +
                      "and the budget's coherent sum assumes they do not.");

        var last = rows[^1];
        double mdsT = Db(K * (d.TemperatureK + tsT) * d.BandwidthHz) + 30 - Db(a);
        var totals = new ChainTotals(
            last.GainDb, last.NfDb, te, last.Iip3Dbm, last.Oip3Dbm, last.Ip1dbDbm, last.Op1dbDbm,
            last.PoutDbm, Db(K * d.TemperatureK * d.BandwidthHz) + 30, last.NoiseOutDbm, mdsT,
            mdsT + d.RequiredSnrDb, d.InputPowerDbm - mdsT,
            double.IsPositiveInfinity(last.Iip3Dbm) ? double.PositiveInfinity : 2.0 / 3.0 * (last.Iip3Dbm - mdsT),
            Db(a), f);
        // A transmit chain's input is its drive, not a signal a receiver takes: no ceiling, but its output's compression.
        var result = new ChainBudgetResult(rows, totals, [], notes) { Design = d, Images = images };
        notes.AddRange(d.IsTransmit ? ChainTransmit.Notes(result) : ChainCapacity.Notes(d, rows, totals));
        return result;
    }

    /// <summary>The design's two-ports stage by stage at the carrier, an isolator in front of every mixer as the benches
    /// place it, cascaded (<see cref="ChainMismatch"/>); null when one has no transmission.</summary>
    private static ChainCascade? Cascade(ChainDesign d, IReadOnlyList<StageBudget> budgets, IReadOnlyList<double> inputHz)
    {
        var parts = new List<IReadOnlyList<ChainTwoPort>>();
        for (int i = 0; i < budgets.Count; i++)
        {
            var s = d.Stages[i];
            var snp = s.Part == ChainParts.Snp ? ChainSnp.Read(s, new ChainStageContext(inputHz[i], d.BaseDirectory), []) : null;
            if (ChainMismatch.Of(s, budgets[i], inputHz[i], d.BaseDirectory, snp) is not { } tp) return null;
            parts.Add(s.Part == ChainParts.Mixer ? [ChainMismatch.Isolator, .. tp] : tp);
        }
        return ChainMismatch.Cascade(parts);
    }

    /// <summary>Stage <paramref name="k"/>'s two-ports at <paramref name="fHz"/>, off its carrier: its values of the
    /// frequency read there, a Filter its response.</summary>
    private static IReadOnlyList<ChainTwoPort>? TwoPortsAt(ChainDesign raw, ChainDesign d, int k, double fHz)
    {
        var s = ChainScaling.At(raw.Stages[k], d.Stages[k], fHz, d.BaseDirectory);
        var b = ChainParts.Find(s.Part)!.Budget(s, new ChainStageContext(fHz, d.BaseDirectory, Response: true));
        var snp = s.Part == ChainParts.Snp ? ChainSnp.Read(d.Stages[k], new ChainStageContext(d.StageInputFrequencies()[k], d.BaseDirectory), []) : null;
        return ChainMismatch.Of(s, b, fHz, d.BaseDirectory, snp);
    }

    /// <summary>Each blocker that reaches an SnP stage outside its file's range: carried at the file's edge value, as the
    /// block clamps it in a simulation.</summary>
    private static IEnumerable<string> BlockersOutsideFiles(ChainDesign d)
    {
        if (!d.Stages.Any(s => s.Part == ChainParts.Snp)) yield break;
        for (int k = 0; k < d.Blockers.Count; k++)
        {
            double f = d.FrequencyHz + d.Blockers[k].OffsetHz;
            foreach (var s in d.Stages)
            {
                if (s.Part == ChainParts.Snp && ChainSnp.GainDbAt(s, d.BaseDirectory, f, out bool outside) is not null && outside)
                    yield return $"{s.Name}: blocker {k + 1} reaches it at {ChainSnp.Hz(f)}, outside its file's range; its gain " +
                                 "there is taken at the file's nearest frequency, as the SnP block takes it in a simulation.";
                f = ChainParts.OutputHz(s, f);
            }
        }
    }

    /// <summary>
    /// The budget as a DataSet, as every run in circuitRF returns one: per-stage cubes over a <c>stage</c> axis labelled
    /// with the stage names, and the chain's totals as scalars. An infinite intercept is written as NaN, which is how a
    /// cube says "none".
    /// </summary>
    public static DataSet ToDataSet(ChainBudgetResult r)
    {
        var ds = new DataSet();
        if (r.Totals is not { } t) return ds;
        var stage = new Axis("stage", [.. Enumerable.Range(0, r.Stages.Count).Select(i => (double)i)], "",
                             [.. r.Stages.Select(s => s.Name)]);
        static double Fin(double v) => double.IsFinite(v) ? v : double.NaN;
        void Per(string name, string unit, Func<ChainStageResult, double> pick)
            => ds.Add(name, new DataCube([stage], [.. r.Stages.Select(s => Fin(pick(s)))]) { Unit = unit });
        void One(string name, string unit, double v) => ds.Add(name, new DataCube([], [Fin(v)]) { Unit = unit });

        Per("G_dB", "dB", s => s.GainDb);
        Per("NF_dB", "dB", s => s.NfDb);
        Per("Te_K", "K", s => s.TeK);
        Per("IIP3_dBm", "dBm", s => s.Iip3Dbm);
        Per("OIP3_dBm", "dBm", s => s.Oip3Dbm);
        Per("IP1dB_dBm", "dBm", s => s.Ip1dbDbm);
        Per("OP1dB_dBm", "dBm", s => s.Op1dbDbm);
        Per("Pout_dBm", "dBm", s => s.PoutDbm);
        Per("Nout_dBm", "dBm", s => s.NoiseOutDbm);
        Per("SNR_dB", "dB", s => s.SnrDb);
        Per("NFpct", "%", s => s.NoiseSharePct);
        Per("Freq_Hz", "Hz", s => s.FrequencyHz);
        Per("StageGain_dB", "dB", s => s.Stage.GainDb);
        Per("StageNF_dB", "dB", s => s.Stage.NfDb);
        Per("StageIIP3_dBm", "dBm", s => s.Stage.Iip3Dbm);
        if (r.Design is { } d)
        {
            // Each Amp's amplifier stages (NaN where it states none) and each stage's DC power.
            ds.Add("Nstg", new DataCube([stage], [.. d.Stages.Select(s => ChainParts.StageCount(s) is { } n ? n : double.NaN)]));
            ds.Add("Pdc_W", new DataCube([stage], [.. d.Stages.Select(ChainParts.StagePdcW)]) { Unit = "W" });
        }

        One("Gain_dB", "dB", t.GainDb);
        One("NFtot_dB", "dB", t.NfDb);
        One("Tetot_K", "K", t.TeK);
        One("IIP3tot_dBm", "dBm", t.Iip3Dbm);
        One("OIP3tot_dBm", "dBm", t.Oip3Dbm);
        One("IP1dBtot_dBm", "dBm", t.Ip1dbDbm);
        One("OP1dBtot_dBm", "dBm", t.Op1dbDbm);
        One("Pouttot_dBm", "dBm", t.PoutDbm);
        One("ThermalFloor_dBm", "dBm", t.ThermalFloorDbm);
        One("NoiseOut_dBm", "dBm", t.NoiseOutDbm);
        One("MDS_dBm", "dBm", t.MdsDbm);
        One("Sensitivity_dBm", "dBm", t.SensitivityDbm);
        One("SNRtot_dB", "dB", t.SnrDb);
        One("SFDR_dB", "dB", t.SfdrDb);
        One("ArrayGain_dB", "dB", t.ArrayGainDb);
        One("Fout_Hz", "Hz", t.OutputFrequencyHz);
        if (r.Images.Any(x => x.Counted)) One("NFimage_dB", "dB", r.ImageNfDb);
        ChainCapacity.AddTo(ds, r, stage);
        ChainLink.AddTo(ds, r);
        ChainTransmit.AddTo(ds, r);
        return ds;
    }

    /// <summary>What a sweep reads at every point, by the name of its cube.</summary>
    public static readonly (string Name, string Unit, Func<ChainBudgetResult, double> Pick)[] SweepQuantities =
    [
        ("EIRP_dBm", "dBm", r => r.Design!.Link?.Budget(r.Design.FrequencyHz).EirpDbm ?? double.NaN),
        ("Prx_dBm", "dBm", r => r.Design!.Link?.Budget(r.Design.FrequencyHz).PrxDbm ?? r.Design.InputPowerDbm),
        ("Pin_dBm", "dBm", r => r.Design!.InputPowerDbm),
        ("Ceiling_dBm", "dBm", r => ChainCapacity.Read(r)!.CeilingDbm),
        ("MDS_dBm", "dBm", r => r.Totals!.MdsDbm),
        ("SNR_dB", "dB", r => r.Totals!.SnrDb),
        ("Margin_dB", "dB", r => ChainCapacity.Read(r)!.MarginDb),
        ("Rate_Gbps", "Gbit/s", r => ChainCapacity.Read(r)!.RateBps / 1e9),
        ("Pdc_W", "W", r => ChainLink.Power(r)!.TotalW),
        ("Ebit_pJ", "pJ", r => ChainLink.Power(r)!.EnergyPerBitJ * 1e12),
        ("Gain_dB", "dB", r => r.Totals!.GainDb),
        ("NFtot_dB", "dB", r => r.Totals!.NfDb),
        ("NFarray_dB", "dB", r => ChainCapacity.Read(r)!.NfArrayDb),
        ("IP1dBtot_dBm", "dBm", r => r.Totals!.Ip1dbDbm),
    ];

    /// <summary>
    /// One sweep of the design (<see cref="ChainSweep"/>): at each point a copy with the carrier, the transmit elements or
    /// the receive Combiner's N set, budgeted again, every expression and table read there and every Filter its own
    /// response there for a sweep of the carrier (a band written as numbers stays put while the carrier moves through it). A cube a quantity over the
    /// sweep's axis (<c>freq</c> in Hz, <c>Mtx</c>, <c>Mrx</c>). A point the budget refuses is NaN, and one note gives
    /// how many and the first reason; a receive array of one has no Combiner. Nothing here is a bench: a sweep point is
    /// verified by transferring the design at that point.
    /// </summary>
    public static DataSet Sweep(ChainDesign d, ChainSweep s, List<string>? notes = null)
    {
        var values = s.Values();
        int cmb = d.Stages.FindIndex(st => st.Part == ChainParts.Combiner);
        var points = values.Select(v =>
        {
            var c = d.Clone();
            c.Sweeps.Clear();   // a point is one chain; one of a single element has no Combiner left to size
            switch (s.Of)
            {
                case ChainSweep.Frequency: c.FrequencyHz = v; break;
                case ChainSweep.TxElements: if (c.Link is not null) c.Link.Tx["Elements"] = ChainValue.Format(v); break;
                default:
                    if (cmb < 0) break;
                    if (v < 2) c.Stages.RemoveAt(cmb);
                    else c.Stages[cmb].Parameters["N"] = ChainValue.Format(v);
                    break;
            }
            // A carrier's in-band filters carry their ripple; an array's size moves no frequency, so its points keep the IL
            // the design point has.
            return Evaluate(c, response: s.Of == ChainSweep.Frequency);
        }).ToList();
        var refused = points.Where(p => p.Totals is null).ToList();
        if (refused.Count > 0)
            notes?.Add($"The {s.Of} sweep leaves out {refused.Count} of its {values.Length} points, which the budget refuses; the first: {refused[0].Refusals[0]}");
        var axis = s.Of switch
        {
            ChainSweep.Frequency => new Axis("freq", values, "Hz"),
            ChainSweep.TxElements => new Axis("Mtx", values),
            _ => new Axis("Mrx", values),
        };
        var ds = new DataSet();
        foreach (var (name, unit, pick) in SweepQuantities)
            ds.Add(name, new DataCube([axis], [.. points.Select(p => p.Totals is null ? double.NaN : pick(p) is var v && double.IsFinite(v) ? v : double.NaN)]) { Unit = unit });
        return ds;
    }

    /// <summary>Every sweep of the design added to <paramref name="ds"/>, a group each, named as its sweep is, and its
    /// response (<see cref="Response"/>) as the group <see cref="ResponseGroup"/>.</summary>
    public static void AddSweeps(DataSet ds, ChainDesign d, List<string>? notes = null)
    {
        foreach (var s in d.Sweeps)
            foreach (var (name, cube) in Sweep(d, s, notes).CubesIn(DataSet.DefaultGroup))
                ds.AddToGroup(s.Of, name, cube);
        if (d.Response is not null && Response(d, notes) is var rsp && rsp.ContainsGroup(DataSet.DefaultGroup))
            foreach (var (name, cube) in rsp.CubesIn(DataSet.DefaultGroup))
                ds.AddToGroup(ResponseGroup, name, cube);
    }

    /// <summary>The DataSet group a design's response is written as.</summary>
    public const string ResponseGroup = "Response";

    /// <summary>What the response reads at every point, by the name of its cube: the whole chain's.</summary>
    public static readonly (string Name, string Unit, Func<ChainTotals, double> Pick)[] ResponseQuantities =
    [
        ("Gain_dB", "dB", t => t.GainDb), ("NF_dB", "dB", t => t.NfDb),
        ("IIP3_dBm", "dBm", t => t.Iip3Dbm), ("OIP3_dBm", "dBm", t => t.Oip3Dbm),
        ("IP1dB_dBm", "dBm", t => t.Ip1dbDbm), ("OP1dB_dBm", "dBm", t => t.Op1dbDbm),
        ("Pout_dBm", "dBm", t => t.PoutDbm), ("Nout_dBm", "dBm", t => t.NoiseOutDbm), ("SNR_dB", "dB", t => t.SnrDb),
        ("Fout_Hz", "Hz", t => t.OutputFrequencyHz),
    ];

    /// <summary>
    /// The design's in-band response (<see cref="ChainDesign.Response"/>): ONE design, as it was budgeted at its carrier,
    /// budgeted again at every input frequency of the band with every LO, every filter's band and every other frequency
    /// parameter where the carrier put them. Each stage is read at the frequency it then sees: a Filter its own response,
    /// an SnP stage its file, a mixer moving the frequency by its fixed LO, a value of the frequency read there; the input
    /// power and the bandwidth stay the carrier's. Cubes of the whole chain's numbers over the band, <c>offset</c> from the
    /// carrier or <c>freq</c>, Hz. Not a carrier sweep (<see cref="Sweep"/>), which designs the chain again at each point.
    /// A point the budget refuses (a table or a file that does not reach it) is NaN, and one note says how many and why.
    /// </summary>
    public static DataSet Response(ChainDesign d, List<string>? notes = null)
    {
        var ds = new DataSet();
        if (d.Response is not { } rsp || Evaluate(d).Design is not { } at) return ds;
        var points = rsp.Values(at.FrequencyHz).Select(f =>
        {
            var c = at.Clone();
            c.FrequencyHz = f;
            c.Link = null;
            c.FractionalBandwidth = null;
            c.Sweeps.Clear();
            c.Response = null;
            c.Blockers.Clear();
            var why = new List<string>();
            var hz = c.StageInputFrequencies();
            for (int i = 0; i < c.Stages.Count; i++) c.Stages[i] = ChainScaling.At(d.Stages[i], at.Stages[i], hz[i], d.BaseDirectory, why);
            return why.Count > 0 ? new ChainBudgetResult([], null, why, []) : Evaluate(c, response: true, raw: d);
        }).ToList();
        var refused = points.Where(p => p.Totals is null).ToList();
        if (refused.Count > 0)
            notes?.Add($"The response leaves out {refused.Count} of its {points.Count} points, which the budget refuses; the first: {refused[0].Refusals[0]}");
        var axis = rsp.Offset ? new Axis("offset", rsp.Values(0), "Hz") : new Axis("freq", rsp.Values(0), "Hz");
        foreach (var (name, unit, pick) in ResponseQuantities)
            ds.Add(name, new DataCube([axis], [.. points.Select(p => p.Totals is { } t && pick(t) is var v && double.IsFinite(v) ? v : double.NaN)]) { Unit = unit });
        return ds;
    }
}

/// <summary>
/// A sweep of a chain: <see cref="Of"/> the carrier frequency (<see cref="Points"/> carriers evenly from
/// <see cref="From"/> to <see cref="To"/>, Hz), or the transmit or receive array's elements (as many sizes spread evenly
/// in ratio, each rounded to a whole element, a size once).
/// </summary>
public sealed record ChainSweep(string Of, double From, double To, int Points)
{
    public const string Frequency = "Frequency", TxElements = "TxElements", RxElements = "RxElements";

    /// <summary>The points, in order.</summary>
    public double[] Values()
    {
        if (Points < 2) return [From];
        if (Of == Frequency) return [.. Enumerable.Range(0, Points).Select(i => From + i * (To - From) / (Points - 1))];
        return [.. Enumerable.Range(0, Points).Select(i => Math.Round(From * Math.Pow(To / From, i / (double)(Points - 1)))).Distinct()];
    }

    /// <summary>Why it cannot be run, or null.</summary>
    public string? Refusal(ChainDesign d) =>
        Of is not (Frequency or TxElements or RxElements) ? $"A sweep is of {Frequency}, {TxElements} or {RxElements}, not '{Of}'."
        : !(From > 0 && To > From) || !double.IsFinite(To) ? $"The {Of} sweep runs from a value above 0 to a larger one."
        : Points < 2 ? $"The {Of} sweep needs two points at least."
        : Of != Frequency && (From != Math.Round(From) || To != Math.Round(To)) ? $"The {Of} sweep runs over whole numbers of elements."
        : Of == TxElements && d.Link is null ? "The TxElements sweep sizes the transmitter, and the chain has no link."
        : Of == RxElements && d.Stages.All(s => s.Part != ChainParts.Combiner) ? "The RxElements sweep sizes the receive array, which is the chain's Combiner, and the chain has none."
        : null;
}
