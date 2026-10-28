namespace Hindsight.Core.Games;

/// <summary>Where a game came from. The library page shows one section per source.</summary>
public enum GameSource { Steam, Epic, Gog, Ubisoft, Ea, BattleNet, Riot, Rockstar, Xbox, Detected, Manual }

public sealed class GameEntry
{
    /// <summary>Stable id, e.g. "steam:730", "epic:Fortnite", "manual:3f2a…".</summary>
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public GameSource Source { get; set; }

    public string? InstallDirectory { get; set; }

    public string? ExecutablePath { get; set; }

    /// <summary>Launcher URI (steam://…, com.epicgames.launcher://…) — preferred over the exe when present.</summary>
    public string? LaunchUri { get; set; }

    public string? LaunchArguments { get; set; }

    /// <summary>Local cover / header image (Steam's own library cache), if one exists.</summary>
    public string? CoverImage { get; set; }

    /// <summary>File to take the icon from (exe or .ico).</summary>
    public string? IconSource { get; set; }

    /// <summary>Executable names (without path) that belong to this game, for processes whose path is hidden.</summary>
    public List<string> ProcessNames { get; set; } = [];

    public bool Hidden { get; set; }

    public DateTime AddedAt { get; set; } = DateTime.Now;

    public static string SourceName(GameSource source) => source switch
    {
        GameSource.Steam => "Steam",
        GameSource.Epic => "Epic Games",
        GameSource.Gog => "GOG",
        GameSource.Ubisoft => "Ubisoft Connect",
        GameSource.Ea => "EA app",
        GameSource.BattleNet => "Battle.net",
        GameSource.Riot => "Riot Games",
        GameSource.Rockstar => "Rockstar Games",
        GameSource.Xbox => "Xbox / Microsoft Store",
        GameSource.Detected => "Algılanan oyunlar",
        _ => "Elle eklenenler",
    };

    public string SourceDisplayName => SourceName(Source);

    public override string ToString() => $"{Name} ({Source})";
}
