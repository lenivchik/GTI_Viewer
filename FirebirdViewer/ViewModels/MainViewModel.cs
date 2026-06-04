using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Input;
using FirebirdViewer.Commands;
using FirebirdViewer.Metadata;
using FirebirdViewer.Models;
using FirebirdViewer.Services;
using Microsoft.Win32;

namespace FirebirdViewer.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly IFirebirdService _db;
    private readonly ISettingsService _settings;

    public MainViewModel(IFirebirdService db, ISettingsService settings)
    {
        _db = db;
        _settings = settings;

        Wells              = new ObservableCollection<WellInfo>();
        Races              = new ObservableCollection<RaceInfo>();
        Columns            = new ObservableCollection<ColumnVisibility>();
        RecentConnections  = new ObservableCollection<ConnectionSettings>();

        Columns.CollectionChanged += OnColumnsCollectionChanged;

        DisconnectCommand     = new AsyncRelayCommand(_ => DisconnectAsync(),       _ => IsConnected);
        RefreshCommand        = new AsyncRelayCommand(_ => RefreshSelectionAsync(), _ => IsConnected && SelectedWell is not null);
        ExportDataCommand     = new RelayCommand(_ => ExportDataToCsv(),       _ => CurrentTableData is not null);
        ExportOperationsCommand = new RelayCommand(_ => ExportOperationsToCsv(), _ => OperationsData is not null);
        ToggleAllColumnsCommand = new RelayCommand(_ => ToggleAllColumns(), _ => Columns.Count > 0);
        MoveColumnUpCommand   = new RelayCommand(_ => MoveSelectedColumn(-1), _ => CanMoveSelected(-1));
        MoveColumnDownCommand = new RelayCommand(_ => MoveSelectedColumn(+1), _ => CanMoveSelected(+1));
        UseRecentCommand      = new RelayCommand(p => UseRecent(p as ConnectionSettings));
        ClearRecentCommand    = new RelayCommand(_ => ClearRecent());
        AddChartCommand       = new RelayCommand(_ => AddChart());
        RemoveChartCommand    = new RelayCommand(p => RemoveChart(p as ChartPanelViewModel));

        // Seed one chart so the Графики tab is never empty.
        AddChart();

        LoadSettings();
    }

    // ============================================================
    // Connection state
    // ============================================================

    private ConnectionSettings _connection = new() { UserName = "CREATOR" };
    public ConnectionSettings Connection
    {
        get => _connection;
        set => SetProperty(ref _connection, value);
    }

    private bool _isConnected;
    public bool IsConnected
    {
        get => _isConnected;
        private set
        {
            if (SetProperty(ref _isConnected, value))
                OnPropertyChanged(nameof(IsDisconnected));
        }
    }
    public bool IsDisconnected => !IsConnected;

    private string _statusText = "Не подключено";
    public string StatusText { get => _statusText; set => SetProperty(ref _statusText, value); }

    private string? _connectionInfoText;
    public string? ConnectionInfoText { get => _connectionInfoText; set => SetProperty(ref _connectionInfoText, value); }

    private string? _cursorText;
    public string? CursorText { get => _cursorText; set => SetProperty(ref _cursorText, value); }

    // ============================================================
    // Wells & races
    // ============================================================

    public ObservableCollection<WellInfo> Wells { get; }
    public ObservableCollection<RaceInfo> Races { get; }

    private WellInfo? _selectedWell;
    public WellInfo? SelectedWell
    {
        get => _selectedWell;
        set
        {
            if (SetProperty(ref _selectedWell, value))
            {
                OnPropertyChanged(nameof(WindowTitle));
                _ = OnWellChangedAsync();
            }
        }
    }

    private RaceInfo? _selectedRace;
    public RaceInfo? SelectedRace
    {
        get => _selectedRace;
        set
        {
            if (SetProperty(ref _selectedRace, value))
            {
                OnPropertyChanged(nameof(WindowTitle));
                _ = LoadRaceContentsAsync();
            }
        }
    }

    public string WindowTitle
    {
        get
        {
            if (SelectedWell is null) return "Просмотр данных ГТИ";
            var raceText = SelectedRace is null || SelectedRace.IsAllRaces
                ? "все рейсы"
                : SelectedRace.Display;
            return $"Просмотр данных ГТИ: {SelectedWell.Name} — {raceText}";
        }
    }

    // ============================================================
    // Column visibility list (drives the left checkbox panel)
    // ============================================================

    public ObservableCollection<ColumnVisibility> Columns { get; }

    private ColumnVisibility? _selectedColumn;
    public ColumnVisibility? SelectedColumn
    {
        get => _selectedColumn;
        set => SetProperty(ref _selectedColumn, value);
    }

    /// <summary>True only when every column is visible. Used by the "Все" checkbox.</summary>
    public bool AllColumnsVisible
    {
        get => Columns.Count > 0 && Columns.All(c => c.IsVisible);
        set => SetAllColumns(value);
    }

    /// <summary>Click handler for the "Все" checkbox: invert the current state cleanly.</summary>
    private void ToggleAllColumns()
    {
        if (Columns.Count == 0) return;
        var anyHidden = Columns.Any(c => !c.IsVisible);
        SetAllColumns(anyHidden);   // if anything is hidden → show all; else hide all
    }

    private void SetAllColumns(bool isVisible)
    {
        foreach (var c in Columns) c.IsVisible = isVisible;
        OnPropertyChanged(nameof(AllColumnsVisible));
    }

    private bool CanMoveSelected(int delta)
    {
        if (SelectedColumn is null) return false;
        int idx = Columns.IndexOf(SelectedColumn);
        int target = idx + delta;
        return idx >= 0 && target >= 0 && target < Columns.Count;
    }

    private void MoveSelectedColumn(int delta)
    {
        if (SelectedColumn is null) return;
        int idx = Columns.IndexOf(SelectedColumn);
        int target = idx + delta;
        if (idx < 0 || target < 0 || target >= Columns.Count) return;
        Columns.Move(idx, target);
        ColumnOrderChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Raised when Up/Down reorders columns; the View re-syncs DataGrid display order.</summary>
    public event EventHandler? ColumnOrderChanged;

    /// <summary>Raised when a recent connection is chosen so the View can open the connection dialog pre-filled.</summary>
    public event EventHandler<ConnectionSettings>? ConnectRequested;

    private void OnColumnsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
            foreach (ColumnVisibility c in e.NewItems) c.PropertyChanged += OnColumnVisibilityChanged;
        if (e.OldItems is not null)
            foreach (ColumnVisibility c in e.OldItems) c.PropertyChanged -= OnColumnVisibilityChanged;
        OnPropertyChanged(nameof(AllColumnsVisible));
    }

    private void OnColumnVisibilityChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ColumnVisibility.IsVisible))
            OnPropertyChanged(nameof(AllColumnsVisible));
    }

    // ============================================================
    // Charts (Графики tab) — collection of chart panels
    // ============================================================

    public ObservableCollection<ChartPanelViewModel> Charts { get; } = new();

    private int _nextChartNumber = 1;

    private void AddChart()
    {
        var chart = new ChartPanelViewModel(_nextChartNumber++);
        chart.SetData(CurrentTableData, Columns);
        Charts.Add(chart);
    }

    private void RemoveChart(ChartPanelViewModel? chart)
    {
        if (chart is null) return;
        Charts.Remove(chart);
    }

    /// <summary>Push the current data set into every chart panel.</summary>
    private void PushDataToCharts()
    {
        foreach (var c in Charts) c.SetData(CurrentTableData, Columns);
    }

    // ============================================================
    // Data
    // ============================================================

    private DataView? _currentTableData;
    public DataView? CurrentTableData { get => _currentTableData; set => SetProperty(ref _currentTableData, value); }

    private DataView? _operationsData;
    public DataView? OperationsData { get => _operationsData; set => SetProperty(ref _operationsData, value); }

    private int _rowLimit = 1000;
    public int RowLimit { get => _rowLimit; set => SetProperty(ref _rowLimit, value); }

    private string? _rowCountText;
    public string? RowCountText { get => _rowCountText; set => SetProperty(ref _rowCountText, value); }

    private string? _operationsCountText;
    public string? OperationsCountText { get => _operationsCountText; set => SetProperty(ref _operationsCountText, value); }

    // ============================================================
    // Recent connections
    // ============================================================

    public ObservableCollection<ConnectionSettings> RecentConnections { get; }

    // ============================================================
    // Commands
    // ============================================================

    public ICommand DisconnectCommand        { get; }
    public ICommand RefreshCommand           { get; }
    public ICommand ExportDataCommand        { get; }
    public ICommand ExportOperationsCommand  { get; }
    public ICommand ToggleAllColumnsCommand  { get; }
    public ICommand MoveColumnUpCommand      { get; }
    public ICommand MoveColumnDownCommand    { get; }
    public ICommand UseRecentCommand         { get; }
    public ICommand ClearRecentCommand       { get; }
    public ICommand AddChartCommand          { get; }
    public ICommand RemoveChartCommand       { get; }

    // ============================================================
    // Connect / disconnect
    // ============================================================

    public async Task<bool> ConnectAsync(ConnectionSettings settings)
    {
        try
        {
            StatusText = "Подключение...";

            await _db.ConnectAsync(settings).ConfigureAwait(true);
            Connection = settings;
            IsConnected = true;

            await LoadParameterCatalogAsync().ConfigureAwait(true);
            await LoadWellsAsync().ConfigureAwait(true);

            StatusText = "Подключено";
            ConnectionInfoText = $"{settings.Display}  ·  {_db.ServerVersion}";

            AddToRecent(settings);
            SaveSettings();
            return true;
        }
        catch (Exception ex)
        {
            IsConnected = false;
            StatusText = "Ошибка подключения";
            MessageBox.Show(ex.Message, "Ошибка подключения",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    private async Task DisconnectAsync()
    {
        await _db.DisconnectAsync().ConfigureAwait(true);
        FriendlyNames.ClearDynamicCatalog();
        IsConnected = false;
        Wells.Clear();
        Races.Clear();
        Columns.Clear();
        SelectedWell = null;
        SelectedRace = null;
        CurrentTableData = OperationsData = null;
        StatusText = "Отключено";
        ConnectionInfoText = null;
        RowCountText = null;
        OperationsCountText = null;
        CursorText = null;
        PushDataToCharts();
    }

    // ============================================================
    // Loading
    // ============================================================

    private async Task LoadParameterCatalogAsync()
    {
        try
        {
            var rows = await _db.GetParameterCatalogAsync().ConfigureAwait(true);
            FriendlyNames.LoadFromParams(rows);
        }
        catch (Exception)
        {
            // PARAMS may be missing on legacy databases — fall back to hardcoded names.
            FriendlyNames.ClearDynamicCatalog();
        }
    }

    private async Task LoadWellsAsync()
    {
        try
        {
            var list = await _db.GetWellsAsync().ConfigureAwait(true);
            Wells.Clear();
            foreach (var w in list) Wells.Add(w);

            StatusText = $"Скважин загружено: {Wells.Count}";

            // Auto-select the first well so the user immediately sees data.
            SelectedWell = Wells.FirstOrDefault();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Ошибка чтения скважин",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task OnWellChangedAsync()
    {
        Races.Clear();
        SelectedRace = null;

        if (SelectedWell is null || !IsConnected)
        {
            CurrentTableData = OperationsData = null;
            Columns.Clear();
            return;
        }

        try
        {
            var list = await _db.GetRacesAsync(SelectedWell.WellId).ConfigureAwait(true);
            Races.Add(RaceInfo.AllRaces);
            foreach (var r in list) Races.Add(r);

            // Default to "Все рейсы" so something useful loads right away.
            SelectedRace = Races.FirstOrDefault();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Ошибка чтения рейсов",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task LoadRaceContentsAsync()
    {
        if (SelectedWell is null || !IsConnected)
        {
            CurrentTableData = OperationsData = null;
            Columns.Clear();
            return;
        }

        var raceId = (SelectedRace is null || SelectedRace.IsAllRaces) ? (long?)null : SelectedRace.RaceId;
        await LoadRaceDataAsync(SelectedWell.WellId, raceId).ConfigureAwait(true);
        await LoadOperationsAsync(SelectedWell.WellId, raceId).ConfigureAwait(true);
    }

    private async Task LoadRaceDataAsync(long wellId, long? raceId)
    {
        try
        {
            var sw = Stopwatch.StartNew();
            var dt = await _db.GetRaceDataAsync(wellId, raceId, RowLimit).ConfigureAwait(true);
            sw.Stop();

            RebuildColumnVisibility(dt);
            CurrentTableData = dt.DefaultView;
            RowCountText = $"Записей: {dt.Rows.Count} (показано не более {RowLimit})";
            PushDataToCharts();
        }
        catch (Exception ex)
        {
            CurrentTableData = null;
            Columns.Clear();
            MessageBox.Show(ex.Message, "Ошибка чтения данных",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task LoadOperationsAsync(long wellId, long? raceId)
    {
        try
        {
            var dt = await _db.GetOperationsAsync(wellId, raceId).ConfigureAwait(true);
            OperationsData = dt.DefaultView;
            OperationsCountText = $"Операций: {dt.Rows.Count}";
        }
        catch (Exception ex)
        {
            OperationsData = null;
            OperationsCountText = null;
            MessageBox.Show(ex.Message, "Ошибка чтения операций",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void RebuildColumnVisibility(DataTable dt)
    {
        // Preserve user's current visibility on refresh.
        var prior = Columns.ToDictionary(c => c.Name, c => c.IsVisible);
        Columns.Clear();
        foreach (DataColumn dc in dt.Columns)
        {
            Columns.Add(new ColumnVisibility
            {
                Name        = dc.ColumnName,
                DisplayName = FriendlyNames.GetColumnDisplay(null, dc.ColumnName),
                Description = FriendlyNames.GetColumnDescription(null, dc.ColumnName),
                IsVisible   = prior.TryGetValue(dc.ColumnName, out var v) ? v : true
            });
        }
        OnPropertyChanged(nameof(AllColumnsVisible));
    }

    private async Task RefreshSelectionAsync() => await LoadRaceContentsAsync().ConfigureAwait(true);

    // ============================================================
    // CSV export
    // ============================================================

    private void ExportDataToCsv()
    {
        var nameHint = SelectedWell is null
            ? "Данные ГТИ"
            : $"{SelectedWell.Name} {(SelectedRace is null || SelectedRace.IsAllRaces ? "все рейсы" : "Рейс " + SelectedRace.Number)}";
        ExportToCsv(CurrentTableData, null, nameHint);
    }

    private void ExportOperationsToCsv()
    {
        var nameHint = SelectedWell is null ? "Операции" : $"Операции — {SelectedWell.Name}";
        ExportToCsv(OperationsData, "OPERATIONS_VIEW", nameHint);
    }

    private static void ExportToCsv(DataView? view, string? tableContext, string suggestedName)
    {
        if (view is null) return;
        var dlg = new SaveFileDialog
        {
            Filter = "CSV (*.csv)|*.csv|Все файлы (*.*)|*.*",
            FileName = $"{suggestedName}.csv",
            DefaultExt = ".csv"
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var dt = view.Table!;
            var sb = new StringBuilder();
            sb.AppendLine(string.Join(",",
                dt.Columns.Cast<DataColumn>()
                  .Select(c => CsvField(FriendlyNames.GetColumnDisplay(tableContext, c.ColumnName)))));
            foreach (DataRowView rv in view)
            {
                sb.AppendLine(string.Join(",",
                    dt.Columns.Cast<DataColumn>()
                      .Select(c => CsvField(rv[c.ColumnName]?.ToString() ?? ""))));
            }
            File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(true));
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Ошибка экспорта",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static string CsvField(string value)
    {
        if (value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0)
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        return value;
    }

    // ============================================================
    // Recent connections
    // ============================================================

    private void AddToRecent(ConnectionSettings c)
    {
        var clone = c.Clone();
        clone.Password = "";
        var existing = RecentConnections.FirstOrDefault(r => r.MatchesIdentity(clone));
        if (existing is not null) RecentConnections.Remove(existing);
        RecentConnections.Insert(0, clone);
        while (RecentConnections.Count > 8) RecentConnections.RemoveAt(RecentConnections.Count - 1);
    }

    private void UseRecent(ConnectionSettings? c)
    {
        if (c is null) return;
        var clone = c.Clone();
        Connection = clone;
        ConnectRequested?.Invoke(this, clone);
    }

    private void ClearRecent()
    {
        RecentConnections.Clear();
        SaveSettings();
    }

    // ============================================================
    // Settings persistence
    // ============================================================

    private void LoadSettings()
    {
        var s = _settings.Load();
        RowLimit = s.RowLimit > 0 ? s.RowLimit : 1000;
        if (s.LastConnection is not null)
        {
            Connection = s.LastConnection.Clone();
            Connection.Password = "";
        }
        RecentConnections.Clear();
        foreach (var c in s.RecentConnections) RecentConnections.Add(c);
    }

    public void SaveSettings()
    {
        var snapshot = new AppSettings
        {
            RowLimit = RowLimit,
            LastConnection = StripPassword(Connection),
            RecentConnections = RecentConnections.Select(StripPassword).ToList()
        };
        _settings.Save(snapshot);
    }

    private static ConnectionSettings StripPassword(ConnectionSettings c)
    {
        var clone = c.Clone();
        clone.Password = "";
        return clone;
    }
}
