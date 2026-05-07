using FirebirdViewer.ViewModels;

namespace FirebirdViewer.Models;

/// <summary>
/// One entry in the "Выбор столбцов" panel: a column name plus a visibility flag.
/// </summary>
public sealed class ColumnVisibility : ObservableObject
{
    public string Name { get; init; } = "";

    private bool _isVisible = true;
    public bool IsVisible
    {
        get => _isVisible;
        set => SetProperty(ref _isVisible, value);
    }
}
