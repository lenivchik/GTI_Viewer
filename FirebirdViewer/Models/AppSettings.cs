namespace FirebirdViewer.Models;

/// <summary>
/// Root JSON object persisted to %APPDATA%\FirebirdViewer\settings.json.
/// </summary>
public sealed class AppSettings
{
    public ConnectionSettings? LastConnection { get; set; }
    public List<ConnectionSettings> RecentConnections { get; set; } = new();
    public int RowLimit { get; set; } = 1000;
}
