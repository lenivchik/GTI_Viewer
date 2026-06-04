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

    private PlotModel _chartModel = new();
    public PlotModel ChartModel
    {
        get => _chartModel;
        private set => SetProperty(ref _chartModel, value);
    }

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

    private void Rebuild()
    {
        var model = new PlotModel { PlotAreaBorderColor = OxyColors.LightGray };
        model.Legends.Add(new OxyPlot.Legends.Legend
        {
            LegendPosition = OxyPlot.Legends.LegendPosition.RightTop,
            LegendPlacement = OxyPlot.Legends.LegendPlacement.Outside,
            LegendOrientation = OxyPlot.Legends.LegendOrientation.Vertical,
        });

        var isTime  = XAxis == ChartXAxisMode.Time;
        var vertical = Orientation == ChartOrientation.Vertical;

        // === Axes ===
        // Independent axis (time or depth) sits on Bottom for horizontal,
        // Left for vertical. Depth axis is inverted in vertical mode so the
        // chart reads top-down like a borehole log.
        var indepPosition = vertical ? AxisPosition.Left : AxisPosition.Bottom;
        Axis indepAxis = isTime
            ? new DateTimeAxis
              {
                  Position = indepPosition,
                  StringFormat = "dd.MM HH:mm",
                  Title = "Время",
                  IntervalLength = 80,
                  StartPosition = vertical ? 1 : 0,
                  EndPosition   = vertical ? 0 : 1,
              }
            : new LinearAxis
              {
                  Position = indepPosition,
                  Title = "Глубина забоя, м",
                  StartPosition = vertical ? 1 : 0,
                  EndPosition   = vertical ? 0 : 1,
              };
        model.Axes.Add(indepAxis);

        // Dependent (value) axis on the other side.
        model.Axes.Add(new LinearAxis
        {
            Position = vertical ? AxisPosition.Bottom : AxisPosition.Left,
            Title = "Значение",
        });

        var table = _data?.Table;
        var selected = Parameters.Where(p => p.IsSelected).ToList();

        if (table is not null && selected.Count > 0)
        {
            var xCol = isTime ? "REC_TIME" : "BOTTOM_DEPTH";
            if (table.Columns.Contains(xCol))
            {
                // Source rows are newest-first; reverse so the line draws chronologically.
                var rowsChronological = new List<DataRow>(table.Rows.Count);
                for (int i = table.Rows.Count - 1; i >= 0; i--) rowsChronological.Add(table.Rows[i]);

                foreach (var p in selected)
                {
                    if (!table.Columns.Contains(p.Name)) continue;
                    var series = new LineSeries
                    {
                        Title = p.DisplayName,
                        StrokeThickness = 1.5,
                        MarkerType = MarkerType.None,
                        Color = ColorPalette.For(p.Name),
                    };
                    foreach (var row in rowsChronological)
                    {
                        var xRaw = row[xCol];
                        var yRaw = row[p.Name];
                        if (xRaw is DBNull || yRaw is DBNull) continue;

                        double xVal;
                        if (isTime)
                        {
                            if (xRaw is not DateTime dtv) continue;
                            xVal = DateTimeAxis.ToDouble(dtv);
                        }
                        else if (!TryToDouble(xRaw, out xVal)) continue;

                        if (!TryToDouble(yRaw, out var yVal)) continue;

                        // In vertical mode the independent variable goes on the Y axis,
                        // so we swap the components of the DataPoint.
                        series.Points.Add(vertical
                            ? new DataPoint(yVal, xVal)
                            : new DataPoint(xVal, yVal));
                    }
                    if (series.Points.Count > 0)
                        model.Series.Add(series);
                }
            }
        }

        ChartModel = model;
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
