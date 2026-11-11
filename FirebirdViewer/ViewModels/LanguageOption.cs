namespace FirebirdViewer.ViewModels;

/// <summary>One entry of the Язык menu: a language in its own script plus a check mark.</summary>
public sealed class LanguageOption : ObservableObject
{
    public LanguageOption(string code, string nativeName)
    {
        Code = code;
        NativeName = nativeName;
    }

    public string Code { get; }
    public string NativeName { get; }

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}
