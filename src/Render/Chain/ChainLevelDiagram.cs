// ================================================================
//  ChainLevelDiagram.cs  —  the RF Chain Budget's level diagram: one figure of the chain's levels node by node
//
//  The x axis is the chain's nodes (the input, then each stage's output); at each, in dBm, the signal, the noise floor
//  and the sensitivity, the IM3, every blocker and a canceller's interference, and the compression limit; between
//  them the SNR (noise up to signal), the headroom (signal up to compression) and the SFDR (noise up to where the IM3
//  would reach it) shaded. With a link, its EIRP stands left of the input, the path's loss on the segment down to the
//  power each element receives, and the receiver's ceiling marked there. A transmit chain's is its output power node by
//  node, the P1dB and the Psat referred to each node, the noise floor and the IM3, and its key the back-off from both,
//  the noise floor in dBm/Hz and the ACLR estimate; no sensitivity and no SFDR, which are a receiver's. Every number is
//  ChainLevels' and ChainTransmit's (and the link's own): nothing here computes a level.
//
//  One renderer, three callers: the window's Blocks view draws it in a strip under the blocks with each node on the
//  gap between two blocks; the CLI's --level-diagram and the docs draw it standalone, the nodes evenly spaced.
//
//  Why not a Data Display plot: a level plan shades BETWEEN its lines and labels its values at the nodes, and under the
//  blocks its nodes stand where the gaps are, which a linear axis cannot place. Its window is the level plots' own rule
//  (ChainBudgetDisplay.LevelWindow); the diagram's window also holds the P1dB, the sensitivity and the EIRP. An IM3
//  below it is a ▼ on the bottom edge at its node.
// ================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using CircuitRF.Design.Chain;
using CircuitRF.Render.DataDisplay;
using SkiaSharp;

namespace CircuitRF.Render.Chain;

public static class ChainLevelDiagram
{
    /// <summary>The link in front of the chain: its EIRP, the path's loss, the receive element's antenna gain, the power
    /// each element receives and the receiver's ceiling, IP1dB less its back-off.</summary>
    public sealed record Front(double EirpDbm, double PathDb, double RxGainDbi, double PrxDbm, double CeilingDbm);

    /// <summary>What the figure draws. <paramref name="ElementNodes"/>: how many nodes, from the input, carry one array
    /// element's levels (up to the combiner's input); 0 with no array. <paramref name="Tx"/>: a transmit chain's readouts,
    /// and <paramref name="PsatDbm"/> its Psat referred to each node (+∞ before anything saturates); null for a receiver.
    /// <paramref name="Image"/>: what the image bands the budget counts add to the NF, as the key says it; null for none.
    /// <paramref name="Desense"/>: the blocker that desensitises the chain most at its output and by how much, as the key
    /// says it; null when none does by <see cref="ChainBlocking.NoteDb"/>.</summary>
    public sealed record Plan(IReadOnlyList<ChainNodeLevel> Nodes, IReadOnlyList<double> FrequencyHz, int ElementNodes, int ArrayN,
                              IReadOnlyList<string> Blockers, double RequiredSnrDb, Front? Link, double Lo, double Hi,
                              ChainTxReadout? Tx = null, IReadOnlyList<double>? PsatDbm = null, string? Image = null,
                              string? Desense = null);

    /// <summary>The level plan of a budget; null when it was refused.</summary>
    public static Plan? For(ChainDesign d, ChainBudgetResult r)
    {
        var lv = ChainLevels.Evaluate(d, r);
        if (lv.Count == 0) return null;
        d = r.Design ?? d;
        int cmb = d.Stages.FindIndex(s => s.Part == ChainParts.Combiner);
        Front? front = null;
        if (d.Link is { } link && ChainCapacity.Read(r) is { } o)
        {
            var l = link.Budget(d.FrequencyHz);
            front = new Front(l.EirpDbm, l.LfsDb + l.AtmDb, ChainValue.Or(link.GetPath("RxAntennaGain"), 0), l.PrxDbm, o.CeilingDbm);
        }
        // The bottom is set by the levels a receiver lives between; the IM3, the compression limit, the SFDR's top, the
        // EIRP and the ceiling reach the top only. A transmitter's: its own levels, and the P1dB, the Psat and the IM3.
        var tx = ChainTransmit.Read(r);
        double[]? psat = tx is null ? null : ChainTransmit.PsatAtNodes(r);
        var framed = lv.SelectMany(n => new[] { n.SignalDbm, n.NoiseDbm, tx is null ? n.SensitivityDbm : double.NaN, n.InterferenceDbm,
                                                PhaseNoiseFloor(n), ReciprocalMixing(n) }.Concat(n.BlockerDbm));
        var topOnly = tx is null ? lv.SelectMany(n => new[] { n.Im3Dbm, n.CompressionDbm, n.NoiseDbm + n.SfdrDb })
                                 : lv.SelectMany(n => new[] { n.Im3Dbm, n.CompressionDbm }).Concat(psat!);
        if (front is not null)
        {
            framed = framed.Append(front.PrxDbm);
            topOnly = topOnly.Concat([front.EirpDbm, front.CeilingDbm]);
        }
        var (lo, hi) = ChainBudgetDisplay.LevelWindow(framed, topOnly);
        // The image bands the budget counts are in the noise floor from their mixer on; the key says how much of it.
        var counted = r.Images.Where(x => x.Counted).ToList();
        string? image = counted.Count == 0 ? null
            : $"image noise +{r.ImageNfDb.ToString("0.00", CultureInfo.InvariantCulture)} dB of NF ("
              + string.Join(", ", counted.Select(x => $"{x.Name} at {ChainSnp.Hz(x.ImageHz)}")) + ")";
        // The blocker that takes the most at the output, by reciprocal mixing and compression together.
        var end = lv[^1];
        int worst = Enumerable.Range(0, end.DesenseRmDb.Count).DefaultIfEmpty(-1)
                              .MaxBy(k => k < 0 ? 0 : end.DesenseRmDb[k] + end.DesenseCompDb[k]);
        string? desense = worst < 0 || end.DesenseRmDb[worst] + end.DesenseCompDb[worst] < ChainBlocking.NoteDb ? null
            : $"blocker {worst + 1} desensitises {(end.DesenseRmDb[worst] + end.DesenseCompDb[worst]).ToString("0.00", CultureInfo.InvariantCulture)} dB";
        return new Plan(lv, [d.FrequencyHz, .. r.Stages.Select(s => s.FrequencyHz)], cmb >= 0 ? cmb + 1 : 0,
                        cmb >= 0 ? ChainParts.ReadCombiner(d.Stages[cmb]).N : 1,
                        [.. d.Blockers.Select((b, k) => $"blocker {k + 1}")], d.RequiredSnrDb, front, lo, hi, tx, psat, image, desense);
    }

    /// <summary>The LOs' phase noise in the channel at a node, dBm: the signal there less what the mask leaves it.</summary>
    private static double PhaseNoiseFloor(ChainNodeLevel n) => n.SignalDbm + n.PhaseNoiseDbc;

    /// <summary>Every blocker's reciprocal mixing at a node together, dBm; −∞ before a mixer with a mask.</summary>
    private static double ReciprocalMixing(ChainNodeLevel n)
    {
        double w = n.ReciprocalDbm.Where(double.IsFinite).Sum(x => Math.Pow(10, x / 10));
        return w > 0 ? 10 * Math.Log10(w) : double.NegativeInfinity;
    }

    // ── where things go ───────────────────────────────────────────────────────

    /// <summary>The room left of the plot for the dB scale, and the slot the link's EIRP takes before the input.</summary>
    public const float AxisGutter = 46f, FrontSlot = 92f, EndGutter = 150f;

    /// <summary>Where the plan is drawn: the plot area, each node's x, the EIRP's x (with a link), the font size, and
    /// whether the figure carries its own title.</summary>
    public sealed record Frame(SKRect Plot, IReadOnlyList<float> NodeX, float FrontX, float Font, string? Title);

    /// <summary>How far left of the plot's left edge the figure reaches: the dB scale, and the link's slot.</summary>
    public static float LeftGutter(Plan p) => AxisGutter + (p.Link is null ? 0 : FrontSlot);

    /// <summary>The standalone figure: the nodes evenly spaced across <paramref name="width"/>.</summary>
    public static Frame Uniform(Plan p, float width, float height, string? title)
    {
        float top = title is null ? 30 : 54, bottom = height - 46;
        float left = LeftGutter(p), right = width - EndGutter;
        float slot = (right - left) / p.Nodes.Count;
        var xs = Enumerable.Range(0, p.Nodes.Count).Select(k => left + (k + 0.5f) * slot).ToArray();
        return new Frame(new SKRect(AxisGutter, top, right, bottom), xs, AxisGutter + FrontSlot / 2, 12f, title);
    }

    /// <summary>Under the blocks: <paramref name="nodeX"/> is each node's x in the strip, which reaches
    /// <see cref="LeftGutter"/> left of the blocks and <see cref="EndGutter"/> past the last node.</summary>
    public static Frame Strip(Plan p, IReadOnlyList<float> nodeX, float height)
        => new(new SKRect(AxisGutter, 20, nodeX[^1] + 24, height - 30), nodeX, AxisGutter + FrontSlot / 2, 11f, null);

    /// <summary>The figure's full width for <paramref name="f"/>: its last node and the labels beyond it.</summary>
    public static float Width(Frame f) => f.Plot.Right + EndGutter - 24;

    /// <summary>
    /// The nodes of a lineup of blocks: the input on the gap before the first block, each stage's output on the gap
    /// after its block (the last one the gap before OUT). <paramref name="blocks"/> are the blocks' left and right
    /// edges; <paramref name="gap"/> the width of the gap before the first and after the last.
    /// </summary>
    public static float[] NodesBetween(IReadOnlyList<(float Left, float Right)> blocks, float gap)
    {
        if (blocks.Count == 0) return [];
        var xs = new float[blocks.Count + 1];
        xs[0] = blocks[0].Left - gap / 2;
        for (int k = 1; k < blocks.Count; k++) xs[k] = (blocks[k - 1].Right + blocks[k].Left) / 2;
        xs[^1] = blocks[^1].Right + gap / 2;
        return xs;
    }

    /// <summary>The nodes whose IM3 lies under the figure's window: each drawn as a ▼ on the bottom edge.</summary>
    public static IReadOnlyList<int> Im3BelowFrame(Plan p)
        => [.. Enumerable.Range(0, p.Nodes.Count).Where(k => double.IsFinite(p.Nodes[k].Im3Dbm) && p.Nodes[k].Im3Dbm < p.Lo)];

    /// <summary>The node nearest <paramref name="x"/>, within half the spacing; −1 for none.</summary>
    public static int NodeAt(Frame f, float x)
    {
        int best = -1;
        float d = float.MaxValue;
        for (int k = 0; k < f.NodeX.Count; k++)
            if (Math.Abs(f.NodeX[k] - x) < d) { d = Math.Abs(f.NodeX[k] - x); best = k; }
        float half = f.NodeX.Count > 1 ? (f.NodeX[^1] - f.NodeX[0]) / (f.NodeX.Count - 1) / 2 : 60;
        return d <= half ? best : -1;
    }

    // ── drawing ───────────────────────────────────────────────────────────────

    private static SKColor Palette(RenderTheme t, int order)
        => t.ToSKColor(TraceProperties.ColorLUT[TraceProperties.LineColorOrder[order % TraceProperties.LineColorOrder.Length]]);

    /// <summary>A level the budget has: an absent one is NaN or −∞, never a number.</summary>
    private static bool Level(double v) => double.IsFinite(v);

    private static string D(double v) => v.ToString("0.0", CultureInfo.InvariantCulture).Replace('-', '−');

    private static string Hz(double f) => f >= 1e9 ? $"{f / 1e9:0.###} GHz" : f >= 1e6 ? $"{f / 1e6:0.###} MHz"
                                        : f >= 1e3 ? $"{f / 1e3:0.###} kHz" : f > 0 ? $"{f:0.###} Hz" : "0 Hz";

    /// <summary>One series: its name, colour, values at the nodes, and how it is stroked.</summary>
    private sealed record Series(string Name, SKColor Color, double[] Values, float Width, bool Dashed, bool Points, bool ValueLabels);

    /// <summary>Draws the plan into <paramref name="f"/>. <paramref name="selected"/>: the node drawn selected, −1 none.</summary>
    public static void Draw(SKCanvas c, Plan p, Frame f, RenderTheme theme, int selected = -1)
    {
        var plot = f.Plot;
        var xs = f.NodeX;
        int n = p.Nodes.Count;
        float Y(double v) => plot.Bottom - (float)((v - p.Lo) / (p.Hi - p.Lo)) * plot.Height;
        float slot = n > 1 ? (xs[^1] - xs[0]) / (n - 1) : 80;

        using var regular = new SKFont(SkiaFonts.PlexRegular, f.Font);
        using var small = new SKFont(SkiaFonts.PlexRegular, f.Font * 0.85f);
        using var bold = new SKFont(SkiaFonts.PlexSemiBold, f.Font);
        using var text = new SKPaint { IsAntialias = true, Color = theme.TextColor };
        using var faint = new SKPaint { IsAntialias = true, Color = theme.TextColor.WithAlpha(150) };
        using var grid = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1, Color = theme.MinorGridColor };
        using var frame = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1, Color = theme.GridColor };
        using var divider = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1, Color = theme.GridColor,
                                          PathEffect = SKPathEffect.CreateDash([4, 3], 0) };

        if (f.Title is { } title) c.DrawText(title, plot.Left, 20, SKTextAlign.Left, bold, text);

        // The selected node, under everything.
        if (selected >= 0 && selected < n)
        {
            using var sel = new SKPaint { Color = RenderTheme.SelectionColorFallback.WithAlpha(38) };
            c.DrawRect(xs[selected] - slot * 0.42f, plot.Top, slot * 0.84f, plot.Height, sel);
        }

        // The dB scale: a line every 10, 20, 50 or 100 dB, the finest that leaves two lines of text between two of them.
        double span = p.Hi - p.Lo, step = new[] { 10.0, 20, 50, 100 }.FirstOrDefault(d => d / span * plot.Height >= 2 * f.Font, 100);
        for (double v = Math.Ceiling(p.Lo / step) * step; v <= p.Hi + 1e-9; v += step)
        {
            c.DrawLine(plot.Left, Y(v), plot.Right, Y(v), grid);
            c.DrawText(D(v).Replace(".0", ""), plot.Left - 5, Y(v) + f.Font * 0.35f, SKTextAlign.Right, small, faint);
        }
        c.Save();
        c.RotateDegrees(-90, plot.Left - AxisGutter + f.Font, plot.MidY);
        c.DrawText("dBm", plot.Left - AxisGutter + f.Font, plot.MidY + f.Font * 0.35f, SKTextAlign.Center, small, faint);
        c.Restore();
        c.DrawRect(plot, frame);

        // The nodes: a line and a name each; the frequency over each run of nodes, a mixer between two runs.
        for (int k = 0; k < n; k++)
        {
            c.DrawLine(xs[k], plot.Top, xs[k], plot.Bottom, grid);
            c.DrawText(p.Nodes[k].Node, xs[k], plot.Bottom + f.Font * 1.15f, SKTextAlign.Center, k == selected ? bold : regular, text);
        }
        // Where the chain's own nodes start: the plot's edge, or the link's column's when it has one.
        float chainLeft = p.Link is null ? plot.Left : f.FrontX + FrontSlot / 2 - 8;
        for (int a = 0, b; a < n; a = b)
        {
            for (b = a + 1; b < n && p.FrequencyHz[b] == p.FrequencyHz[a]; b++) { }
            if (a > 0) Line(c, (xs[a - 1] + xs[a]) / 2, plot.Top, (xs[a - 1] + xs[a]) / 2, plot.Bottom, divider);
            float l = a > 0 ? (xs[a - 1] + xs[a]) / 2 : chainLeft, r = b < n ? (xs[b - 1] + xs[b]) / 2 : plot.Right;
            c.DrawText(Hz(p.FrequencyHz[a]), (l + r) / 2, plot.Top - 6, SKTextAlign.Center, small, faint);
        }
        if (p.ElementNodes > 0 && p.ArrayN > 1)
        {
            // Over the plot, in the frequencies' row: left-aligned on the first node, the run's own label centred over it.
            float l = Math.Max(xs[0] - slot * 0.4f, chainLeft + 4), r = xs[p.ElementNodes - 1] + slot * 0.4f;
            c.DrawText($"each of {p.ArrayN} elements", l, plot.Top - 6, SKTextAlign.Left, small, faint);
            // A bracket, so it reads as the span it marks and not as the frequency's underline.
            c.DrawLine(l, plot.Top - 2.5f, r, plot.Top - 2.5f, frame);
            c.DrawLine(l, plot.Top - 6, l, plot.Top, frame);
            c.DrawLine(r, plot.Top - 6, r, plot.Top, frame);
        }

        double[] Of(Func<ChainNodeLevel, double> pick) => [.. p.Nodes.Select(pick)];
        var signal = Of(x => x.SignalDbm);
        var noise = Of(x => x.NoiseDbm);
        var comp = Of(x => x.CompressionDbm);
        var sfdrTop = Of(x => x.NoiseDbm + x.SfdrDb);

        // The bands, under the lines.
        // The SFDR is hatched rather than filled: it overlaps the other two, and three tints over each other read as one grey.
        void Band(double[] lower, double[] upper, SKColor color, bool hatched = false)
        {
            using var fill = new SKPaint { IsAntialias = true, Color = color.WithAlpha(34) };
            using var hatch = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1, Color = color.WithAlpha(72) };
            for (int a = 0; a < n; a++)
            {
                if (!Level(lower[a]) || !Level(upper[a])) continue;
                int b = a;
                while (b + 1 < n && Level(lower[b + 1]) && Level(upper[b + 1])) b++;
                using var path = new SKPath();
                if (a == b)
                {
                    // A band at one node alone has no width between nodes: a narrow bar, so the key never names a band
                    // nobody can see.
                    float w = slot * 0.15f;
                    path.AddRect(new SKRect(xs[a] - w, Y(upper[a]), xs[a] + w, Y(lower[a])));
                }
                else
                {
                    path.MoveTo(xs[a], Y(upper[a]));
                    for (int k = a + 1; k <= b; k++) path.LineTo(xs[k], Y(upper[k]));
                    for (int k = b; k >= a; k--) path.LineTo(xs[k], Y(lower[k]));
                    path.Close();
                }
                if (hatched) Hatch(c, path, hatch, 7); else c.DrawPath(path, fill);
                a = b;
            }
        }
        var cSignal = Palette(theme, 1);
        var cNoise = Palette(theme, 3);
        var cIm3 = Palette(theme, 4);
        var cComp = theme.LimitColor;
        c.Save();
        c.ClipRect(plot);
        if (p.Tx is null) Band(noise, sfdrTop, cIm3, hatched: true);
        Band(noise, signal, cNoise);
        Band(signal, comp, cComp);

        // The link: the EIRP before the input, the path down to the power each element receives, the ceiling there.
        var ends = new List<(double V, string Text, SKColor Color, bool Below)>();
        if (p.Link is { } lk)
        {
            float fx = f.FrontX, x0 = xs[0];
            using var pathPaint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2, Color = cSignal,
                                                PathEffect = SKPathEffect.CreateDash([6, 4], 0) };
            Line(c, fx, Y(lk.EirpDbm), x0, Y(lk.PrxDbm), pathPaint);
            Dot(c, fx, Y(lk.EirpDbm), cSignal, 3.5f, filled: true);
            if (Math.Abs(lk.PrxDbm - signal[0]) > 0.05)
            {
                // The power arriving is above the ceiling: the input is held there.
                Dot(c, x0, Y(lk.PrxDbm), cSignal, 3.5f, filled: false);
                Line(c, x0, Y(lk.PrxDbm), x0, Y(signal[0]), pathPaint);
            }
            using var ceil = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, Color = cComp,
                                           PathEffect = SKPathEffect.CreateDash([4, 3], 0) };
            if (Level(lk.CeilingDbm))
            {
                // The input's own mark: it starts at the chain's left edge, never in the link's column, and its name sits
                // above it, right of the input, clear of the path arriving from the left.
                Line(c, Math.Max(x0 - slot * 0.35f, chainLeft + 2), Y(lk.CeilingDbm), x0 + slot * 0.35f, Y(lk.CeilingDbm), ceil);
                Say(c, $"ceiling {D(lk.CeilingDbm)}", x0 + 5, Y(lk.CeilingDbm) - 4, SKTextAlign.Left, small, Readable(theme, cComp), theme.BackgroundColor);
            }
            // Under its point, at the column's left: the frame's top is often only 10 dB above it, and the path leaves
            // the point down to the right.
            Say(c, $"EIRP {D(lk.EirpDbm)}", plot.Left + 3, Y(lk.EirpDbm) + f.Font + 2, SKTextAlign.Left, small, Readable(theme, cSignal),
                theme.BackgroundColor);
            // At the foot of the link's column, clear of the segment that falls across it.
            float fy = plot.Bottom - f.Font * (lk.RxGainDbi != 0 ? 1.6f : 0.6f);
            c.DrawText($"path −{D(lk.PathDb).TrimStart('−')} dB", fx, fy, SKTextAlign.Center, small, faint);
            if (lk.RxGainDbi != 0)
                c.DrawText($"antenna {(lk.RxGainDbi > 0 ? "+" : "−")}{Math.Abs(lk.RxGainDbi).ToString("0.#", CultureInfo.InvariantCulture)} dBi",
                           fx, fy + f.Font, SKTextAlign.Center, small, faint);
        }
        c.Restore();
        if (p.Link is not null)
        {
            Line(c, chainLeft, plot.Top, chainLeft, plot.Bottom, divider);
            c.DrawText("link", f.FrontX, plot.Top - 6, SKTextAlign.Center, small, faint);
            c.DrawText("TX", f.FrontX, plot.Bottom + f.Font * 1.15f, SKTextAlign.Center, regular, text);
        }

        // The lines: the quiet ones first, the signal on top.
        var series = new List<Series>();
        if (p.Tx is null) series.Add(new("sensitivity", Palette(theme, 3), Of(x => x.SensitivityDbm), 1.2f, true, false, false));
        // An LO's phase noise and the blockers' reciprocal mixing, in the channel, from the mixer that states its mask on;
        // under the noise floor, whose values are written over them.
        if (p.Nodes.Any(x => Level(PhaseNoiseFloor(x)))) series.Add(new("LO phase noise", Palette(theme, 8), Of(PhaseNoiseFloor), 1.4f, true, false, false));
        if (p.Nodes.Any(x => Level(ReciprocalMixing(x)))) series.Add(new("reciprocal mixing", Palette(theme, 9), Of(ReciprocalMixing), 1.4f, true, false, false));
        series.Add(new("noise floor", cNoise, noise, 2f, false, true, true));
        // A transmitter's ceiling over its P1dB, dashed in the compression's colour; named at the output, not at each node.
        if (p.PsatDbm is { } ps) series.Add(new("Psat", cComp, [.. ps], 1.4f, true, true, false));
        series.Add(new("P1dB", cComp, comp, 1.6f, false, true, true));
        series.Add(new("IM3", cIm3, Of(x => x.Im3Dbm), 1.6f, false, true, false));
        for (int k = 0; k < p.Blockers.Count; k++)
        {
            int kk = k;
            series.Add(new(p.Blockers[k], Palette(theme, 5 + k), Of(x => x.BlockerDbm[kk]), 1.4f, true, true, false));
        }
        if (p.Nodes.Any(x => Level(x.InterferenceDbm))) series.Add(new("interference", Palette(theme, 2), Of(x => x.InterferenceDbm), 1.6f, false, true, false));
        series.Add(new(p.Tx is null ? "signal" : "Pout", cSignal, signal, 2.6f, false, true, true));

        bool roomForValues = slot >= 44;
        c.Save();
        c.ClipRect(new SKRect(plot.Left, plot.Top - f.Font, plot.Right, plot.Bottom));
        foreach (var s in series)
        {
            using var stroke = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = s.Width, Color = s.Color,
                                             StrokeJoin = SKStrokeJoin.Round,
                                             PathEffect = s.Dashed ? SKPathEffect.CreateDash([6, 4], 0) : null };
            using var path = new SKPath();
            bool pen = false;
            for (int k = 0; k < n; k++)
            {
                if (!Level(s.Values[k])) { pen = false; continue; }
                if (pen) path.LineTo(xs[k], Y(s.Values[k])); else path.MoveTo(xs[k], Y(s.Values[k]));
                pen = true;
            }
            Stroke(c, path, stroke);
            for (int k = 0; k < n; k++)
            {
                if (!Level(s.Values[k])) continue;
                if (s.Points) Dot(c, xs[k], Y(s.Values[k]), s.Color, k == selected ? 4f : 2.6f, filled: true);
                if (s.ValueLabels && (roomForValues || k == selected))
                {
                    // The signal's and the compression's value above their point, the noise floor's below; the input's
                    // signal right of its point where a link's path arrives from the left. Each on a halo of the page, so
                    // it reads over a band's tint.
                    float y = s.Name == "noise floor" ? Y(s.Values[k]) + f.Font + 2 : Y(s.Values[k]) - 5;
                    bool aside = k == 0 && p.Link is not null && s.Name == "signal";
                    Say(c, D(s.Values[k]), aside ? xs[k] + 6 : xs[k], y, aside ? SKTextAlign.Left : SKTextAlign.Center, small,
                        Readable(theme, s.Color), theme.BackgroundColor);
                }
            }
            int last = Array.FindLastIndex(s.Values, Level);
            if (last >= 0) ends.Add((s.Values[last], $"{s.Name} {D(s.Values[last])}", s.Color, s.Values[last] < p.Lo));
        }
        c.Restore();

        // An IM3 under the frame keeps its node: a ▼ on the bottom edge, its value in the line's name at the output. Drawn,
        // not a character: the figure's faces have no glyph for one.
        void Down(float x, float bottom, float h, SKColor color)
        {
            using var paint = new SKPaint { IsAntialias = true, Color = color };
            using var tri = new SKPath();
            tri.MoveTo(x - h * 0.6f, bottom - h);
            tri.LineTo(x + h * 0.6f, bottom - h);
            tri.LineTo(x, bottom);
            tri.Close();
            c.DrawPath(tri, paint);
        }
        foreach (int k in Im3BelowFrame(p)) Down(xs[k], plot.Bottom - 1, f.Font * 0.55f, cIm3);

        // Each line named at the output, nudged apart where two would overlap, with a leader to its own level.
        float lx = plot.Right + 6, gap = f.Font * 1.2f;
        var placed = ends.Select(e => (Y: Math.Clamp(Y(e.V), plot.Top, plot.Bottom), At: Math.Clamp(Y(e.V), plot.Top, plot.Bottom), e.Text, e.Color, e.Below))
                         .OrderBy(e => e.At).ToList();
        for (int i = 1; i < placed.Count; i++)
            if (placed[i].Y < placed[i - 1].Y + gap) placed[i] = placed[i] with { Y = placed[i - 1].Y + gap };
        float over = placed.Count > 0 ? placed[^1].Y - (plot.Bottom + f.Font) : 0;
        if (over > 0) for (int i = 0; i < placed.Count; i++) placed[i] = placed[i] with { Y = placed[i].Y - over };
        for (int i = placed.Count - 2; i >= 0; i--)
            if (placed[i].Y > placed[i + 1].Y - gap) placed[i] = placed[i] with { Y = placed[i + 1].Y - gap };
        foreach (var e in placed)
        {
            using var lead = new SKPaint { IsAntialias = true, StrokeWidth = 1, Color = e.Color.WithAlpha(140), Style = SKPaintStyle.Stroke };
            c.DrawLine(xs[^1] + 3, e.At, lx - 2, e.Y, lead);
            Say(c, e.Text, lx, e.Y + f.Font * 0.35f, SKTextAlign.Left, small, Readable(theme, e.Color));
            if (e.Below) Down(lx + small.MeasureText(e.Text) + f.Font * 0.6f, e.Y + f.Font * 0.3f, f.Font * 0.5f, Readable(theme, e.Color));
        }

        // The bands' widths at the output, as their key.
        var o = p.Nodes[^1];
        var keys = new List<(string Text, SKColor Color)> { ($"SNR {D(o.SnrDb)} dB", cNoise) };
        if (p.Tx is { } tx)
        {
            // A transmitter's: the back-off from both limits, the floor per hertz, the ACLR the intercept gives.
            keys.Add(($"back-off {D(tx.BackoffP1dbDb)} dB from P1dB, {D(tx.BackoffPsatDb)} dB from Psat", cComp));
            keys.Add(($"noise floor {D(tx.NoisePsdDbmHz)} dBm/Hz", cNoise));
            if (Level(tx.AclrDbc)) keys.Add(($"ACLR {D(tx.AclrDbc)} dBc (estimate, from OIP3)", cIm3));
        }
        else if (Level(o.MarginDb))
        {
            // The budget's margin, as the Levels tab gives it: P1dB less the strongest at the output, which it names.
            int top = -1;
            for (int k = 0; k < o.BlockerDbm.Count; k++)
                if (Level(o.BlockerDbm[k]) && o.BlockerDbm[k] > (top < 0 ? o.SignalDbm : o.BlockerDbm[top])) top = k;
            keys.Add(($"margin to P1dB {D(o.MarginDb)} dB ({(top < 0 ? "signal" : p.Blockers[top])})", cComp));
        }
        if (p.Tx is null)
        {
            if (Level(o.SfdrDb)) keys.Add(($"SFDR {D(o.SfdrDb)} dB", cIm3));
            keys.Add(($"sensitivity: MDS + {D(p.RequiredSnrDb).Replace(".0", "")} dB", Palette(theme, 3)));
        }
        if (p.Image is { } im) keys.Add((im, cNoise));
        if (p.Desense is { } ds) keys.Add((ds, Palette(theme, 9)));
        float kx = plot.Left, ky = plot.Bottom + f.Font * 2.4f;
        foreach (var (t, col) in keys)
        {
            bool line = t.StartsWith("sens", StringComparison.Ordinal) || t.StartsWith("noise", StringComparison.Ordinal)
                        || t.StartsWith("ACLR", StringComparison.Ordinal) || t.StartsWith("image", StringComparison.Ordinal)
                        || t.StartsWith("blocker", StringComparison.Ordinal),
                 hatched = t.StartsWith("SFDR", StringComparison.Ordinal);
            using var sw = new SKPaint
            {
                IsAntialias = true, Color = col.WithAlpha(line || hatched ? (byte)255 : (byte)90), StrokeWidth = 1.2f,
                Style = line || hatched ? SKPaintStyle.Stroke : SKPaintStyle.Fill,
                PathEffect = t.StartsWith("sens", StringComparison.Ordinal) ? SKPathEffect.CreateDash([4, 2], 0) : null,
            };
            var swatch = new SKRect(kx, ky - f.Font * 0.7f, kx + f.Font * 0.9f, ky);
            if (line) Line(c, kx, ky - f.Font * 0.35f, kx + f.Font * 0.9f, ky - f.Font * 0.35f, sw);
            else if (hatched) { using var box = new SKPath(); box.AddRect(swatch); Hatch(c, box, sw, 3); }
            else c.DrawRect(swatch, sw);
            c.DrawText(t, kx + f.Font * 1.2f, ky, SKTextAlign.Left, small, text);
            kx += f.Font * 1.2f + small.MeasureText(t) + f.Font * 1.4f;
        }
    }

    /// <summary>A line's colour as its text is drawn: a pale one (the palette's pink on white) mixed toward the text
    /// colour until it reads.</summary>
    private static SKColor Readable(RenderTheme t, SKColor c)
    {
        for (int i = 0; i < 6 && RenderTheme.Contrast(c, t.BackgroundColor) < 3.0; i++)
            c = new SKColor((byte)((c.Red + t.TextColor.Red) / 2), (byte)((c.Green + t.TextColor.Green) / 2), (byte)((c.Blue + t.TextColor.Blue) / 2));
        return c;
    }

    /// <param name="halo">Drawn round the letters first, so they read over whatever is under them; none: plain.</param>
    private static void Say(SKCanvas c, string text, float x, float y, SKTextAlign align, SKFont font, SKColor color, SKColor? halo = null)
    {
        if (halo is { } h)
        {
            using var ring = new SKPaint { IsAntialias = true, Color = h.WithAlpha(220), Style = SKPaintStyle.Stroke, StrokeWidth = 3,
                                           StrokeJoin = SKStrokeJoin.Round };
            c.DrawText(text, x, y, align, font, ring);
        }
        using var p = new SKPaint { IsAntialias = true, Color = color };
        c.DrawText(text, x, y, align, font, p);
    }

    /// <summary>
    /// Strokes <paramref name="path"/>, a dashed paint's dashes as geometry: Skia's SVG page drops a stroke that carries a
    /// path effect (the Data Display's dashed lines are drawn as segments for the same reason), so the dash is applied
    /// here and the outline it gives is filled, which every page carries.
    /// </summary>
    private static void Stroke(SKCanvas c, SKPath path, SKPaint paint)
    {
        if (paint.PathEffect is null) { c.DrawPath(path, paint); return; }
        using var outline = new SKPath();
        if (!paint.GetFillPath(path, outline)) return;
        using var fill = new SKPaint { IsAntialias = true, Color = paint.Color, Style = SKPaintStyle.Fill };
        c.DrawPath(outline, fill);
    }

    private static void Line(SKCanvas c, float x0, float y0, float x1, float y1, SKPaint paint)
    {
        using var path = new SKPath();
        path.MoveTo(x0, y0);
        path.LineTo(x1, y1);
        Stroke(c, path, paint);
    }

    /// <summary>Diagonal lines <paramref name="pitch"/> apart across <paramref name="area"/>, clipped to it. Drawn as lines
    /// rather than with a 2D-line path effect, which an SVG page cannot carry and drops.</summary>
    private static void Hatch(SKCanvas c, SKPath area, SKPaint line, float pitch)
    {
        var b = area.Bounds;
        c.Save();
        c.ClipPath(area, antialias: true);
        for (float x = b.Left - b.Height; x < b.Right; x += pitch) c.DrawLine(x, b.Bottom, x + b.Height, b.Top, line);
        c.Restore();
    }

    private static void Dot(SKCanvas c, float x, float y, SKColor color, float r, bool filled)
    {
        using var p = new SKPaint { IsAntialias = true, Color = color, Style = filled ? SKPaintStyle.Fill : SKPaintStyle.Stroke, StrokeWidth = 1.5f };
        c.DrawCircle(x, y, r, p);
    }

    // ── the standalone figure ─────────────────────────────────────────────────

    /// <summary>The figure on a page of its own: background, the plan, the nodes evenly spaced.</summary>
    public static (Action<SKCanvas> Compose, PagePlacement Page) Figure(Plan p, string? title, RenderTheme theme,
                                                                         float width = 0, float height = 520)
    {
        if (width <= 0) width = Math.Max(760, LeftGutter(p) + EndGutter + 104 * p.Nodes.Count);
        var f = Uniform(p, width, height, title);
        return (c =>
        {
            using var bg = new SKPaint { Color = theme.BackgroundColor };
            c.DrawRect(0, 0, width, height, bg);
            Draw(c, p, f, theme);
        }, new PagePlacement(width, height, 0));
    }

    /// <summary>True for the kinds <see cref="Write"/> writes: an <c>.svg</c> or a <c>.png</c>.</summary>
    public static bool Writes(string path) => Path.GetExtension(path).ToLowerInvariant() is ".svg" or ".png";

    /// <summary>Writes the figure: an <c>.svg</c>, or a <c>.png</c> at twice its size, making its folder as <c>render</c>
    /// does. False for any other kind, and nothing written.</summary>
    public static bool Write(string path, Plan p, string? title)
    {
        if (!Writes(path)) return false;
        var (compose, page) = Figure(p, title, RenderTheme.Light);
        if (Path.GetDirectoryName(Path.GetFullPath(path)) is { Length: > 0 } dir) Directory.CreateDirectory(dir);
        if (Path.GetExtension(path).Equals(".svg", StringComparison.OrdinalIgnoreCase)) PlotDocumentWriter.WriteSvg(path, compose, page);
        else File.WriteAllBytes(path, PlotDocumentWriter.BuildPngBytes(compose, page, 2f)!);
        return true;
    }
}
