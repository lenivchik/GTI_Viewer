using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Data;
using System.Globalization;
using System.Linq;
using FirebirdViewer.Models;
using OxyPlot;
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
    }

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
    private const string IndepKey = "indep";

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
        indepAxis.Key = IndepKey;
        indepAxis.Position = indepPosition;
        indepAxis.StartPosition = vertical ? 1 : 0;
        indepAxis.EndPosition   = vertical ? 0 : 1;
        ApplyGrid(indepAxis);
        newAxes.Add(indepAxis);

        var table = _data?.Table;
        var selected = Parameters.Where(p => p.IsSelected).ToList();

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

            if (SeparateScales)
            {
                valueKey = "v_" + p.Name;
                var axis = new LinearAxis
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
                if (tier == 0) ApplyGrid(axis);
                newAxes.Add(axis);
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
                XAxisKey = vertical ? valueKey : IndepKey,
                YAxisKey = vertical ? IndepKey : valueKey,
            };
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

                // Horizontal: X = independent, Y = value.
                // Vertical:   X = value,       Y = independent.
                series.Points.Add(vertical
                    ? new DataPoint(value, indep)
                    : new DataPoint(indep, value));
            }
            if (series.Points.Count > 0)
                newSeries.Add(series);
        }

        SwapModelContents(newAxes, newSeries);
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
        }
        ChartModel.InvalidatePlot(true);
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
