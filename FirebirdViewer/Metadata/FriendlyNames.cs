using System.Collections.Generic;
using System.Globalization;
using FirebirdViewer.Models;

namespace FirebirdViewer.Metadata;

public sealed record ColumnInfo(string DisplayName, string Unit, string Description);

public sealed record TableInfo(string DisplayName, string Description);

/// <summary>
/// Converts raw Firebird table/column identifiers into friendly Russian
/// labels suitable for end users. Names are taken from the GtiRealtimeCharts
/// parameter catalog so the two applications agree on terminology.
/// </summary>
public static class FriendlyNames
{
    public static TableInfo? GetTable(string? tableName)
    {
        if (string.IsNullOrWhiteSpace(tableName)) return null;
        return Tables.TryGetValue(tableName.Trim(), out var info) ? info : null;
    }

    public static string GetTableDisplay(string? tableName)
    {
        var info = GetTable(tableName);
        return info is not null ? info.DisplayName : Prettify(tableName ?? string.Empty);
    }

    public static ColumnInfo? GetColumn(string? tableName, string? columnName)
    {
        if (string.IsNullOrWhiteSpace(columnName)) return null;
        var col = columnName.Trim();

        // 1) PARAMS table loaded from the live database — most authoritative source.
        if (!string.IsNullOrWhiteSpace(tableName)
            && _dynamicByTableField.TryGetValue((tableName.Trim(), col), out var fromParams1))
        {
            return fromParams1;
        }
        if (_dynamicByField.TryGetValue(col, out var fromParams2))
        {
            return fromParams2;
        }

        // 2) Hardcoded per-table dictionary (REC_HEADERS, OPERATIONS_VIEW, etc.).
        if (!string.IsNullOrWhiteSpace(tableName)
            && Columns.TryGetValue(tableName.Trim(), out var perTable)
            && perTable.TryGetValue(col, out var info))
        {
            return info;
        }

        // 3) Common hardcoded fallback (IDs, time, depth).
        if (Common.TryGetValue(col, out var common)) return common;

        // 4) Last resort: scan every per-table dictionary so joined views still resolve.
        if (string.IsNullOrWhiteSpace(tableName))
        {
            foreach (var dict in Columns.Values)
                if (dict.TryGetValue(col, out var any)) return any;
        }

        return null;
    }

    // ============================================================
    // Dynamic catalog (populated from the PARAMS table at connect time)
    // ============================================================

    private static readonly Dictionary<(string Table, string Field), ColumnInfo> _dynamicByTableField
        = new(new TableFieldComparer());
    private static readonly Dictionary<string, ColumnInfo> _dynamicByField
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

            var display     = string.IsNullOrWhiteSpace(r.FullName) ? r.RegistrVar : r.FullName;
            var description = string.IsNullOrWhiteSpace(r.ShortName)
                ? (r.FullName ?? "")
                : $"{r.FullName} ({r.ShortName})";
            var info = new ColumnInfo(display, r.Unit ?? "", description);

            _dynamicByField[r.TableField] = info;
            if (!string.IsNullOrWhiteSpace(r.TableName))
                _dynamicByTableField[(r.TableName, r.TableField)] = info;
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

    public static string GetColumnDisplay(string? tableName, string? columnName)
    {
        var info = GetColumn(tableName, columnName);
        if (info is null) return Prettify(columnName ?? string.Empty);
        return string.IsNullOrEmpty(info.Unit)
            ? info.DisplayName
            : $"{info.DisplayName}, {info.Unit}";
    }

    public static string? GetColumnDescription(string? tableName, string? columnName)
        => GetColumn(tableName, columnName)?.Description;

    /// <summary>Friendly fallback for any unknown identifier: lower-case, replace _ with space, capitalize first letter.</summary>
    public static string Prettify(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return raw ?? string.Empty;
        var s = raw.Trim().Replace('_', ' ');
        // Lower-case ASCII letters but keep already-capitalized non-ASCII (Russian) intact-looking.
        // For typical SQL identifiers this gives "Mud Pressure" → "Mud pressure".
        if (s.Length == 0) return s;
        s = s.ToLower(CultureInfo.CurrentCulture);
        return char.ToUpper(s[0], CultureInfo.CurrentCulture) + s.Substring(1);
    }

    // ============================================================
    // Table catalog
    // ============================================================

    private static readonly Dictionary<string, TableInfo> Tables = new(System.StringComparer.OrdinalIgnoreCase)
    {
        ["REC_HEADERS"]              = new("Время и глубина",                "Время записи, забой, долото и привязка к рейсу."),
        ["REC_COMMON"]               = new("Параметры бурения",              "Основные параметры (вес, давление, обороты, расход и др.)."),
        ["REC_LAG"]                  = new("Газовые показания",              "Показания хроматографа и суммарного газа (с задержкой)."),
        ["REC_OTHER"]                = new("Дополнительные параметры",       "Параметры, передаваемые через каталог PARAMS."),
        ["REC_OTHER_WELL"]           = new("Параметры скважины (доп.)",      "Параметры, привязанные к конкретной скважине."),
        ["WELLBORES"]                = new("Скважины",                       "Список скважин."),
        ["RACES"]                    = new("Рейсы",                          "Спуско-подъёмные рейсы по скважине."),
        ["OPERATIONS"]               = new("Операции",                       "Операции, выполненные на скважине."),
        ["OPER_TYPES"]               = new("Типы операций",                  "Справочник видов операций."),
        ["TROUBLES"]                 = new("Осложнения",                     "Зарегистрированные осложнения и аварии."),
        ["TROUBLE_TYPES"]            = new("Типы осложнений",                "Справочник видов осложнений."),
        ["PARAMS"]                   = new("Каталог параметров",             "Описание параметров, регистрируемых станцией."),
        ["OTHER_WELL_PARAMS"]        = new("Параметры скважины (каталог)",   "Описание дополнительных параметров скважины."),
        ["OTHER_WELL_PARAMS_INDEX"]  = new("Индекс параметров скважины",     "Сопоставление параметров и физических каналов P_Vn."),
        ["MUD_DATA"]                 = new("Данные по раствору",             "Замеры свойств бурового раствора."),
    };

    // ============================================================
    // Columns common across many tables (fallback when table not found)
    // ============================================================

    private static readonly Dictionary<string, ColumnInfo> Common = new(System.StringComparer.OrdinalIgnoreCase)
    {
        ["REC_TIME"]         = new("Время",                "",      "Момент регистрации записи."),
        ["START_TIME"]       = new("Начало",               "",      "Время начала."),
        ["STOP_TIME"]        = new("Окончание",            "",      "Время окончания."),
        ["BOTTOM_DEPTH"]     = new("Глубина забоя",        "м",     "Глубина забоя скважины."),
        ["BIT_DEPTH"]        = new("Глубина долота",       "м",     "Глубина положения долота."),
        ["BOTTOM_LAG_DEPTH"] = new("Глубина забоя (лаг)",  "м",     "Глубина забоя с учётом отставания газового анализа."),
        ["TOP_DEPTH"]        = new("Верхняя глубина",      "м",     "Верхняя граница интервала."),
        ["WELL_ID"]          = new("Скважина (ID)",        "",      "Внутренний идентификатор скважины."),
        ["RACE_ID"]          = new("Рейс (ID)",            "",      "Внутренний идентификатор рейса."),
        ["REC_HEADER_ID"]    = new("Запись (ID)",          "",      "Внутренний идентификатор записи."),
        ["REC_ID"]           = new("Запись (ID)",          "",      "Внутренний идентификатор записи."),
        ["PARAM_ID"]         = new("Параметр (ID)",        "",      "Внутренний идентификатор параметра."),
        ["NAME"]             = new("Название",             "",      "Название."),
        ["FULL_NAME"]        = new("Полное название",      "",      "Развёрнутое название параметра."),
        ["CLUSTER"]          = new("Куст",                 "",      "Название куста."),
        ["NUMBER"]           = new("Номер",                "",      "Порядковый номер."),
        ["COMMENT"]          = new("Комментарий",          "",      "Произвольный комментарий."),
        ["IS_CURRENT"]       = new("Текущая",              "",      "Признак текущей скважины."),
        ["REGISTR_VAR"]      = new("Имя параметра",        "",      "Системное имя параметра в каталоге."),
        ["PARAM_UNIT"]       = new("Единица измерения",    "",      "Единица измерения параметра."),
        ["TABLE_NAME"]       = new("Таблица-источник",     "",      "Таблица, в которой лежит значение."),
        ["TABLE_FIELD"]      = new("Поле-источник",        "",      "Поле в таблице-источнике."),
        ["PARAM_VALUE"]      = new("Значение",             "",      "Числовое значение параметра."),
        ["OPER_ID"]          = new("Операция (ID)",        "",      "Идентификатор типа операции."),
        ["USER_OPER_ID"]     = new("Польз. операция (ID)", "",      "Идентификатор пользовательской операции."),
        ["TROUBLE_TYPE_ID"]  = new("Тип осложнения (ID)",  "",      "Идентификатор типа осложнения."),
    };

    // ============================================================
    // Per-table columns. Only entries that PARAMS does NOT cover live
    // here: REC_HEADERS holds metadata (time, depth), and OPERATIONS_VIEW
    // is the synthetic alias schema for the Операции tab. Everything in
    // REC_COMMON / REC_LAG comes from the PARAMS table at connect time.
    // ============================================================

    private static readonly Dictionary<string, Dictionary<string, ColumnInfo>> Columns = new(System.StringComparer.OrdinalIgnoreCase)
    {
        ["REC_HEADERS"] = new(System.StringComparer.OrdinalIgnoreCase)
        {
            ["REC_HEADER_ID"]    = new("Запись (ID)",         "",  "Внутренний идентификатор записи."),
            ["RACE_ID"]          = new("Рейс (ID)",           "",  "Привязка к рейсу."),
            ["REC_TIME"]         = new("Время",               "",  "Момент регистрации записи."),
            ["BOTTOM_DEPTH"]     = new("Глубина забоя",       "м", "Текущая глубина забоя."),
            ["BIT_DEPTH"]        = new("Глубина долота",      "м", "Текущая глубина положения долота."),
            ["BOTTOM_LAG_DEPTH"] = new("Глубина забоя (лаг)", "м", "Глубина забоя с учётом отставания."),
        },

        // Synthetic schema for the Операции tab. The query in FirebirdService aliases
        // its result columns to these names so they get nice Russian headers.
        ["OPERATIONS_VIEW"] = new(System.StringComparer.OrdinalIgnoreCase)
        {
            ["OP_NUMBER"]         = new("№",                 "",    "Внутренний номер записи операции."),
            ["WORK_KIND_NAME"]    = new("Вид работ",         "",    "Категория работы (из справочника WORK_TYPES)."),
            ["OPER_NAME"]         = new("Операция",          "",    "Название операции (USER_OPER_ID имеет приоритет, иначе OPER_ID — из справочника OPER_TYPES)."),
            ["SUB_OPER_NAME"]     = new("Подоперация",       "",    "Уточняющая операция (из справочника SUBOPER_TYPES)."),
            ["OP_START"]          = new("Начало",            "",    "Время начала операции."),
            ["OP_STOP"]           = new("Окончание",         "",    "Время окончания операции."),
            ["OP_DURATION_HOURS"] = new("Длительность",      "час", "Длительность операции в часах."),
            ["OP_COMMENT"]        = new("Комментарий",       "",    "Комментарий к операции."),
        },
    };
}
