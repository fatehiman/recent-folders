using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace RecentFolders;

/// <summary>Connects the tray icon, the popup window, the folder list and the Explorer watcher.</summary>
internal sealed class TrayApp : ApplicationContext
{
    private const string AppName = "Recent Folders";

    private readonly string exePath;
    private readonly string confPath;
    private readonly FolderStore store;
    private readonly ExplorerWatcher watcher;
    private readonly TrayIcon tray;
    private readonly PopupForm popup;
    private readonly System.Windows.Forms.Timer showTimer = new();
    private readonly EventWaitHandle openEvent;
    private readonly RegisteredWaitHandle openWait;

    private readonly ContextMenuStrip trayMenu = new();
    private readonly ToolStripMenuItem miMouseOver;
    private readonly ToolStripMenuItem miStartup;

    private readonly ContextMenuStrip itemMenu = new();
    private readonly ToolStripMenuItem miPin;
    private FolderEntry? menuTarget;

    private readonly ContextMenuStrip backgroundMenu = new();

    private AppConfig cfg;
    private DateTime confStamp;
    private Func<string, bool> isExcluded = _ => false;

    public TrayApp()
    {
        exePath = Environment.ProcessPath ?? Application.ExecutablePath;
        var dir = Path.GetDirectoryName(exePath)!;
        var baseName = Path.GetFileNameWithoutExtension(exePath);
        confPath = Path.Combine(dir, baseName + ".conf");

        cfg = AppConfig.Load(confPath, out var error);
        confStamp = ConfStamp();
        if (error != null) ShowConfigError(error);
        isExcluded = BuildExcluder(cfg);

        store = new FolderStore(ResolveDataFile(dir, baseName));
        store.Load();
        store.Changed += OnStoreChanged;

        popup = new PopupForm();
        popup.Apply(cfg);
        _ = popup.Handle; // create the window now, so other threads can BeginInvoke on it
        WirePanel();

        // Tray menu
        var miOpen = new ToolStripMenuItem("Open", null, (_, _) => ShowPopup(false));
        miOpen.Font = new Font(miOpen.Font, FontStyle.Bold);
        miMouseOver = new ToolStripMenuItem("Open on mouse over", null, (_, _) => ToggleMouseOver());
        miStartup = new ToolStripMenuItem("Start with Windows", null, (_, _) => ToggleStartup());
        trayMenu.Items.AddRange(new ToolStripItem[]
        {
            miOpen,
            miMouseOver,
            new ToolStripSeparator(),
            new ToolStripMenuItem("Add folder...", null, (_, _) => AddFolderDialog(null)),
            new ToolStripMenuItem("Clear history...", null, (_, _) => ClearHistory()),
            new ToolStripSeparator(),
            new ToolStripMenuItem("Settings...", null, (_, _) => OpenSettings()),
            miStartup,
            new ToolStripSeparator(),
            new ToolStripMenuItem("Exit", null, (_, _) => ExitThread()),
        });

        // Badge menu
        miPin = new ToolStripMenuItem("Pin", null, (_, _) => { if (menuTarget != null) store.SetPinned(menuTarget.Path, !menuTarget.Pinned); });
        var miItemOpen = new ToolStripMenuItem("Open", null, (_, _) => { if (menuTarget != null) OpenFolder(menuTarget.Path); });
        miItemOpen.Font = new Font(miItemOpen.Font, FontStyle.Bold);
        itemMenu.Items.AddRange(new ToolStripItem[]
        {
            miItemOpen,
            miPin,
            new ToolStripMenuItem("Copy path", null, (_, _) => { if (menuTarget != null) Clipboard.SetText(menuTarget.Path); }),
            new ToolStripSeparator(),
            new ToolStripMenuItem("Remove from list", null, (_, _) => { if (menuTarget != null) store.Remove(menuTarget.Path); }),
        });
        itemMenu.Closed += (_, _) => MenuClosed();

        // Menu on the empty area of the window
        backgroundMenu.Items.AddRange(new ToolStripItem[]
        {
            new ToolStripMenuItem("Add folder...", null, (_, _) => AddFolderDialog(popup)),
            new ToolStripMenuItem("Settings...", null, (_, _) => OpenSettings()),
        });
        backgroundMenu.Closed += (_, _) => MenuClosed();

        tray = new TrayIcon(LoadIcon(), AppName) { ShowTip = !MouseOverEnabled };
        tray.MouseMoved += OnTrayMouseMove;
        tray.LeftClick += OnTrayClick;
        tray.RightClick += ShowTrayMenu;
        tray.Show();

        showTimer.Tick += OnShowTimer;

        watcher = new ExplorerWatcher(OnVisit);
        watcher.Configure(cfg.Tracking.PollIntervalSeconds, cfg.Tracking.DwellSeconds);
        watcher.Start();

        // Starting the exe again opens the window of this copy.
        openEvent = new EventWaitHandle(false, EventResetMode.AutoReset, Program.OpenEventName);
        openWait = ThreadPool.RegisterWaitForSingleObject(openEvent,
            (_, _) => popup.BeginInvoke(() => ShowPopup(false)), null, Timeout.Infinite, false);
    }


    private bool MouseOverEnabled => store.OpenOnMouseOver ?? cfg.MouseOver.OpenOnMouseOver;

    // ---------------- Tray icon ----------------

    private void OnTrayMouseMove()
    {
        if (!MouseOverEnabled || trayMenu.Visible || popup.IsOpen || showTimer.Enabled) return;
        if (cfg.MouseOver.ShowDelayMs <= 0)
        {
            ShowPopup(true);
            return;
        }
        showTimer.Interval = cfg.MouseOver.ShowDelayMs;
        showTimer.Start();
    }

    private void OnShowTimer(object? sender, EventArgs e)
    {
        showTimer.Stop();
        if (trayMenu.Visible || popup.IsOpen) return;
        // Only open if the mouse is still on the icon.
        if (tray.GetIconRect() is { } rect)
        {
            rect.Inflate(2, 2);
            if (!rect.Contains(Cursor.Position)) return;
        }
        ShowPopup(true);
    }

    private void OnTrayClick()
    {
        showTimer.Stop();
        if (popup.IsOpen && !popup.OpenedByMouseOver)
        {
            popup.HideAnimated();
            return;
        }
        // The click on the tray made the popup lose focus and start to hide: keep it closed.
        if ((DateTime.UtcNow - popup.LastDeactivated).TotalMilliseconds < 400) return;
        ShowPopup(false);
    }

    private void ShowTrayMenu()
    {
        showTimer.Stop();
        popup.HideNow();
        miMouseOver.Checked = MouseOverEnabled;
        miStartup.Checked = Autostart.IsEnabled(exePath);
        Native.SetForegroundWindow(tray.Handle);
        trayMenu.Show(Cursor.Position);
    }

    private void ToggleMouseOver()
    {
        store.OpenOnMouseOver = !MouseOverEnabled;
        tray.ShowTip = !MouseOverEnabled;
    }

    private void ToggleStartup()
    {
        try
        {
            Autostart.Set(!Autostart.IsEnabled(exePath), exePath);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    // ---------------- Popup ----------------

    private void ShowPopup(bool mouseOver)
    {
        ReloadConfigIfChanged();
        popup.Panel.SetSections(store.BuildSections(cfg, isExcluded));
        var icon = tray.GetIconRect() ?? Rectangle.Empty;
        popup.ShowAt(ComputeBounds(icon), icon, mouseOver);
    }

    /// <summary>Puts the window just above the taskbar (or next to it, if the taskbar is on another edge), centered on the icon.</summary>
    private Rectangle ComputeBounds(Rectangle icon)
    {
        var anchor = icon.IsEmpty ? new Rectangle(Cursor.Position, new Size(1, 1)) : icon;
        var center = new Point(anchor.Left + anchor.Width / 2, anchor.Top + anchor.Height / 2);
        double scale = MonitorScale(center);
        int w = (int)Math.Round(cfg.Window.Width * scale);
        int h = (int)Math.Round(cfg.Window.Height * scale);
        int gap = (int)Math.Round(cfg.Window.Gap * scale);

        var screen = Screen.FromPoint(center);
        var bounds = screen.Bounds;
        var work = screen.WorkingArea;

        var abd = new Native.APPBARDATA { cbSize = Marshal.SizeOf<Native.APPBARDATA>() };
        Rectangle bar = Rectangle.Empty;
        int edge = Native.ABE_BOTTOM;
        if (Native.SHAppBarMessage(Native.ABM_GETTASKBARPOS, ref abd) != IntPtr.Zero)
        {
            bar = abd.rc.ToRectangle();
            edge = abd.uEdge;
        }
        if (bar.IsEmpty || !bar.IntersectsWith(bounds))
        {
            // Taskbar of another monitor: use the free space of this monitor instead.
            bar = Rectangle.FromLTRB(bounds.Left, work.Bottom, bounds.Right, bounds.Bottom);
            edge = Native.ABE_BOTTOM;
        }

        int x, y;
        switch (edge)
        {
            case Native.ABE_TOP:
                x = center.X - w / 2;
                y = Math.Max(bar.Bottom, anchor.Bottom) + gap;
                break;
            case Native.ABE_LEFT:
                x = Math.Max(bar.Right, anchor.Right) + gap;
                y = center.Y - h / 2;
                break;
            case Native.ABE_RIGHT:
                x = Math.Min(bar.Left, anchor.Left) - gap - w;
                y = center.Y - h / 2;
                break;
            default:
                // Bottom taskbar (normal on Windows 11). Min() also handles icons in the "hidden icons" flyout.
                x = center.X - w / 2;
                y = Math.Min(bar.Top, anchor.Top) - gap - h;
                break;
        }
        x = Math.Clamp(x, bounds.Left + gap, Math.Max(bounds.Left + gap, bounds.Right - w - gap));
        y = Math.Clamp(y, bounds.Top + gap, Math.Max(bounds.Top + gap, bounds.Bottom - h - gap));
        return new Rectangle(x, y, w, h);
    }

    private static double MonitorScale(Point p)
    {
        var monitor = Native.MonitorFromPoint(new Native.POINT { X = p.X, Y = p.Y }, Native.MONITOR_DEFAULTTONEAREST);
        return Native.GetDpiForMonitor(monitor, 0, out uint dpi, out _) == 0 ? dpi / 96.0 : 1.0;
    }

    private void WirePanel()
    {
        var panel = popup.Panel;
        panel.ItemClicked += e => OpenFolder(e.Path);
        panel.AddClicked += () => AddFolderDialog(popup);
        panel.FoldersDropped += paths =>
        {
            foreach (var p in paths) store.AddPinned(p);
        };
        panel.ItemMenu += (entry, pt) =>
        {
            menuTarget = entry;
            miPin.Text = entry.Pinned ? "Unpin" : "Pin";
            ShowPopupMenu(itemMenu, pt);
        };
        panel.BackgroundMenu += pt => ShowPopupMenu(backgroundMenu, pt);
    }

    private void ShowPopupMenu(ContextMenuStrip menu, Point pt)
    {
        popup.Busy = true;
        menu.Show(pt);
    }

    private void MenuClosed()
    {
        popup.Busy = false;
        popup.TouchInside();
    }

    private void OnStoreChanged()
    {
        if (popup.InvokeRequired)
        {
            popup.BeginInvoke(OnStoreChanged);
            return;
        }
        if (popup.Visible) popup.Panel.SetSections(store.BuildSections(cfg, isExcluded));
    }

    // ---------------- Actions ----------------

    private void OpenFolder(string path)
    {
        if (!Directory.Exists(path))
        {
            popup.HideNow();
            var answer = MessageBox.Show($"This folder was not found:\n{path}\n\nRemove it from the list?",
                AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (answer == DialogResult.Yes) store.Remove(path);
            return;
        }
        popup.HideAnimated();
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void AddFolderDialog(IWin32Window? owner)
    {
        popup.Busy = true;
        try
        {
            using var dialog = new FolderBrowserDialog
            {
                Description = "Choose a folder to pin",
                UseDescriptionForTitle = true,
                ShowNewFolderButton = false,
            };
            var result = owner == null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
            if (result == DialogResult.OK && Directory.Exists(dialog.SelectedPath))
                store.AddPinned(dialog.SelectedPath);
        }
        finally
        {
            popup.Busy = false;
            popup.TouchInside();
        }
    }

    private void ClearHistory()
    {
        var answer = MessageBox.Show("Remove all recent and most used folders?\nPinned folders are kept.",
            AppName, MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
        if (answer == DialogResult.OK) store.ClearHistory();
    }

    private void OpenSettings()
    {
        popup.HideNow();
        try
        {
            if (!File.Exists(confPath)) AppConfig.Load(confPath, out _);
            Process.Start(new ProcessStartInfo("notepad.exe", $"\"{confPath}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    // ---------------- Tracking ----------------

    /// <summary>Called on the watcher thread when a folder was open for "dwellSeconds".</summary>
    private void OnVisit(string path)
    {
        if (isExcluded(path) || !Directory.Exists(path)) return;
        store.RecordVisit(path, cfg.Tracking.MaxHistory);
    }

    private static Func<string, bool> BuildExcluder(AppConfig config)
    {
        var patterns = config.Tracking.ExcludePaths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => new Regex(
                "^" + Regex.Escape(p.Trim().Replace('/', '\\').TrimEnd('\\')).Replace(@"\*", ".*").Replace(@"\?", ".") + "$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            .ToList();
        return path => patterns.Any(r => r.IsMatch(path));
    }

    // ---------------- Settings and files ----------------

    private DateTime ConfStamp() => File.Exists(confPath) ? File.GetLastWriteTimeUtc(confPath) : DateTime.MinValue;

    /// <summary>The .conf file is read again when it was changed (checked each time the window opens).</summary>
    private void ReloadConfigIfChanged()
    {
        var stamp = ConfStamp();
        if (stamp == confStamp) return;
        confStamp = stamp;
        var next = AppConfig.Load(confPath, out var error);
        if (error != null)
        {
            ShowConfigError(error, keepOld: true);
            return;
        }
        cfg = next;
        isExcluded = BuildExcluder(cfg);
        popup.Apply(cfg);
        watcher.Configure(cfg.Tracking.PollIntervalSeconds, cfg.Tracking.DwellSeconds);
        tray.ShowTip = !MouseOverEnabled;
    }

    private void ShowConfigError(string error, bool keepOld = false)
    {
        MessageBox.Show($"There is an error in the settings file:\n{confPath}\n\n{error}\n\n" +
                        (keepOld ? "The previous settings are still used." : "Default settings are used."),
            AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    /// <summary>Data file is next to the exe; if that folder is read-only, it goes to %LOCALAPPDATA%.</summary>
    private string ResolveDataFile(string dir, string baseName)
    {
        var path = string.IsNullOrWhiteSpace(cfg.DataFile)
            ? Path.Combine(dir, baseName + ".data.json")
            : Path.GetFullPath(Environment.ExpandEnvironmentVariables(cfg.DataFile), dir);
        if (CanWrite(path)) return path;
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "recent-folders", baseName + ".data.json");
    }

    private static bool CanWrite(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var test = path + ".write-test";
            File.WriteAllText(test, "");
            File.Delete(test);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static Icon LoadIcon()
    {
        using var stream = typeof(TrayApp).Assembly.GetManifestResourceStream("app.ico")!;
        int size = Native.GetSystemMetrics(Native.SM_CXSMICON);
        return new Icon(stream, size, size);
    }

    protected override void ExitThreadCore()
    {
        openWait.Unregister(null);
        openEvent.Dispose();
        watcher.Dispose();
        tray.Dispose();
        showTimer.Dispose();
        popup.Dispose();
        trayMenu.Dispose();
        itemMenu.Dispose();
        backgroundMenu.Dispose();
        base.ExitThreadCore();
    }
}
