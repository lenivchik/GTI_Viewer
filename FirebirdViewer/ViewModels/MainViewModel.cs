using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Data;
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
    private readonly ILiveDataService _live;

    public MainViewModel(IFirebirdService db, ISettingsService settings, ILiveDataService live)
    {
        _db = db;
        _settings = settings;
        _live = live;

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
        ToggleLiveCommand     = new RelayCommand(_ => IsLiveMode = !IsLiveMode, _ => IsConnected);

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

    /// <summary>
    /// Orientation applied to every chart panel. Changing it propagates to all
    /// existing charts and is inherited by new ones. The Графики tab layout
    /// also reads this — horizontal stacks panels top-to-bottom, vertical
    /// stacks them left-to-right.
    /// </summary>
    private ChartOrientation _globalChartOrientation = ChartOrientation.Horizontal;
    public ChartOrientation GlobalChartOrientation
    {
        get => _globalChartOrientation;
        set
        {
            if (SetProperty(ref _globalChartOrientation, value))
            {
                OnPropertyChanged(nameof(GlobalOrientationIsHorizontal));
                OnPropertyChanged(nameof(GlobalOrientationIsVertical));
                foreach (var c in Charts) c.Orientation = value;
            }
        }
    }

    public bool GlobalOrientationIsHorizontal
    {
        get => GlobalChartOrientation == ChartOrientation.Horizontal;
        set { if (value) GlobalChartOrientation = ChartOrientation.Horizontal; }
    }
    public bool GlobalOrientationIsVertical
    {
        get => GlobalChartOrientation == ChartOrientation.Vertical;
        set { if (value) GlobalChartOrientation = ChartOrientation.Vertical; }
    }

    private void AddChart()
    {
        var chart = new ChartPanelViewModel(_nextChartNumber++) { Orientation = GlobalChartOrientation };
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

    /// <summary>
    /// Redraw every chart after a real-time append. The column list did not change, so the
    /// panels only re-read the table — and they keep the operator's current zoom window.
    /// </summary>
    private void PushLiveDataToCharts()
    {
        foreach (var c in Charts) c.NotifyDataAppended();
    }

    // ============================================================
    // Data
    // ============================================================

    private DataView? _currentTableData;
    public DataView? CurrentTableData { get => _currentTableData; set => SetProperty(ref _currentTableData, value); }

    private DataView? _operationsData;
    public DataView? OperationsData { get => _operationsData; set => SetProperty(ref _operationsData, value); }

    private DataView? _toolsData;
    public DataView? ToolsData { get => _toolsData; set => SetProperty(ref _toolsData, value); }

    private int _rowLimit = 1000;
    public int RowLimit { get => _rowLimit; set => SetProperty(ref _rowLimit, value); }

    private string? _rowCountText;
    public string? RowCountText { get => _rowCountText; set => SetProperty(ref _rowCountText, value); }

    private string? _operationsCountText;
    public string? OperationsCountText { get => _operationsCountText; set => SetProperty(ref _operationsCountText, value); }

    private string? _toolsCountText;
    public string? ToolsCountText { get => _toolsCountText; set => SetProperty(ref _toolsCountText, value); }

    // ============================================================
    // Real-time mode (Реальное время)
    //
    // The archive is read once per selection; real-time mode keeps that view
    // current by asking the server, every few seconds, only for the rows the
    // registrar has written since the previous poll. New rows are spliced onto
    // the top of the table already on screen, so the grid, the charts and the
    // counters move as drilling goes on instead of freezing at the moment the
    // race was opened.
    // ============================================================

    /// <summary>Carries the polling watermark. Hidden in the grid unless the user asks for it.</summary>
    private const string RecIdColumn = "REC_HEADER_ID";

    /// <summary>Operations, tool composition and the race list are re-read on this slower beat.</summary>
    private static readonly TimeSpan ContextRefreshInterval = TimeSpan.FromSeconds(30);

    /// <summary>Consecutive failed polls before real-time mode gives up and says so.</summary>
    private const int MaxLivePollFailures = 3;

    /// <summary>How many of the newest records are re-read on the slow beat to pick up late values.</summary>
    private const int RecentRefreshRows = 200;

    /// <summary>Newest REC_HEADER_ID on screen — the starting point of the next poll.</summary>
    private long _lastRecHeaderId;

    private int _livePollFailures;
    private DateTime _lastContextRefreshUtc;

    /// <summary>
    /// Set while a poll runs. Load failures are then reported in the status bar and leave
    /// the current data alone — a modal dialog every few seconds, or a grid wiped by one
    /// dropped packet, would make the window unusable.
    /// </summary>
    private bool _inLivePoll;

    /// <summary>
    /// Depth of the loads the operator asked for (connect, well, race, F5). A poll steps
    /// aside while one is running: the data it would read is already on its way, and the
    /// two of them would otherwise be queued against the same connection back to back.
    /// </summary>
    private int _loadDepth;

    /// <summary>Polling intervals offered in the toolbar.</summary>
    public static IReadOnlyList<LiveIntervalOption> LiveIntervalOptions { get; } = new[]
    {
        new LiveIntervalOption(1,  "1 с"),
        new LiveIntervalOption(2,  "2 с"),
        new LiveIntervalOption(5,  "5 с"),
        new LiveIntervalOption(10, "10 с"),
        new LiveIntervalOption(30, "30 с"),
        new LiveIntervalOption(60, "1 мин"),
    };

    private bool _isLiveMode;
    public bool IsLiveMode
    {
        get => _isLiveMode;
        set { if (SetProperty(ref _isLiveMode, value)) ApplyLiveMode(); }
    }

    private int _liveIntervalSeconds = 5;
    public int LiveIntervalSeconds
    {
        get => _liveIntervalSeconds;
        set
        {
            if (value <= 0) value = 5;
            if (SetProperty(ref _liveIntervalSeconds, value))
                _live.Interval = TimeSpan.FromSeconds(value);
        }
    }

    /// <summary>True only while the timer is actually polling — drives the indicator in the status bar.</summary>
    private bool _isLiveRunning;
    public bool IsLiveRunning { get => _isLiveRunning; private set => SetProperty(ref _isLiveRunning, value); }

    private string? _liveStatusText;
    public string? LiveStatusText { get => _liveStatusText; private set => SetProperty(ref _liveStatusText, value); }

    /// <summary>Race filter for the queries: null means «все рейсы».</summary>
    private long? CurrentRaceId =>
        SelectedRace is null || SelectedRace.IsAllRaces ? null : SelectedRace.RaceId;

    /// <summary>Start or stop polling so it matches the toggle and what is currently selected.</summary>
    private void ApplyLiveMode()
    {
        if (IsLiveMode && IsConnected && SelectedWell is not null) StartLive();
        else StopLive();
    }

    private void StartLive()
    {
        _livePollFailures = 0;
        _lastContextRefreshUtc = DateTime.UtcNow;
        _live.Interval = TimeSpan.FromSeconds(LiveIntervalSeconds);
        if (!_live.IsRunning) _live.Start(PollLiveAsync);
        IsLiveRunning = true;
        LiveStatusText = $"Реальное время: опрос каждые {LiveIntervalSeconds} с";
    }

    /// <summary><paramref name="reason"/> overrides the default text when polling stops after errors.</summary>
    private void StopLive(string? reason = null)
    {
        _live.Stop();
        IsLiveRunning = false;
        LiveStatusText = reason ?? (!IsLiveMode ? null
            : !IsConnected ? "Реальное время: нет подключения"
            : "Реальное время: скважина не выбрана");
    }

    /// <summary>
    /// One real-time beat: read the rows written since the previous poll, splice them onto
    /// the top of the grid and redraw the charts. Operations, tool composition and the race
    /// list follow on <see cref="ContextRefreshInterval"/> — they change far less often than
    /// the parameter stream and each costs a full query.
    /// </summary>
    private async Task PollLiveAsync(CancellationToken ct)
    {
        var well = SelectedWell;
        if (!IsConnected || well is null) { StopLive(); return; }

        // A load the operator asked for is in flight; it brings this data anyway.
        if (_loadDepth > 0) return;

        var raceId = CurrentRaceId;
        _inLivePoll = true;
        try
        {
            int appended;
            var fullRead = false;
            if (CurrentTableData?.Table is { } table && _lastRecHeaderId > 0)
            {
                // The poll's token is deliberately not handed to the database layer: the
                // Firebird client cannot cleanly abandon a command it has already sent, and
                // a half-read answer left on the socket breaks the next one. It is checked
                // between calls instead — that is what StillSelected does.
                var fresh = await _db.GetRaceDataSinceAsync(well.WellId, raceId, _lastRecHeaderId, RowLimit)
                                     .ConfigureAwait(true);
                if (!StillSelected(well, raceId, ct)) return;
                appended = AppendFreshRows(table, fresh);
            }
            else
            {
                // Nothing on screen to append onto (the selection has just changed, or the
                // previous read failed) — read the window in full and start a new watermark.
                var dt = await _db.GetRaceDataAsync(well.WellId, raceId, RowLimit).ConfigureAwait(true);
                if (!StillSelected(well, raceId, ct)) return;
                ApplyRaceData(dt);
                appended = dt.Rows.Count;
                fullRead = true;   // ApplyRaceData has already published counters and charts
            }

            var updated = 0;
            if (DateTime.UtcNow - _lastContextRefreshUtc >= ContextRefreshInterval)
            {
                _lastContextRefreshUtc = DateTime.UtcNow;

                if (CurrentTableData?.Table is { } current)
                {
                    updated = await TopUpRecentRowsAsync(well.WellId, raceId, current, ct).ConfigureAwait(true);
                    if (!StillSelected(well, raceId, ct)) return;
                }

                await RefreshRaceListAsync(well.WellId, ct).ConfigureAwait(true);
                if (!StillSelected(well, raceId, ct)) return;
                await LoadOperationsAsync(well.WellId, raceId).ConfigureAwait(true);
                if (!StillSelected(well, raceId, ct)) return;
                await LoadToolsAsync(well.WellId, raceId).ConfigureAwait(true);
            }

            if ((appended > 0 && !fullRead) || updated > 0)
            {
                RowCountText = $"Записей: {CurrentTableData?.Count ?? 0} (показано не более {RowLimit})";
                PushLiveDataToCharts();
            }

            _livePollFailures = 0;
            var stamp = DateTime.Now.ToString("HH:mm:ss");
            LiveStatusText = appended > 0 ? $"Реальное время: {stamp}, +{appended}"
                : updated > 0             ? $"Реальное время: {stamp}, уточнено записей: {updated}"
                                          : $"Реальное время: {stamp}, новых записей нет";
        }
        catch (OperationCanceledException)
        {
            // Real-time mode was switched off while the query was running.
        }
        catch (Exception ex)
        {
            _livePollFailures++;
            LiveStatusText = $"Реальное время: ошибка обновления — {ex.Message}";

            if (_livePollFailures >= MaxLivePollFailures)
            {
                var message = ex.Message;
                IsLiveMode = false;                       // ApplyLiveMode stops the timer
                StopLive($"Реальное время выключено: {_livePollFailures} ошибки подряд");
                MessageBox.Show(
                    $"Автообновление остановлено после {_livePollFailures} неудачных попыток подряд.\n\n{message}"
                    + "\n\nЕсли ошибка повторяется, переподключитесь: Файл → Подключиться к базе...",
                    "Реальное время", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        finally
        {
            _inLivePoll = false;
        }
    }

    /// <summary>
    /// Guard for everything that resumes after an await: the operator may have picked another
    /// well or race in the meantime, and those rows must not land on the new selection.
    /// </summary>
    private bool StillSelected(WellInfo well, long? raceId, CancellationToken ct) =>
        !ct.IsCancellationRequested && ReferenceEquals(well, SelectedWell) && raceId == CurrentRaceId;

    /// <summary>
    /// Splice freshly read rows (oldest first) onto the top of the table on screen, advance
    /// the watermark and drop whatever now falls past the row limit. Returns the row count.
    /// </summary>
    private int AppendFreshRows(DataTable target, DataTable fresh)
    {
        if (fresh.Rows.Count == 0) return 0;
        if (!target.Columns.Contains(RecIdColumn) || !fresh.Columns.Contains(RecIdColumn)) return 0;

        foreach (DataRow src in fresh.Rows)
        {
            var row = target.NewRow();
            foreach (DataColumn col in target.Columns)
            {
                if (fresh.Columns.Contains(col.ColumnName))
                    row[col] = src[col.ColumnName];
            }
            target.Rows.InsertAt(row, 0);   // newest first, same order as the full read

            var id = ToInt64(src[RecIdColumn]);
            if (id > _lastRecHeaderId) _lastRecHeaderId = id;
        }

        var limit = RowLimit > 0 ? RowLimit : 1000;
        while (target.Rows.Count > limit) target.Rows.RemoveAt(target.Rows.Count - 1);

        return fresh.Rows.Count;
    }

    /// <summary>
    /// Re-read the newest records and copy changed values into the rows already on screen.
    /// The gas analysis is logged with a lag, so a record can gain values minutes after its
    /// header was written; without this pass the live view would keep the first, half-empty
    /// version of those rows forever. Records newer than the watermark are skipped here —
    /// they belong to the incremental poll, which is what advances the watermark.
    /// </summary>
    private async Task<int> TopUpRecentRowsAsync(long wellId, long? raceId, DataTable target, CancellationToken ct)
    {
        if (!target.Columns.Contains(RecIdColumn)) return 0;

        var rows = RowLimit > 0 ? Math.Min(RecentRefreshRows, RowLimit) : RecentRefreshRows;
        var fresh = await _db.GetRaceDataAsync(wellId, raceId, rows).ConfigureAwait(true);
        if (ct.IsCancellationRequested || !fresh.Columns.Contains(RecIdColumn)) return 0;

        var byId = new Dictionary<long, DataRow>(target.Rows.Count);
        foreach (DataRow row in target.Rows) byId[ToInt64(row[RecIdColumn])] = row;

        var updated = 0;
        foreach (DataRow src in fresh.Rows)
        {
            if (!byId.TryGetValue(ToInt64(src[RecIdColumn]), out var row)) continue;

            var touched = false;
            foreach (DataColumn col in target.Columns)
            {
                if (!fresh.Columns.Contains(col.ColumnName)) continue;
                var value = src[col.ColumnName];
                if (Equals(row[col], value)) continue;
                row[col] = value;
                touched = true;
            }
            if (touched) updated++;
        }
        return updated;
    }

    /// <summary>
    /// Merge races that appeared since the list was built — a new one is opened every time
    /// the crew trips in. The current selection is deliberately left alone: re-assigning it
    /// would reload everything under the operator.
    /// </summary>
    private async Task RefreshRaceListAsync(long wellId, CancellationToken ct)
    {
        var list = await _db.GetRacesAsync(wellId).ConfigureAwait(true);
        if (ct.IsCancellationRequested) return;

        var known = new HashSet<long>(Races.Select(r => r.RaceId));
        var insertAt = Races.Count > 0 && Races[0].IsAllRaces ? 1 : 0;

        // GetRacesAsync returns newest first; walking it backwards and always inserting at
        // the same slot keeps the combo newest-first once several races have appeared.
        foreach (var race in list.Reverse())
        {
            if (known.Add(race.RaceId)) Races.Insert(insertAt, race);
        }
    }

    /// <summary>Remember the newest REC_HEADER_ID on screen — where the next poll resumes.</summary>
    private void UpdateWatermark(DataTable? dt)
    {
        _lastRecHeaderId = 0;
        if (dt is null || !dt.Columns.Contains(RecIdColumn)) return;
        foreach (DataRow row in dt.Rows)
        {
            var id = ToInt64(row[RecIdColumn]);
            if (id > _lastRecHeaderId) _lastRecHeaderId = id;
        }
    }

    /// <summary>REC_HEADER_ID arrives as BIGINT, but legacy databases type it as NUMERIC.</summary>
    private static long ToInt64(object? value) => value switch
    {
        long l    => l,
        int i     => i,
        short s   => s,
        decimal m => (long)m,
        double d  => (long)d,
        _         => 0,
    };

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
    public ICommand ToggleLiveCommand        { get; }

    // ============================================================
    // Connect / disconnect
    // ============================================================

    public async Task<bool> ConnectAsync(ConnectionSettings settings)
    {
        StopLive();   // a poll from the previous connection must not outlive it
        _loadDepth++;
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
            ApplyLiveMode();

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
        finally
        {
            _loadDepth--;
        }
    }

    private async Task DisconnectAsync()
    {
        StopLive();   // no poll may be in flight while the connection is closing
        await _db.DisconnectAsync().ConfigureAwait(true);
        FriendlyNames.ClearDynamicCatalog();
        IsConnected = false;
        _lastRecHeaderId = 0;
        Wells.Clear();
        Races.Clear();
        Columns.Clear();
        SelectedWell = null;
        SelectedRace = null;
        CurrentTableData = OperationsData = ToolsData = null;
        StatusText = "Отключено";
        ConnectionInfoText = null;
        RowCountText = null;
        OperationsCountText = null;
        ToolsCountText = null;
        CursorText = null;
        StopLive();   // again, now that the state is cleared, so the indicator reads "нет подключения"
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
            CurrentTableData = OperationsData = ToolsData = null;
            Columns.Clear();
            _lastRecHeaderId = 0;
            StopLive();
            return;
        }

        _loadDepth++;
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
        finally
        {
            _loadDepth--;
        }
    }

    private async Task LoadRaceContentsAsync()
    {
        if (SelectedWell is null || !IsConnected)
        {
            CurrentTableData = OperationsData = ToolsData = null;
            Columns.Clear();
            _lastRecHeaderId = 0;
            StopLive();
            return;
        }

        var raceId = CurrentRaceId;
        _loadDepth++;
        try
        {
            await LoadRaceDataAsync(SelectedWell.WellId, raceId).ConfigureAwait(true);
            await LoadOperationsAsync(SelectedWell.WellId, raceId).ConfigureAwait(true);
            await LoadToolsAsync(SelectedWell.WellId, raceId).ConfigureAwait(true);
        }
        finally
        {
            _loadDepth--;
        }

        // The archive for this selection is on screen; from here real-time mode keeps it current.
        ApplyLiveMode();
    }

    private async Task LoadRaceDataAsync(long wellId, long? raceId)
    {
        try
        {
            var dt = await _db.GetRaceDataAsync(wellId, raceId, RowLimit).ConfigureAwait(true);
            ApplyRaceData(dt);
        }
        catch (Exception ex)
        {
            CurrentTableData = null;
            Columns.Clear();
            _lastRecHeaderId = 0;
            MessageBox.Show(ex.Message, "Ошибка чтения данных",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>Publish a freshly read window: columns, grid, watermark, counter, charts.</summary>
    private void ApplyRaceData(DataTable dt)
    {
        RebuildColumnVisibility(dt);
        CurrentTableData = dt.DefaultView;
        UpdateWatermark(dt);
        RowCountText = $"Записей: {dt.Rows.Count} (показано не более {RowLimit})";
        PushDataToCharts();
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
            if (_inLivePoll) { LiveStatusText = $"Реальное время: операции — {ex.Message}"; return; }
            OperationsData = null;
            OperationsCountText = null;
            MessageBox.Show(ex.Message, "Ошибка чтения операций",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task LoadToolsAsync(long wellId, long? raceId)
    {
        try
        {
            var dt = await _db.GetToolsAsync(wellId, raceId).ConfigureAwait(true);
            ToolsData = dt.DefaultView;
            ToolsCountText = $"Инструмент: {dt.Rows.Count}";
        }
        catch (Exception ex)
        {
            if (_inLivePoll) { LiveStatusText = $"Реальное время: инструмент — {ex.Message}"; return; }
            ToolsData = null;
            ToolsCountText = null;
            MessageBox.Show(ex.Message, "Ошибка чтения инструмента",
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
                // REC_HEADER_ID is plumbing for real-time polling, not a drilling parameter:
                // it is there for every new table but stays hidden until the user asks for it.
                IsVisible   = prior.TryGetValue(dc.ColumnName, out var v)
                                ? v
                                : !string.Equals(dc.ColumnName, RecIdColumn, StringComparison.OrdinalIgnoreCase)
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
        LiveIntervalSeconds = s.LiveIntervalSeconds > 0 ? s.LiveIntervalSeconds : 5;
        IsLiveMode = s.LiveMode;
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
            LiveMode = IsLiveMode,
            LiveIntervalSeconds = LiveIntervalSeconds,
            LastConnection = StripPassword(Connection),
            RecentConnections = RecentConnections.Select(StripPassword).ToList()
        };
        _settings.Save(snapshot);
    }

    /// <summary>Called when the window closes: stop polling before the app tears down.</summary>
    public void Shutdown()
    {
        _live.Dispose();
        IsLiveRunning = false;
    }

    private static ConnectionSettings StripPassword(ConnectionSettings c)
    {
        var clone = c.Clone();
        clone.Password = "";
        return clone;
    }
}

/// <summary>One entry in the real-time interval combo: seconds plus its Russian label.</summary>
public sealed record LiveIntervalOption(int Seconds, string Label);
