using FirebirdViewer.ViewModels;

namespace FirebirdViewer.Models;

/// <summary>
/// One entry in the "Показывать колонки" panel: a column name plus a visibility flag.
/// <see cref="Name"/> is the raw database identifier (used for binding); <see cref="DisplayName"/>
/// is the label shown to the user, in the interface language — it is reassigned when the
/// language changes.
/// </summary>
public sealed class ColumnVisibility : ObservableObject
{
    public string Name { get; init; } = "";

    private string _displayName = "";
    public string DisplayName
    {
        get => _displayName;
        set => SetProperty(ref _displayName, value);
    }

    private string? _description;
    public string? Description
    {
        get => _description;
        set => SetProperty(ref _description, value);
    }

    private bool _isVisible = true;
    public bool IsVisible
    {
        get => _isVisible;
        set => SetProperty(ref _isVisible, value);
    }
}
