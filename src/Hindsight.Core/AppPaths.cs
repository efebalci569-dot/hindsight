namespace Hindsight.Core;

/// <summary>
/// Where Hindsight keeps its files. Settings and the game list roam with the user (%APPDATA%);
/// the replay buffer, thumbnails and logs are machine-local caches (%LOCALAPPDATA%).
/// </summary>
public static class AppPaths
{
    private static string? _dataOverride;

    /// <summary>Used by <c>--data-dir</c> so tests and demos never touch the real profile.</summary>
    public static void OverrideDataRoot(string directory) => _dataOverride = Path.GetFullPath(directory);

    public static string DataDir => Ensure(_dataOverride is null
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Hindsight")
        : Path.Combine(_dataOverride, "data"));

    public static string LocalDir => Ensure(_dataOverride is null
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Hindsight")
        : Path.Combine(_dataOverride, "local"));

    public static string DefaultClipsDir => _dataOverride is null
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Hindsight")
        : Path.Combine(_dataOverride, "clips");

    public static string DefaultBufferDir => Path.Combine(LocalDir, "buffer");

    public static string ThumbsDir => Ensure(Path.Combine(LocalDir, "thumbs"));

    public static string ArtDir => Ensure(Path.Combine(LocalDir, "art"));

    public static string LogsDir => Ensure(Path.Combine(LocalDir, "logs"));

    public static string SettingsFile => Path.Combine(DataDir, "settings.json");

    public static string GamesFile => Path.Combine(DataDir, "games.json");

    public static string StatsFile => Path.Combine(DataDir, "playtime.json");

    public static string ClipIndexFile => Path.Combine(DataDir, "clips.json");

    public static string EncoderCacheFile => Path.Combine(LocalDir, "encoders.json");

    public static string AppDirectory => AppContext.BaseDirectory;

    public static string Ensure(string directory)
    {
        Directory.CreateDirectory(directory);
        return directory;
    }

    /// <summary>Turns a game name into a folder/file name that is valid on Windows.</summary>
    public static string SafeName(string name, string fallback = "Diğer")
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Trim().Select(c => invalid.Contains(c) || char.IsControl(c) ? ' ' : c).ToArray();
        var cleaned = string.Join(' ', new string(chars).Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim(' ', '.');
        if (cleaned.Length > 60)
        {
            cleaned = cleaned[..60].TrimEnd(' ', '.');
        }

        string[] reserved = ["CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "LPT1", "LPT2", "LPT3"];
        return cleaned.Length == 0 || reserved.Contains(cleaned, StringComparer.OrdinalIgnoreCase) ? fallback : cleaned;
    }
}
