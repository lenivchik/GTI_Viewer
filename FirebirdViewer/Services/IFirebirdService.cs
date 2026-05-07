using System.Data;
using FirebirdViewer.Models;

namespace FirebirdViewer.Services;

public interface IFirebirdService : IAsyncDisposable
{
    bool IsConnected { get; }
    ConnectionSettings? Current { get; }

    Task ConnectAsync(ConnectionSettings settings, CancellationToken ct = default);
    Task DisconnectAsync();

    Task<IReadOnlyList<DatabaseObject>> GetTablesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<DatabaseObject>> GetViewsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<DatabaseObject>> GetProceduresAsync(CancellationToken ct = default);

    Task<DataTable> GetTableDataAsync(string objectName, int rowLimit, CancellationToken ct = default);
    Task<DataTable> GetColumnsAsync(string tableName, CancellationToken ct = default);
    Task<DataTable> GetIndexesAsync(string tableName, CancellationToken ct = default);

    /// <summary>Executes a SELECT/WITH and returns a result set.</summary>
    Task<DataTable> ExecuteQueryAsync(string sql, CancellationToken ct = default);
    /// <summary>Executes a non-query (INSERT/UPDATE/DELETE/DDL) and returns affected rows.</summary>
    Task<int> ExecuteNonQueryAsync(string sql, CancellationToken ct = default);

    /// <summary>Server version string, populated after Connect.</summary>
    string? ServerVersion { get; }
}
