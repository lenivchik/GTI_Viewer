using System.ComponentModel;
using System.Globalization;
using System.Resources;

namespace FirebirdViewer.Localization;

/// <summary>A selectable interface language, shown in its own script.</summary>
public sealed record UiLanguage(string Code, string NativeName);

/// <summary>
/// The interface language and the translated strings.
///
/// Strings live in Resources/Strings.resx (Russian — the neutral language) with one
/// satellite file per translation: Strings.kk.resx, Strings.uz-Latn.resx. A key missing
/// from a translation falls back to Russian, so a half-translated build still runs.
///
/// XAML binds through <see cref="TrExtension"/> to the indexer below; switching the
/// language raises a change on it, so every bound label updates in the open window.
/// C# code calls <see cref="T"/> / <see cref="F"/> when it builds a string.
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{
    public static Loc Instance { get; } = new();

    private static readonly ResourceManager Strings =
        new("FirebirdViewer.Resources.Strings", typeof(Loc).Assembly);

    /// <summary>Languages offered in the menu. Names are in their own script on purpose:
    /// someone who switched by mistake can still find their way back.</summary>
    public static IReadOnlyList<UiLanguage> Languages { get; } = new[]
    {
        new UiLanguage("ru",      "Русский"),
        new UiLanguage("kk",      "Қазақша"),
        new UiLanguage("uz-Latn", "Oʻzbekcha"),
    };

    public const string DefaultCode = "ru";

    /// <summary>Culture the strings are taken from.</summary>
    public static CultureInfo Culture { get; private set; } = CultureInfo.GetCultureInfo(DefaultCode);

    /// <summary>Code of the current language, one of <see cref="Languages"/>.</summary>
    public static string Code { get; private set; } = DefaultCode;

    /// <summary>True when the interface is in the database's own language (Russian).</summary>
    public static bool IsNeutral => Code == DefaultCode;

    /// <summary>Raised after the language changed and the XAML bindings were notified.</summary>
    public static event EventHandler? LanguageChanged;

    public event PropertyChangedEventHandler? PropertyChanged;

    private Loc() { }

    /// <summary>XAML binding target: <c>{Binding [Key], Source={x:Static l:Loc.Instance}}</c>.</summary>
    public string this[string key] => T(key);

    /// <summary>Translated string, or <c>[key]</c> when the key exists in no resource file.</summary>
    public static string T(string key) => Strings.GetString(key, Culture) ?? $"[{key}]";

    /// <summary>Translated string, or null when neither the current language nor Russian has it.</summary>
    public static string? TryT(string key) => Strings.GetString(key, Culture);

    /// <summary>Translated format string with arguments filled in.</summary>
    public static string F(string key, params object?[] args) => string.Format(Culture, T(key), args);

    /// <summary>
    /// Language to start with when the user never picked one: the Windows display language
    /// if we have a translation for it, otherwise Russian.
    /// </summary>
    public static string SystemDefaultCode()
    {
        var ui = CultureInfo.CurrentUICulture;
        return ui.TwoLetterISOLanguageName switch
        {
            "kk" => "kk",
            "uz" => "uz-Latn",
            _    => DefaultCode,
        };
    }

    /// <summary>Normalises a stored or requested code to one we ship; unknown → Russian.</summary>
    public static string Normalize(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return DefaultCode;
        foreach (var l in Languages)
            if (string.Equals(l.Code, code, StringComparison.OrdinalIgnoreCase)) return l.Code;
        return code.StartsWith("uz", StringComparison.OrdinalIgnoreCase) ? "uz-Latn"
             : code.StartsWith("kk", StringComparison.OrdinalIgnoreCase) ? "kk"
             : DefaultCode;
    }

    /// <summary>Switch the interface language. Safe to call before any window exists.</summary>
    public static void SetLanguage(string? code)
    {
        var normalized = Normalize(code);
        var changed = normalized != Code;

        Code = normalized;
        Culture = CultureInfo.GetCultureInfo(normalized);

        // UI culture only: number and date formatting keep following the Windows settings.
        CultureInfo.DefaultThreadCurrentUICulture = Culture;
        Thread.CurrentThread.CurrentUICulture = Culture;

        if (!changed) return;
        // "Item[]" refreshes every binding to the indexer at once.
        Instance.PropertyChanged?.Invoke(Instance, new PropertyChangedEventArgs("Item[]"));
        LanguageChanged?.Invoke(null, EventArgs.Empty);
    }
}
