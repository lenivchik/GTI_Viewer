namespace FirebirdViewer.Localization;

/// <summary>
/// A message remembered as resource key + arguments instead of a finished string, so it
/// can be produced again in another language — used for the status-bar texts, which
/// otherwise would stay in the old language until the next event rewrote them.
/// </summary>
public sealed class LocalizedText
{
    public LocalizedText(string key, params object?[] args)
    {
        Key = key;
        Args = args;
    }

    public string Key { get; }
    public object?[] Args { get; }

    /// <summary>The message in the current interface language.</summary>
    public string Text => Loc.F(Key, Args);

    public override string ToString() => Text;
}
