using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Windows.Input;
using FirebirdViewer.Commands;

namespace FirebirdViewer.ViewModels;

public enum ChartXAxisMode { Time, Depth }
public enum ChartOrientation { Horizontal, Vertical }

/// <summary>
/// One chart panel on the Графики tab. Holds all state and data but knows
/// nothing about the charting library — the View's ChartPlotBinder renders it.
/// </summary>
public sealed class ChartPanelViewModel : ObservableObject
{
    public ChartPanelViewModel(int number)
    {
        Title = $"График {number}";
        Parameters = new ObservableCollection<ChartParameterRef>();
        SelectionStats = new ObservableCollection<CurveStat>();
        ClearSelectionCommand = new RelayCommand(_ => ClearSelection(), _ => HasSelectionStats);
    }

    // ---- Raised for the renderer ---------------------------------------------

    /// <summary>Rebuild the whole plot (data, axes, orientation, scales changed).</summary>
    public event Action? RenderRequested;
    /// <summary>Remove the band-selection rectangle the renderer drew.</summary>
    public event Action? SelectionCleared;

    private void RequestRender()
    {
        BuildSeries();
        RenderRequested?.Invoke();
    }

    // ---- Parameters -----------------------------------------------------------

    public ObservableCollection<ChartParameterRef> Parameters { get; }

    private string _title;
    public string Title { get => _title; set => SetProperty(ref _title, value); }

    // ---- X axis: time or depth ------------------------------------------------

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
                RequestRender();
            }
        }
    }
    public bool XAxisIsTime  { get => XAxis == ChartXAxisMode.Time;  set { if (value) XAxis = ChartXAxisMode.Time; } }
    public bool XAxisIsDepth { get => XAxis == ChartXAxisMode.Depth; set { if (value) XAxis = ChartXAxisMode.Depth; } }

    // ---- Orientation ----------------------------------------------------------

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
                RenderRequested?.Invoke();   // layout only — data unchanged
            }
        }
    }
    public bool OrientationIsHorizontal { get => Orientation == ChartOrientation.Horizontal; set { if (value) Orientation = ChartOrientation.Horizontal; } }
    public bool OrientationIsVertical   { get => Orientation == ChartOrientation.Vertical;   set { if (value) Orientation = ChartOrientation.Vertical; } }

    // ---- Separate value scale per curve --------------------------------------

    private bool _separateScales = true;
    public bool SeparateScales
    {
        get => _separateScales;
        set { if (SetProperty(ref _separateScales, value)) RenderRequested?.Invoke(); }
    }

    // ---- Data -----------------------------------------------------------------

    private DataView? _data;
    private readonly List<ChartSeriesData> _series = new();

    /// <summary>Called by <see cref="MainViewModel"/> whenever the data or column list changes.</summary>
    public void SetData(DataView? data, IEnumerable<ColumnVisibility> columns)
    {
        _data = data;
        SyncParameters(columns);
        RequestRender();
    }

    private void SyncParameters(IEnumerable<ColumnVisibility> columns)
    {
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
            RequestRender();
    }

    /// <summary>Build the numeric series for the currently selected parameters.</summary>
    private void BuildSeries()
    {
        _series.Clear();
        var table = _data?.Table;
        if (table is null) return;

        var isTime = XAxis == ChartXAxisMode.Time;
        var xCol = isTime ? "REC_TIME" : "BOTTOM_DEPTH";
        if (!table.Columns.Contains(xCol)) return;

        // Rows arrive newest-first; walk them in reverse for chronological order.
        foreach (var p in Parameters.Where(p => p.IsSelected))
        {
            if (!table.Columns.Contains(p.Name)) continue;

            var indep = new List<double>(table.Rows.Count);
            var vals  = new List<double>(table.Rows.Count);

            for (int i = table.Rows.Count - 1; i >= 0; i--)
            {
                var row = table.Rows[i];
                var xRaw = row[xCol];
                var yRaw = row[p.Name];
                if (xRaw is DBNull || yRaw is DBNull) continue;

                double x;
                if (isTime)
                {
                    if (xRaw is not DateTime dt) continue;
                    x = dt.ToOADate();   // ScottPlot's DateTime axis consumes OA dates
                }
                else if (!TryToDouble(xRaw, out x)) continue;

                if (!TryToDouble(yRaw, out var y)) continue;

                indep.Add(x);
                vals.Add(y);
            }

            if (vals.Count > 0)
                _series.Add(new ChartSeriesData(p.DisplayName, ColorPalette.For(p.Name), indep.ToArray(), vals.ToArray()));
        }
    }

    /// <summary>Immutable snapshot the renderer draws from.</summary>
    public ChartSnapshot GetSnapshot() => new(
        XAxis == ChartXAxisMode.Time,
        Orientation == ChartOrientation.Vertical,
        SeparateScales,
        _series.ToArray());

    // ---- Band-selection statistics -------------------------------------------

    public ObservableCollection<CurveStat> SelectionStats { get; }
    public bool HasSelectionStats => SelectionStats.Count > 0;

    private string? _selectionRangeText;
    public string? SelectionRangeText { get => _selectionRangeText; private set => SetProperty(ref _selectionRangeText, value); }

    public ICommand ClearSelectionCommand { get; }

    /// <summary>Compute min/avg/max for each curve over [lo, hi] on the independent axis.</summary>
    public IReadOnlyList<CurveStat> ComputeBandStats(double lo, double hi)
    {
        if (lo > hi) (lo, hi) = (hi, lo);
        var list = new List<CurveStat>();
        foreach (var s in _series)
        {
            double min = double.PositiveInfinity, max = double.NegativeInfinity, sum = 0;
            int n = 0;
            for (int i = 0; i < s.Independent.Length; i++)
            {
                var x = s.Independent[i];
                if (x < lo || x > hi) continue;
                var v = s.Values[i];
                if (double.IsNaN(v)) continue;
                if (v < min) min = v;
                if (v > max) max = v;
                sum += v;
                n++;
            }
            if (n > 0) list.Add(CurveStat.Create(s.Name, s.Color, min, sum / n, max, n));
        }
        return list;
    }

    /// <summary>Called by the renderer when the user finishes a Shift-drag.</summary>
    public void SetSelection(IReadOnlyList<CurveStat> stats, string rangeText)
    {
        SelectionStats.Clear();
        foreach (var s in stats) SelectionStats.Add(s);
        SelectionRangeText = stats.Count > 0 ? $"Участок: {rangeText}" : $"Участок: {rangeText} — нет точек";
        OnPropertyChanged(nameof(HasSelectionStats));
    }

    public void ClearSelection()
    {
        SelectionStats.Clear();
        SelectionRangeText = null;
        OnPropertyChanged(nameof(HasSelectionStats));
        SelectionCleared?.Invoke();
    }

    // ---- Helpers --------------------------------------------------------------

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

/// <summary>One parameter check-box in a chart's parameter popup.</summary>
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

/// <summary>Numeric data for one plotted curve (independent axis + values, same length).</summary>
public sealed record ChartSeriesData(string Name, ChartColor Color, double[] Independent, double[] Values);

/// <summary>Everything the renderer needs for one repaint.</summary>
public sealed record ChartSnapshot(bool IsTime, bool Vertical, bool SeparateScales, IReadOnlyList<ChartSeriesData> Series);

/// <summary>Min / average / max of one curve over a selected band.</summary>
public sealed class CurveStat
{
    public string Name  { get; init; } = "";
    public System.Windows.Media.Brush Swatch { get; init; } = System.Windows.Media.Brushes.Gray;
    public string MinText { get; init; } = "";
    public string AvgText { get; init; } = "";
    public string MaxText { get; init; } = "";
    public int    Count   { get; init; }

    public static CurveStat Create(string name, ChartColor colour, double min, double avg, double max, int count)
    {
        var brush = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(colour.R, colour.G, colour.B));
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
