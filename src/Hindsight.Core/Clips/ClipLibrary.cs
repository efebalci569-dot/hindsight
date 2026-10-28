using System.Security.Cryptography;
using System.Text;

namespace Hindsight.Core.Clips;

public enum ClipKind { Clip, Recording, Screenshot, Gif }

/// <summary>What Hindsight remembers about a file in the clips folder (the file itself is the source of truth).</summary>
public sealed class ClipMeta
{
    public string? GameId { get; set; }

    public string? GameName { get; set; }

    public ClipKind Kind { get; set; }

    public double DurationSeconds { get; set; }

    public int Width { get; set; }

    public int Height { get; set; }

    public bool Favorite { get; set; }

    public DateTime CreatedAt { get; set; }
}

public sealed class ClipItem
{
    public required string Path { get; init; }

    public required ClipMeta Meta { get; init; }

    public long SizeBytes { get; set; }

    public string FileName => System.IO.Path.GetFileNameWithoutExtension(Path);

    public string GameName => Meta.GameName ?? "Diğer";

    public bool IsVideo => Meta.Kind is ClipKind.Clip or ClipKind.Recording;

    public bool IsImage => Meta.Kind is ClipKind.Screenshot or ClipKind.Gif;

    /// <summary>Cache file name for the thumbnail: changes when the file changes.</summary>
    public string ThumbnailKey
    {
        get
        {
            var stamp = $"{Path.ToLowerInvariant()}|{SizeBytes}|{Meta.CreatedAt.Ticks}";
            return Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(stamp)))[..20].ToLowerInvariant();
        }
    }
}

/// <summary>
/// Clips live in &lt;clips folder&gt;\&lt;game&gt;\… — one folder per game, so they are also tidy in Explorer.
/// A small JSON index adds what the file system cannot store (game id, favourite, duration).
/// </summary>
public sealed class ClipLibrary
{
    private static readonly string[] Extensions = [".mp4", ".mkv", ".png", ".jpg", ".gif"];
    private readonly Lock _gate = new();
    private readonly Dictionary<string, ClipMeta> _index;
    private List<ClipItem> _items = [];

    public ClipLibrary(string root)
    {
        Root = root;
        _index = new Dictionary<string, ClipMeta>(
            JsonFile.Load<Dictionary<string, ClipMeta>>(AppPaths.ClipIndexFile) ?? [], StringComparer.OrdinalIgnoreCase);
    }

    public string Root { get; private set; }

    public event EventHandler? Changed;

    public IReadOnlyList<ClipItem> Items
    {
        get
        {
            lock (_gate)
            {
                return [.. _items];
            }
        }
    }

    public long TotalBytes
    {
        get
        {
            lock (_gate)
            {
                return _items.Sum(i => i.SizeBytes);
            }
        }
    }

    public void SetRoot(string root)
    {
        Root = root;
        Refresh();
    }

    /// <summary>Re-reads the folder (fast: one directory walk, no media probing).</summary>
    public void Refresh()
    {
        var items = new List<ClipItem>();
        Dictionary<string, ClipItem> existing;
        lock (_gate)
        {
            existing = _items.ToDictionary(i => i.Path, StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            Directory.CreateDirectory(Root);
            foreach (var file in Directory.EnumerateFiles(Root, "*.*", SearchOption.AllDirectories))
            {
                var ext = Path.GetExtension(file).ToLowerInvariant();
                if (!Extensions.Contains(ext) || file.EndsWith(".part.mp4", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                FileInfo info;
                try
                {
                    info = new FileInfo(file);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                var key = Relative(file);
                ClipMeta meta;
                lock (_gate)
                {
                    if (!_index.TryGetValue(key, out meta!))
                    {
                        meta = Guess(file, info);
                        _index[key] = meta;
                    }
                }

                // Reuse unchanged items so the UI keeps its tiles (and their loaded thumbnails).
                items.Add(existing.TryGetValue(file, out var known) && known.SizeBytes == info.Length && ReferenceEquals(known.Meta, meta)
                    ? known
                    : new ClipItem { Path = file, Meta = meta, SizeBytes = info.Length });
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Could not read the clips folder: {ex.Message}");
        }

        lock (_gate)
        {
            _items = items.OrderByDescending(i => i.Meta.CreatedAt).ToList();
            var live = _items.Select(i => Relative(i.Path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var stale in _index.Keys.Where(k => !live.Contains(k)).ToList())
            {
                _index.Remove(stale);
            }
        }

        SaveIndex();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public string NewClipPath(string gameName, ClipKind kind)
    {
        var folder = Path.Combine(Root, AppPaths.SafeName(gameName));
        if (kind == ClipKind.Screenshot)
        {
            folder = Path.Combine(folder, "Ekran Görüntüleri");
        }

        Directory.CreateDirectory(folder);
        var ext = kind switch
        {
            ClipKind.Screenshot => ".png",
            ClipKind.Gif => ".gif",
            _ => ".mp4",
        };
        var prefix = kind == ClipKind.Recording ? "Kayıt " : string.Empty;
        var name = $"{prefix}{AppPaths.SafeName(gameName)} {DateTime.Now:yyyy-MM-dd HH-mm-ss}";
        var path = Path.Combine(folder, name + ext);
        for (var i = 2; File.Exists(path); i++)
        {
            path = Path.Combine(folder, $"{name} ({i}){ext}");
        }

        return path;
    }

    public ClipItem Add(string path, ClipMeta meta)
    {
        var item = new ClipItem { Path = path, Meta = meta, SizeBytes = SafeLength(path) };
        lock (_gate)
        {
            _index[Relative(path)] = meta;
            _items.RemoveAll(i => i.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
            _items.Insert(0, item);
        }

        SaveIndex();
        Changed?.Invoke(this, EventArgs.Empty);
        return item;
    }

    public void SetFavorite(ClipItem item, bool favorite)
    {
        item.Meta.Favorite = favorite;
        SaveIndex();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void UpdateMeta(ClipItem item)
    {
        SaveIndex();
    }

    /// <summary>Renames the file (same folder). Returns the new item, or an error message.</summary>
    public (ClipItem? Item, string? Error) Rename(ClipItem item, string newName)
    {
        var clean = AppPaths.SafeName(newName, string.Empty);
        if (clean.Length == 0)
        {
            return (null, "Geçerli bir ad yaz.");
        }

        var target = Path.Combine(Path.GetDirectoryName(item.Path)!, clean + Path.GetExtension(item.Path));
        if (File.Exists(target))
        {
            return (null, "Bu adla bir dosya zaten var.");
        }

        try
        {
            File.Move(item.Path, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, "Yeniden adlandırılamadı: " + ex.Message);
        }

        lock (_gate)
        {
            _index.Remove(Relative(item.Path));
            _index[Relative(target)] = item.Meta;
            _items.RemoveAll(i => i.Path.Equals(item.Path, StringComparison.OrdinalIgnoreCase));
        }

        var renamed = new ClipItem { Path = target, Meta = item.Meta, SizeBytes = item.SizeBytes };
        lock (_gate)
        {
            _items.Add(renamed);
            _items = _items.OrderByDescending(i => i.Meta.CreatedAt).ToList();
        }

        SaveIndex();
        Changed?.Invoke(this, EventArgs.Empty);
        return (renamed, null);
    }

    /// <summary>Moves the file to the Recycle Bin (recoverable).</summary>
    public bool Recycle(ClipItem item)
    {
        if (!RecycleBin.Send(item.Path))
        {
            return false;
        }

        Forget(item.Path);
        return true;
    }

    /// <summary>Deletes for good — only used by the opt-in storage limit.</summary>
    internal bool DeletePermanently(ClipItem item)
    {
        try
        {
            File.Delete(item.Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        Forget(item.Path);
        return true;
    }

    private void Forget(string path)
    {
        lock (_gate)
        {
            _index.Remove(Relative(path));
            _items.RemoveAll(i => i.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
        }

        SaveIndex();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// With the storage limit on, removes the oldest non-favourite clips until the folder fits. Returns how many were removed.
    /// </summary>
    public int EnforceLimit(long limitBytes)
    {
        var removed = 0;
        var candidates = Items.Where(i => !i.Meta.Favorite).OrderBy(i => i.Meta.CreatedAt).ToList();
        var total = TotalBytes;
        foreach (var item in candidates)
        {
            if (total <= limitBytes)
            {
                break;
            }

            if (DeletePermanently(item))
            {
                total -= item.SizeBytes;
                removed++;
            }
        }

        if (removed > 0)
        {
            Log.Info($"Storage limit: removed {removed} old clip(s).");
        }

        return removed;
    }

    private ClipMeta Guess(string file, FileInfo info)
    {
        var ext = Path.GetExtension(file).ToLowerInvariant();
        var parent = Path.GetDirectoryName(file)!;
        if (Path.GetFileName(parent).Equals("Ekran Görüntüleri", StringComparison.OrdinalIgnoreCase))
        {
            parent = Path.GetDirectoryName(parent)!;
        }

        var inRoot = string.Equals(Path.GetFullPath(parent).TrimEnd('\\'), Path.GetFullPath(Root).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        return new ClipMeta
        {
            GameName = inRoot ? "Diğer" : Path.GetFileName(parent),
            Kind = ext switch
            {
                ".png" or ".jpg" => ClipKind.Screenshot,
                ".gif" => ClipKind.Gif,
                _ => Path.GetFileName(file).StartsWith("Kayıt ", StringComparison.OrdinalIgnoreCase) ? ClipKind.Recording : ClipKind.Clip,
            },
            CreatedAt = info.CreationTime,
        };
    }

    private string Relative(string path) => Path.GetRelativePath(Root, path);

    private static long SafeLength(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private void SaveIndex()
    {
        Dictionary<string, ClipMeta> copy;
        lock (_gate)
        {
            copy = new Dictionary<string, ClipMeta>(_index);
        }

        JsonFile.Save(AppPaths.ClipIndexFile, copy);
    }
}
