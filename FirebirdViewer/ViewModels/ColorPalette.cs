namespace FirebirdViewer.ViewModels;

/// <summary>Library-agnostic RGB colour (0–255 per channel).</summary>
public readonly record struct ChartColor(byte R, byte G, byte B);

/// <summary>
/// Deterministic colour-per-parameter so that adding / removing a series
/// never reshuffles the other curves' colours. Returns a plain RGB triple so
/// no charting library type leaks into the view-model layer.
/// </summary>
internal static class ColorPalette
{
    private static readonly ChartColor[] Palette =
    {
        new(0x1F, 0x77, 0xB4), // blue
        new(0xD6, 0x27, 0x28), // red
        new(0x2C, 0xA0, 0x2C), // green
        new(0xFF, 0x7F, 0x0E), // orange
        new(0x94, 0x67, 0xBD), // purple
        new(0x8C, 0x56, 0x4B), // brown
        new(0xE3, 0x77, 0xC2), // pink
        new(0x7F, 0x7F, 0x7F), // grey
        new(0xBC, 0xBD, 0x22), // olive
        new(0x17, 0xBE, 0xCF), // cyan
        new(0x00, 0x00, 0x80), // navy
        new(0x80, 0x00, 0x00), // maroon
        new(0x00, 0x80, 0x00), // dark green
        new(0xFF, 0xD7, 0x00), // gold
        new(0xA0, 0x52, 0x2D), // sienna
    };

    /// <summary>Picks a stable colour for <paramref name="key"/> using a string hash mod palette length.</summary>
    public static ChartColor For(string key)
    {
        if (string.IsNullOrEmpty(key)) return Palette[0];
        // FNV-1a (simple, allocation-free, stable across runs).
        uint hash = 2166136261u;
        foreach (var ch in key)
        {
            hash ^= ch;
            hash *= 16777619u;
        }
        return Palette[(int)(hash % (uint)Palette.Length)];
    }
}
