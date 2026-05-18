using System.Collections.Generic;
using System.Globalization;

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

        if (!string.IsNullOrWhiteSpace(tableName)
            && Columns.TryGetValue(tableName.Trim(), out var perTable)
            && perTable.TryGetValue(col, out var info))
        {
            return info;
        }

        if (Common.TryGetValue(col, out var common)) return common;

        // Fallback: when no table context is given (e.g. joined views), look across
        // every per-table dictionary so REC_COMMON / REC_LAG columns still resolve.
        if (string.IsNullOrWhiteSpace(tableName))
        {
            foreach (var dict in Columns.Values)
                if (dict.TryGetValue(col, out var any)) return any;
        }

        return null;
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
    // Per-table columns (taken from GtiParameterCatalog where possible)
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

        ["REC_COMMON"] = new(System.StringComparer.OrdinalIgnoreCase)
        {
            ["REC_HEADER_ID"]      = new("Запись (ID)",                "",       "Привязка к записи."),
            ["WOH"]                = new("Вес на крюке",               "т",      "Показания датчика веса на крюке."),
            ["MUD_PRESSURE"]       = new("Давление на входе",          "атм",    "Показания датчика давления на входе."),
            ["PUMP_CNT1"]          = new("Ходы насоса 1",              "ход/мин", "Показания первого датчика ходов насоса."),
            ["PUMP_CNT2"]          = new("Ходы насоса 2",              "ход/мин", "Показания второго датчика ходов насоса."),
            ["PUMP_CNT3"]          = new("Ходы насоса 3",              "ход/мин", "Показания третьего датчика ходов насоса."),
            ["TABLE_TORQUE"]       = new("Крутящий момент ротора",              "у.е.",   "Показания датчика момента на роторе."),
            ["ROTOR_SPEED"]        = new("Обороты ротора",             "об/мин", "Показания датчика оборотов ротора."),
            ["TR_BLOCK_POS"]       = new("Положение тальблока",                   "м",      "Высота положения крюка над роторным столом."),
            ["WOB"]                = new("Нагрузка на долото",         "т",      "Значение нагрузки на долото (WOB)."),
            ["PIPE_WRENCH_TORQUE"] = new("Момент на ключе",            "кН·м",   "Показания датчика момента на ключе."),
            ["AKB_WRENCH_TORQUE"]  = new("Момент на ключе (АКБ)",      "кН·м",   "Показания АКБ-датчика момента на ключе."),
            ["VOLUME1"]            = new("Объём - 1",          "м³",     "Объём ПЖ в первой ёмкости."),
            ["VOLUME2"]            = new("Объём - 2",          "м³",     "Объём ПЖ во второй ёмкости."),
            ["VOLUME3"]            = new("Объём - 3",          "м³",     "Объём ПЖ в третьей ёмкости."),
            ["VOLUME4"]            = new("Объём - 4",          "м³",     "Объём ПЖ в четвёртой ёмкости."),
            ["VOLUME5"]            = new("Объём - 5",          "м³",     "Объём ПЖ в пятой ёмкости."),
            ["VOLUME6"]            = new("Объём - 6",          "м³",     "Объём ПЖ в шестой ёмкости."),
            ["VOLUME7"]            = new("Объём - 7",          "м³",     "Объём ПЖ в седьмой ёмкости."),
            ["VOLUME8"]            = new("Объём - 8",          "м³",     "Объём ПЖ в восьмой ёмкости."),
            ["VOLUME9"]            = new("Объём - 9",          "м³",     "Объём ПЖ в девятой ёмкости."),
            ["VOLUME10"]           = new("Объём - 10",         "м³",     "Объём ПЖ в десятой ёмкости."),
            ["VOLUME11"]           = new("Объём - 11",         "м³",     "Объём ПЖ в одиннадцатой ёмкости."),
            ["VOLUME12"]           = new("Объём - 12",         "м³",     "Объём ПЖ в двенадцатой ёмкости."),
            ["VOLUME13"]           = new("Объём - 13",         "м³",     "Объём ПЖ в тринадцатой ёмкости."),
            ["VOLUME14"]           = new("Объём - 14",         "м³",     "Объём ПЖ в четырнадцатой ёмкости."),
            ["VOLUME15"]           = new("Объём - 15",         "м³",     "Объём ПЖ в пятнадцатой ёмкости."),
            ["VOLUME16"]           = new("Объём - 16",         "м³",     "Объём ПЖ в шестнадцатой ёмкости."),
            ["ACTIVE_VOLUME"]      = new("Объём ПЖ в активных ёмкостях",  "м³",     "Объём раствора в ёмкостях, участвующих в циркуляции."),
            ["MUD_DENSITY_IN"]     = new("Плотность на входе",         "г/см³",  "Показания датчика плотности ПЖ на входе."),
            ["MUD_DENSITY_OUT"]    = new("Плотность на выходе",        "г/см³",  "Показания датчика плотности ПЖ на выходе."),
            ["MUD_TEMP_IN"]        = new("Температура на входе",       "°C",     "Показания датчика температуры на входе."),
            ["MUD_TEMP_OUT"]       = new("Температура на выходе",      "°C",     "Показания датчика температуры на выходе."),
            ["MUD_COND_IN"]        = new("Электропроводность на входе", "у.е.",  "Показания датчика электропроводности на входе."),
            ["MUD_COND_OUT"]       = new("Электропроводность на выходе","у.е.",  "Показания датчика электропроводности на выходе."),
            ["CIRC_RATE_IN"]       = new("Расход ПЖ на входе",            "л/с",    "Показания датчика расхода на входе."),
            ["CIRC_RATE_OUT"]      = new("Расход ПЖ на выходе",           "л/с",    "Показания датчика расхода на выходе."),
            ["TRIP_RATE"]          = new("Скорость СПО",               "м/с",    "Скорость перемещения колонны при спуско-подъёмных операциях."),
            ["ROP"]                = new("Скорость механическая",      "м/ч",    "Механическая скорость бурения."),
            ["DRILLING_TIME_LOG"]  = new("ДМК",                        "мин/м",  "Время бурения одного метра."),
            ["DRILLING_RATE"]      = new("Скорость бурения",           "м/ч",    "Скорость углубления забоя, усреднённая за минуту."),
            ["SUMM_GAZ3"]          = new("Сумм. газ (выносной)",       "%",      "Показания выносного датчика суммарного содержания газов."),
        },

        ["REC_LAG"] = new(System.StringComparer.OrdinalIgnoreCase)
        {
            ["REC_HEADER_ID"] = new("Запись (ID)",            "",  "Привязка к записи."),
            ["C1"]            = new("Метан",                  "%", "Содержание метана в пробе (хроматограф)."),
            ["C2"]            = new("Этан",                   "%", "Содержание этана в пробе."),
            ["C3"]            = new("Пропан",                 "%", "Содержание пропана в пробе."),
            ["C4"]            = new("Бутан",                  "%", "Содержание бутана в пробе."),
            ["C5"]            = new("Пентан",                 "%", "Содержание пентана в пробе."),
            ["C6"]            = new("Гексан",                 "%", "Содержание гексана в пробе."),
            ["SUMM_GAZ"]      = new("Сумм. газ (хроматограф)", "%","Сумма абсолютных значений с хроматографа."),
            ["SUMM_INT"]      = new("Сумм. газ (встроенный)",  "%","Показания встроенного газосумматора."),
        },

        ["WELLBORES"] = new(System.StringComparer.OrdinalIgnoreCase)
        {
            ["WELL_ID"]    = new("Скважина (ID)", "", "Идентификатор скважины."),
            ["NAME"]       = new("Скважина",      "", "Название скважины."),
            ["CLUSTER"]    = new("Куст",          "", "Куст."),
            ["IS_CURRENT"] = new("Текущая",       "", "Признак текущей скважины."),
        },

        ["RACES"] = new(System.StringComparer.OrdinalIgnoreCase)
        {
            ["RACE_ID"]    = new("Рейс (ID)",   "", "Идентификатор рейса."),
            ["WELL_ID"]    = new("Скважина (ID)", "", "Привязка к скважине."),
            ["NUMBER"]     = new("Номер рейса", "", "Порядковый номер рейса."),
            ["START_TIME"] = new("Начало",      "", "Время начала рейса."),
            ["STOP_TIME"]  = new("Окончание",   "", "Время окончания рейса."),
        },

        ["OPERATIONS"] = new(System.StringComparer.OrdinalIgnoreCase)
        {
            ["REC_ID"]       = new("Запись (ID)",            "", "Идентификатор операции."),
            ["WELL_ID"]      = new("Скважина (ID)",          "", "Привязка к скважине."),
            ["OPER_ID"]      = new("Тип операции (ID)",      "", "Идентификатор типа операции."),
            ["USER_OPER_ID"] = new("Польз. тип операции",    "", "Пользовательский тип операции."),
            ["SUBOPER_ID"]   = new("Тип подоперации",        "", "Идентификатор подтипа операции."),
            ["START_TIME"]   = new("Начало",                 "", "Время начала операции."),
            ["STOP_TIME"]    = new("Окончание",              "", "Время окончания операции."),
            ["COMMENT"]      = new("Комментарий",            "", "Комментарий к операции."),
        },

        ["TROUBLES"] = new(System.StringComparer.OrdinalIgnoreCase)
        {
            ["REC_ID"]          = new("Запись (ID)",       "",  "Идентификатор осложнения."),
            ["WELL_ID"]         = new("Скважина (ID)",     "",  "Привязка к скважине."),
            ["TROUBLE_TYPE_ID"] = new("Тип осложнения",    "",  "Идентификатор типа осложнения."),
            ["START_TIME"]      = new("Начало",            "",  "Время начала."),
            ["STOP_TIME"]       = new("Окончание",         "",  "Время окончания."),
            ["TOP_DEPTH"]       = new("Верхняя глубина",   "м", "Верхняя граница интервала."),
            ["BOTTOM_DEPTH"]    = new("Нижняя глубина",    "м", "Нижняя граница интервала."),
            ["COMMENT"]         = new("Комментарий",       "",  "Комментарий."),
        },

        ["OPER_TYPES"] = new(System.StringComparer.OrdinalIgnoreCase)
        {
            ["OPER_ID"] = new("Тип операции (ID)", "", "Идентификатор."),
            ["NAME"]    = new("Тип операции",      "", "Название типа операции."),
        },

        ["TROUBLE_TYPES"] = new(System.StringComparer.OrdinalIgnoreCase)
        {
            ["TROUBLE_TYPE_ID"] = new("Тип осложнения (ID)", "", "Идентификатор."),
            ["NAME"]            = new("Тип осложнения",      "", "Название типа осложнения."),
        },

        ["PARAMS"] = new(System.StringComparer.OrdinalIgnoreCase)
        {
            ["PARAM_ID"]    = new("Параметр (ID)",      "", "Идентификатор параметра."),
            ["REGISTR_VAR"] = new("Имя параметра",      "", "Системное имя параметра."),
            ["FULL_NAME"]   = new("Полное название",   "", "Полное (читаемое) название параметра."),
            ["PARAM_UNIT"]  = new("Единица измерения", "", "Единица измерения."),
            ["TABLE_NAME"]  = new("Таблица-источник",  "", "Таблица, в которой хранится значение."),
            ["TABLE_FIELD"] = new("Поле-источник",     "", "Поле в таблице-источнике."),
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
