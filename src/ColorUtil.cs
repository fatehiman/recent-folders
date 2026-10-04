using System.Globalization;

namespace RecentFolders;

internal static class ColorUtil
{
    /// <summary>
    /// Stable color for a folder name: the same name always gives the same light color.
    /// (string.GetHashCode is random per process, so we use FNV-1a.)
    /// </summary>
    public static Color ForName(string name, double saturation, double lightness)
    {
        uint hash = 2166136261;
        foreach (char c in name.ToLowerInvariant())
        {
            hash ^= c;
            hash *= 16777619;
        }
        return FromHsl(hash % 360, saturation, lightness);
    }

    public static Color FromHsl(double h, double s, double l)
    {
        double c = (1 - Math.Abs(2 * l - 1)) * s;
        double x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        double m = l - c / 2;
        (double r, double g, double b) = h switch
        {
            < 60 => (c, x, 0d),
            < 120 => (x, c, 0d),
            < 180 => (0d, c, x),
            < 240 => (0d, x, c),
            < 300 => (x, 0d, c),
            _ => (c, 0d, x),
        };
        return Color.FromArgb(To255(r + m), To255(g + m), To255(b + m));
    }

    public static Color Darken(Color c, double amount) => Color.FromArgb(c.A,
        To255(c.R / 255.0 * (1 - amount)), To255(c.G / 255.0 * (1 - amount)), To255(c.B / 255.0 * (1 - amount)));

    public static Color Lighten(Color c, double amount) => Color.FromArgb(c.A,
        To255((c.R + (255 - c.R) * amount) / 255), To255((c.G + (255 - c.G) * amount) / 255), To255((c.B + (255 - c.B) * amount) / 255));

    /// <summary>Reads "#RGB", "#RRGGBB", "#AARRGGBB" or a color name like "white".</summary>
    public static Color Parse(string? text, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(text)) return fallback;
        var s = text.Trim();
        try
        {
            if (s.StartsWith('#'))
            {
                var hex = s[1..];
                if (hex.Length == 3) hex = string.Concat(hex.Select(ch => $"{ch}{ch}"));
                if (hex.Length == 6) hex = "FF" + hex;
                if (hex.Length != 8) return fallback;
                return Color.FromArgb(unchecked((int)uint.Parse(hex, NumberStyles.HexNumber)));
            }
            var named = Color.FromName(s);
            return named.IsKnownColor ? named : fallback;
        }
        catch
        {
            return fallback;
        }
    }

    private static int To255(double v) => (int)Math.Round(Math.Clamp(v, 0, 1) * 255);
}
