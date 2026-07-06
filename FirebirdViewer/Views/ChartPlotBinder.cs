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

    // Axes we added via AddLeftAxis/AddBottomAxis. plot.Clear() removes plottables
    // but not axes, so we must remove these ourselves before each re-render.
    private readonly System.Collections.Generic.List<ScottPlot.IAxis> _addedAxes = new();

    public ChartPlotBinder(WpfPlot plot, ChartPanelViewModel vm)
    {
        _plot = plot;
        _vm = vm;

        _vm.RenderRequested += Render;
        _vm.SelectionCleared += OnSelectionCleared;

        _plot.PreviewMouseLeftButtonDown += OnMouseDown;
        _plot.PreviewMouseMove += OnMouseMove;
        _plot.PreviewMouseLeftButtonUp += OnMouseUp;

        Render();
    }

    public void Dispose()
    {
        _vm.RenderRequested -= Render;
        _vm.SelectionCleared -= OnSelectionCleared;
        _plot.PreviewMouseLeftButtonDown -= OnMouseDown;
        _plot.PreviewMouseMove -= OnMouseMove;
        _plot.PreviewMouseLeftButtonUp -= OnMouseUp;
    }

    private static ScottPlot.Color ToScott(ChartColor c) => new(c.R, c.G, c.B);

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

            if (snap.Vertical)
            {
                scatter.Axes.YAxis = plot.Axes.Left;   // independent
                scatter.Axes.XAxis = valueAxis;        // value
            }
            else
            {
                scatter.Axes.XAxis = plot.Axes.Bottom; // independent
                scatter.Axes.YAxis = valueAxis;        // value
            }

            index++;
        }

        plot.Axes.AutoScale();

        // In vertical mode read top-down like a borehole log.
        if (snap.Vertical) plot.Axes.InvertY();

        // Lock the value axes so the mouse wheel zooms only the independent axis;
        // otherwise the curves slide out of their own value window while zooming.
        foreach (var axis in valueAxes)
        {
            if (axis is ScottPlot.IYAxis yAxis)
                plot.Axes.Rules.Add(new ScottPlot.AxisRules.LockedVertical(yAxis));
            else if (axis is ScottPlot.IXAxis xAxis)
                plot.Axes.Rules.Add(new ScottPlot.AxisRules.LockedHorizontal(xAxis));
        }

        plot.ShowLegend();
        _plot.Refresh();
    }

    // ============================================================
    // Shift + drag band selection → per-curve statistics
    // ============================================================

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Shift) return;

        _dragging = true;
        _dragStartIndep = IndepAt(e);
        _plot.CaptureMouse();
        e.Handled = true;   // suppress ScottPlot's own pan/zoom for this drag
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

        var stats = _vm.ComputeBandStats(lo, hi);
        _vm.SetSelection(stats, FormatRange(lo, hi));
        e.Handled = true;
    }

    /// <summary>Independent-axis coordinate under the mouse (X when horizontal, Y when vertical).</summary>
    private double IndepAt(MouseEventArgs e)
    {
        var p = e.GetPosition(_plot);
        double scale = VisualTreeHelper.GetDpi(_plot).DpiScaleX;
        var coord = _plot.Plot.GetCoordinates(new Pixel((float)(p.X * scale), (float)(p.Y * scale)));
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
