using System.Drawing.Drawing2D;

namespace RecentFolders;

/// <summary>Draws the sections and the folder badges, with mouse wheel scrolling.</summary>
internal sealed class BadgePanel : Control
{
    private sealed class Hit
    {
        public Rectangle Rect;
        public FolderEntry? Entry; // null = the "+ Add" button
        public Color Fill;
        public string Text = "";
    }

    private const string PinGlyph = "";
    private const TextFormatFlags Flags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;

    private AppConfig cfg = new();
    private BadgeStyle style = BadgeStyles.Pill;
    private List<Section> sections = new();
    private readonly List<Hit> hits = new();
    private readonly List<(string Text, Rectangle Rect)> titles = new();
    private readonly ToolTip tip = new() { ShowAlways = true, InitialDelay = 500 };
    private Font? badgeFont, titleFont, iconFont;
    private Rectangle emptyRect;
    private string emptyText = "";
    private int contentHeight, scroll, hover = -1, lineStep = 20;

    public event Action<FolderEntry>? ItemClicked;
    public event Action<FolderEntry, Point>? ItemMenu;
    public event Action<Point>? BackgroundMenu;
    public event Action? AddClicked;
    public event Action<string[]>? FoldersDropped;

    public BadgePanel()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        AllowDrop = true;
    }

    private float Dpi => DeviceDpi / 96f;
    private int S(float v) => (int)Math.Round(v * Dpi);

    public void Apply(AppConfig config)
    {
        cfg = config;
        style = BadgeStyles.Resolve(config);
        RebuildFonts();
        Relayout();
    }

    public void SetSections(List<Section> value)
    {
        sections = value;
        Relayout();
    }

    public void ResetScroll()
    {
        scroll = 0;
        Invalidate();
    }

    private void RebuildFonts()
    {
        badgeFont?.Dispose();
        titleFont?.Dispose();
        iconFont?.Dispose();
        // Pixel units + our own DPI factor, so text is correct on every monitor.
        float px = 96f / 72f * Dpi;
        badgeFont = new Font(cfg.Font.Family, cfg.Font.Size * px, cfg.Badge.Bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel);
        titleFont = new Font(cfg.Font.Family, cfg.Font.TitleSize * px, FontStyle.Bold, GraphicsUnit.Pixel);
        iconFont = new Font("Segoe Fluent Icons", cfg.Font.Size * 0.85f * px, FontStyle.Regular, GraphicsUnit.Pixel);
        if (iconFont.Name != "Segoe Fluent Icons")
        {
            iconFont.Dispose();
            iconFont = new Font("Segoe MDL2 Assets", cfg.Font.Size * 0.85f * px, FontStyle.Regular, GraphicsUnit.Pixel);
        }
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        RebuildFonts();
        Relayout();
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        Relayout();
    }

    private static Size Measure(string text, Font font) => TextRenderer.MeasureText(text, font, Size.Empty, Flags);

    private void Relayout()
    {
        hits.Clear();
        titles.Clear();
        if (badgeFont == null || titleFont == null || iconFont == null) return;

        int pad = S(cfg.Window.Padding);
        int gap = S(cfg.Badge.Spacing);
        int padX = S(style.PaddingX);
        int badgeH = Measure("Ag", badgeFont).Height + 2 * S(style.PaddingY);
        int width = Math.Max(1, ClientSize.Width - 2 * pad - S(4)); // room for the scrollbar
        int maxText = S(cfg.Badge.MaxTextWidth);
        int pinW = cfg.Badge.ShowPinIcon ? Measure(PinGlyph, iconFont).Width + S(3) : 0;
        int y = pad;
        bool any = false;

        foreach (var section in sections)
        {
            bool addHere = section.Key == "pinned" && cfg.Badge.ShowAddButton;
            if (section.Items.Count == 0 && !addHere) continue;
            any |= section.Items.Count > 0;

            if (cfg.Window.ShowSectionTitles)
            {
                var title = section.Title.ToUpperInvariant();
                var size = Measure(title, titleFont);
                titles.Add((title, new Rectangle(pad, y, size.Width, size.Height)));
                y += size.Height + S(4);
            }

            int x = pad;
            void Place(Hit hit, int w)
            {
                w = Math.Min(w, width);
                if (x > pad && x + w > pad + width)
                {
                    x = pad;
                    y += badgeH + gap;
                }
                hit.Rect = new Rectangle(x, y, w, badgeH);
                hits.Add(hit);
                x += w + gap;
            }

            foreach (var entry in section.Items)
            {
                var text = entry.Name;
                int textW = Math.Min(Measure(text, badgeFont).Width, maxText);
                var fill = ColorUtil.ForName(text, cfg.Badge.Saturation, cfg.Badge.Lightness);
                Place(new Hit { Entry = entry, Text = text, Fill = fill }, textW + 2 * padX + (entry.Pinned ? pinW : 0));
            }
            if (addHere)
            {
                const string addText = "+ Add";
                Place(new Hit { Text = addText }, Measure(addText, badgeFont).Width + 2 * padX);
            }
            y += badgeH + gap + gap / 2;
        }

        emptyText = any ? "" : $"No folders yet.\nA folder you keep open in File Explorer for {cfg.Tracking.DwellSeconds} seconds appears here.";
        emptyRect = new Rectangle(pad, y, width, Math.Max(S(40), ClientSize.Height - y - pad));
        contentHeight = any ? y - gap - gap / 2 + pad : emptyRect.Bottom;
        lineStep = badgeH + gap;
        hover = -1;
        ClampScroll();
        Invalidate();
    }

    private void ClampScroll() => scroll = Math.Clamp(scroll, 0, Math.Max(0, contentHeight - ClientSize.Height));

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        if (badgeFont == null || titleFont == null) return;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var titleColor = ColorUtil.Parse(cfg.Window.SectionTitleColor, Color.DimGray);
        var textColor = ColorUtil.Parse(cfg.Badge.TextColor, Color.Black);

        foreach (var (text, rect) in titles)
            TextRenderer.DrawText(g, text, titleFont, Shift(rect), titleColor, Flags);

        for (int i = 0; i < hits.Count; i++)
        {
            var r = Shift(hits[i].Rect);
            if (r.Bottom < 0 || r.Top > ClientSize.Height) continue;
            DrawBadge(g, hits[i], r, i == hover, textColor, titleColor);
        }

        if (emptyText.Length > 0)
            TextRenderer.DrawText(g, emptyText, titleFont, Shift(emptyRect), titleColor,
                TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.HorizontalCenter);

        // Thin scrollbar, only when the content is taller than the window.
        int view = ClientSize.Height;
        if (contentHeight > view && view > 0)
        {
            int thumbH = Math.Max(S(20), view * view / contentHeight);
            int thumbY = (int)((long)scroll * (view - thumbH) / (contentHeight - view));
            var bar = new RectangleF(ClientSize.Width - S(6), thumbY + S(2), S(4), thumbH - S(4));
            using var path = RoundRect(bar, bar.Width / 2);
            using var brush = new SolidBrush(ColorUtil.Parse(cfg.Window.ScrollbarColor, Color.Silver));
            g.FillPath(brush, path);
        }
    }

    private Rectangle Shift(Rectangle r)
    {
        r.Offset(0, -scroll);
        return r;
    }

    private void DrawBadge(Graphics g, Hit hit, Rectangle r, bool hot, Color textColor, Color mutedColor)
    {
        var rf = new RectangleF(r.X + 0.5f, r.Y + 0.5f, r.Width - 1, r.Height - 1);
        float radius = style.CornerRadius < 0 ? rf.Height / 2 : style.CornerRadius * Dpi;
        using var path = RoundRect(rf, radius);

        if (hit.Entry == null)
        {
            // "+ Add" button: dashed border, no fill.
            if (hot)
            {
                using var hb = new SolidBrush(Color.FromArgb(28, mutedColor));
                g.FillPath(hb, path);
            }
            using var dash = new Pen(mutedColor, Math.Max(1, Dpi)) { DashStyle = DashStyle.Dash };
            g.DrawPath(dash, path);
            TextRenderer.DrawText(g, hit.Text, badgeFont, r, mutedColor,
                Flags | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            return;
        }

        var fill = ColorUtil.Lighten(hit.Fill, style.FillLighten);
        if (hot) fill = ColorUtil.Darken(fill, 0.08);

        if (style.Shadow)
        {
            var sr = rf;
            sr.Offset(0, 1.5f * Dpi);
            using var sp = RoundRect(sr, radius);
            using var sb = new SolidBrush(Color.FromArgb(45, 0, 0, 0));
            g.FillPath(sb, sp);
        }

        if (style.Gradient)
        {
            using var lb = new LinearGradientBrush(r, ColorUtil.Lighten(fill, 0.3), ColorUtil.Darken(fill, 0.04), LinearGradientMode.Vertical);
            g.FillPath(lb, path);
        }
        else
        {
            using var b = new SolidBrush(fill);
            g.FillPath(b, path);
        }

        if (style.BorderWidth > 0)
        {
            using var pen = new Pen(ColorUtil.Darken(hit.Fill, style.BorderDarken), style.BorderWidth * Dpi);
            g.DrawPath(pen, path);
        }

        int padX = S(style.PaddingX);
        var textRect = Rectangle.FromLTRB(r.Left + padX, r.Top, r.Right - padX, r.Bottom);
        if (hit.Entry.Pinned && cfg.Badge.ShowPinIcon && iconFont != null)
        {
            int iw = Measure(PinGlyph, iconFont).Width;
            TextRenderer.DrawText(g, PinGlyph, iconFont, new Rectangle(textRect.Left, r.Top, iw, r.Height), textColor,
                Flags | TextFormatFlags.VerticalCenter);
            textRect = Rectangle.FromLTRB(textRect.Left + iw + S(3), r.Top, textRect.Right, r.Bottom);
        }
        TextRenderer.DrawText(g, hit.Text, badgeFont, textRect, textColor,
            Flags | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }

    private static GraphicsPath RoundRect(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        radius = Math.Min(radius, Math.Min(r.Width, r.Height) / 2);
        if (radius <= 0.5f)
        {
            path.AddRectangle(r);
            return path;
        }
        float d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private int HitTest(Point p)
    {
        p.Offset(0, scroll);
        return hits.FindIndex(h => h.Rect.Contains(p));
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int index = HitTest(e.Location);
        if (index == hover) return;
        hover = index;
        Cursor = index >= 0 ? Cursors.Hand : Cursors.Default;
        tip.SetToolTip(this, index < 0 ? null : hits[index].Entry?.Path ?? "Pin a folder (you can also drag a folder here)");
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (hover < 0) return;
        hover = -1;
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        int index = HitTest(e.Location);
        var hit = index >= 0 ? hits[index] : null;
        if (e.Button == MouseButtons.Left && hit != null)
        {
            if (hit.Entry == null) AddClicked?.Invoke();
            else ItemClicked?.Invoke(hit.Entry);
        }
        else if (e.Button == MouseButtons.Right)
        {
            var screen = PointToScreen(e.Location);
            if (hit?.Entry != null) ItemMenu?.Invoke(hit.Entry, screen);
            else BackgroundMenu?.Invoke(screen);
        }
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        scroll -= e.Delta / 120 * lineStep;
        ClampScroll();
        Invalidate();
    }

    protected override void OnDragEnter(DragEventArgs e)
    {
        base.OnDragEnter(e);
        e.Effect = GetDroppedFolders(e).Length > 0 ? DragDropEffects.Link : DragDropEffects.None;
    }

    protected override void OnDragDrop(DragEventArgs e)
    {
        base.OnDragDrop(e);
        var folders = GetDroppedFolders(e);
        if (folders.Length > 0) FoldersDropped?.Invoke(folders);
    }

    private static string[] GetDroppedFolders(DragEventArgs e) =>
        e.Data?.GetData(DataFormats.FileDrop) is string[] paths ? paths.Where(Directory.Exists).ToArray() : Array.Empty<string>();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            tip.Dispose();
            badgeFont?.Dispose();
            titleFont?.Dispose();
            iconFont?.Dispose();
        }
        base.Dispose(disposing);
    }
}
