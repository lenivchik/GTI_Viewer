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
///   • plot.Axes.Bottom / .Left / .AddLeftAxis() / .AddBottomAxis()
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

    // Axes we added via AddLeftAxis/AddBottomAxis. plot.Clear() removes plottables
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
        _vm.PointInfoCleared += OnPointInfoCleared;

        _plot.PreviewMouseLeftButtonDown += OnMouseDown;
        _plot.PreviewMouseMove += OnMouseMove;
        _plot.PreviewMouseLeftButtonUp += OnMouseUp;
        _plot.PreviewMouseDoubleClick += OnDoubleClick;

        Render();
    }

    public void Dispose()
    {
        _vm.RenderRequested -= Render;
        _vm.SelectionCleared -= OnSelectionCleared;
        _vm.PointInfoCleared -= OnPointInfoCleared;
        _plot.PreviewMouseLeftButtonDown -= OnMouseDown;
        _plot.PreviewMouseMove -= OnMouseMove;
        _plot.PreviewMouseLeftButtonUp -= OnMouseUp;
        _plot.PreviewMouseDoubleClick -= OnDoubleClick;
    }

    private static ScottPlot.Color ToScott(ChartColor c) => new(c.R, c.G, c.B);

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
        _plotted.Clear();

        // The independent (time/depth) axis: bottom when horizontal, left when vertical.
        var indepAxis = snap.Vertical ? (ScottPlot.IAxis)plot.Axes.Left : plot.Axes.Bottom;
        indepAxis.Label.Text = snap.IsTime ? "Время" : "Глубина забоя, м";
        indepAxis.TickGenerator = snap.IsTime
            ? new ScottPlot.TickGenerators.DateTimeAutomatic()
            : new ScottPlot.TickGenerators.NumericAutomatic();

        // The default value axis (reused each render) — reset its colour in case a
        // previous separate-scale render tinted it.
        var defaultValueAxis = snap.Vertical ? (ScottPlot.IAxis)plot.Axes.Bottom : plot.Axes.Left;
        defaultValueAxis.Label.ForeColor = ScottPlot.Colors.Black;
        defaultValueAxis.TickLabelStyle.ForeColor = ScottPlot.Colors.Black;
        defaultValueAxis.Label.Text = snap.SeparateScales ? "" : "Значение";

        var valueAxes = new System.Collections.Generic.List<ScottPlot.IAxis>();

        int index = 0;
        foreach (var s in snap.Series)
        {
            var colour = ToScott(s.Color);

            // Value axis: shared → the default cross-axis; separate → one per curve.
            ScottPlot.IAxis valueAxis;
            if (!snap.SeparateScales || index == 0)
            {
                valueAxis = defaultValueAxis;
            }
            else
            {
                valueAxis = snap.Vertical ? plot.Axes.AddBottomAxis() : plot.Axes.AddLeftAxis();
                _addedAxes.Add(valueAxis);
            }
            if (snap.SeparateScales)
            {
                valueAxis.Label.Text = s.Name;
                valueAxis.Label.ForeColor = colour;
                valueAxis.TickLabelStyle.ForeColor = colour;
            }
            if (!valueAxes.Contains(valueAxis)) valueAxes.Add(valueAxis);

            // Horizontal: X = independent, Y = value.  Vertical: swap.
            double[] xs = snap.Vertical ? s.Values : s.Independent;
            double[] ys = snap.Vertical ? s.Independent : s.Values;

            var scatter = plot.Add.Scatter(xs, ys);
            scatter.Color = colour;
            scatter.LineWidth = 1.5f;
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

            index++;
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

        plot.ShowLegend();
        _plot.Refresh();

        _vm.ClearPointInfo();   // last click's value no longer applies to the new plot
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
        if (!_dragging) return;
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

        if (best is null || bestSq > 20 * 20) return;   // nothing close enough
        ShowPoint(best, bestIndex, _vm.Orientation == ChartOrientation.Vertical);
    }

    private void ShowPoint(PlottedCurve c, int i, bool vertical)
    {
        var plot = _plot.Plot;
        if (_pickMarker is not null) plot.Remove(_pickMarker);

        var value = c.Data.Values[i];
        var indep = c.Data.Independent[i];
        var xv = vertical ? value : indep;
        var yv = vertical ? indep : value;

        var marker = plot.Add.Marker(xv, yv);
        marker.Color = ToScott(c.Data.Color);
        marker.Size = 11;
        marker.Axes.XAxis = c.X;
        marker.Axes.YAxis = c.Y;
        _pickMarker = marker;
        _plot.Refresh();

        var indepStr = _vm.XAxis == ChartXAxisMode.Time
            ? DateTime.FromOADate(indep).ToString("dd.MM.yyyy HH:mm:ss")
            : $"{indep.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)} м";
        var valStr = value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        _vm.SetPointInfo($"{c.Data.Name}: {valStr}   ({indepStr})");
    }

    private void OnPointInfoCleared()
    {
        if (_pickMarker is null) return;
        _plot.Plot.Remove(_pickMarker);
        _pickMarker = null;
        _plot.Refresh();
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
