using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Styling;
using CircuitRF.Render.Chain;
using CircuitRF.Render.DataDisplay;

namespace CircuitRF.Ui.Views.Chain;

/// <summary>
/// The Blocks view's Levels strip: the chain's level diagram (<see cref="ChainLevelDiagram"/>, the drawing the CLI's
/// <c>--level-diagram</c> writes) under the blocks, each node on the gap between two blocks. The window measures the
/// gaps and hands them in as <see cref="NodeX"/>; a click on a node selects the stage whose output it is, and the selected
/// stage's node is drawn selected.
/// </summary>
public sealed class ChainLevelStrip : Control
{
    public static readonly StyledProperty<ChainLevelDiagram.Plan?> PlanProperty =
        AvaloniaProperty.Register<ChainLevelStrip, ChainLevelDiagram.Plan?>(nameof(Plan));

    public static readonly StyledProperty<IReadOnlyList<float>?> NodeXProperty =
        AvaloniaProperty.Register<ChainLevelStrip, IReadOnlyList<float>?>(nameof(NodeX));

    public static readonly StyledProperty<int> SelectedStageProperty =
        AvaloniaProperty.Register<ChainLevelStrip, int>(nameof(SelectedStage), -1, defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public ChainLevelDiagram.Plan? Plan { get => GetValue(PlanProperty); set => SetValue(PlanProperty, value); }

    /// <summary>Each node's x in this control, from the input to the output.</summary>
    public IReadOnlyList<float>? NodeX { get => GetValue(NodeXProperty); set => SetValue(NodeXProperty, value); }

    /// <summary>The stage drawn selected (its output node), and the one a click on a node selects; −1 for none.</summary>
    public int SelectedStage { get => GetValue(SelectedStageProperty); set => SetValue(SelectedStageProperty, value); }

    static ChainLevelStrip() => AffectsRender<ChainLevelStrip>(PlanProperty, NodeXProperty, SelectedStageProperty);

    public ChainLevelStrip() => Cursor = new Cursor(StandardCursorType.Hand);

    /// <summary>Where the plan is drawn now; null until the window has measured the blocks.</summary>
    public ChainLevelDiagram.Frame? Frame =>
        Plan is { } p && NodeX is { } xs && xs.Count == p.Nodes.Count && Bounds.Height > 60
            ? ChainLevelDiagram.Strip(p, xs, (float)Bounds.Height) : null;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (Frame is not { } f || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        int node = ChainLevelDiagram.NodeAt(f, (float)e.GetPosition(this).X);
        if (node < 0) return;
        // Node 0 is the input, which is no stage's output.
        SelectedStage = node - 1;
        e.Handled = true;
    }

    public override void Render(DrawingContext context)
    {
        if (Plan is not { } p || Frame is not { } f) return;
        var theme = ActualThemeVariant == ThemeVariant.Dark ? RenderTheme.Dark : RenderTheme.Light;
        context.Custom(new Op(new Rect(Bounds.Size), p, f, theme, SelectedStage >= 0 ? SelectedStage + 1 : -1));
    }

    private sealed class Op(Rect bounds, ChainLevelDiagram.Plan plan, ChainLevelDiagram.Frame frame, RenderTheme theme, int selected)
        : ICustomDrawOperation
    {
        public Rect Bounds => bounds;
        public bool HitTest(Point p) => bounds.Contains(p);
        public bool Equals(ICustomDrawOperation? other) => false;
        public void Dispose() { }

        public void Render(ImmediateDrawingContext context)
        {
            if (context.TryGetFeature<ISkiaSharpApiLeaseFeature>() is not { } lease) return;
            using var l = lease.Lease();
            ChainLevelDiagram.Draw(l.SkCanvas, plan, frame, theme, selected);
        }
    }
}
