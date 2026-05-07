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

        AllTables         = new ObservableCollection<DatabaseObject>();
        Columns           = new ObservableCollection<ColumnVisibility>();
        RecentConnections = new ObservableCollection<ConnectionSettings>();

        Columns.CollectionChanged += OnColumnsCollectionChanged;

        DisconnectCommand     = new AsyncRelayCommand(_ => DisconnectAsync(),       _ => IsConnected);
        RefreshCommand        = new AsyncRelayCommand(_ => RefreshSelectedAsync(), _ => IsConnected && SelectedTable is not null);
        ExecuteQueryCommand   = new AsyncRelayCommand(_ => ExecuteQueryAsync(),    _ => IsConnected && !string.IsNullOrWhiteSpace(QueryText));
        ExportDataCommand     = new RelayCommand(_ => ExportDataToCsv(),  _ => CurrentTableData is not null);
        ExportQueryCommand    = new RelayCommand(_ => ExportQueryToCsv(), _ => QueryResultData is not null);
        ShowAllColumnsCommand = new RelayCommand(_ => SetAllColumns(true));
        HideAllColumnsCommand = new RelayCommand(_ => SetAllColumns(false));
        MoveColumnUpCommand   = new RelayCommand(_ => MoveSelectedColumn(-1), _ => CanMoveSelected(-1));
        MoveColumnDownCommand = new RelayCommand(_ => MoveSelectedColumn(+1), _ => CanMoveSelected(+1));
        UseRecentCommand      = new RelayCommand(p => UseRecent(p as ConnectionSettings));
        ClearRecentCommand    = new RelayCommand(_ => ClearRecent());

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
    // Tables and selection
    // ============================================================

    public ObservableCollection<DatabaseObject> AllTables { get; }

    private DatabaseObject? _selectedTable;
    public DatabaseObject? SelectedTable
    {
        get => _selectedTable;
        set
        {
            if (SetProperty(ref _selectedTable, value))
            {
                OnPropertyChanged(nameof(WindowTitle));
                _ = LoadSelectedTableAsync();
            }
        }
    }

    public string WindowTitle => SelectedTable is null
        ? "Просмотр данных ГТИ"
        : $"Просмотр данных: «{SelectedTable.DisplayName}»";

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

    /// <summary>Tri-state "Все" checkbox. Null = mixed.</summary>
    public bool? AllColumnsVisible
    {
        get
        {
            if (Columns.Count == 0) return false;
            int visible = Columns.Count(c => c.IsVisible);
            if (visible == Columns.Count) return true;
            if (visible == 0) return false;
            return null;
        }
        set
        {
            if (!value.HasValue) return; // ignore null writes from the three-state CheckBox
            SetAllColumns(value.Value);
        }
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
    // Data tabs
    // ============================================================

    private DataView? _currentTableData;
    public DataView? CurrentTableData { get => _currentTableData; set => SetProperty(ref _currentTableData, value); }

    private DataView? _columnsData;
    public DataView? ColumnsData { get => _columnsData; set => SetProperty(ref _columnsData, value); }

    private DataView? _indexesData;
    public DataView? IndexesData { get => _indexesData; set => SetProperty(ref _indexesData, value); }

    private int _rowLimit = 1000;
    public int RowLimit { get => _rowLimit; set => SetProperty(ref _rowLimit, value); }

    private string? _rowCountText;
    public string? RowCountText { get => _rowCountText; set => SetProperty(ref _rowCountText, value); }

    // ============================================================
    // SQL tab
    // ============================================================

    private string _queryText = "SELECT FIRST 50 * FROM RDB$RELATIONS WHERE RDB$SYSTEM_FLAG = 0";
    public string QueryText { get => _queryText; set => SetProperty(ref _queryText, value); }

    private DataView? _queryResultData;
    public DataView? QueryResultData { get => _queryResultData; set => SetProperty(ref _queryResultData, value); }

    private string? _queryStatus;
    public string? QueryStatus { get => _queryStatus; set => SetProperty(ref _queryStatus, value); }

    // ============================================================
    // Recent connections
    // ============================================================

    public ObservableCollection<ConnectionSettings> RecentConnections { get; }

    // ============================================================
    // Commands
    // ============================================================

    public ICommand DisconnectCommand     { get; }
    public ICommand RefreshCommand        { get; }
    public ICommand ExecuteQueryCommand   { get; }
    public ICommand ExportDataCommand     { get; }
    public ICommand ExportQueryCommand    { get; }
    public ICommand ShowAllColumnsCommand { get; }
    public ICommand HideAllColumnsCommand { get; }
    public ICommand MoveColumnUpCommand   { get; }
    public ICommand MoveColumnDownCommand { get; }
    public ICommand UseRecentCommand      { get; }
    public ICommand ClearRecentCommand    { get; }

    // ============================================================
    // Connect / disconnect (called from MainWindow after dialog OK)
    // ============================================================

    public async Task<bool> ConnectAsync(ConnectionSettings settings)
    {
        try
        {
            StatusText = "Подключение...";

            await _db.ConnectAsync(settings).ConfigureAwait(true);
            Connection = settings;
            IsConnected = true;

            await LoadTablesAsync().ConfigureAwait(true);

            StatusText = "Загружено";
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
        IsConnected = false;
        AllTables.Clear();
        Columns.Clear();
        SelectedTable = null;
        CurrentTableData = ColumnsData = IndexesData = QueryResultData = null;
        StatusText = "Отключено";
        ConnectionInfoText = null;
        RowCountText = null;
        CursorText = null;
    }

    // ============================================================
    // Loading
    // ============================================================

    private async Task LoadTablesAsync()
    {
        var t = await _db.GetTablesAsync().ConfigureAwait(true);
        var v = await _db.GetViewsAsync().ConfigureAwait(true);

        // Show tables that have a friendly Russian label first; unknowns fall to the bottom.
        var ordered = t.Concat(v)
            .OrderBy(x => FriendlyNames.GetTable(x.Name) is null)
            .ThenBy(x => x.DisplayName, StringComparer.CurrentCulture);

        AllTables.Clear();
        foreach (var x in ordered) AllTables.Add(x);

        StatusText = $"Загружено: {t.Count + v.Count} источников данных";
    }

    private async Task LoadSelectedTableAsync()
    {
        if (SelectedTable is null || !IsConnected)
        {
            CurrentTableData = ColumnsData = IndexesData = null;
            Columns.Clear();
            return;
        }
        await LoadDataAsync(SelectedTable.Name).ConfigureAwait(true);
        await LoadColumnsAsync(SelectedTable.Name).ConfigureAwait(true);
        await LoadIndexesAsync(SelectedTable.Name).ConfigureAwait(true);
    }

    private async Task LoadDataAsync(string objectName)
    {
        try
        {
            var sw = Stopwatch.StartNew();
            var dt = await _db.GetTableDataAsync(objectName, RowLimit).ConfigureAwait(true);
            sw.Stop();

            RebuildColumnVisibility(dt);
            CurrentTableData = dt.DefaultView;
            RowCountText = $"Записей: {dt.Rows.Count} (показано не более {RowLimit})";
        }
        catch (Exception ex)
        {
            CurrentTableData = null;
            Columns.Clear();
            MessageBox.Show(ex.Message, "Ошибка чтения данных",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task LoadColumnsAsync(string tableName)
    {
        try
        {
            var dt = await _db.GetColumnsAsync(tableName).ConfigureAwait(true);
            ColumnsData = dt.DefaultView;
        }
        catch (Exception ex)
        {
            ColumnsData = null;
            MessageBox.Show(ex.Message, "Ошибка чтения схемы",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task LoadIndexesAsync(string tableName)
    {
        try
        {
            var dt = await _db.GetIndexesAsync(tableName).ConfigureAwait(true);
            IndexesData = dt.DefaultView;
        }
        catch (Exception ex)
        {
            IndexesData = null;
            MessageBox.Show(ex.Message, "Ошибка чтения индексов",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void RebuildColumnVisibility(DataTable dt)
    {
        // Preserve user's current visibility on refresh of the same table.
        var prior = Columns.ToDictionary(c => c.Name, c => c.IsVisible);
        Columns.Clear();
        var tableName = SelectedTable?.Name;
        foreach (DataColumn dc in dt.Columns)
        {
            Columns.Add(new ColumnVisibility
            {
                Name        = dc.ColumnName,
                DisplayName = FriendlyNames.GetColumnDisplay(tableName, dc.ColumnName),
                Description = FriendlyNames.GetColumnDescription(tableName, dc.ColumnName),
                IsVisible   = prior.TryGetValue(dc.ColumnName, out var v) ? v : true
            });
        }
        OnPropertyChanged(nameof(AllColumnsVisible));
    }

    private async Task RefreshSelectedAsync() => await LoadSelectedTableAsync().ConfigureAwait(true);

    // ============================================================
    // SQL
    // ============================================================

    private async Task ExecuteQueryAsync()
    {
        var sql = QueryText?.Trim() ?? "";
        if (sql.Length == 0) return;
        try
        {
            QueryStatus = "Выполняется...";
            var sw = Stopwatch.StartNew();
            var head = sql.TrimStart().ToUpperInvariant();
            bool isResultSet = head.StartsWith("SELECT") || head.StartsWith("WITH");

            if (isResultSet)
            {
                var dt = await _db.ExecuteQueryAsync(sql).ConfigureAwait(true);
                sw.Stop();
                QueryResultData = dt.DefaultView;
                QueryStatus = $"OK · {dt.Rows.Count} строк · {sw.ElapsedMilliseconds} мс";
            }
            else
            {
                var affected = await _db.ExecuteNonQueryAsync(sql).ConfigureAwait(true);
                sw.Stop();
                QueryResultData = null;
                QueryStatus = $"OK · затронуто строк: {affected} · {sw.ElapsedMilliseconds} мс";
            }
        }
        catch (Exception ex)
        {
            QueryStatus = "Ошибка";
            MessageBox.Show(ex.Message, "Ошибка SQL",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ============================================================
    // CSV export
    // ============================================================

    private void ExportDataToCsv()  => ExportToCsv(CurrentTableData, SelectedTable?.Name, SelectedTable?.DisplayName ?? "data");
    private void ExportQueryToCsv() => ExportToCsv(QueryResultData, null, "Результат запроса");

    private static void ExportToCsv(DataView? view, string? tableName, string suggestedName)
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
                  .Select(c => CsvField(FriendlyNames.GetColumnDisplay(tableName, c.ColumnName)))));
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
