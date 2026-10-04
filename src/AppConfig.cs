using System.Text.Json;
using System.Text.Json.Serialization;

namespace RecentFolders;

/// <summary>Settings from the .conf file (JSON with // comments) next to the exe.</summary>
internal sealed class AppConfig
{
    public WindowSettings Window { get; set; } = new();
    public FadeSettings Fade { get; set; } = new();
    public MouseOverSettings MouseOver { get; set; } = new();
    public FontSettings Font { get; set; } = new();
    public BadgeSettings Badge { get; set; } = new();
    public List<BadgeStyle> CustomStyles { get; set; } = new();
    public List<string> Sections { get; set; } = new() { "pinned", "frequent", "recent" };
    public TrackingSettings Tracking { get; set; } = new();
    public string DataFile { get; set; } = "";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    /// <summary>Loads the file. Creates it with default content when it does not exist.</summary>
    public static AppConfig Load(string path, out string? error)
    {
        error = null;
        try
        {
            if (!File.Exists(path)) WriteDefault(path);
            if (!File.Exists(path)) return new AppConfig();
            var cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path), Options) ?? new AppConfig();
            cfg.Normalize();
            return cfg;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return new AppConfig();
        }
    }

    private static void WriteDefault(string path)
    {
        try
        {
            using var src = typeof(AppConfig).Assembly.GetManifestResourceStream("default.conf")!;
            using var dst = File.Create(path);
            src.CopyTo(dst);
        }
        catch
        {
            // Folder may be read-only. Defaults are used.
        }
    }

    /// <summary>Fix missing objects and keep numbers in a safe range.</summary>
    private void Normalize()
    {
        Window ??= new();
        Fade ??= new();
        MouseOver ??= new();
        Font ??= new();
        Badge ??= new();
        CustomStyles ??= new();
        Tracking ??= new();
        Sections ??= new() { "pinned", "frequent", "recent" };
        DataFile ??= "";

        Window.Width = Math.Clamp(Window.Width, 120, 3000);
        Window.Height = Math.Clamp(Window.Height, 80, 3000);
        Window.Opacity = Math.Clamp(Window.Opacity, 0.3, 1.0);
        Window.Padding = Math.Clamp(Window.Padding, 0, 100);
        Window.Gap = Math.Clamp(Window.Gap, 0, 200);
        Fade.FadeInMs = Math.Clamp(Fade.FadeInMs, 0, 3000);
        Fade.FadeOutMs = Math.Clamp(Fade.FadeOutMs, 0, 3000);
        MouseOver.ShowDelayMs = Math.Clamp(MouseOver.ShowDelayMs, 0, 5000);
        MouseOver.HideDelayMs = Math.Clamp(MouseOver.HideDelayMs, 0, 10000);
        Font.Family = string.IsNullOrWhiteSpace(Font.Family) ? "Segoe UI" : Font.Family;
        Font.Size = Math.Clamp(Font.Size, 6, 40);
        Font.TitleSize = Math.Clamp(Font.TitleSize, 5, 40);
        Badge.Saturation = Math.Clamp(Badge.Saturation, 0, 1);
        Badge.Lightness = Math.Clamp(Badge.Lightness, 0.70, 0.97); // light colors only
        Badge.Spacing = Math.Clamp(Badge.Spacing, 0, 50);
        Badge.MaxTextWidth = Math.Clamp(Badge.MaxTextWidth, 30, 2000);
        Tracking.PollIntervalSeconds = Math.Clamp(Tracking.PollIntervalSeconds, 1, 60);
        Tracking.DwellSeconds = Math.Clamp(Tracking.DwellSeconds, 1, 3600);
        Tracking.MaxRecent = Math.Clamp(Tracking.MaxRecent, 0, 200);
        Tracking.MaxFrequent = Math.Clamp(Tracking.MaxFrequent, 0, 200);
        Tracking.MinUsesForFrequent = Math.Max(1, Tracking.MinUsesForFrequent);
        Tracking.FrequentDays = Math.Clamp(Tracking.FrequentDays, 0, 3650);
        Tracking.MaxHistory = Math.Clamp(Tracking.MaxHistory, 10, 5000);
        Tracking.ExcludePaths ??= new();
    }
}

internal sealed class WindowSettings
{
    public int Width { get; set; } = 300;
    public int Height { get; set; } = 200;
    public string BackgroundColor { get; set; } = "#F9F9F9";
    public double Opacity { get; set; } = 1.0;
    public string Corners { get; set; } = "round";
    public string BorderColor { get; set; } = "";
    public int Padding { get; set; } = 10;
    public int Gap { get; set; } = 8;
    public bool ShowSectionTitles { get; set; } = true;
    public string SectionTitleColor { get; set; } = "#6B6B6B";
    public string ScrollbarColor { get; set; } = "#B4B4B4";
}

internal sealed class FadeSettings
{
    public bool Enabled { get; set; } = true;
    public int FadeInMs { get; set; } = 150;
    public int FadeOutMs { get; set; } = 200;
}

internal sealed class MouseOverSettings
{
    public bool OpenOnMouseOver { get; set; } = true;
    public int ShowDelayMs { get; set; } = 250;
    public int HideDelayMs { get; set; } = 400;
}

internal sealed class FontSettings
{
    public string Family { get; set; } = "Segoe UI";
    public float Size { get; set; } = 9;
    public float TitleSize { get; set; } = 7.5f;
}

internal sealed class BadgeSettings
{
    public string Style { get; set; } = "pill";
    public string TextColor { get; set; } = "#000000";
    public bool Bold { get; set; } = true;
    public double Saturation { get; set; } = 0.70;
    public double Lightness { get; set; } = 0.86;
    public int Spacing { get; set; } = 6;
    public int MaxTextWidth { get; set; } = 150;
    public bool ShowPinIcon { get; set; } = true;
    public bool ShowAddButton { get; set; } = true;
}

internal sealed class TrackingSettings
{
    public int PollIntervalSeconds { get; set; } = 5;
    public int DwellSeconds { get; set; } = 30;
    public int MaxRecent { get; set; } = 12;
    public int MaxFrequent { get; set; } = 8;
    public int MinUsesForFrequent { get; set; } = 2;
    public int FrequentDays { get; set; } = 30;
    public bool ShowDuplicates { get; set; }
    public int MaxHistory { get; set; } = 300;
    public List<string> ExcludePaths { get; set; } = new();
}
