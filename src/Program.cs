namespace RecentFolders;

internal static class Program
{
    /// <summary>Set by a second copy of the app: the running copy then opens its window.</summary>
    public const string OpenEventName = @"Local\RecentFolders.Open";

    [STAThread]
    private static void Main()
    {
        // Only one copy of the app may run at the same time.
        using var mutex = new Mutex(true, @"Local\RecentFolders.SingleInstance", out bool first);
        if (!first)
        {
            if (EventWaitHandle.TryOpenExisting(OpenEventName, out var open)) open.Set();
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApp());
    }
}
