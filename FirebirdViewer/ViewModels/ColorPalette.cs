using OxyPlot;

namespace FirebirdViewer.ViewModels;

/// <summary>
/// Deterministic colour-per-parameter so that adding / removing a series
/// never reshuffles the other curves' colours.
/// </summary>
internal static class ColorPalette
{
    private static readonly OxyColor[] Palette =
    {
        OxyColor.FromRgb(0x1F, 0x77, 0xB4), // blue
        OxyColor.FromRgb(0xD6, 0x27, 0x28), // red
        OxyColor.FromRgb(0x2C, 0xA0, 0x2C), // green
        OxyColor.FromRgb(0xFF, 0x7F, 0x0E), // orange
        OxyColor.FromRgb(0x94, 0x67, 0xBD), // purple
        OxyColor.FromRgb(0x8C, 0x56, 0x4B), // brown
        OxyColor.FromRgb(0xE3, 0x77, 0xC2), // pink
        OxyColor.FromRgb(0x7F, 0x7F, 0x7F), // grey
        OxyColor.FromRgb(0xBC, 0xBD, 0x22), // olive
        OxyColor.FromRgb(0x17, 0xBE, 0xCF), // cyan
        OxyColor.FromRgb(0x00, 0x00, 0x80), // navy
        OxyColor.FromRgb(0x80, 0x00, 0x00), // maroon
        OxyColor.FromRgb(0x00, 0x80, 0x00), // dark green
        OxyColor.FromRgb(0xFF, 0xD7, 0x00), // gold
        OxyColor.FromRgb(0xA0, 0x52, 0x2D), // sienna
    };

    /// <summary>Picks a stable colour for <paramref name="key"/> using a string hash mod palette length.</summary>
    public static OxyColor For(string key)
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
