namespace FirebirdViewer.Models;

/// <summary>
/// Connection parameters for a Firebird database.
/// </summary>
public sealed class ConnectionSettings
{
    public string Server { get; set; } = "localhost";
    public int Port { get; set; } = 3050;
    public string Database { get; set; } = "";
    public string UserName { get; set; } = "SYSDBA";
    public string Charset { get; set; } = "UTF8";

    /// <summary>Password is intentionally excluded from saved settings.</summary>
    public string Password { get; set; } = "";

    public string Display => string.IsNullOrEmpty(Database)
        ? $"{UserName}@{Server}:{Port}"
        : $"{UserName}@{Server}:{Port} — {Database}";

    public ConnectionSettings Clone() => new()
    {
        Server = Server,
        Port = Port,
        Database = Database,
        UserName = UserName,
        Charset = Charset,
        Password = Password
    };

    /// <summary>Equality on everything that uniquely identifies a connection (no password).</summary>
    public bool MatchesIdentity(ConnectionSettings other) =>
        other is not null
        && string.Equals(Server, other.Server, StringComparison.OrdinalIgnoreCase)
        && Port == other.Port
        && string.Equals(Database, other.Database, StringComparison.OrdinalIgnoreCase)
        && string.Equals(UserName, other.UserName, StringComparison.OrdinalIgnoreCase);
}
