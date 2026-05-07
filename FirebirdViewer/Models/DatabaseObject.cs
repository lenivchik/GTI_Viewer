namespace FirebirdViewer.Models;

public enum DatabaseObjectKind
{
    Table,
    View,
    Procedure
}

/// <summary>
/// A named object inside a Firebird database (table, view, or stored procedure).
/// </summary>
public sealed class DatabaseObject
{
    public string Name { get; init; } = "";
    public DatabaseObjectKind Kind { get; init; }

    public override string ToString() => Name;
}
