using System.Collections.Generic;
using System.Globalization;
using FirebirdViewer.Localization;
using FirebirdViewer.Models;

namespace FirebirdViewer.Metadata;

public sealed record ColumnInfo(string DisplayName, string Unit, string Description);

/// <summary>
/// Converts raw Firebird column identifiers into labels an end user can read, in the
/// current interface language.
///
/// Parameter columns (REC_COMMON / REC_LAG) are described by the live PARAMS catalog.
/// In Russian its FULL_NAME is shown exactly as stored. In other languages the name is
/// looked up in the translation files by PARAMS.TABLE_FIELD (keys <c>Param_&lt;field&gt;</c>)
/// and the database's Russian name stays in the tooltip; a parameter nobody translated
/// yet keeps its Russian name rather than turning into a raw identifier.
///
/// The few columns PARAMS does not describe (record time and depths, and the aliases of
/// the Инструменты / Операции queries) take their labels from <c>Col_&lt;FIELD&gt;</c> keys.
/// </summary>
public static class FriendlyNames
{
    public static ColumnInfo? GetColumn(string? tableName, string? columnName)
    {
        if (string.IsNullOrWhiteSpace(columnName)) return null;
        var col = columnName.Trim();

        // 1) PARAMS table loaded from the live database — most authoritative source.
        if (!string.IsNullOrWhiteSpace(tableName)
            && _dynamicByTableField.TryGetValue((tableName.Trim(), col), out var fromParams1))
        {
            return FromCatalog(fromParams1);
        }
        if (_dynamicByField.TryGetValue(col, out var fromParams2))
        {
            return FromCatalog(fromParams2);
        }

        // 2) Built-in per-table columns (TOOLS_VIEW, OPERATIONS_VIEW).
        if (!string.IsNullOrWhiteSpace(tableName)
            && Columns.TryGetValue(tableName.Trim(), out var perTable)
            && perTable.TryGetValue(col, out var spec))
        {
            return FromResources(spec);
        }

        // 3) Record metadata present in every race-data table (ID, time, depths).
        if (Common.TryGetValue(col, out var common)) return FromResources(common);

        // 4) Last resort: scan every per-table dictionary so joined views still resolve.
        if (string.IsNullOrWhiteSpace(tableName))
        {
            foreach (var dict in Columns.Values)
                if (dict.TryGetValue(col, out var any)) return FromResources(any);
        }

        // 5) No catalog row (legacy database without PARAMS): a translation keyed by the
        //    field name still beats the raw identifier.
        var translated = TranslateParameter(col);
        return translated is null ? null : new ColumnInfo(translated, "", "");
    }

    public static string GetColumnDisplay(string? tableName, string? columnName)
    {
        var info = GetColumn(tableName, columnName);
        if (info is null) return Prettify(columnName ?? string.Empty);
        return string.IsNullOrEmpty(info.Unit)
            ? info.DisplayName
            : $"{info.DisplayName}, {info.Unit}";
    }

    public static string? GetColumnDescription(string? tableName, string? columnName)
    {
        var description = GetColumn(tableName, columnName)?.Description;
        return string.IsNullOrWhiteSpace(description) ? null : description;
    }

    /// <summary>Friendly fallback for any unknown identifier: lower-case, replace _ with space, capitalize first letter.</summary>
    public static string Prettify(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return raw ?? string.Empty;
        var s = raw.Trim().Replace('_', ' ');
        // For typical SQL identifiers this gives "MUD_PRESSURE" → "Mud pressure".
        if (s.Length == 0) return s;
        s = s.ToLower(CultureInfo.CurrentCulture);
        return char.ToUpper(s[0], CultureInfo.CurrentCulture) + s.Substring(1);
    }

    // ============================================================
    // Translation helpers
    // ============================================================

    /// <summary>
    /// Translated parameter name for a PARAMS.TABLE_FIELD, or null in Russian (the database
    /// name is used as is) and when no translation exists. A numbered field such as
    /// <c>volume7</c> falls back to its family key <c>Param_volume_N</c> with {0} = 7.
    /// </summary>
    public static string? TranslateParameter(string? field)
    {
        if (Loc.IsNeutral || string.IsNullOrWhiteSpace(field)) return null;
        var f = field.Trim().ToLowerInvariant();

        var exact = Loc.TryT("Param_" + f);
        if (exact is not null) return exact;

        int digits = f.Length;
        while (digits > 0 && char.IsAsciiDigit(f[digits - 1])) digits--;
        if (digits == 0 || digits == f.Length) return null;

        var family = Loc.TryT("Param_" + f[..digits] + "_N");
        return family is null ? null : string.Format(Loc.Culture, family, f[digits..]);
    }

    /// <summary>
    /// Unit in the current language. Units are keyed by the Russian spelling, hex-encoded
    /// because they contain characters a resource key cannot (<c>/ * ° ³</c>): «м/ч» →
    /// <c>Unit_043C002F0447</c>. A unit without a translation is shown as stored.
    /// </summary>
    public static string TranslateUnit(string? unit)
    {
        if (string.IsNullOrWhiteSpace(unit)) return unit ?? "";
        if (Loc.IsNeutral) return unit;

        var trimmed = unit.Trim();
        var key = new System.Text.StringBuilder("Unit_", 5 + trimmed.Length * 4);
        foreach (var ch in trimmed) key.Append(((int)ch).ToString("X4", CultureInfo.InvariantCulture));
        return Loc.TryT(key.ToString()) ?? unit;
    }

    private static ColumnInfo FromCatalog(ParamCatalogRow r)
    {
        var dbName = string.IsNullOrWhiteSpace(r.FullName) ? r.RegistrVar : r.FullName;
        var dbDescription = string.IsNullOrWhiteSpace(r.ShortName)
            ? (r.FullName ?? "")
            : $"{r.FullName} ({r.ShortName})";

        if (Loc.IsNeutral) return new ColumnInfo(dbName, r.Unit ?? "", dbDescription);

        var unit = TranslateUnit(r.Unit);
        var translated = TranslateParameter(r.TableField);
        if (translated is null) return new ColumnInfo(dbName, unit, dbDescription);

        // The Russian name stays in the tooltip: it is what GtiRealtimeCharts and the
        // crew's paperwork call this parameter.
        return new ColumnInfo(translated, unit, $"{translated}\n{dbDescription}");
    }

    private static ColumnInfo FromResources(ColumnSpec spec) =>
        new(Loc.T(spec.Key), TranslateUnit(spec.Unit), Loc.TryT(spec.Key + "_Desc") ?? "");

    // ============================================================
    // Dynamic catalog (populated from the PARAMS table at connect time)
    // ============================================================

    // The raw rows are kept rather than finished labels, so a language switch only needs
    // the lookup to run again — no second trip to the database.
    private static readonly Dictionary<(string Table, string Field), ParamCatalogRow> _dynamicByTableField
        = new(new TableFieldComparer());
    private static readonly Dictionary<string, ParamCatalogRow> _dynamicByField
        = new(System.StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Replace the dynamic catalog with the contents of the PARAMS table.
    /// Called by <c>MainViewModel</c> once per successful connection.
    /// </summary>
    public static void LoadFromParams(IEnumerable<ParamCatalogRow> rows)
    {
        _dynamicByTableField.Clear();
        _dynamicByField.Clear();
        if (rows is null) return;

        foreach (var r in rows)
        {
            if (string.IsNullOrWhiteSpace(r.TableField)) continue;

            _dynamicByField[r.TableField] = r;
            if (!string.IsNullOrWhiteSpace(r.TableName))
                _dynamicByTableField[(r.TableName, r.TableField)] = r;
        }
    }

    public static void ClearDynamicCatalog()
    {
        _dynamicByTableField.Clear();
        _dynamicByField.Clear();
    }

    private sealed class TableFieldComparer : IEqualityComparer<(string Table, string Field)>
    {
        public bool Equals((string Table, string Field) x, (string Table, string Field) y)
            => System.StringComparer.OrdinalIgnoreCase.Equals(x.Table, y.Table)
            && System.StringComparer.OrdinalIgnoreCase.Equals(x.Field, y.Field);

        public int GetHashCode((string Table, string Field) obj)
        {
            var h1 = System.StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Table ?? "");
            var h2 = System.StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Field ?? "");
            return h1 * 397 ^ h2;
        }
    }

    // ============================================================
    // Built-in columns. The label is the resource Key, the tooltip Key + "_Desc";
    // the unit is the Russian spelling and goes through TranslateUnit like a PARAMS unit.
    // ============================================================

    private sealed record ColumnSpec(string Key, string Unit);

    private static ColumnSpec Spec(string field, string unit = "") => new("Col_" + field, unit);

    /// <summary>Record metadata that the race-data query returns next to the parameters.</summary>
    private static readonly Dictionary<string, ColumnSpec> Common = new(System.StringComparer.OrdinalIgnoreCase)
    {
        ["REC_HEADER_ID"]    = Spec("REC_HEADER_ID"),
        ["REC_TIME"]         = Spec("REC_TIME"),
        ["BOTTOM_DEPTH"]     = Spec("BOTTOM_DEPTH", "м"),
        ["BIT_DEPTH"]        = Spec("BIT_DEPTH", "м"),
        ["BOTTOM_LAG_DEPTH"] = Spec("BOTTOM_LAG_DEPTH", "м"),
    };

    private static readonly Dictionary<string, Dictionary<string, ColumnSpec>> Columns = new(System.StringComparer.OrdinalIgnoreCase)
    {
        // Synthetic schema for the Инструменты tab. GetToolsAsync aliases its result
        // columns to these names; the underlying tables are BOTTOM_HOLE_ASSEMBLY (компоновка),
        // DRILL_STRING_ITEM_TYPE (типы элементов) и DRILL_STRING_ITEM (типоразмеры).
        ["TOOLS_VIEW"] = new(System.StringComparer.OrdinalIgnoreCase)
        {
            ["TL_POS"]          = Spec("TL_POS"),
            ["TL_NAME"]         = Spec("TL_NAME"),
            ["TL_BRAND"]        = Spec("TL_BRAND"),
            ["TL_COUNT"]        = Spec("TL_COUNT"),
            ["TL_DIAMETER"]     = Spec("TL_DIAMETER", "мм"),
            ["TL_WALL"]         = Spec("TL_WALL", "мм"),
            ["TL_WEIGHT_M"]     = Spec("TL_WEIGHT_M", "кг"),
            ["TL_LEN"]          = Spec("TL_LEN", "м"),
            ["TL_TOTAL_LEN"]    = Spec("TL_TOTAL_LEN", "м"),
            ["TL_TOTAL_WEIGHT"] = Spec("TL_TOTAL_WEIGHT", "т"),
        },

        // Synthetic schema for the Операции tab. GetOperationsAsync aliases its result
        // columns to these names.
        ["OPERATIONS_VIEW"] = new(System.StringComparer.OrdinalIgnoreCase)
        {
            ["OP_NUMBER"]         = Spec("OP_NUMBER"),
            ["WORK_KIND_NAME"]    = Spec("WORK_KIND_NAME"),
            ["OPER_NAME"]         = Spec("OPER_NAME"),
            ["SUB_OPER_NAME"]     = Spec("SUB_OPER_NAME"),
            ["OP_START"]          = Spec("OP_START"),
            ["OP_STOP"]           = Spec("OP_STOP"),
            ["OP_DURATION_HOURS"] = Spec("OP_DURATION_HOURS", "час"),
            ["OP_COMMENT"]        = Spec("OP_COMMENT"),
        },
    };
}
