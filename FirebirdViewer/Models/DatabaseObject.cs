using FirebirdViewer.Metadata;

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

    /// <summary>Friendly Russian label shown in the UI.</summary>
    public string DisplayName => FriendlyNames.GetTableDisplay(Name);

    /// <summary>Tooltip describing the object (or null when no description is known).</summary>
    public string? Description => FriendlyNames.GetTable(Name)?.Description;

    public override string ToString() => DisplayName;
}
