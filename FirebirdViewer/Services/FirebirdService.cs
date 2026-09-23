using System.Data;
using FirebirdSql.Data.FirebirdClient;
using FirebirdViewer.Models;

namespace FirebirdViewer.Services;

public sealed class FirebirdService : IFirebirdService
{
    private FbConnection? _connection;

    /// <summary>
    /// One Firebird connection carries one request/response conversation. Two commands in
    /// flight at once interleave on the socket, the client then reads an operation code
    /// where it expected a response and throws «operation = N». Real-time polling runs
    /// alongside whatever the operator is doing, so every call takes this gate and the
    /// connection is used by one caller at a time.
    /// </summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    public bool IsConnected => _connection?.State == ConnectionState.Open;
    public string? ServerVersion { get; private set; }

    // ===== Connection lifecycle =====

    public async Task ConnectAsync(ConnectionSettings settings, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await OpenConnectionAsync(settings, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Caller holds <see cref="_gate"/>.</summary>
    private async Task OpenConnectionAsync(ConnectionSettings settings, CancellationToken ct)
    {
        await CloseConnectionAsync().ConfigureAwait(false);

        var csb = new FbConnectionStringBuilder
        {
            DataSource = string.IsNullOrWhiteSpace(settings.Server) ? "localhost" : settings.Server,
            Port       = settings.Port > 0 ? settings.Port : 3050,
            Database   = settings.Database,
            UserID     = settings.UserName,
            Password   = settings.Password,
            Charset    = string.IsNullOrEmpty(settings.Charset) ? "UTF8" : settings.Charset,
            Dialect    = 3,
            ServerType = FbServerType.Default,
            Pooling    = false
        };

        _connection = new FbConnection(csb.ConnectionString);
        await _connection.OpenAsync(ct).ConfigureAwait(false);

        ServerVersion = _connection.ServerVersion;
    }

    public async Task DisconnectAsync()
    {
        // Best-effort, as before: if a query is wedged, close anyway rather than hang.
        var acquired = await _gate.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        try
        {
            await CloseConnectionAsync().ConfigureAwait(false);
        }
        finally
        {
            if (acquired) _gate.Release();
        }
    }

    /// <summary>Caller holds <see cref="_gate"/>.</summary>
    private async Task CloseConnectionAsync()
    {
        if (_connection is null) return;
        try
        {
            if (_connection.State != ConnectionState.Closed)
                await _connection.CloseAsync().ConfigureAwait(false);
        }
        catch
        {
            // swallow — disconnect is best-effort
        }
        finally
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
            _connection = null;
            ServerVersion = null;
        }
    }

    // ===== GTI queries =====

    public Task<IReadOnlyList<WellInfo>> GetWellsAsync(CancellationToken ct = default)
        => GatedAsync(() => ReadWellsAsync(ct), ct);

    private async Task<IReadOnlyList<WellInfo>> ReadWellsAsync(CancellationToken ct)
    {
        const string sql = @"
            SELECT WELL_ID,
                   COALESCE(NAME, 'Скважина ' || CAST(WELL_ID AS VARCHAR(30))) AS WELL_NAME,
                   CLUSTER
            FROM WELLBORES
            ORDER BY COALESCE(IS_CURRENT, 0) DESC, NAME";

        var list = new List<WellInfo>();
        await using var cmd = new FbCommand(sql, _connection);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new WellInfo
            {
                WellId  = reader.GetInt64(0),
                Name    = reader.GetString(1),
                Cluster = reader.IsDBNull(2) ? null : reader.GetString(2),
            });
        }
        return list;
    }

    public Task<IReadOnlyList<RaceInfo>> GetRacesAsync(long wellId, CancellationToken ct = default)
        => GatedAsync(() => ReadRacesAsync(wellId, ct), ct);

    private async Task<IReadOnlyList<RaceInfo>> ReadRacesAsync(long wellId, CancellationToken ct)
    {
        const string sql = @"
            SELECT FIRST 200
                   RACE_ID, WELL_ID, NUMBER, START_TIME, STOP_TIME
            FROM RACES
            WHERE WELL_ID = @wellId
            ORDER BY RACE_ID DESC";

        var list = new List<RaceInfo>();
        await using var cmd = new FbCommand(sql, _connection);
        cmd.Parameters.AddWithValue("wellId", wellId);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new RaceInfo
            {
                RaceId    = reader.GetInt64(0),
                WellId    = reader.GetInt64(1),
                Number    = reader.GetInt32(2),
                StartTime = reader.IsDBNull(3) ? null : reader.GetDateTime(3),
                StopTime  = reader.IsDBNull(4) ? null : reader.GetDateTime(4),
            });
        }
        return list;
    }

    // Column list and joins shared by the full read and by the incremental read that
    // real-time mode uses. REC_HEADER_ID leads the list on purpose: it is the watermark
    // that lets polling ask the server only for rows written since the previous read.
    private const string RaceDataColumns = @"
                   h.REC_HEADER_ID,
                   h.REC_TIME,
                   h.BOTTOM_DEPTH,
                   h.BIT_DEPTH,
                   h.BOTTOM_LAG_DEPTH,
                   c.WOH,
                   c.MUD_PRESSURE,
                   c.PUMP_CNT1, c.PUMP_CNT2, c.PUMP_CNT3,
                   c.TABLE_TORQUE,
                   c.ROTOR_SPEED,
                   c.TR_BLOCK_POS,
                   c.WOB,
                   c.PIPE_WRENCH_TORQUE,
                   c.MUD_DENSITY_IN, c.MUD_DENSITY_OUT,
                   c.MUD_TEMP_IN, c.MUD_TEMP_OUT,
                   c.MUD_COND_IN, c.MUD_COND_OUT,
                   c.CIRC_RATE_IN, c.CIRC_RATE_OUT,
                   c.TRIP_RATE,
                   c.ROP,
                   c.DRILLING_TIME_LOG,
                   c.DRILLING_RATE,
                   c.SUMM_GAZ3,
                   c.VOLUME1, c.VOLUME2, c.VOLUME3, c.VOLUME4, c.VOLUME5, c.VOLUME6,
                   c.ACTIVE_VOLUME,
                   l.C1, l.C2, l.C3, l.C4, l.C5, l.C6,
                   l.SUMM_GAZ, l.SUMM_INT";

    private const string RaceDataFrom = @"
            FROM REC_HEADERS h
            JOIN RACES r        ON r.RACE_ID = h.RACE_ID
            LEFT JOIN REC_COMMON c ON c.REC_HEADER_ID = h.REC_HEADER_ID
            LEFT JOIN REC_LAG    l ON l.REC_HEADER_ID = h.REC_HEADER_ID";

    public Task<DataTable> GetRaceDataAsync(long wellId, long? raceId, int rowLimit, CancellationToken ct = default)
        => GatedAsync(() => ReadRaceDataAsync(wellId, raceId, rowLimit, ct), ct);

    private async Task<DataTable> ReadRaceDataAsync(long wellId, long? raceId, int rowLimit, CancellationToken ct)
    {
        if (rowLimit <= 0) rowLimit = 1000;
        var raceFilter = raceId.HasValue ? "AND r.RACE_ID = @raceId" : string.Empty;

        // Wide row containing the most useful drilling parameters from REC_HEADERS,
        // REC_COMMON and REC_LAG. All friendly Russian names are mapped in FriendlyNames.
        var sql = $@"
            SELECT FIRST {rowLimit}{RaceDataColumns}{RaceDataFrom}
            WHERE r.WELL_ID = @wellId
              {raceFilter}
            ORDER BY h.REC_HEADER_ID DESC";

        return await FillRaceDataAsync(sql, wellId, raceId, null, ct).ConfigureAwait(false);
    }

    public Task<DataTable> GetRaceDataSinceAsync(long wellId, long? raceId, long afterRecHeaderId, int maxRows, CancellationToken ct = default)
        => GatedAsync(() => ReadRaceDataSinceAsync(wellId, raceId, afterRecHeaderId, maxRows, ct), ct);

    private async Task<DataTable> ReadRaceDataSinceAsync(long wellId, long? raceId, long afterRecHeaderId, int maxRows, CancellationToken ct)
    {
        if (maxRows <= 0) maxRows = 1000;
        var raceFilter = raceId.HasValue ? "AND r.RACE_ID = @raceId" : string.Empty;

        // Oldest-first, unlike the full read: FIRST then keeps the rows immediately after
        // the watermark, so a burst that exceeds maxRows is delivered in order over the
        // following polls instead of leaving a hole in the middle of the history.
        var sql = $@"
            SELECT FIRST {maxRows}{RaceDataColumns}{RaceDataFrom}
            WHERE r.WELL_ID = @wellId
              AND h.REC_HEADER_ID > @afterId
              {raceFilter}
            ORDER BY h.REC_HEADER_ID";

        return await FillRaceDataAsync(sql, wellId, raceId, afterRecHeaderId, ct).ConfigureAwait(false);
    }

    private async Task<DataTable> FillRaceDataAsync(
        string sql, long wellId, long? raceId, long? afterRecHeaderId, CancellationToken ct)
    {
        var dt = new DataTable("RaceData");
        await using var cmd = new FbCommand(sql, _connection);
        cmd.Parameters.AddWithValue("wellId", wellId);
        if (raceId.HasValue) cmd.Parameters.AddWithValue("raceId", raceId.Value);
        if (afterRecHeaderId.HasValue) cmd.Parameters.AddWithValue("afterId", afterRecHeaderId.Value);
        using var adapter = new FbDataAdapter(cmd);
        await Task.Run(() => adapter.Fill(dt), ct).ConfigureAwait(false);
        return dt;
    }

    public Task<DataTable> GetOperationsAsync(long wellId, long? raceId, CancellationToken ct = default)
        => GatedAsync(() => ReadOperationsAsync(wellId, raceId, ct), ct);

    private async Task<DataTable> ReadOperationsAsync(long wellId, long? raceId, CancellationToken ct)
    {
        // Filter by RACE_ID when one is selected. RACES is joined just for the predicate.
        var raceJoin   = raceId.HasValue ? "JOIN RACES r ON r.WELL_ID = o.WELL_ID AND r.RACE_ID = @raceId" : "";
        var raceFilter = raceId.HasValue ? "AND o.START_TIME >= r.START_TIME AND (r.STOP_TIME IS NULL OR o.START_TIME <= r.STOP_TIME)" : "";

        var sql = $@"
            SELECT FIRST 500
                   o.REC_ID                                         AS OP_NUMBER,
                   work_t.NAME                                      AS WORK_KIND_NAME,
                   COALESCE(user_t.NAME, oper_t.NAME)               AS OPER_NAME,
                   sub_t.NAME                                       AS SUB_OPER_NAME,
                   o.START_TIME                                     AS OP_START,
                   o.STOP_TIME                                      AS OP_STOP,
                   CASE
                     WHEN o.START_TIME IS NOT NULL AND o.STOP_TIME IS NOT NULL
                       THEN CAST((o.STOP_TIME - o.START_TIME) * 24 AS DOUBLE PRECISION)
                     ELSE NULL
                   END                                              AS OP_DURATION_HOURS,
                   o.COMMENT                                        AS OP_COMMENT
            FROM OPERATIONS o
            LEFT JOIN WORK_TYPES    work_t ON work_t.WORK_ID   = o.WORK_ID
            LEFT JOIN OPER_TYPES    oper_t ON oper_t.OPER_ID   = o.OPER_ID
            LEFT JOIN OPER_TYPES    user_t ON user_t.OPER_ID   = o.USER_OPER_ID
            LEFT JOIN SUBOPER_TYPES sub_t  ON sub_t.SUBOPER_ID = o.SUBOPER_ID
            {raceJoin}
            WHERE o.WELL_ID = @wellId
              {raceFilter}
            ORDER BY COALESCE(o.START_TIME, CURRENT_TIMESTAMP) DESC, o.REC_ID DESC";

        var dt = new DataTable("Operations");
        await using var cmd = new FbCommand(sql, _connection);
        cmd.Parameters.AddWithValue("wellId", wellId);
        if (raceId.HasValue) cmd.Parameters.AddWithValue("raceId", raceId.Value);
        using var adapter = new FbDataAdapter(cmd);
        await Task.Run(() => adapter.Fill(dt), ct).ConfigureAwait(false);
        return dt;
    }

    public Task<DataTable> GetToolsAsync(long wellId, long? raceId, CancellationToken ct = default)
        => GatedAsync(() => ReadToolsAsync(wellId, raceId, ct), ct);

    private async Task<DataTable> ReadToolsAsync(long wellId, long? raceId, CancellationToken ct)
    {
        var raceFilter = raceId.HasValue ? "AND b.RACE_ID = @raceId" : string.Empty;

        // Инструмент = компоновка бурильной колонны по рейсам. BOTTOM_HOLE_ASSEMBLY хранит
        // позиции компоновки, DRILL_STRING_ITEM_TYPE — тип элемента («Свеча», «Долото», «УБТ»,
        // «Забойный двигатель»…), DRILL_STRING_ITEM — конкретный типоразмер (марку) с его
        // размерами. Размеры, проставленные в самой компоновке, имеют приоритет над
        // справочными; там, где компоновка их не задаёт, берутся из справочника.
        //
        // Суммарные колонки считаются по строке: длина × количество, а вес — из веса
        // погонного метра (кг) × суммарную длину, переведённый в тонны.
        var sql = $@"
            SELECT FIRST 500
                   b.POS                                        AS TL_POS,
                   t.NAME                                       AS TL_NAME,
                   i.NAME                                       AS TL_BRAND,
                   COALESCE(b.NMBR_OF_ITEMS, 1)                 AS TL_COUNT,
                   COALESCE(b.DIAMETER, i.DIAMETER)             AS TL_DIAMETER,
                   COALESCE(b.WALL_THICKNESS, i.WALL_THICKNESS) AS TL_WALL,
                   COALESCE(b.WEIGHT, i.WEIGHT)                 AS TL_WEIGHT_M,
                   COALESCE(b.LEN, i.LEN)                       AS TL_LEN,
                   CAST(COALESCE(b.LEN, i.LEN)
                        * COALESCE(b.NMBR_OF_ITEMS, 1) AS DOUBLE PRECISION)        AS TL_TOTAL_LEN,
                   CAST(COALESCE(b.WEIGHT, i.WEIGHT) * COALESCE(b.LEN, i.LEN)
                        * COALESCE(b.NMBR_OF_ITEMS, 1) / 1000 AS DOUBLE PRECISION) AS TL_TOTAL_WEIGHT
            FROM BOTTOM_HOLE_ASSEMBLY b
            JOIN RACES r ON r.RACE_ID = b.RACE_ID
            LEFT JOIN DRILL_STRING_ITEM_TYPE t ON t.DSIT_ID = b.DSIT_ID
            LEFT JOIN DRILL_STRING_ITEM      i ON i.DSI_ID  = b.DSI_ID
            WHERE r.WELL_ID = @wellId
              {raceFilter}
            ORDER BY b.RACE_ID DESC, b.POS";

        var dt = new DataTable("Tools");
        await using var cmd = new FbCommand(sql, _connection);
        cmd.Parameters.AddWithValue("wellId", wellId);
        if (raceId.HasValue) cmd.Parameters.AddWithValue("raceId", raceId.Value);
        using var adapter = new FbDataAdapter(cmd);
        await Task.Run(() => adapter.Fill(dt), ct).ConfigureAwait(false);
        return dt;
    }

    public Task<IReadOnlyList<ParamCatalogRow>> GetParameterCatalogAsync(CancellationToken ct = default)
        => GatedAsync(() => ReadParameterCatalogAsync(ct), ct);

    private async Task<IReadOnlyList<ParamCatalogRow>> ReadParameterCatalogAsync(CancellationToken ct)
    {
        const string sql = @"
            SELECT REGISTR_VAR, FULL_NAME, SHORT_NAME, PARAM_UNIT, TABLE_NAME, TABLE_FIELD
            FROM PARAMS";
        var list = new List<ParamCatalogRow>();
        await using var cmd = new FbCommand(sql, _connection);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new ParamCatalogRow
            {
                RegistrVar = reader.IsDBNull(0) ? "" : reader.GetString(0).Trim(),
                FullName   = reader.IsDBNull(1) ? "" : reader.GetString(1).Trim(),
                ShortName  = reader.IsDBNull(2) ? "" : reader.GetString(2).Trim(),
                Unit       = reader.IsDBNull(3) ? "" : reader.GetString(3).Trim(),
                TableName  = reader.IsDBNull(4) ? "" : reader.GetString(4).Trim(),
                TableField = reader.IsDBNull(5) ? "" : reader.GetString(5).Trim(),
            });
        }
        return list;
    }

    // ===== Serialization =====

    /// <summary>
    /// Run one database call with the connection to itself. Waiting on the gate is the
    /// whole point: a real-time poll that arrives mid-query queues behind it instead of
    /// corrupting the conversation both are having with the server.
    /// </summary>
    private async Task<T> GatedAsync<T>(Func<Task<T>> read, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureConnected();
            return await read().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    // ===== Disposal =====

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
        _gate.Dispose();
    }

    private void EnsureConnected()
    {
        if (!IsConnected || _connection is null)
            throw new InvalidOperationException("Not connected to a database.");
    }
}
