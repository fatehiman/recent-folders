using System.Runtime.InteropServices;

namespace RecentFolders;

/// <summary>
/// Own tray icon (instead of WinForms NotifyIcon), because we need the icon position
/// (Shell_NotifyIconGetRect) and control over the standard tooltip.
/// </summary>
internal sealed class TrayIcon : NativeWindow, IDisposable
{
    private const int CallbackMessage = Native.WM_APP + 1;
    private const int IconId = 1;
    private static readonly int TaskbarCreated = Native.RegisterWindowMessage("TaskbarCreated");

    private readonly Icon icon;
    private readonly string tooltip;
    private bool showTip = true;
    private bool added;

    public event Action? MouseMoved;
    public event Action? LeftClick;
    public event Action? RightClick;

    public TrayIcon(Icon icon, string tooltip)
    {
        this.icon = icon;
        this.tooltip = tooltip;
        CreateHandle(new CreateParams { Caption = "RecentFoldersTray" });
    }

    /// <summary>Show the standard Windows tooltip. We hide it when our own window opens on mouse over.</summary>
    public bool ShowTip
    {
        get => showTip;
        set
        {
            showTip = value;
            if (added) Send(Native.NIM_MODIFY);
        }
    }

    public void Show()
    {
        added = Send(Native.NIM_ADD);
        if (!added) return;
        var data = Data(0);
        data.uTimeoutOrVersion = Native.NOTIFYICON_VERSION_4;
        Native.Shell_NotifyIcon(Native.NIM_SETVERSION, ref data);
    }

    /// <summary>Screen rectangle of the icon, or null when Windows does not tell it.</summary>
    public Rectangle? GetIconRect()
    {
        var id = new Native.NOTIFYICONIDENTIFIER
        {
            cbSize = Marshal.SizeOf<Native.NOTIFYICONIDENTIFIER>(),
            hWnd = Handle,
            uID = IconId,
        };
        return Native.Shell_NotifyIconGetRect(ref id, out var rect) == 0 ? rect.ToRectangle() : null;
    }

    private bool Send(int message)
    {
        int flags = Native.NIF_MESSAGE | Native.NIF_ICON | Native.NIF_TIP | (showTip ? Native.NIF_SHOWTIP : 0);
        var data = Data(flags);
        return Native.Shell_NotifyIcon(message, ref data);
    }

    private Native.NOTIFYICONDATA Data(int flags) => new()
    {
        cbSize = Marshal.SizeOf<Native.NOTIFYICONDATA>(),
        hWnd = Handle,
        uID = IconId,
        uFlags = flags,
        uCallbackMessage = CallbackMessage,
        hIcon = icon.Handle,
        szTip = tooltip,
        szInfo = "",
        szInfoTitle = "",
    };

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == CallbackMessage)
        {            switch ((int)(m.LParam.ToInt64() & 0xFFFF))
            {
                case Native.WM_MOUSEMOVE:
                case Native.NIN_POPUPOPEN:
                    MouseMoved?.Invoke();
                    break;
                case Native.NIN_SELECT:
                case Native.NIN_KEYSELECT:
                    LeftClick?.Invoke();
                    break;
                case Native.WM_CONTEXTMENU:
                    RightClick?.Invoke();
                    break;
            }
            return;
        }
        if (m.Msg == TaskbarCreated)
        {
            // Explorer was restarted: add the icon again.
            Show();
        }
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        if (added)
        {
            var data = Data(0);
            Native.Shell_NotifyIcon(Native.NIM_DELETE, ref data);
            added = false;
        }
        DestroyHandle();
    }
}
