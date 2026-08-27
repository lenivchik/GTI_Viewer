using System.Data;
using FirebirdViewer.Models;

namespace FirebirdViewer.Services;

/// <summary>
/// Data access for the GTI database. This is the only abstraction the
/// view-models see — nothing above this layer references FirebirdSql types.
/// </summary>
public interface IFirebirdService : IAsyncDisposable
{
    bool IsConnected { get; }

    /// <summary>Server version string, populated after Connect.</summary>
    string? ServerVersion { get; }

    Task ConnectAsync(ConnectionSettings settings, CancellationToken ct = default);
    Task DisconnectAsync();

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
}
