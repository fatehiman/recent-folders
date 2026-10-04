namespace RecentFolders;

/// <summary>How a badge looks. Sizes are in pixels at 100% display scale.</summary>
internal sealed class BadgeStyle
{
    public string Name { get; set; } = "";
    /// <summary>-1 = fully round ends (pill).</summary>
    public float CornerRadius { get; set; } = -1;
    public float PaddingX { get; set; } = 10;
    public float PaddingY { get; set; } = 3;
    public float BorderWidth { get; set; }
    /// <summary>0..1, how much darker the border is than the fill.</summary>
    public float BorderDarken { get; set; } = 0.25f;
    /// <summary>0..1, makes the fill lighter (1 = white).</summary>
    public float FillLighten { get; set; }
    public bool Shadow { get; set; }
    public bool Gradient { get; set; }
}

internal static class BadgeStyles
{
    public static readonly BadgeStyle Pill = new() { Name = "pill", CornerRadius = -1, PaddingX = 10, PaddingY = 3 };

    public static readonly BadgeStyle[] Builtin =
    {
        Pill,
        new() { Name = "rounded", CornerRadius = 6, PaddingX = 8, PaddingY = 4 },
        new() { Name = "square", CornerRadius = 0, PaddingX = 8, PaddingY = 4 },
        new() { Name = "outline", CornerRadius = 6, PaddingX = 8, PaddingY = 3, BorderWidth = 1.5f, BorderDarken = 0.35f, FillLighten = 0.5f },
        new() { Name = "raised", CornerRadius = 6, PaddingX = 9, PaddingY = 4, BorderWidth = 1, BorderDarken = 0.15f, Shadow = true, Gradient = true },
        new() { Name = "compact", CornerRadius = 3, PaddingX = 5, PaddingY = 1 },
    };

    /// <summary>Custom styles from the .conf win over built-in styles with the same name.</summary>
    public static BadgeStyle Resolve(AppConfig cfg)
    {
        var name = cfg.Badge.Style?.Trim() ?? "";
        return cfg.CustomStyles.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? Builtin.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? Pill;
    }
}
