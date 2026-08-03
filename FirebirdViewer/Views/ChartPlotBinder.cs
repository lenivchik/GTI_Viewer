using System;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using FirebirdViewer.ViewModels;
using ScottPlot;
using ScottPlot.WPF;

namespace FirebirdViewer.Views;

/// <summary>
/// Bridges one <see cref="ChartPanelViewModel"/> to one ScottPlot <see cref="WpfPlot"/>.
/// ALL ScottPlot API usage in the app lives here, so a library upgrade only ever
/// touches this file.
///
/// ScottPlot 5 API touch-points to confirm when building:
///   • plot.Add.Scatter(double[], double[])  → Scatter { Color, LineWidth, MarkerSize, LegendText }
///   • plot.Axes.Bottom / .Left / .Top / .AddLeftAxis() / .AddTopAxis()
///   • plottable.Axes.XAxis / .YAxis
///   • axis.Label.Text / axis.Label.ForeColor / axis.TickLabelStyle.ForeColor
///   • axis.TickGenerator = new ScottPlot.TickGenerators.DateTimeAutomatic()
///   • plot.Axes.Rules.Add(new ScottPlot.AxisRules.LockedVertical/LockedHorizontal(axis))
///   • plot.Axes.AutoScale() / .InvertY() / .GetLimits()
///   • plot.Add.Rectangle(l, r, b, t) → Rectangle { FillStyle.Color, LineStyle.Color }
///   • plot.Remove(plottable) / plot.GetCoordinates(Pixel) / wpfPlot.Refresh()
/// </summary>
public sealed class ChartPlotBinder : IDisposable
{
    private readonly WpfPlot _plot;
    private readonly ChartPanelViewModel _vm;

    private bool _dragging;
    private double _dragStartIndep;
    private IPlottable? _bandRect;
    private IPlottable? _pickMarker;
    private IPlottable? _pickLabel;

    // Axes we added via AddLeftAxis/AddTopAxis. plot.Clear() removes plottables
    // but not axes, so we must remove these ourselves before each re-render.
    private readonly System.Collections.Generic.List<ScottPlot.IAxis> _addedAxes = new();

    // Each plotted curve with its scatter and the concrete axes it was drawn
    // against — used to hit-test double-clicks against every curve.
    private sealed record PlottedCurve(
        ScottPlot.Plottables.Scatter Scatter, ScottPlot.IXAxis X, ScottPlot.IYAxis Y, ChartSeriesData Data);
    private readonly System.Collections.Generic.List<PlottedCurve> _plotted = new();

    public ChartPlotBinder(WpfPlot plot, ChartPanelViewModel vm)
    {
        _plot = plot;
        _vm = vm;

        _vm.RenderRequested += Render;
        _vm.SelectionCleared += OnSelectionCleared;
        _vm.ZoomRequested += OnZoom;
        _vm.ZoomResetRequested += OnZoomReset;

        _plot.PreviewMouseLeftButtonDown += OnMouseDown;
        _plot.PreviewMouseMove += OnMouseMove;
        _plot.PreviewMouseLeftButtonUp += OnMouseUp;
        _plot.PreviewMouseDoubleClick += OnDoubleClick;
        _plot.PreviewMouseWheel += OnMouseWheel;
        _plot.MouseLeave += OnMouseLeave;

        Render();
    }

    public void Dispose()
    {
        _vm.RenderRequested -= Render;
        _vm.SelectionCleared -= OnSelectionCleared;
        _vm.ZoomRequested -= OnZoom;
        _vm.ZoomResetRequested -= OnZoomReset;
        _plot.PreviewMouseLeftButtonDown -= OnMouseDown;
        _plot.PreviewMouseMove -= OnMouseMove;
        _plot.PreviewMouseLeftButtonUp -= OnMouseUp;
        _plot.PreviewMouseDoubleClick -= OnDoubleClick;
        _plot.PreviewMouseWheel -= OnMouseWheel;
        _plot.MouseLeave -= OnMouseLeave;
    }

    // ============================================================
    // Scale vs. scroll
    // ============================================================

    /// <summary>
    /// Plain wheel scrolls the surrounding list; Ctrl+wheel zooms the chart. Without
    /// this split the plot swallowed every wheel event, so there was no way to move
    /// up and down the screen once the pointer was over a chart.
    /// </summary>
    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;   // let ScottPlot zoom

        e.Handled = true;
        if (VisualTreeHelper.GetParent(_plot) is UIElement parent)
        {
            parent.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
            {
                RoutedEvent = UIElement.MouseWheelEvent,
                Source = _plot,
            });
        }
    }

    /// <summary>Scale the independent axis about its centre. factor &gt; 1 zooms in.</summary>
    private void OnZoom(double factor)
    {
        if (factor <= 0) return;
        var plot = _plot.Plot;
        var axis = _vm.Orientation == ChartOrientation.Vertical
            ? (ScottPlot.IAxis)plot.Axes.Left
            : plot.Axes.Bottom;

        double min = axis.Min, max = axis.Max;
        if (!double.IsFinite(min) || !double.IsFinite(max) || max <= min) return;

        double centre = (min + max) / 2;
        double half = (max - min) / 2 / factor;
        axis.Min = centre - half;
        axis.Max = centre + half;
        _plot.Refresh();
    }

    /// <summary>Back to the full data range (a full re-render also restores the axis rules).</summary>
    private void OnZoomReset() => Render();

    private void OnMouseLeave(object sender, MouseEventArgs e) => _vm.CursorText = null;

    private static ScottPlot.Color ToScott(ChartColor c) => new(c.R, c.G, c.B);

    /// <summary>
    /// Tick generator for the independent axis. DateTimeAutomatic only emits ticks
    /// on a horizontal axis — on the vertical (Left) axis it produces none, which
    /// left the time scale blank in portrait mode. There we fall back to numeric
    /// ticks and format the OA-date values ourselves.
    /// </summary>
    private static ScottPlot.ITickGenerator MakeIndepTickGenerator(ChartSnapshot snap)
    {
        if (!snap.IsTime)
            return new ScottPlot.TickGenerators.NumericAutomatic();

        if (!snap.Vertical)
            return new ScottPlot.TickGenerators.DateTimeAutomatic();

        var gen = new ScottPlot.TickGenerators.NumericAutomatic();
        gen.LabelFormatter = FormatOaDate;
        return gen;
    }

    private static string FormatOaDate(double oa)
    {
        // OA dates outside DateTime's range throw; show nothing rather than crash.
        if (oa < -657435.0 || oa > 2958465.99999999) return string.Empty;
        return DateTime.FromOADate(oa).ToString("dd.MM HH:mm");
    }

    private static string FormatOaDateLong(double oa)
    {
        if (oa < -657435.0 || oa > 2958465.99999999) return string.Empty;
        return DateTime.FromOADate(oa).ToString("dd.MM.yyyy HH:mm:ss");
    }

    /// <summary>Maps our CurveLineType onto ScottPlot's line pattern + connect style.</summary>
    private static void ApplyLineType(ScottPlot.Plottables.Scatter scatter, CurveLineType type)
    {
        scatter.LinePattern = type switch
        {
            CurveLineType.Dashed => ScottPlot.LinePattern.Dashed,
            CurveLineType.Dotted => ScottPlot.LinePattern.Dotted,
            _                    => ScottPlot.LinePattern.Solid,
        };
        scatter.ConnectStyle = type == CurveLineType.Step
            ? ScottPlot.ConnectStyle.StepHorizontal
            : ScottPlot.ConnectStyle.Straight;
    }

    // ============================================================
    // Rendering
    // ============================================================

    private void Render()
    {
        var snap = _vm.GetSnapshot();
        var plot = _plot.Plot;

        // Reset: remove plottables, the axes we added last time, and any rules.
        plot.Clear();
        foreach (var a in _addedAxes) plot.Axes.Remove(a);
        _addedAxes.Clear();
        plot.Axes.Rules.Clear();
        _bandRect = null;
        _pickMarker = null;
        _pickLabel = null;
        _plotted.Clear();

        // ScottPlot keeps four default axes alive for the lifetime of the plot. Hide
        // them all up front, then show only the one we use as the independent axis —
        // otherwise the axis configured in the previous orientation (e.g. Bottom
        // holding "Время") keeps rendering after a switch to vertical.
        plot.Axes.Bottom.IsVisible = false;
        plot.Axes.Left.IsVisible   = false;
        plot.Axes.Top.IsVisible    = false;
        plot.Axes.Right.IsVisible  = false;

        // The independent (time/depth) axis: bottom when horizontal, left when vertical.
        var indepAxis = snap.Vertical ? (ScottPlot.IAxis)plot.Axes.Left : plot.Axes.Bottom;
        indepAxis.IsVisible = true;
        indepAxis.Label.Text = snap.IsTime ? "Время" : "Глубина забоя, м";
        indepAxis.Label.ForeColor = ScottPlot.Colors.Black;
        indepAxis.TickLabelStyle.ForeColor = ScottPlot.Colors.Black;
        indepAxis.TickGenerator = MakeIndepTickGenerator(snap);

        // Value scales always get their own dedicated axes (never a default one), so
        // ScottPlot stacks them in separate tiers and their labels cannot overlap.
        // Vertical → Top (above the plot); horizontal → Left.
        ScottPlot.IAxis NewValueAxis()
        {
            var a = snap.Vertical ? (ScottPlot.IAxis)plot.Axes.AddTopAxis() : plot.Axes.AddLeftAxis();
            _addedAxes.Add(a);
            return a;
        }

        var valueAxes = new System.Collections.Generic.List<ScottPlot.IAxis>();

        // Shared mode: one axis for every curve.
        ScottPlot.IAxis? sharedValueAxis = null;
        if (!snap.SeparateScales)
        {
            sharedValueAxis = NewValueAxis();
            sharedValueAxis.Label.Text = "Значение";
            valueAxes.Add(sharedValueAxis);
        }

        // With nothing selected, still show one value axis so the panel looks like a chart.
        if (snap.Series.Count == 0 && sharedValueAxis is null)
        {
            var placeholder = NewValueAxis();
            placeholder.Label.Text = "Значение";
        }

        foreach (var s in snap.Series)
        {
            var colour = ToScott(s.Color);

            ScottPlot.IAxis valueAxis;
            if (snap.SeparateScales)
            {
                valueAxis = NewValueAxis();
                valueAxis.Label.Text = s.Name;
                valueAxis.Label.ForeColor = colour;
                valueAxis.TickLabelStyle.ForeColor = colour;
                valueAxes.Add(valueAxis);
            }
            else
            {
                valueAxis = sharedValueAxis!;
            }

            // Horizontal: X = independent, Y = value.  Vertical: swap.
            double[] xs = snap.Vertical ? s.Values : s.Independent;
            double[] ys = snap.Vertical ? s.Independent : s.Values;

            var scatter = plot.Add.Scatter(xs, ys);
            scatter.Color = colour;
            scatter.LineWidth = (float)s.LineWidth;
            scatter.MarkerSize = 0;
            scatter.LegendText = s.Name;
            ApplyLineType(scatter, s.LineType);

            ScottPlot.IXAxis xAxisUsed;
            ScottPlot.IYAxis yAxisUsed;
            if (snap.Vertical)
            {
                yAxisUsed = plot.Axes.Left;                  // independent
                xAxisUsed = (ScottPlot.IXAxis)valueAxis;     // value
            }
            else
            {
                xAxisUsed = plot.Axes.Bottom;                // independent
                yAxisUsed = (ScottPlot.IYAxis)valueAxis;     // value
            }
            scatter.Axes.XAxis = xAxisUsed;
            scatter.Axes.YAxis = yAxisUsed;
            _plotted.Add(new PlottedCurve(scatter, xAxisUsed, yAxisUsed, s));
        }

        plot.Axes.AutoScale();

        // In vertical mode read top-down like a borehole log.
        if (snap.Vertical) plot.Axes.InvertY();

        // Lock each value axis to its autoscaled range so the mouse wheel zooms only
        // the independent axis; otherwise the curves slide out of their value window.
        foreach (var axis in valueAxes)
        {
            if (snap.Vertical)
            {
                var xAxis = (ScottPlot.IXAxis)axis;
                var lim = plot.Axes.GetLimits(xAxis, plot.Axes.Left);
                plot.Axes.Rules.Add(new ScottPlot.AxisRules.LockedHorizontal(xAxis, lim.Left, lim.Right));
            }
            else
            {
                var yAxis = (ScottPlot.IYAxis)axis;
                var lim = plot.Axes.GetLimits(plot.Axes.Bottom, yAxis);
                plot.Axes.Rules.Add(new ScottPlot.AxisRules.LockedVertical(yAxis, lim.Bottom, lim.Top));
            }
        }

        // The built-in legend is hidden: the window shows a clickable WPF legend
        // strip above the plot instead, where colour / line type / thickness are edited.
        plot.HideLegend();
        _plot.Refresh();
    }

    // ============================================================
    // Mouse: Shift-drag = band statistics; double-click = point value
    // ============================================================

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Shift)
        {
            _dragging = true;
            _dragStartIndep = IndepAt(e);
            _plot.CaptureMouse();
            e.Handled = true;   // suppress ScottPlot's own pan/zoom for this drag
        }
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging)
        {
            // Track where the pointer is along the time/depth axis.
            var at = IndepAt(e);
            _vm.CursorText = double.IsFinite(at)
                ? (_vm.XAxis == ChartXAxisMode.Time
                    ? FormatOaDateLong(at)
                    : $"{at.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)} м")
                : null;
            return;
        }

        DrawBand(_dragStartIndep, IndepAt(e));
        e.Handled = true;
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        _plot.ReleaseMouseCapture();

        var lo = _dragStartIndep;
        var hi = IndepAt(e);
        if (lo > hi) (lo, hi) = (hi, lo);

        DrawBand(lo, hi);
        _vm.SetSelection(_vm.ComputeBandStats(lo, hi), FormatRange(lo, hi));
        e.Handled = true;
    }

    // ============================================================
    // Double-click → nearest vertex value (ScottPlot's Data.GetNearest)
    // ============================================================

    private void OnDoubleClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;   // suppress ScottPlot's default double-click action
        var mouse = MousePixel(e);
        var plot = _plot.Plot;

        double bestSq = double.MaxValue;
        PlottedCurve? best = null;
        int bestIndex = -1;

        foreach (var c in _plotted)
        {
            // Mouse position in this curve's own axis space, then ask ScottPlot for
            // the nearest actual data point (vertex) via the standard GetNearest.
            var mouseCoord = plot.GetCoordinates(mouse, c.X, c.Y);
            var nearest = c.Scatter.Data.GetNearest(mouseCoord, plot.LastRender);
            if (!nearest.IsReal) continue;

            // Disambiguate between curves by real pixel distance to the vertex.
            var vpx = plot.GetPixel(new Coordinates(nearest.X, nearest.Y), c.X, c.Y);
            var ddx = vpx.X - mouse.X;
            var ddy = vpx.Y - mouse.Y;
            var sq = ddx * ddx + ddy * ddy;
            if (sq < bestSq) { bestSq = sq; best = c; bestIndex = nearest.Index; }
        }

        if (best is null || bestSq > 20 * 20)
        {
            ClearPick();    // double-clicking empty space dismisses the label
            return;
        }
        ShowPoint(best, bestIndex, _vm.Orientation == ChartOrientation.Vertical);
    }

    private void ShowPoint(PlottedCurve c, int i, bool vertical)
    {
        var plot = _plot.Plot;
        ClearPick(refresh: false);

        var value = c.Data.Values[i];
        var indep = c.Data.Independent[i];
        var xv = vertical ? value : indep;
        var yv = vertical ? indep : value;
        var colour = ToScott(c.Data.Color);

        var marker = plot.Add.Marker(xv, yv);
        marker.Color = colour;
        marker.Size = 11;
        marker.Axes.XAxis = c.X;
        marker.Axes.YAxis = c.Y;
        _pickMarker = marker;

        // Value label pinned next to the point itself.
        var indepStr = _vm.XAxis == ChartXAxisMode.Time
            ? DateTime.FromOADate(indep).ToString("dd.MM.yyyy HH:mm:ss")
            : $"{indep.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)} м";
        var valStr = value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

        var label = plot.Add.Text($"{c.Data.Name}\n{valStr}\n{indepStr}", xv, yv);
        label.Axes.XAxis = c.X;
        label.Axes.YAxis = c.Y;
        label.LabelFontSize = 12;
        label.LabelBold = true;
        label.LabelFontColor = ScottPlot.Colors.Black;
        label.LabelBackgroundColor = ScottPlot.Colors.White.WithAlpha(230);
        label.LabelBorderColor = colour;
        label.LabelBorderWidth = 1;
        label.LabelPadding = 4;

        // Open the label toward the middle of the plot so it is never clipped by an
        // edge: a point in the upper half gets a label hanging below it, a point on
        // the right gets one extending left, and so on. LabelAlignment names the
        // corner of the label pinned to the point.
        var px = plot.GetPixel(new Coordinates(xv, yv), c.X, c.Y);
        var rect = plot.LastRender.DataRect;
        bool upperHalf = px.Y < (rect.Top + rect.Bottom) / 2f;   // pixel Y grows downward
        bool rightHalf = px.X > (rect.Left + rect.Right) / 2f;

        label.LabelAlignment = (upperHalf, rightHalf) switch
        {
            (true,  false) => ScottPlot.Alignment.UpperLeft,   // extends down-right
            (true,  true)  => ScottPlot.Alignment.UpperRight,  // extends down-left
            (false, false) => ScottPlot.Alignment.LowerLeft,   // extends up-right
            (false, true)  => ScottPlot.Alignment.LowerRight,  // extends up-left
        };
        label.OffsetX = rightHalf ? -8 : 8;
        label.OffsetY = upperHalf ? 8 : -8;
        _pickLabel = label;

        _plot.Refresh();
    }

    /// <summary>Remove the clicked-point marker and its value label.</summary>
    private void ClearPick(bool refresh = true)
    {
        var plot = _plot.Plot;
        if (_pickMarker is not null) { plot.Remove(_pickMarker); _pickMarker = null; }
        if (_pickLabel is not null)  { plot.Remove(_pickLabel);  _pickLabel = null; }
        if (refresh) _plot.Refresh();
    }

    private Pixel MousePixel(MouseEventArgs e)
    {
        var p = e.GetPosition(_plot);
        double scale = VisualTreeHelper.GetDpi(_plot).DpiScaleX;
        return new Pixel((float)(p.X * scale), (float)(p.Y * scale));
    }

    /// <summary>Independent-axis coordinate under the mouse (X when horizontal, Y when vertical).</summary>
    private double IndepAt(MouseEventArgs e)
    {
        var coord = _plot.Plot.GetCoordinates(MousePixel(e));
        return _vm.Orientation == ChartOrientation.Vertical ? coord.Y : coord.X;
    }

    private void DrawBand(double a, double b)
    {
        var plot = _plot.Plot;
        if (_bandRect is not null) { plot.Remove(_bandRect); _bandRect = null; }

        var lo = Math.Min(a, b);
        var hi = Math.Max(a, b);
        var lim = plot.Axes.GetLimits();

        // Band spans the whole plot across the value direction.
        var rect = _vm.Orientation == ChartOrientation.Vertical
            ? plot.Add.Rectangle(lim.Left, lim.Right, lo, hi)
            : plot.Add.Rectangle(lo, hi, lim.Bottom, lim.Top);

        rect.FillStyle.Color = new ScottPlot.Color((byte)70, (byte)130, (byte)180).WithAlpha((byte)45);
        rect.LineStyle.Color = new ScottPlot.Color((byte)70, (byte)130, (byte)180);
        _bandRect = rect;
        _plot.Refresh();
    }

    private void OnSelectionCleared()
    {
        if (_bandRect is not null)
        {
            _plot.Plot.Remove(_bandRect);
            _bandRect = null;
            _plot.Refresh();
        }
    }

    private string FormatRange(double lo, double hi)
    {
        if (_vm.XAxis == ChartXAxisMode.Time)
        {
            var a = DateTime.FromOADate(lo);
            var b = DateTime.FromOADate(hi);
            return $"{a:dd.MM HH:mm:ss} – {b:dd.MM HH:mm:ss}";
        }
        return $"глубина {lo:0.##} – {hi:0.##} м";
    }
}
