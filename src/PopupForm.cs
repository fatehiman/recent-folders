namespace RecentFolders;

/// <summary>Borderless window above the tray icon, with fade in / fade out.</summary>
internal sealed class PopupForm : Form
{
    public readonly BadgePanel Panel = new() { Dock = DockStyle.Fill };

    private readonly System.Windows.Forms.Timer anim = new() { Interval = 15 };
    private readonly System.Windows.Forms.Timer track = new() { Interval = 30 };
    private AppConfig cfg = new();
    private Rectangle desired, iconRect;
    private bool byMouseOver;
    private bool entered; // the mouse was inside the window since it opened
    private int fadeDir; // 1 = fading in, -1 = fading out, 0 = idle
    private double fadeFrom;
    private DateTime fadeStart, lastInside;

    /// <summary>True while a menu or dialog of the popup is open: do not auto-hide.</summary>
    public bool Busy { get; set; }

    /// <summary>Time when the popup hid itself because it lost focus.</summary>
    public DateTime LastDeactivated { get; private set; }

    public bool OpenedByMouseOver => byMouseOver;

    /// <summary>Visible and not fading out.</summary>
    public bool IsOpen => Visible && fadeDir >= 0;

    public PopupForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        KeyPreview = true;
        Text = "Recent Folders";
        Controls.Add(Panel);
        anim.Tick += (_, _) => StepFade();
        track.Tick += (_, _) => TrackMouse();
        // React at the moment the mouse leaves, not only on the next timer tick.
        Panel.MouseLeave += (_, _) => TrackMouse();
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= Native.WS_EX_TOOLWINDOW | Native.WS_EX_TOPMOST;
            return cp;
        }
    }

    public void Apply(AppConfig config)
    {
        cfg = config;
        BackColor = Panel.BackColor = ColorUtil.Parse(config.Window.BackgroundColor, Color.White);
        Panel.Apply(config);
        if (IsHandleCreated) ApplyDwm();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyDwm();
    }

    /// <summary>Windows 11 rounded corners and border color.</summary>
    private void ApplyDwm()
    {
        int corner = (cfg.Window.Corners ?? "").Trim().ToLowerInvariant() switch
        {
            "square" or "none" => Native.DWMWCP_DONOTROUND,
            "small" => Native.DWMWCP_ROUNDSMALL,
            _ => Native.DWMWCP_ROUND,
        };
        Native.DwmSetWindowAttribute(Handle, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));

        var border = (cfg.Window.BorderColor ?? "").Trim();
        int color;
        if (border.Length == 0) color = unchecked((int)Native.DWMWA_COLOR_DEFAULT);
        else if (border.Equals("none", StringComparison.OrdinalIgnoreCase)) color = unchecked((int)Native.DWMWA_COLOR_NONE);
        else
        {
            var c = ColorUtil.Parse(border, Color.Gray);
            color = c.R | (c.G << 8) | (c.B << 16); // COLORREF
        }
        Native.DwmSetWindowAttribute(Handle, Native.DWMWA_BORDER_COLOR, ref color, sizeof(int));
    }

    private double TargetOpacity => cfg.Window.Opacity;

    /// <param name="bounds">Window rectangle in screen pixels.</param>
    /// <param name="icon">Tray icon rectangle (empty if unknown).</param>
    /// <param name="mouseOver">true = opened by mouse over (hides when the mouse leaves); false = opened by click.</param>
    public void ShowAt(Rectangle bounds, Rectangle icon, bool mouseOver)
    {
        desired = bounds;
        iconRect = icon;
        byMouseOver = mouseOver;
        lastInside = DateTime.UtcNow;
        bool wasOpen = IsOpen;

        if (!Visible)
        {
            entered = false;
            Panel.ResetScroll();
            Opacity = cfg.Fade.Enabled ? 0 : TargetOpacity;
            Bounds = bounds;
            Show();
        }
        else
        {
            Bounds = bounds;
        }
        Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);

        if (!mouseOver)
        {
            Activate();
            Native.SetForegroundWindow(Handle);
        }
        if (!wasOpen) StartFade(1);
        track.Start();
    }

    public void HideAnimated()
    {
        if (!Visible || fadeDir < 0) return;
        StartFade(-1);
    }

    public void HideNow()
    {
        anim.Stop();
        track.Stop();
        fadeDir = 0;
        if (Visible) Hide();
    }

    /// <summary>
    /// Call after a menu or dialog closes. The mouse may now be outside the window,
    /// so we wait again until it enters (or until hideDelayMs passes in mouse-over mode).
    /// </summary>
    public void ResumeAfterMenu()
    {
        lastInside = DateTime.UtcNow;
        entered = false;
    }

    private void StartFade(int dir)
    {
        int ms = dir > 0 ? cfg.Fade.FadeInMs : cfg.Fade.FadeOutMs;
        if (!cfg.Fade.Enabled || ms <= 0)
        {
            anim.Stop();
            fadeDir = 0;
            if (dir > 0) Opacity = TargetOpacity;
            else HideNow();
            return;
        }
        fadeDir = dir;
        fadeFrom = Opacity;
        fadeStart = DateTime.UtcNow;
        anim.Start();
    }

    private void StepFade()
    {
        int ms = fadeDir > 0 ? cfg.Fade.FadeInMs : cfg.Fade.FadeOutMs;
        double to = fadeDir > 0 ? TargetOpacity : 0;
        double t = ms <= 0 ? 1 : Math.Min(1, (DateTime.UtcNow - fadeStart).TotalMilliseconds / ms);
        Opacity = fadeFrom + (to - fadeFrom) * t;
        if (t < 1) return;
        anim.Stop();
        if (fadeDir < 0) HideNow();
        fadeDir = 0;
    }

    private void TrackMouse()
    {
        if (!Visible)
        {
            track.Stop();
            return;
        }
        var now = DateTime.UtcNow;
        if (Busy)
        {
            lastInside = now;
            return;
        }

        var cursor = Cursor.Position;
        if (Bounds.Contains(cursor))
        {
            entered = true;
            lastInside = now;
            if (fadeDir < 0) StartFade(1);
            return;
        }

        // The mouse was in the window and now it is out: close at once (no hideDelayMs).
        // This is for both ways of opening (mouse over and click / menu).
        if (entered)
        {
            HideAnimated();
            return;
        }

        // The mouse did not enter the window yet.
        // Opened by click / menu: stay open (it closes when it loses focus).
        if (!byMouseOver) return;

        // Opened by mouse over: the mouse may still be on the icon or on its way to the window.
        // The "hot zone" is the window plus the tray icon (and the space between them).
        var zone = iconRect.IsEmpty ? Bounds : Rectangle.Union(Bounds, iconRect);
        int margin = (int)(6 * DeviceDpi / 96f);
        zone.Inflate(margin, margin);
        if (zone.Contains(cursor))
        {
            lastInside = now;
            if (fadeDir < 0) StartFade(1);
        }
        else if ((DateTime.UtcNow - lastInside).TotalMilliseconds >= cfg.MouseOver.HideDelayMs)
        {
            HideAnimated();
        }
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        if (Busy || !Visible) return;
        LastDeactivated = DateTime.UtcNow;
        HideAnimated();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape) HideAnimated();
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        // Windows suggests its own size; we keep the size we calculated for this monitor.
        if (!desired.IsEmpty) Bounds = desired;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            HideNow();
            return;
        }
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            anim.Dispose();
            track.Dispose();
        }
        base.Dispose(disposing);
    }
}
