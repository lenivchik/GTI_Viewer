using System.Data;
using FirebirdViewer.Models;

namespace FirebirdViewer.Services;

public interface IFirebirdService : IAsyncDisposable
{
    bool IsConnected { get; }
    ConnectionSettings? Current { get; }

    Task ConnectAsync(ConnectionSettings settings, CancellationToken ct = default);
    Task DisconnectAsync();

    // ----- Generic listings (kept for completeness; UI no longer exposes them by default) -----
    Task<IReadOnlyList<DatabaseObject>> GetTablesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<DatabaseObject>> GetViewsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<DatabaseObject>> GetProceduresAsync(CancellationToken ct = default);
    Task<DataTable> GetTableDataAsync(string objectName, int rowLimit, CancellationToken ct = default);
    Task<DataTable> GetColumnsAsync(string tableName, CancellationToken ct = default);
    Task<DataTable> GetIndexesAsync(string tableName, CancellationToken ct = default);

    // ----- GTI-oriented queries -----
    /// <summary>Wells (скважины) from the WELLBORES table.</summary>
    Task<IReadOnlyList<WellInfo>> GetWellsAsync(CancellationToken ct = default);
    /// <summary>Races (рейсы) for a given well, newest first.</summary>
    Task<IReadOnlyList<RaceInfo>> GetRacesAsync(long wellId, CancellationToken ct = default);
    /// <summary>Wide drilling data: REC_HEADERS joined with REC_COMMON and REC_LAG, filtered by well/race.</summary>
    Task<DataTable> GetRaceDataAsync(long wellId, long? raceId, int rowLimit, CancellationToken ct = default);
    /// <summary>Operations (операции) for a given well, optionally restricted to a race.</summary>
    Task<DataTable> GetOperationsAsync(long wellId, long? raceId, CancellationToken ct = default);
    /// <summary>Parameter catalog from the PARAMS table — friendly names, units, source columns.</summary>
    Task<IReadOnlyList<ParamCatalogRow>> GetParameterCatalogAsync(CancellationToken ct = default);

    /// <summary>Executes a SELECT/WITH and returns a result set.</summary>
    Task<DataTable> ExecuteQueryAsync(string sql, CancellationToken ct = default);
    /// <summary>Executes a non-query (INSERT/UPDATE/DELETE/DDL) and returns affected rows.</summary>
    Task<int> ExecuteNonQueryAsync(string sql, CancellationToken ct = default);

    /// <summary>Server version string, populated after Connect.</summary>
    string? ServerVersion { get; }
}
