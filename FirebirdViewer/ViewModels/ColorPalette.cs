using System;
using System.Collections.Generic;

namespace FirebirdViewer.ViewModels;

/// <summary>Library-agnostic RGB colour (0–255 per channel).</summary>
public readonly record struct ChartColor(byte R, byte G, byte B)
{
    /// <summary>A frozen WPF brush for showing this colour as a swatch in the UI.</summary>
    public System.Windows.Media.Brush Brush
    {
        get
        {
            var b = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(R, G, B));
            b.Freeze();
            return b;
        }
    }
}

/// <summary>
/// Colours for curves. <see cref="For"/> assigns a stable default from a small
/// set of well-separated hues; <see cref="Options"/> is the full picker palette.
/// Returns plain RGB triples so no charting library type leaks into the VM layer.
/// </summary>
public static class ColorPalette
{
    /// <summary>Distinct, readable colours used for automatic assignment.</summary>
    private static readonly ChartColor[] Defaults =
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

    /// <summary>
    /// Full picker palette: 12 hues × 5 shades, plus a greyscale row.
    /// Laid out row-by-row so a 12-column grid shows one hue family per column.
    /// </summary>
    public static IReadOnlyList<ChartColor> Options { get; } = BuildFullPalette();

    private static ChartColor[] BuildFullPalette()
    {
        const int hues = 12;
        // Lightness levels, dark → light. Saturation drops slightly at the extremes
        // so the darkest and lightest rows stay distinguishable.
        var levels = new[] { (L: 0.25, S: 0.85), (L: 0.38, S: 0.90), (L: 0.50, S: 0.95), (L: 0.63, S: 0.85), (L: 0.76, S: 0.75) };

        // The auto-assign colours come first so a curve's current colour is always
        // present in the picker (and therefore shows as the selected swatch).
        var seen = new HashSet<ChartColor>();
        var list = new List<ChartColor>(Defaults.Length + hues * levels.Length + hues);
        foreach (var c in Defaults)
            if (seen.Add(c)) list.Add(c);

        foreach (var (l, s) in levels)
            for (int h = 0; h < hues; h++)
            {
                var c = FromHsl(h * 360.0 / hues, s, l);
                if (seen.Add(c)) list.Add(c);
            }

        // Greyscale row (same width as the hue rows).
        for (int i = 0; i < hues; i++)
        {
            var v = (byte)Math.Round(255.0 * i / (hues - 1));
            var c = new ChartColor(v, v, v);
            if (seen.Add(c)) list.Add(c);
        }

        return list.ToArray();
    }

    private static ChartColor FromHsl(double hDeg, double s, double l)
    {
        double c = (1 - Math.Abs(2 * l - 1)) * s;
        double hp = ((hDeg % 360) + 360) % 360 / 60.0;
        double x = c * (1 - Math.Abs(hp % 2 - 1));
        double r = 0, g = 0, b = 0;
        switch ((int)hp)
        {
            case 0: r = c; g = x; break;
            case 1: r = x; g = c; break;
            case 2: g = c; b = x; break;
            case 3: g = x; b = c; break;
            case 4: r = x; b = c; break;
            default: r = c; b = x; break;
        }
        double m = l - c / 2;
        return new ChartColor(To255(r + m), To255(g + m), To255(b + m));
    }

    private static byte To255(double v) => (byte)Math.Clamp(Math.Round(v * 255), 0, 255);

    /// <summary>Picks a stable colour for <paramref name="key"/> using a string hash mod palette length.</summary>
    public static ChartColor For(string key)
    {
        if (string.IsNullOrEmpty(key)) return Defaults[0];
        // FNV-1a (simple, allocation-free, stable across runs).
        uint hash = 2166136261u;
        foreach (var ch in key)
        {
            hash ^= ch;
            hash *= 16777619u;
        }
        return Defaults[(int)(hash % (uint)Defaults.Length)];
    }
}
