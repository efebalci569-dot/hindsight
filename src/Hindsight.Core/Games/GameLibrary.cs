using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace Hindsight.Core.Games;

/// <summary>
/// The list of games: what the scanners found, plus games added by hand or detected while playing.
/// Matching a running process to a game is a longest-prefix match on install folders (cheap enough to run every 2 s).
/// </summary>
public sealed class GameLibrary
{
    private readonly Lock _gate = new();
    private List<GameEntry> _games;
    private (string Prefix, GameEntry Game)[] _directoryIndex = [];
    private Dictionary<string, GameEntry> _processIndex = new(StringComparer.OrdinalIgnoreCase);

    public GameLibrary()
    {
        _games = JsonFile.Load<List<GameEntry>>(AppPaths.GamesFile) ?? [];
        RebuildIndex();
    }

    public event EventHandler? Changed;

    public DateTime? LastScan { get; private set; }

    public IReadOnlyList<GameEntry> Games
    {
        get
        {
            lock (_gate)
            {
                return [.. _games];
            }
        }
    }

    public static IReadOnlyList<IGameScanner> DefaultScanners() =>
    [
        new SteamScanner(), new EpicScanner(), new GogScanner(), new UbisoftScanner(), new UninstallRegistryScanner(), new XboxScanner(),
    ];

    /// <summary>Runs every scanner. Scanned games are replaced; manual and detected ones (and user flags) are kept.</summary>
    public async Task<int> ScanAsync(CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        var found = await Task.Run(() =>
        {
            var list = new List<GameEntry>();
            foreach (var scanner in DefaultScanners())
            {
                try
                {
                    list.AddRange(scanner.Scan(cancellationToken));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Log.Error($"Scanner {scanner.GetType().Name} failed", ex);
                }
            }

            return list;
        }, cancellationToken).ConfigureAwait(false);

        lock (_gate)
        {
            var previous = _games.ToDictionary(g => g.Id, StringComparer.OrdinalIgnoreCase);
            var merged = new List<GameEntry>();
            foreach (var game in found.DistinctBy(g => g.Id, StringComparer.OrdinalIgnoreCase))
            {
                if (previous.TryGetValue(game.Id, out var old))
                {
                    game.Hidden = old.Hidden;
                    game.AddedAt = old.AddedAt;
                    if (old.Name != game.Name && old.Source is GameSource.Manual)
                    {
                        game.Name = old.Name;
                    }
                }

                merged.Add(game);
            }

            // Keep games the user added or Hindsight detected, unless a scanner now knows the same folder.
            var scannedDirs = merged.Where(g => g.InstallDirectory is not null)
                .Select(g => Normalize(g.InstallDirectory!)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            merged.AddRange(_games.Where(g => g.Source is GameSource.Manual or GameSource.Detected &&
                                              !(g.Source == GameSource.Detected && g.InstallDirectory is not null &&
                                                scannedDirs.Any(d => Normalize(g.InstallDirectory).StartsWith(d, StringComparison.OrdinalIgnoreCase)))));
            _games = merged.OrderBy(g => g.Source).ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            RebuildIndex();
            LastScan = DateTime.Now;
        }

        Save();
        Log.Info($"Game scan: {found.Count} found in {sw.ElapsedMilliseconds} ms.");
        Changed?.Invoke(this, EventArgs.Empty);
        return found.Count;
    }

    public GameEntry? Find(string? id)
    {
        if (id is null)
        {
            return null;
        }

        lock (_gate)
        {
            return _games.FirstOrDefault(g => g.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>The game a process belongs to, by exe path (install folder) or, if the path is hidden, by exe name.</summary>
    public GameEntry? Match(string? exePath, string processName)
    {
        lock (_gate)
        {
            if (exePath is not null)
            {
                var path = Normalize(exePath);
                foreach (var (prefix, game) in _directoryIndex)
                {
                    if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        return game;
                    }
                }
            }

            return _processIndex.TryGetValue(processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? processName : processName + ".exe", out var byName)
                ? byName
                : null;
        }
    }

    public GameEntry AddManual(string exePath, string? name = null)
    {
        var dir = Path.GetDirectoryName(exePath)!;
        var game = new GameEntry
        {
            Id = "manual:" + ShortHash(exePath),
            Name = string.IsNullOrWhiteSpace(name) ? FriendlyName(exePath) : name.Trim(),
            Source = GameSource.Manual,
            InstallDirectory = dir,
            ExecutablePath = exePath,
            IconSource = exePath,
            ProcessNames = [Path.GetFileName(exePath)],
        };
        Upsert(game);
        return game;
    }

    /// <summary>A full-screen app that is not in the library — remembered so its clips get a proper folder.</summary>
    public GameEntry AddDetected(string? exePath, string processName, string windowTitle)
    {
        var key = exePath ?? processName;
        var name = exePath is not null ? FriendlyName(exePath) : processName;
        if (!string.IsNullOrWhiteSpace(windowTitle) && windowTitle.Length <= 40 && name.Equals(processName, StringComparison.OrdinalIgnoreCase))
        {
            name = windowTitle.Trim();
        }

        var game = new GameEntry
        {
            Id = "detected:" + ShortHash(key),
            Name = name,
            Source = GameSource.Detected,
            InstallDirectory = exePath is null ? null : Path.GetDirectoryName(exePath),
            ExecutablePath = exePath,
            IconSource = exePath,
            ProcessNames = [processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? processName : processName + ".exe"],
        };

        // Detected games match by exact exe, not by folder (the folder may hold unrelated tools).
        if (Find(game.Id) is { } existing)
        {
            return existing;
        }

        Upsert(game);
        Log.Info($"New full-screen app detected as a game: {game.Name} ({key})");
        return game;
    }

    public void Rename(string id, string name) => Mutate(id, g => g.Name = name.Trim());

    public void SetHidden(string id, bool hidden) => Mutate(id, g => g.Hidden = hidden);

    public void RemoveWhere(Func<GameEntry, bool> predicate)
    {
        int removed;
        lock (_gate)
        {
            removed = _games.RemoveAll(g => predicate(g));
            if (removed > 0)
            {
                RebuildIndex();
            }
        }

        if (removed > 0)
        {
            Save();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Remove(string id)
    {
        lock (_gate)
        {
            _games.RemoveAll(g => g.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            RebuildIndex();
        }

        Save();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Mutate(string id, Action<GameEntry> change)
    {
        lock (_gate)
        {
            var game = _games.FirstOrDefault(g => g.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (game is null)
            {
                return;
            }

            change(game);
            RebuildIndex();
        }

        Save();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Upsert(GameEntry game)
    {
        lock (_gate)
        {
            _games.RemoveAll(g => g.Id.Equals(game.Id, StringComparison.OrdinalIgnoreCase));
            _games.Add(game);
            RebuildIndex();
        }

        Save();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Save()
    {
        List<GameEntry> copy;
        lock (_gate)
        {
            copy = [.. _games];
        }

        JsonFile.Save(AppPaths.GamesFile, copy);
    }

    private void RebuildIndex()
    {
        // Longest folder first so a game inside another game's folder still wins.
        _directoryIndex = _games
            .Where(g => g.Source != GameSource.Detected && !string.IsNullOrWhiteSpace(g.InstallDirectory) && !IsTooBroad(g.InstallDirectory!))
            .Select(g => (Prefix: Normalize(g.InstallDirectory!), Game: g))
            .OrderByDescending(p => p.Prefix.Length)
            .ToArray();

        _processIndex = new Dictionary<string, GameEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var game in _games)
        {
            foreach (var name in game.ProcessNames)
            {
                _processIndex.TryAdd(name, game);
            }

            if (game.ExecutablePath is not null)
            {
                _processIndex.TryAdd(Path.GetFileName(game.ExecutablePath), game);
            }
        }
    }

    /// <summary>A drive root or Program Files itself would match everything.</summary>
    private static bool IsTooBroad(string directory)
    {
        var full = Normalize(directory);
        string[] broad =
        [
            Normalize(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)),
            Normalize(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)),
            Normalize(Environment.GetFolderPath(Environment.SpecialFolder.Windows)),
        ];
        return full.Length <= 4 || broad.Contains(full, StringComparer.OrdinalIgnoreCase);
    }

    internal static string Normalize(string path)
    {
        try
        {
            return Path.GetFullPath(path).TrimEnd('\\') + "\\";
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path.TrimEnd('\\') + "\\";
        }
    }

    private static string FriendlyName(string exePath)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(exePath);
            var product = info.ProductName?.Trim();
            if (!string.IsNullOrWhiteSpace(product) && product.Length <= 60 &&
                !product.Contains("Microsoft", StringComparison.OrdinalIgnoreCase) && !product.Contains("Unity", StringComparison.OrdinalIgnoreCase) &&
                !product.Contains("Unreal", StringComparison.OrdinalIgnoreCase))
            {
                return product;
            }

            var description = info.FileDescription?.Trim();
            if (!string.IsNullOrWhiteSpace(description) && description.Length <= 60)
            {
                return description;
            }
        }
        catch (Exception ex) when (ex is FileNotFoundException or IOException or UnauthorizedAccessException)
        {
        }

        return Path.GetFileNameWithoutExtension(exePath);
    }

    private static string ShortHash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToLowerInvariant())))[..12].ToLowerInvariant();
}
