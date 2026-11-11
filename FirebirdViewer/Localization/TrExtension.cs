using System.Windows.Data;
using System.Windows.Markup;

namespace FirebirdViewer.Localization;

/// <summary>
/// XAML shorthand for a translated, live-updating string:
/// <c>Header="{l:Tr Menu_File}"</c>.
///
/// It produces a one-way binding to <see cref="Loc"/>'s indexer, so the text follows the
/// language when it is switched at runtime. Works in attributes, inside data templates
/// (the source is explicit, not the DataContext) and in style setters.
///
/// Keys use letters, digits and underscores only — they are placed inside a binding path.
/// </summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TrExtension : MarkupExtension
{
    public TrExtension() { }

    public TrExtension(string key) => Key = key;

    [ConstructorArgument("key")]
    public string Key { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new Binding($"[{Key}]")
        {
            Source = Loc.Instance,
            Mode = BindingMode.OneWay,
        };
        return binding.ProvideValue(serviceProvider);
    }
}
