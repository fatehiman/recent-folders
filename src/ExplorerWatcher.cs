using System.Runtime.InteropServices;

namespace RecentFolders;

/// <summary>
/// Checks the open File Explorer windows (and tabs) every few seconds.
/// When a folder stays open for "dwell" seconds, it counts as one visit.
/// Folders you only pass through on the way to another folder are not counted.
/// </summary>
internal sealed class ExplorerWatcher : IDisposable
{
    private readonly Action<string> onVisit;
    private readonly ManualResetEvent stop = new(false);
    private readonly Thread thread;
    private readonly Dictionary<string, (DateTime Since, bool Recorded)> seen = new(StringComparer.OrdinalIgnoreCase);
    private volatile int pollSeconds = 5;
    private volatile int dwellSeconds = 30;
    private object? shell;

    public ExplorerWatcher(Action<string> onVisit)
    {
        this.onVisit = onVisit;
        thread = new Thread(Run) { IsBackground = true, Name = "ExplorerWatcher", Priority = ThreadPriority.BelowNormal };
        // Shell.Application is a COM object and needs an STA thread.
        thread.SetApartmentState(ApartmentState.STA);
    }

    public void Configure(int poll, int dwell)
    {
        pollSeconds = poll;
        dwellSeconds = dwell;
    }

    public void Start() => thread.Start();

    private void Run()
    {
        while (!stop.WaitOne(TimeSpan.FromSeconds(pollSeconds)))
        {
            try
            {
                Poll();
            }
            catch
            {
                // Explorer may have restarted. Create the COM object again next time.
                ReleaseShell();
            }
        }
        ReleaseShell();
    }

    private void Poll()
    {
        // Very cheap check first: no Explorer window means no COM work at all.
        if (Native.FindWindow("CabinetWClass", null) == IntPtr.Zero)
        {
            seen.Clear();
            return;
        }

        var now = DateTime.UtcNow;
        var current = GetOpenPaths();

        foreach (var gone in seen.Keys.Where(k => !current.Contains(k)).ToList())
            seen.Remove(gone);

        foreach (var path in current)
        {
            if (!seen.TryGetValue(path, out var state))
            {
                seen[path] = (now, false);
            }
            else if (!state.Recorded && (now - state.Since).TotalSeconds >= dwellSeconds)
            {
                seen[path] = (state.Since, true);
                onVisit(path);
            }
        }
    }

    private HashSet<string> GetOpenPaths()
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        shell ??= Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!);
        dynamic windows = ((dynamic)shell!).Windows();
        try
        {
            int count = windows.Count;
            for (int i = 0; i < count; i++)
            {
                dynamic? window = null;
                try
                {
                    window = windows.Item(i);
                    if (window == null) continue;
                    string fullName = window.FullName ?? "";
                    if (!fullName.EndsWith("explorer.exe", StringComparison.OrdinalIgnoreCase)) continue;
                    string? path = window.Document?.Folder?.Self?.Path;
                    if (IsFileSystemPath(path)) result.Add(FolderStore.Normalize(path!));
                }
                catch
                {
                    // A window can close while we read it.
                }
                finally
                {
                    if (window != null) Marshal.ReleaseComObject((object)window);
                }
            }
        }
        finally
        {
            Marshal.ReleaseComObject((object)windows);
        }
        return result;
    }

    /// <summary>Skip virtual folders like "This PC", "Home", "Recycle Bin" (their path starts with "::").</summary>
    private static bool IsFileSystemPath(string? path) =>
        !string.IsNullOrEmpty(path) &&
        ((path.Length >= 2 && path[1] == ':') || path.StartsWith(@"\\", StringComparison.Ordinal)) &&
        !path.StartsWith("::", StringComparison.Ordinal);

    private void ReleaseShell()
    {
        if (shell == null) return;
        try { Marshal.ReleaseComObject(shell); } catch { }
        shell = null;
    }

    public void Dispose()
    {
        stop.Set();
        thread.Join(2000);
    }
}
