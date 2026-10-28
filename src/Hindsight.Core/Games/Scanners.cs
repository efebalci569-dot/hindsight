using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Win32;

namespace Hindsight.Core.Games;

public interface IGameScanner
{
    GameSource Source { get; }

    IEnumerable<GameEntry> Scan(CancellationToken cancellationToken);
}

/// <summary>Registry helpers that never throw.</summary>
internal static class Reg
{
    public static string? Get(RegistryKey root, string path, string value)
    {
        try
        {
            using var key = root.OpenSubKey(path);
            return key?.GetValue(value)?.ToString();
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    public static string[] SubKeys(RegistryKey root, string path)
    {
        try
        {
            using var key = root.OpenSubKey(path);
            return key?.GetSubKeyNames() ?? [];
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return [];
        }
    }
}

/// <summary>
/// Steam: libraryfolders.vdf lists every library; each appmanifest_*.acf is one installed app.
/// Cover art comes from Steam's local library cache (no network).
/// </summary>
public sealed class SteamScanner : IGameScanner
{
    private static readonly HashSet<string> NonGames =
    [
        "228980", "1070560", "1391110", "1628350", "1493710", "1826330", "1161040", "2180100", "961940", "1054830",
        "1113280", "1245040", "1420170", "1580130", "1887720", "2348590", "2805730", "3658110", "250820", "431960",
        "1905180", "223850", "2371090",
    ];

    private static readonly string[] NonGameHints =
        ["Redistributable", "Steam Linux Runtime", "Proton ", "Steamworks", "Dedicated Server", " SDK", "SteamVR", "Soundtrack"];

    public GameSource Source => GameSource.Steam;

    public static string? FindSteam()
    {
        string?[] candidates =
        [
            Reg.Get(Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath"),
            Reg.Get(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath"),
            Reg.Get(Registry.LocalMachine, @"SOFTWARE\Valve\Steam", "InstallPath"),
        ];
        return candidates.Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => Path.GetFullPath(c!.Replace('/', '\\')))
            .FirstOrDefault(Directory.Exists);
    }

    public IEnumerable<GameEntry> Scan(CancellationToken cancellationToken)
    {
        var steam = FindSteam();
        if (steam is null)
        {
            yield break;
        }

        var seen = new HashSet<string>();
        foreach (var library in LibraryFolders(steam))
        {
            var apps = Path.Combine(library, "steamapps");
            if (!Directory.Exists(apps))
            {
                continue;
            }

            string[] manifests;
            try
            {
                manifests = Directory.GetFiles(apps, "appmanifest_*.acf");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var manifest in manifests)
            {
                cancellationToken.ThrowIfCancellationRequested();
                GameEntry? game = null;
                try
                {
                    game = Read(manifest, apps, steam);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or VdfFormatException)
                {
                    Log.Warn($"Steam manifest skipped ({Path.GetFileName(manifest)}): {ex.Message}");
                }

                if (game is not null && seen.Add(game.Id))
                {
                    yield return game;
                }
            }
        }
    }

    internal static IEnumerable<string> LibraryFolders(string steam)
    {
        var folders = new List<string> { steam };
        var file = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
        try
        {
            if (File.Exists(file))
            {
                var root = VdfParser.ParseFile(file);
                var libraries = root["libraryfolders"] ?? root["LibraryFolders"];
                foreach (var (key, node) in libraries?.Children ?? new Dictionary<string, VdfNode>())
                {
                    var path = node.IsObject ? node.GetString("path") : (int.TryParse(key, out _) ? node.Value : null);
                    if (!string.IsNullOrWhiteSpace(path))
                    {
                        folders.Add(path);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or VdfFormatException)
        {
            Log.Warn($"libraryfolders.vdf: {ex.Message}");
        }

        return folders.Select(f => f.Replace('/', '\\').TrimEnd('\\')).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static GameEntry? Read(string manifest, string appsDirectory, string steam)
    {
        var state = VdfParser.ParseFile(manifest)["AppState"];
        var appId = state?.GetString("appid");
        var name = state?.GetString("name");
        if (string.IsNullOrWhiteSpace(appId) || !appId.All(char.IsAsciiDigit) || string.IsNullOrWhiteSpace(name) ||
            NonGames.Contains(appId) || NonGameHints.Any(h => name.Contains(h, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        var installDir = state!.GetString("installdir");
        var directory = string.IsNullOrWhiteSpace(installDir) ? null : Path.Combine(appsDirectory, "common", installDir);
        if (directory is null || !Directory.Exists(directory))
        {
            return null;
        }

        return new GameEntry
        {
            Id = $"steam:{appId}",
            Name = name.Trim(),
            Source = GameSource.Steam,
            InstallDirectory = directory,
            LaunchUri = $"steam://rungameid/{appId}",
            CoverImage = FindCover(steam, appId),
        };
    }

    /// <summary>Portrait capsule from Steam's cache: old clients use appid_library_600x900.jpg, new ones per-app folders.</summary>
    internal static string? FindCover(string steam, string appId)
    {
        var cache = Path.Combine(steam, "appcache", "librarycache");
        string[] names = ["library_600x900.jpg", "library_capsule.jpg", "header.jpg", "library_header.jpg"];
        foreach (var name in names)
        {
            var legacy = Path.Combine(cache, $"{appId}_{name}");
            if (File.Exists(legacy))
            {
                return legacy;
            }

            var perApp = Path.Combine(cache, appId);
            if (!Directory.Exists(perApp))
            {
                continue;
            }

            try
            {
                var match = Directory.EnumerateFiles(perApp, name, SearchOption.AllDirectories).FirstOrDefault();
                if (match is not null)
                {
                    return match;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return null;
    }
}

/// <summary>Epic Games Launcher: one JSON *.item manifest per install under ProgramData.</summary>
public sealed class EpicScanner : IGameScanner
{
    public GameSource Source => GameSource.Epic;

    public IEnumerable<GameEntry> Scan(CancellationToken cancellationToken)
    {
        var dataPath = Reg.Get(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Epic Games\EpicGamesLauncher", "AppDataPath");
        string?[] candidates =
        [
            dataPath is null ? null : Path.Combine(dataPath, "Manifests"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Epic", "EpicGamesLauncher", "Data", "Manifests"),
        ];
        var directory = candidates.FirstOrDefault(c => c is not null && Directory.Exists(c));
        if (directory is null)
        {
            yield break;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(directory, "*.item"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            GameEntry? game = null;
            try
            {
                game = Parse(File.ReadAllText(file));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                Log.Warn($"Epic manifest skipped ({Path.GetFileName(file)}): {ex.Message}");
            }

            if (game is not null && seen.Add(game.Id))
            {
                yield return game;
            }
        }
    }

    internal static GameEntry? Parse(string json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true });
        var root = document.RootElement;
        string? Get(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        var name = Get("DisplayName");
        var app = Get("AppName");
        var ns = Get("CatalogNamespace");
        var item = Get("CatalogItemId");
        var location = Get("InstallLocation");
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(app) || string.IsNullOrWhiteSpace(location) || !Directory.Exists(location))
        {
            return null;
        }

        var main = Get("MainGameAppName");
        if (!string.IsNullOrWhiteSpace(main) && !string.Equals(main, app, StringComparison.OrdinalIgnoreCase))
        {
            return null; // DLC
        }

        if (root.TryGetProperty("AppCategories", out var categories) && categories.ValueKind == JsonValueKind.Array)
        {
            var list = categories.EnumerateArray().Select(c => c.GetString() ?? string.Empty).ToList();
            if (list.Count > 0 && !list.Contains("games", StringComparer.OrdinalIgnoreCase))
            {
                return null; // Unreal Engine, plugins, …
            }
        }

        var exe = Get("LaunchExecutable");
        var exePath = string.IsNullOrWhiteSpace(exe) ? null : Path.GetFullPath(Path.Combine(location, exe));
        return new GameEntry
        {
            Id = $"epic:{app}",
            Name = name.Trim(),
            Source = GameSource.Epic,
            InstallDirectory = location,
            ExecutablePath = exePath,
            IconSource = exePath,
            LaunchUri = ns is null || item is null
                ? null
                : $"com.epicgames.launcher://apps/{Uri.EscapeDataString(ns)}%3A{Uri.EscapeDataString(item)}%3A{Uri.EscapeDataString(app)}?action=launch&silent=true",
            ProcessNames = exePath is null ? [] : [Path.GetFileName(exePath)],
        };
    }
}

/// <summary>GOG (with or without Galaxy) registers every game under HKLM\SOFTWARE\WOW6432Node\GOG.com\Games.</summary>
public sealed class GogScanner : IGameScanner
{
    private const string GamesKey = @"SOFTWARE\WOW6432Node\GOG.com\Games";

    public GameSource Source => GameSource.Gog;

    public IEnumerable<GameEntry> Scan(CancellationToken cancellationToken)
    {
        foreach (var id in Reg.SubKeys(Registry.LocalMachine, GamesKey))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = $@"{GamesKey}\{id}";
            string? Read(string value) => Reg.Get(Registry.LocalMachine, key, value);
            if (!string.IsNullOrWhiteSpace(Read("dependsOn")))
            {
                continue; // DLC
            }

            var name = Read("gameName") ?? Read("GAMENAME");
            var path = Read("path") ?? Read("PATH");
            var exe = Read("exe") ?? Read("EXE");
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
            {
                continue;
            }

            yield return new GameEntry
            {
                Id = $"gog:{Read("gameID") ?? id}",
                Name = name.Trim(),
                Source = GameSource.Gog,
                InstallDirectory = path,
                ExecutablePath = exe,
                IconSource = exe,
                LaunchArguments = Read("launchParam"),
                ProcessNames = [Path.GetFileName(exe)],
            };
        }
    }
}

/// <summary>Ubisoft Connect: HKLM\SOFTWARE\WOW6432Node\Ubisoft\Launcher\Installs\{id}\InstallDir.</summary>
public sealed class UbisoftScanner : IGameScanner
{
    private const string InstallsKey = @"SOFTWARE\WOW6432Node\Ubisoft\Launcher\Installs";
    private const string UninstallKey = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall";

    public GameSource Source => GameSource.Ubisoft;

    public IEnumerable<GameEntry> Scan(CancellationToken cancellationToken)
    {
        foreach (var id in Reg.SubKeys(Registry.LocalMachine, InstallsKey).Where(i => i.All(char.IsAsciiDigit)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var dir = Reg.Get(Registry.LocalMachine, $@"{InstallsKey}\{id}", "InstallDir");
            if (string.IsNullOrWhiteSpace(dir))
            {
                continue;
            }

            dir = Path.GetFullPath(dir.Replace('/', '\\')).TrimEnd('\\');
            if (!Directory.Exists(dir))
            {
                continue;
            }

            var name = Reg.Get(Registry.LocalMachine, $@"{UninstallKey}\Uplay Install {id}", "DisplayName") ?? new DirectoryInfo(dir).Name;
            yield return new GameEntry
            {
                Id = $"ubisoft:{id}",
                Name = name.Trim(),
                Source = GameSource.Ubisoft,
                InstallDirectory = dir,
                LaunchUri = $"uplay://launch/{id}/0",
            };
        }
    }
}

/// <summary>
/// EA app, Battle.net, Riot and Rockstar games all leave a normal "Programs and Features" entry
/// with their publisher and install folder; this reads those (the launchers themselves are skipped).
/// </summary>
public sealed partial class UninstallRegistryScanner : IGameScanner
{
    private static readonly (string Publisher, GameSource Source, string[] Skip)[] Publishers =
    [
        ("Electronic Arts", GameSource.Ea, ["EA app", "EA Desktop", "Origin", "EA Javelin", "EA Anticheat"]),
        ("Blizzard Entertainment", GameSource.BattleNet, ["Battle.net"]),
        ("Riot Games", GameSource.Riot, ["Riot Client", "Riot Vanguard"]),
        ("Rockstar Games", GameSource.Rockstar, ["Rockstar Games Launcher", "Social Club"]),
    ];

    private static readonly string[] Roots =
    [
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
        @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
    ];

    public GameSource Source => GameSource.Ea; // reports several sources

    public IEnumerable<GameEntry> Scan(CancellationToken cancellationToken)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            foreach (var root in Roots)
            {
                foreach (var sub in Reg.SubKeys(hive, root))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var path = $@"{root}\{sub}";
                    var publisher = Reg.Get(hive, path, "Publisher");
                    var name = Reg.Get(hive, path, "DisplayName");
                    if (publisher is null || string.IsNullOrWhiteSpace(name))
                    {
                        continue;
                    }

                    var match = Publishers.FirstOrDefault(p => publisher.Contains(p.Publisher, StringComparison.OrdinalIgnoreCase));
                    if (match.Publisher is null || match.Skip.Any(s => name.Contains(s, StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    var location = Reg.Get(hive, path, "InstallLocation")?.Trim('"', ' ');
                    if (string.IsNullOrWhiteSpace(location) || !Directory.Exists(location))
                    {
                        continue;
                    }

                    location = Path.GetFullPath(location).TrimEnd('\\');
                    if (!seen.Add(location))
                    {
                        continue;
                    }

                    var icon = CleanIconPath(Reg.Get(hive, path, "DisplayIcon"));
                    var game = new GameEntry
                    {
                        Id = $"{match.Source.ToString().ToLowerInvariant()}:{AppPaths.SafeName(name).ToLowerInvariant().Replace(' ', '-')}",
                        Name = name.Trim(),
                        Source = match.Source,
                        InstallDirectory = location,
                        IconSource = icon,
                    };

                    if (match.Source == GameSource.Riot && RiotLaunch(Reg.Get(hive, path, "UninstallString")) is { } riot)
                    {
                        game.ExecutablePath = riot.Exe;
                        game.LaunchArguments = riot.Args;
                    }
                    else if (icon is not null && icon.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                             !Path.GetFileName(icon).Contains("unins", StringComparison.OrdinalIgnoreCase))
                    {
                        game.ExecutablePath = icon;
                        game.ProcessNames = [Path.GetFileName(icon)];
                    }

                    yield return game;
                }
            }
        }
    }

    internal static string? CleanIconPath(string? icon)
    {
        if (string.IsNullOrWhiteSpace(icon))
        {
            return null;
        }

        icon = icon.Trim();
        var comma = icon.LastIndexOf(',');
        if (comma > 2 && icon[(comma + 1)..].Trim().TrimStart('-').All(char.IsAsciiDigit))
        {
            icon = icon[..comma];
        }

        icon = icon.Trim('"', ' ');
        return File.Exists(icon) ? icon : null;
    }

    /// <summary>"RiotClientServices.exe" --uninstall-product=valorant --uninstall-patchline=live → the matching launch command.</summary>
    internal static (string Exe, string Args)? RiotLaunch(string? uninstall)
    {
        if (string.IsNullOrWhiteSpace(uninstall))
        {
            return null;
        }

        var match = RiotRegex().Match(uninstall);
        if (!match.Success)
        {
            return null;
        }

        var exe = match.Groups["exe"].Value;
        var product = match.Groups["product"].Value;
        var patch = match.Groups["patch"].Success ? match.Groups["patch"].Value : "live";
        return (exe, $"--launch-product={product} --launch-patchline={patch}");
    }

    [GeneratedRegex("^\"?(?<exe>[^\"]+?\\.exe)\"?\\s+--uninstall-product=(?<product>[\\w.-]+)(?:\\s+--uninstall-patchline=(?<patch>[\\w.-]+))?", RegexOptions.IgnoreCase)]
    private static partial Regex RiotRegex();
}

/// <summary>Xbox app / Game Pass installs land in X:\XboxGames\&lt;Game&gt;\Content on any drive.</summary>
public sealed class XboxScanner : IGameScanner
{
    public GameSource Source => GameSource.Xbox;

    public IEnumerable<GameEntry> Scan(CancellationToken cancellationToken)
    {
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
        {
            var root = Path.Combine(drive.RootDirectory.FullName, "XboxGames");
            if (!Directory.Exists(root))
            {
                continue;
            }

            string[] folders;
            try
            {
                folders = Directory.GetDirectories(root);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var folder in folders)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var content = Path.Combine(folder, "Content");
                if (!Directory.Exists(content))
                {
                    continue;
                }

                var name = Path.GetFileName(folder);
                string? exe = null;
                string? logo = null;
                var config = Path.Combine(content, "MicrosoftGame.config");
                try
                {
                    if (File.Exists(config))
                    {
                        var xml = XDocument.Load(config);
                        var visuals = xml.Descendants().FirstOrDefault(e => e.Name.LocalName == "ShellVisuals");
                        var display = visuals?.Attribute("DefaultDisplayName")?.Value;
                        foreach (var attribute in new[] { "Square480x480Logo", "Square150x150Logo", "StoreLogo" })
                        {
                            if (visuals?.Attribute(attribute)?.Value is { Length: > 0 } relative && File.Exists(Path.Combine(content, relative)))
                            {
                                logo = Path.Combine(content, relative);
                                break;
                            }
                        }

                        if (!string.IsNullOrWhiteSpace(display) && !display.StartsWith("ms-resource", StringComparison.OrdinalIgnoreCase))
                        {
                            name = display;
                        }

                        var exeName = xml.Descendants().FirstOrDefault(e => e.Name.LocalName == "Executable")?.Attribute("Name")?.Value;
                        if (!string.IsNullOrWhiteSpace(exeName))
                        {
                            exe = Path.Combine(content, exeName);
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
                {
                }

                yield return new GameEntry
                {
                    Id = $"xbox:{Path.GetFileName(folder).ToLowerInvariant()}",
                    Name = name.Trim(),
                    Source = GameSource.Xbox,
                    InstallDirectory = content,
                    ExecutablePath = exe is not null && File.Exists(exe) ? exe : null,
                    IconSource = logo ?? (exe is not null && File.Exists(exe) ? exe : null),
                    ProcessNames = exe is null ? [] : [Path.GetFileName(exe)],
                };
            }
        }
    }
}
