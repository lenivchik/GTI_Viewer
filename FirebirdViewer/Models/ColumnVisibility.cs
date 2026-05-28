using FirebirdViewer.ViewModels;

namespace FirebirdViewer.Models;

/// <summary>
/// One entry in the "Показывать колонки" panel: a column name plus a visibility flag.
/// <see cref="Name"/> is the raw database identifier (used for binding); <see cref="DisplayName"/>
/// is the friendly Russian label shown to the user.
/// </summary>
public sealed class ColumnVisibility : ObservableObject
{
    public string Name { get; init; } = "";

    public string DisplayName { get; init; } = "";

    public string? Description { get; init; }

    private bool _isVisible = true;
    public bool IsVisible
    {
        get => _isVisible;
        set => SetProperty(ref _isVisible, value);
    }

    /// <summary>True when this parameter is plotted on the Графики tab.</summary>
    private bool _isInChart;
    public bool IsInChart
    {
        get => _isInChart;
        set => SetProperty(ref _isInChart, value);
    }
}
