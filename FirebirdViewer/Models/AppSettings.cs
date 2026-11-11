namespace FirebirdViewer.Models;

/// <summary>
/// Root JSON object persisted to %APPDATA%\FirebirdViewer\settings.json.
/// </summary>
public sealed class AppSettings
{
    public ConnectionSettings? LastConnection { get; set; }
    public List<ConnectionSettings> RecentConnections { get; set; } = new();
    public int RowLimit { get; set; } = 1000;

    /// <summary>Re-arm real-time mode on the next connection.</summary>
    public bool LiveMode { get; set; }

    /// <summary>Seconds between real-time polls.</summary>
    public int LiveIntervalSeconds { get; set; } = 5;

    /// <summary>Interface language code ("ru", "kk", "uz-Latn"); null until the user picks one,
    /// in which case the Windows display language decides.</summary>
    public string? Language { get; set; }
}
