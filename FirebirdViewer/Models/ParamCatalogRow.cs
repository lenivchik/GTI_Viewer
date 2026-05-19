namespace FirebirdViewer.Models;

/// <summary>
/// One row from the PARAMS table — the database's own catalog of parameter names,
/// units and source columns. We load this on connect so the UI always reflects the
/// actual database instead of a hardcoded list.
/// </summary>
public sealed class ParamCatalogRow
{
    public string RegistrVar { get; init; } = "";
    public string FullName   { get; init; } = "";
    public string ShortName  { get; init; } = "";
    public string Unit       { get; init; } = "";
    public string TableName  { get; init; } = "";
    public string TableField { get; init; } = "";
}
