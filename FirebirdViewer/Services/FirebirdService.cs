using System.Data;
using FirebirdSql.Data.FirebirdClient;
using FirebirdViewer.Models;

namespace FirebirdViewer.Services;

public sealed class FirebirdService : IFirebirdService
{
    private FbConnection? _connection;

    public bool IsConnected => _connection?.State == ConnectionState.Open;
    public ConnectionSettings? Current { get; private set; }
    public string? ServerVersion { get; private set; }

    public async Task ConnectAsync(ConnectionSettings settings, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        await DisconnectAsync().ConfigureAwait(false);

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
        Current = settings.Clone();
    }

    public async Task DisconnectAsync()
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
            Current = null;
            ServerVersion = null;
        }
    }

    // ===== Object listings =====

    private async Task<IReadOnlyList<DatabaseObject>> ListAsync(string sql, DatabaseObjectKind kind, CancellationToken ct)
    {
        EnsureConnected();
        var list = new List<DatabaseObject>();
        await using var cmd = new FbCommand(sql, _connection);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new DatabaseObject { Name = reader.GetString(0), Kind = kind });
        }
        return list;
    }

    public Task<IReadOnlyList<DatabaseObject>> GetTablesAsync(CancellationToken ct = default) =>
        ListAsync(@"
            SELECT TRIM(RDB$RELATION_NAME)
            FROM RDB$RELATIONS
            WHERE RDB$VIEW_BLR IS NULL
              AND (RDB$SYSTEM_FLAG IS NULL OR RDB$SYSTEM_FLAG = 0)
            ORDER BY 1",
            DatabaseObjectKind.Table, ct);

    public Task<IReadOnlyList<DatabaseObject>> GetViewsAsync(CancellationToken ct = default) =>
        ListAsync(@"
            SELECT TRIM(RDB$RELATION_NAME)
            FROM RDB$RELATIONS
            WHERE RDB$VIEW_BLR IS NOT NULL
              AND (RDB$SYSTEM_FLAG IS NULL OR RDB$SYSTEM_FLAG = 0)
            ORDER BY 1",
            DatabaseObjectKind.View, ct);

    public Task<IReadOnlyList<DatabaseObject>> GetProceduresAsync(CancellationToken ct = default) =>
        ListAsync(@"
            SELECT TRIM(RDB$PROCEDURE_NAME)
            FROM RDB$PROCEDURES
            WHERE (RDB$SYSTEM_FLAG IS NULL OR RDB$SYSTEM_FLAG = 0)
            ORDER BY 1",
            DatabaseObjectKind.Procedure, ct);

    // ===== Data / metadata =====

    public async Task<DataTable> GetTableDataAsync(string objectName, int rowLimit, CancellationToken ct = default)
    {
        EnsureConnected();
        if (string.IsNullOrWhiteSpace(objectName)) throw new ArgumentException("Empty object name.", nameof(objectName));
        if (rowLimit <= 0) rowLimit = 1000;

        // Quote identifiers to handle case-sensitive names safely.
        var safeName = objectName.Replace("\"", "\"\"");
        var sql = $"SELECT FIRST {rowLimit} * FROM \"{safeName}\"";

        var dt = new DataTable(objectName);
        await using var cmd = new FbCommand(sql, _connection);
        using var adapter = new FbDataAdapter(cmd);
        await Task.Run(() => adapter.Fill(dt), ct).ConfigureAwait(false);
        return dt;
    }

    public async Task<DataTable> GetColumnsAsync(string tableName, CancellationToken ct = default)
    {
        EnsureConnected();
        const string sql = @"
            SELECT
                rf.RDB$FIELD_POSITION + 1                              AS POS,
                TRIM(rf.RDB$FIELD_NAME)                                AS FIELD_NAME,
                CASE f.RDB$FIELD_TYPE
                    WHEN 7   THEN 'SMALLINT'
                    WHEN 8   THEN 'INTEGER'
                    WHEN 10  THEN 'FLOAT'
                    WHEN 12  THEN 'DATE'
                    WHEN 13  THEN 'TIME'
                    WHEN 14  THEN 'CHAR'
                    WHEN 16  THEN 'BIGINT'
                    WHEN 23  THEN 'BOOLEAN'
                    WHEN 27  THEN 'DOUBLE PRECISION'
                    WHEN 35  THEN 'TIMESTAMP'
                    WHEN 37  THEN 'VARCHAR'
                    WHEN 261 THEN 'BLOB'
                    ELSE 'OTHER (' || f.RDB$FIELD_TYPE || ')'
                END                                                   AS FIELD_TYPE,
                f.RDB$FIELD_LENGTH                                    AS LENGTH,
                f.RDB$FIELD_PRECISION                                 AS PRECISION_,
                f.RDB$FIELD_SCALE                                     AS SCALE_,
                CASE WHEN COALESCE(rf.RDB$NULL_FLAG, 0) = 1 THEN 'NO' ELSE 'YES' END AS NULLABLE,
                CASE
                    WHEN EXISTS (
                        SELECT 1
                        FROM RDB$RELATION_CONSTRAINTS rc
                        JOIN RDB$INDEX_SEGMENTS s ON rc.RDB$INDEX_NAME = s.RDB$INDEX_NAME
                        WHERE rc.RDB$RELATION_NAME = rf.RDB$RELATION_NAME
                          AND rc.RDB$CONSTRAINT_TYPE = 'PRIMARY KEY'
                          AND s.RDB$FIELD_NAME = rf.RDB$FIELD_NAME
                    ) THEN 'PK' ELSE ''
                END                                                   AS PRIMARY_KEY,
                TRIM(rf.RDB$DEFAULT_SOURCE)                           AS DEFAULT_VALUE
            FROM RDB$RELATION_FIELDS rf
            JOIN RDB$FIELDS f ON rf.RDB$FIELD_SOURCE = f.RDB$FIELD_NAME
            WHERE rf.RDB$RELATION_NAME = @t
            ORDER BY rf.RDB$FIELD_POSITION";

        var dt = new DataTable("Columns");
        await using var cmd = new FbCommand(sql, _connection);
        cmd.Parameters.Add("@t", FbDbType.VarChar).Value = tableName;
        using var adapter = new FbDataAdapter(cmd);
        await Task.Run(() => adapter.Fill(dt), ct).ConfigureAwait(false);
        return dt;
    }

    public async Task<DataTable> GetIndexesAsync(string tableName, CancellationToken ct = default)
    {
        EnsureConnected();
        const string sql = @"
            SELECT
                TRIM(i.RDB$INDEX_NAME)                                          AS INDEX_NAME,
                CASE COALESCE(i.RDB$UNIQUE_FLAG, 0) WHEN 1 THEN 'YES' ELSE 'NO' END AS IS_UNIQUE,
                CASE COALESCE(i.RDB$INDEX_TYPE, 0)  WHEN 1 THEN 'DESC' ELSE 'ASC' END AS DIRECTION,
                TRIM(s.RDB$FIELD_NAME)                                          AS FIELD_NAME,
                s.RDB$FIELD_POSITION + 1                                        AS POS,
                CASE
                    WHEN rc.RDB$CONSTRAINT_TYPE = 'PRIMARY KEY' THEN 'PRIMARY KEY'
                    WHEN rc.RDB$CONSTRAINT_TYPE = 'UNIQUE'      THEN 'UNIQUE'
                    WHEN rc.RDB$CONSTRAINT_TYPE = 'FOREIGN KEY' THEN 'FOREIGN KEY'
                    ELSE ''
                END                                                             AS CONSTRAINT_TYPE
            FROM RDB$INDICES i
            JOIN RDB$INDEX_SEGMENTS s ON s.RDB$INDEX_NAME = i.RDB$INDEX_NAME
            LEFT JOIN RDB$RELATION_CONSTRAINTS rc ON rc.RDB$INDEX_NAME = i.RDB$INDEX_NAME
            WHERE i.RDB$RELATION_NAME = @t
              AND (i.RDB$SYSTEM_FLAG IS NULL OR i.RDB$SYSTEM_FLAG = 0)
            ORDER BY i.RDB$INDEX_NAME, s.RDB$FIELD_POSITION";

        var dt = new DataTable("Indexes");
        await using var cmd = new FbCommand(sql, _connection);
        cmd.Parameters.Add("@t", FbDbType.VarChar).Value = tableName;
        using var adapter = new FbDataAdapter(cmd);
        await Task.Run(() => adapter.Fill(dt), ct).ConfigureAwait(false);
        return dt;
    }

    public async Task<DataTable> ExecuteQueryAsync(string sql, CancellationToken ct = default)
    {
        EnsureConnected();
        var dt = new DataTable("Result");
        await using var cmd = new FbCommand(sql, _connection);
        using var adapter = new FbDataAdapter(cmd);
        await Task.Run(() => adapter.Fill(dt), ct).ConfigureAwait(false);
        return dt;
    }

    public async Task<int> ExecuteNonQueryAsync(string sql, CancellationToken ct = default)
    {
        EnsureConnected();
        await using var cmd = new FbCommand(sql, _connection);
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    // ===== Disposal =====

    public async ValueTask DisposeAsync() => await DisconnectAsync().ConfigureAwait(false);

    private void EnsureConnected()
    {
        if (!IsConnected || _connection is null)
            throw new InvalidOperationException("Not connected to a database.");
    }
}
