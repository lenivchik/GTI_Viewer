using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Windows.Input;
using FirebirdViewer.Commands;
using FirebirdViewer.Models;
using OxyPlot;
using OxyPlot.Annotations;
using OxyPlot.Axes;
using OxyPlot.Series;

namespace FirebirdViewer.ViewModels;

public enum ChartXAxisMode { Time, Depth }
public enum ChartOrientation { Horizontal, Vertical }

/// <summary>
/// One chart panel on the Графики tab: its own parameter list, X-axis,
/// orientation and rendered <see cref="OxyPlot.PlotModel"/>.
/// </summary>
public sealed class ChartPanelViewModel : ObservableObject
{
    public ChartPanelViewModel(int number)
    {
        Title = $"График {number}";
        Parameters = new ObservableCollection<ChartParameterRef>();

        // Stable PlotModel instance. We mutate Series/Axes in-place and call
        // InvalidatePlot(true). Replacing the model would race with the
        // PlotView attach/detach lifecycle when the ItemsPanel direction
        // flips (yielding OxyPlot's "Plot model is already in use" error).
        ChartModel = new PlotModel { PlotAreaBorderColor = OxyColors.LightGray };
        ChartModel.Legends.Add(new OxyPlot.Legends.Legend
        {
            LegendPosition = OxyPlot.Legends.LegendPosition.RightTop,
            LegendPlacement = OxyPlot.Legends.LegendPlacement.Outside,
            LegendOrientation = OxyPlot.Legends.LegendOrientation.Vertical,
        });

        SelectionStats = new ObservableCollection<CurveStat>();
        ClearSelectionCommand = new RelayCommand(_ => ClearSelection(), _ => HasSelectionStats);

        // Shift + drag selects a band along the independent axis and reports
        // per-curve statistics. Everything else keeps OxyPlot's default input.
        PlotController = new PlotController();
        PlotController.BindMouseDown(OxyMouseButton.Left, OxyModifierKeys.Shift,
            new DelegatePlotCommand<OxyMouseDownEventArgs>((view, controller, args) =>
                controller.AddMouseManipulator(
                    view,
                    new BandStatsManipulator(view, this, Orientation == ChartOrientation.Vertical),
                    args)));
    }

    /// <summary>Controller wired to the PlotView; adds the Shift-drag stats manipulator.</summary>
    public PlotController PlotController { get; }

    public ObservableCollection<ChartParameterRef> Parameters { get; }

    private string _title;
    public string Title { get => _title; set => SetProperty(ref _title, value); }

    private ChartXAxisMode _xAxis = ChartXAxisMode.Time;
    public ChartXAxisMode XAxis
    {
        get => _xAxis;
        set
        {
            if (SetProperty(ref _xAxis, value))
            {
                OnPropertyChanged(nameof(XAxisIsTime));
                OnPropertyChanged(nameof(XAxisIsDepth));
                Rebuild();
            }
        }
    }

    public bool XAxisIsTime
    {
        get => XAxis == ChartXAxisMode.Time;
        set { if (value) XAxis = ChartXAxisMode.Time; }
    }
    public bool XAxisIsDepth
    {
        get => XAxis == ChartXAxisMode.Depth;
        set { if (value) XAxis = ChartXAxisMode.Depth; }
    }

    private ChartOrientation _orientation = ChartOrientation.Horizontal;
    public ChartOrientation Orientation
    {
        get => _orientation;
        set
        {
            if (SetProperty(ref _orientation, value))
            {
                OnPropertyChanged(nameof(OrientationIsHorizontal));
                OnPropertyChanged(nameof(OrientationIsVertical));
                Rebuild();
            }
        }
    }

    public bool OrientationIsHorizontal
    {
        get => Orientation == ChartOrientation.Horizontal;
        set { if (value) Orientation = ChartOrientation.Horizontal; }
    }
    public bool OrientationIsVertical
    {
        get => Orientation == ChartOrientation.Vertical;
        set { if (value) Orientation = ChartOrientation.Vertical; }
    }

    /// <summary>
    /// When true, every curve gets its own auto-scaled value axis so a 0–10
    /// parameter and a 0–100 parameter both fill the plot. When false, all
    /// curves share a single value axis.
    /// </summary>
    private bool _separateScales = true;
    public bool SeparateScales
    {
        get => _separateScales;
        set { if (SetProperty(ref _separateScales, value)) Rebuild(); }
    }

    /// <summary>Stable plot model. Mutated in place by <see cref="Rebuild"/>; never replaced.</summary>
    public PlotModel ChartModel { get; }

    private DataView? _data;

    /// <summary>
    /// Called by <see cref="MainViewModel"/> whenever the underlying data set
    /// or the column list changes. Keeps Parameters in sync (preserving the
    /// IsSelected state for parameters that survive) and triggers a rebuild.
    /// </summary>
    public void SetData(DataView? data, IEnumerable<ColumnVisibility> columns)
    {
        _data = data;
        SyncParameters(columns);
        Rebuild();
    }

    private void SyncParameters(IEnumerable<ColumnVisibility> columns)
    {
        // Preserve the previously-selected names so the chart keeps its lines
        // across data reloads.
        var selectedNames = new HashSet<string>(
            Parameters.Where(p => p.IsSelected).Select(p => p.Name),
            StringComparer.OrdinalIgnoreCase);

        foreach (var p in Parameters) p.PropertyChanged -= OnParameterChanged;
        Parameters.Clear();

        foreach (var col in columns)
        {
            var p = new ChartParameterRef
            {
                Name        = col.Name,
                DisplayName = col.DisplayName,
                Description = col.Description,
                IsSelected  = selectedNames.Contains(col.Name),
            };
            p.PropertyChanged += OnParameterChanged;
            Parameters.Add(p);
        }
    }

    private void OnParameterChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChartParameterRef.IsSelected))
            Rebuild();
    }

    // Light grid colours shared by all axes.
    private static readonly OxyColor MajorGrid = OxyColor.FromAColor(50, OxyColors.Gray);
    private static readonly OxyColor MinorGrid = OxyColor.FromAColor(22, OxyColors.Gray);
    internal const string IndepAxisKey = "indep";

    // Deepest allowed zoom-in, as a fraction of an axis's data span. Zooming in
    // past ~10 000× makes OxyPlot's coordinate transforms lose precision and the
    // curves vanish, so we cap MinimumRange at span × this factor.
    private const double MinRangeFactor = 1e-4;

    private void Rebuild()
    {
        var isTime   = XAxis == ChartXAxisMode.Time;
        var vertical = Orientation == ChartOrientation.Vertical;

        // Build the new content into local lists first, then swap into the
        // stable PlotModel under its SyncRoot. This keeps the same model
        // instance attached to the PlotView and avoids OxyPlot's
        // "Plot model is already in use" race during orientation flips.
        var newAxes = new List<Axis>();
        var newSeries = new List<OxyPlot.Series.Series>();

        // === Independent axis (time or depth) ===
        // Horizontal: Bottom. Vertical: Left, reversed so the chart reads
        // top-down like a borehole log.
        var indepPosition = vertical ? AxisPosition.Left : AxisPosition.Bottom;
        Axis indepAxis = isTime
            ? new DateTimeAxis
              {
                  StringFormat = "dd.MM HH:mm",
                  Title = "Время",
                  IntervalLength = 80,
              }
            : new LinearAxis
              {
                  Title = "Глубина забоя, м",
              };
        indepAxis.Key = IndepAxisKey;
        indepAxis.Position = indepPosition;
        indepAxis.StartPosition = vertical ? 1 : 0;
        indepAxis.EndPosition   = vertical ? 0 : 1;
        ApplyGrid(indepAxis);
        newAxes.Add(indepAxis);

        var table = _data?.Table;
        var selected = Parameters.Where(p => p.IsSelected).ToList();

        // Track data extents so we can bound the zoom on each axis.
        double indepMin = double.PositiveInfinity, indepMax = double.NegativeInfinity;
        double sharedMin = double.PositiveInfinity, sharedMax = double.NegativeInfinity;

        // In vertical mode value scales sit on Top of the chart (borehole-log
        // convention) — easier to read than stacked at the bottom. In
        // horizontal mode they sit on the Left as usual.
        var valuePosition = vertical ? AxisPosition.Top : AxisPosition.Left;

        if (table is null || selected.Count == 0)
        {
            // Placeholder so an empty panel still looks like a chart.
            var empty = new LinearAxis { Position = valuePosition, Title = "Значение", Key = "value" };
            ApplyGrid(empty);
            newAxes.Add(empty);
            SwapModelContents(newAxes, newSeries);
            return;
        }

        var xCol = isTime ? "REC_TIME" : "BOTTOM_DEPTH";
        if (!table.Columns.Contains(xCol))
        {
            SwapModelContents(newAxes, newSeries);
            return;
        }

        // Source rows are newest-first; reverse so the line draws chronologically.
        var rowsChronological = new List<DataRow>(table.Rows.Count);
        for (int i = table.Rows.Count - 1; i >= 0; i--) rowsChronological.Add(table.Rows[i]);

        // === Value axes ===
        if (!SeparateScales)
        {
            var shared = new LinearAxis { Position = valuePosition, Title = "Значение", Key = "value" };
            ApplyGrid(shared);
            newAxes.Add(shared);
        }

        int tier = 0;
        foreach (var p in selected)
        {
            if (!table.Columns.Contains(p.Name)) continue;

            var colour = ColorPalette.For(p.Name);
            string valueKey;
            LinearAxis? perAxis = null;

            if (SeparateScales)
            {
                valueKey = "v_" + p.Name;
                perAxis = new LinearAxis
                {
                    Position = valuePosition,
                    Key = valueKey,
                    Title = p.DisplayName,
                    TitleColor = colour,
                    TextColor = colour,
                    AxislineColor = colour,
                    AxislineStyle = LineStyle.Solid,
                    TicklineColor = colour,
                    PositionTier = tier,
                };
                // Grid only on the first tier to avoid a clutter of mismatched lines.
                if (tier == 0) ApplyGrid(perAxis);
                newAxes.Add(perAxis);
                tier++;
            }
            else
            {
                valueKey = "value";
            }

            var series = new LineSeries
            {
                Title = p.DisplayName,
                StrokeThickness = 1.5,
                MarkerType = MarkerType.None,
                Color = colour,
                XAxisKey = vertical ? valueKey : IndepAxisKey,
                YAxisKey = vertical ? IndepAxisKey : valueKey,
            };
            double vMin = double.PositiveInfinity, vMax = double.NegativeInfinity;
            foreach (var row in rowsChronological)
            {
                var xRaw = row[xCol];
                var yRaw = row[p.Name];
                if (xRaw is DBNull || yRaw is DBNull) continue;

                double indep;
                if (isTime)
                {
                    if (xRaw is not DateTime dtv) continue;
                    indep = DateTimeAxis.ToDouble(dtv);
                }
                else if (!TryToDouble(xRaw, out indep)) continue;

                if (!TryToDouble(yRaw, out var value)) continue;

                if (indep < indepMin) indepMin = indep;
                if (indep > indepMax) indepMax = indep;
                if (value < vMin) vMin = value;
                if (value > vMax) vMax = value;

                // Horizontal: X = independent, Y = value.
                // Vertical:   X = value,       Y = independent.
                series.Points.Add(vertical
                    ? new DataPoint(value, indep)
                    : new DataPoint(indep, value));
            }
            if (series.Points.Count > 0)
            {
                newSeries.Add(series);
                if (perAxis is not null) LimitZoom(perAxis, vMin, vMax);
                if (vMin < sharedMin) sharedMin = vMin;
                if (vMax > sharedMax) sharedMax = vMax;
            }
        }

        // Bound zoom on the independent axis and (in shared mode) the value axis.
        LimitZoom(indepAxis, indepMin, indepMax);
        if (!SeparateScales)
        {
            var shared = newAxes.OfType<LinearAxis>().FirstOrDefault(a => a.Key == "value");
            if (shared is not null) LimitZoom(shared, sharedMin, sharedMax);
        }

        SwapModelContents(newAxes, newSeries);
    }

    /// <summary>Cap how far an axis can be zoomed in, based on its data span.</summary>
    private static void LimitZoom(Axis axis, double lo, double hi)
    {
        if (double.IsFinite(lo) && double.IsFinite(hi) && hi > lo)
            axis.MinimumRange = (hi - lo) * MinRangeFactor;
    }

    /// <summary>Replace the model's axes/series under its SyncRoot, then invalidate.</summary>
    private void SwapModelContents(List<Axis> newAxes, List<OxyPlot.Series.Series> newSeries)
    {
        lock (ChartModel.SyncRoot)
        {
            ChartModel.Axes.Clear();
            foreach (var a in newAxes) ChartModel.Axes.Add(a);
            ChartModel.Series.Clear();
            foreach (var s in newSeries) ChartModel.Series.Add(s);
            // Any prior band-selection rectangle refers to axes that were just
            // replaced, so drop it along with its statistics.
            ChartModel.Annotations.Clear();
        }
        ResetSelectionState();
        ChartModel.InvalidatePlot(true);
    }

    // ============================================================
    // Band selection (Shift + drag) → per-curve statistics
    // ============================================================

    public ObservableCollection<CurveStat> SelectionStats { get; }
    public bool HasSelectionStats => SelectionStats.Count > 0;

    private string? _selectionRangeText;
    public string? SelectionRangeText { get => _selectionRangeText; private set => SetProperty(ref _selectionRangeText, value); }

    public ICommand ClearSelectionCommand { get; }

    private RectangleAnnotation? _selectionAnnotation;

    /// <summary>Clear the stats list and range text without touching the model.</summary>
    private void ResetSelectionState()
    {
        _selectionAnnotation = null;
        if (SelectionStats.Count > 0) SelectionStats.Clear();
        SelectionRangeText = null;
        OnPropertyChanged(nameof(HasSelectionStats));
    }

    /// <summary>Remove the selection rectangle and its statistics.</summary>
    public void ClearSelection()
    {
        lock (ChartModel.SyncRoot)
        {
            if (_selectionAnnotation is not null)
                ChartModel.Annotations.Remove(_selectionAnnotation);
        }
        ResetSelectionState();
        ChartModel.InvalidatePlot(false);
    }

    /// <summary>Called by the manipulator when the user finishes a Shift-drag.</summary>
    internal void OnBandSelected(RectangleAnnotation annotation, IReadOnlyList<CurveStat> stats, string rangeText)
    {
        _selectionAnnotation = annotation;
        SelectionStats.Clear();
        foreach (var s in stats) SelectionStats.Add(s);
        SelectionRangeText = stats.Count > 0
            ? $"Участок: {rangeText}"
            : $"Участок: {rangeText} — нет точек";
        OnPropertyChanged(nameof(HasSelectionStats));
        ChartModel.InvalidatePlot(false);
    }

    private static void ApplyGrid(Axis axis)
    {
        axis.MajorGridlineStyle = LineStyle.Solid;
        axis.MajorGridlineColor = MajorGrid;
        axis.MinorGridlineStyle = LineStyle.Dot;
        axis.MinorGridlineColor = MinorGrid;
    }

    private static bool TryToDouble(object value, out double result)
    {
        switch (value)
        {
            case double d:  result = d;          return true;
            case float f:   result = f;          return true;
            case decimal m: result = (double)m;  return true;
            case int i:     result = i;          return true;
            case long l:    result = l;          return true;
            case short s:   result = s;          return true;
            case byte b:    result = b;          return true;
        }
        var s2 = value?.ToString();
        return double.TryParse(s2, NumberStyles.Float, CultureInfo.InvariantCulture, out result)
            || double.TryParse(s2, NumberStyles.Float, CultureInfo.CurrentCulture, out result);
    }
}

public sealed class ChartParameterRef : ObservableObject
{
    public string  Name        { get; init; } = "";
    public string  DisplayName { get; init; } = "";
    public string? Description { get; init; }

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}

/// <summary>Min / average / max of one curve over a selected band.</summary>
public sealed class CurveStat
{
    public string Name  { get; init; } = "";
    public System.Windows.Media.Brush Swatch { get; init; } = System.Windows.Media.Brushes.Gray;
    public string MinText { get; init; } = "";
    public string AvgText { get; init; } = "";
    public string MaxText { get; init; } = "";
    public int    Count   { get; init; }

    public static CurveStat Create(string name, OxyColor colour, double min, double avg, double max, int count)
    {
        var brush = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromArgb(colour.A, colour.R, colour.G, colour.B));
        brush.Freeze();
        return new CurveStat
        {
            Name    = name,
            Swatch  = brush,
            MinText = min.ToString("0.##", CultureInfo.InvariantCulture),
            AvgText = avg.ToString("0.##", CultureInfo.InvariantCulture),
            MaxText = max.ToString("0.##", CultureInfo.InvariantCulture),
            Count   = count,
        };
    }
}

/// <summary>
/// Shift-drag manipulator: paints a band across the independent axis and, on
/// release, reports min/avg/max for every curve whose points fall inside it.
/// </summary>
public sealed class BandStatsManipulator : MouseManipulator
{
    private readonly ChartPanelViewModel _vm;
    private readonly bool _vertical;
    private Axis? _indep;
    private RectangleAnnotation? _rect;
    private double _start;

    public BandStatsManipulator(IPlotView view, ChartPanelViewModel vm, bool vertical) : base(view)
    {
        _vm = vm;
        _vertical = vertical;
    }

    public override void Started(OxyMouseEventArgs e)
    {
        base.Started(e);
        var model = PlotView.ActualModel;
        if (model is null) return;
        _indep = model.Axes.FirstOrDefault(a => a.Key == ChartPanelViewModel.IndepAxisKey);
        if (_indep is null) return;

        _vm.ClearSelection();               // drop any previous band
        _start = Indep(e);

        _rect = new RectangleAnnotation
        {
            Fill = OxyColor.FromAColor(45, OxyColors.SteelBlue),
            Stroke = OxyColors.SteelBlue,
            StrokeThickness = 1,
            Layer = AnnotationLayer.BelowSeries,
        };
        if (_vertical) _rect.YAxisKey = _indep.Key; else _rect.XAxisKey = _indep.Key;
        UpdateRect(e);

        lock (model.SyncRoot) model.Annotations.Add(_rect);
        PlotView.InvalidatePlot(false);
        e.Handled = true;
    }

    public override void Delta(OxyMouseEventArgs e)
    {
        base.Delta(e);
        if (_rect is null) return;
        UpdateRect(e);
        PlotView.InvalidatePlot(false);
        e.Handled = true;
    }

    public override void Completed(OxyMouseEventArgs e)
    {
        base.Completed(e);
        var model = PlotView.ActualModel;
        if (_rect is null || _indep is null || model is null) return;

        UpdateRect(e);
        var lo = Math.Min(_start, Indep(e));
        var hi = Math.Max(_start, Indep(e));

        var stats = new List<CurveStat>();
        foreach (var s in model.Series.OfType<LineSeries>())
        {
            double min = double.PositiveInfinity, max = double.NegativeInfinity, sum = 0;
            int n = 0;
            foreach (var pt in s.Points)
            {
                var indep = _vertical ? pt.Y : pt.X;
                if (indep < lo || indep > hi) continue;
                var val = _vertical ? pt.X : pt.Y;
                if (double.IsNaN(val)) continue;
                if (val < min) min = val;
                if (val > max) max = val;
                sum += val;
                n++;
            }
            if (n > 0) stats.Add(CurveStat.Create(s.Title, s.ActualColor, min, sum / n, max, n));
        }

        _vm.OnBandSelected(_rect, stats, FormatRange(lo, hi));
        e.Handled = true;
    }

    private double Indep(OxyMouseEventArgs e)
        => _indep!.InverseTransform(_vertical ? e.Position.Y : e.Position.X);

    private void UpdateRect(OxyMouseEventArgs e)
    {
        if (_rect is null) return;
        var lo = Math.Min(_start, Indep(e));
        var hi = Math.Max(_start, Indep(e));
        if (_vertical) { _rect.MinimumY = lo; _rect.MaximumY = hi; }
        else           { _rect.MinimumX = lo; _rect.MaximumX = hi; }
    }

    private string FormatRange(double lo, double hi)
    {
        if (_indep is DateTimeAxis)
        {
            var a = DateTimeAxis.ToDateTime(lo);
            var b = DateTimeAxis.ToDateTime(hi);
            return $"{a:dd.MM HH:mm:ss} – {b:dd.MM HH:mm:ss}";
        }
        return $"глубина {lo.ToString("0.##", CultureInfo.InvariantCulture)} – {hi.ToString("0.##", CultureInfo.InvariantCulture)} м";
    }
}
