using System.Text.Json;
using System.Text.Json.Serialization;

namespace RecentFolders;

internal sealed class FolderEntry
{
    public string Path { get; set; } = "";
    public bool Pinned { get; set; }
    public int PinOrder { get; set; }
    /// <summary>Total visits since the beginning.</summary>
    public int Count { get; set; }
    public DateTime LastUsed { get; set; }
    /// <summary>Time (UTC) of the last visits, newest last. Used for "Most used" in the last N days.</summary>
    public List<DateTime> Visits { get; set; } = new();

    [JsonIgnore] public string Name => FolderStore.DisplayName(Path);

    public FolderEntry Clone()
    {
        var copy = (FolderEntry)MemberwiseClone();
        copy.Visits = new List<DateTime>(Visits);
        return copy;
    }
}

internal sealed class StoreData
{
    /// <summary>Value of the tray menu toggle. null = use the value from the .conf file.</summary>
    public bool? OpenOnMouseOver { get; set; }
    public List<FolderEntry> Folders { get; set; } = new();
}

internal sealed record Section(string Key, string Title, List<FolderEntry> Items);

/// <summary>Keeps pinned, recent and most used folders in a small JSON file.</summary>
internal sealed class FolderStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly object gate = new();
    private StoreData data = new();

    public string FilePath { get; }

    /// <summary>Raised after any change. Can be raised on the watcher thread.</summary>
    public event Action? Changed;

    public FolderStore(string filePath) => FilePath = filePath;

    public bool? OpenOnMouseOver
    {
        get { lock (gate) return data.OpenOnMouseOver; }
        set
        {
            lock (gate)
            {
                data.OpenOnMouseOver = value;
                SaveLocked();
            }
        }
    }

    public void Load()
    {
        lock (gate)
        {
            try
            {
                if (File.Exists(FilePath) && new FileInfo(FilePath).Length > 0)
                    data = JsonSerializer.Deserialize<StoreData>(File.ReadAllText(FilePath), Json) ?? new();
            }
            catch
            {
                // Keep a copy of the broken file and start empty.
                try { File.Copy(FilePath, FilePath + ".bad", true); } catch { }
                data = new();
            }
            data.Folders ??= new();
            data.Folders.RemoveAll(f => string.IsNullOrWhiteSpace(f.Path));
            foreach (var f in data.Folders)
            {
                f.Visits ??= new();
                // Data from version 1.0.0 has no visit times: use the last-used time for the old visits.
                if (f.Visits.Count == 0 && f.Count > 0 && f.LastUsed != default)
                    f.Visits.AddRange(Enumerable.Repeat(f.LastUsed, Math.Min(f.Count, MaxVisitsPerFolder)));
            }
        }
    }

    /// <summary>Visit times kept per folder (keeps the data file small).</summary>
    private const int MaxVisitsPerFolder = 100;

    public void RecordVisit(string path, int maxHistory)
    {
        path = Normalize(path);
        lock (gate)
        {
            var entry = FindOrAdd(path);
            entry.Count++;
            entry.LastUsed = DateTime.UtcNow;
            entry.Visits.Add(entry.LastUsed);
            if (entry.Visits.Count > MaxVisitsPerFolder)
                entry.Visits.RemoveRange(0, entry.Visits.Count - MaxVisitsPerFolder);
            var extra = data.Folders.Where(f => !f.Pinned).OrderByDescending(f => f.LastUsed).Skip(maxHistory).ToList();
            foreach (var f in extra) data.Folders.Remove(f);
            SaveLocked();
        }
        Changed?.Invoke();
    }

    public void AddPinned(string path)
    {
        path = Normalize(path);
        lock (gate)
        {
            var entry = FindOrAdd(path);
            if (entry.LastUsed == default) entry.LastUsed = DateTime.UtcNow;
            if (!entry.Pinned)
            {
                entry.Pinned = true;
                entry.PinOrder = NextPinOrder();
            }
            SaveLocked();
        }
        Changed?.Invoke();
    }

    public void SetPinned(string path, bool pinned)
    {
        lock (gate)
        {
            var entry = Find(path);
            if (entry == null) return;
            entry.Pinned = pinned;
            if (pinned) entry.PinOrder = NextPinOrder();
            SaveLocked();
        }
        Changed?.Invoke();
    }

    public void Remove(string path)
    {
        lock (gate)
        {
            data.Folders.RemoveAll(f => SamePath(f.Path, path));
            SaveLocked();
        }
        Changed?.Invoke();
    }

    /// <summary>Removes all folders that are not pinned.</summary>
    public void ClearHistory()
    {
        lock (gate)
        {
            data.Folders.RemoveAll(f => !f.Pinned);
            SaveLocked();
        }
        Changed?.Invoke();
    }

    public List<Section> BuildSections(AppConfig cfg, Func<string, bool> isExcluded)
    {
        var t = cfg.Tracking;
        List<FolderEntry> all;
        lock (gate) all = data.Folders.Select(f => f.Clone()).ToList();

        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<Section>();
        foreach (var key in cfg.Sections.Select(s => s.Trim().ToLowerInvariant()).Distinct())
        {
            IEnumerable<FolderEntry>? items = key switch
            {
                "pinned" => all.Where(f => f.Pinned).OrderBy(f => f.PinOrder),
                "frequent" => all
                    .Select(f => (Entry: f, Uses: UsesInWindow(f, t.FrequentDays)))
                    .Where(x => (t.ShowDuplicates || !x.Entry.Pinned) && x.Uses >= t.MinUsesForFrequent && !isExcluded(x.Entry.Path))
                    .OrderByDescending(x => x.Uses).ThenByDescending(x => x.Entry.LastUsed)
                    .Select(x => x.Entry),
                "recent" => all
                    .Where(f => (t.ShowDuplicates || !f.Pinned) && f.LastUsed != default && !isExcluded(f.Path))
                    .OrderByDescending(f => f.LastUsed),
                _ => null,
            };
            if (items == null) continue;
            if (!t.ShowDuplicates) items = items.Where(f => !used.Contains(f.Path));

            int max = key switch { "frequent" => t.MaxFrequent, "recent" => t.MaxRecent, _ => int.MaxValue };
            var list = items.Take(max).ToList();
            foreach (var f in list) used.Add(f.Path);
            result.Add(new Section(key, Title(key), list));
        }
        return result;
    }

    /// <summary>Visits in the last <paramref name="days"/> days. 0 days = all visits since the beginning.</summary>
    private static int UsesInWindow(FolderEntry f, int days)
    {
        if (days <= 0) return f.Count;
        var since = DateTime.UtcNow.AddDays(-days);
        return f.Visits.Count(v => v >= since);
    }

    private static string Title(string key) => key switch
    {
        "pinned" => "Pinned",
        "frequent" => "Most used",
        _ => "Recent",
    };

    private FolderEntry? Find(string path) => data.Folders.Find(f => SamePath(f.Path, path));

    private FolderEntry FindOrAdd(string path)
    {
        var entry = Find(path);
        if (entry != null) return entry;
        entry = new FolderEntry { Path = path };
        data.Folders.Add(entry);
        return entry;
    }

    private int NextPinOrder() => data.Folders.Where(f => f.Pinned).Select(f => f.PinOrder).DefaultIfEmpty(0).Max() + 1;

    private void SaveLocked()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(data, Json));
            File.Move(tmp, FilePath, true);
        }
        catch
        {
            // Not fatal: we try again on the next change.
        }
    }

    private static bool SamePath(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    public static string Normalize(string path)
    {
        var p = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
        if (p.Length == 2 && p[1] == ':') p += "\\";
        try { p = Path.GetFullPath(p); } catch { }
        if (p.Length > 3) p = p.TrimEnd('\\');
        return p;
    }

    public static string DisplayName(string path)
    {
        var trimmed = path.TrimEnd('\\');
        if (trimmed.Length == 2 && trimmed[1] == ':') return trimmed.ToUpperInvariant();
        var name = Path.GetFileName(trimmed);
        return string.IsNullOrEmpty(name) ? path : name;
    }
}
