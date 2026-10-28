namespace Hindsight.Core.Games;

public sealed class GameStats
{
    public double TotalSeconds { get; set; }

    public int Sessions { get; set; }

    public DateTime? LastPlayed { get; set; }

    /// <summary>Seconds per day (yyyy-MM-dd), last 30 days only.</summary>
    public Dictionary<string, double> Daily { get; set; } = [];
}

/// <summary>Play time measured by Hindsight itself while a game is detected (honest numbers: only what was seen).</summary>
public sealed class PlaytimeStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, GameStats> _stats;
    private DateTime _lastFlush = DateTime.Now;

    public PlaytimeStore()
    {
        _stats = JsonFile.Load<Dictionary<string, GameStats>>(AppPaths.StatsFile) ?? [];
    }

    public GameStats? Get(string gameId)
    {
        lock (_gate)
        {
            return _stats.TryGetValue(gameId, out var s) ? s : null;
        }
    }

    public double SecondsThisWeek()
    {
        var since = DateTime.Today.AddDays(-6);
        lock (_gate)
        {
            return _stats.Values.SelectMany(s => s.Daily)
                .Where(d => DateTime.TryParse(d.Key, out var day) && day >= since)
                .Sum(d => d.Value);
        }
    }

    public void BeginSession(string gameId)
    {
        lock (_gate)
        {
            var stats = GetOrAdd(gameId);
            stats.Sessions++;
            stats.LastPlayed = DateTime.Now;
        }
    }

    /// <summary>Adds elapsed time; called every tick while a game runs, so a crash loses at most a minute.</summary>
    public void Add(string gameId, double seconds)
    {
        if (seconds <= 0 || seconds > 3600)
        {
            return;
        }

        lock (_gate)
        {
            var stats = GetOrAdd(gameId);
            stats.TotalSeconds += seconds;
            stats.LastPlayed = DateTime.Now;
            var key = DateTime.Today.ToString("yyyy-MM-dd");
            stats.Daily[key] = stats.Daily.GetValueOrDefault(key) + seconds;
            foreach (var old in stats.Daily.Keys.Where(k => DateTime.TryParse(k, out var d) && d < DateTime.Today.AddDays(-30)).ToList())
            {
                stats.Daily.Remove(old);
            }
        }

        if (DateTime.Now - _lastFlush > TimeSpan.FromMinutes(1))
        {
            Flush();
        }
    }

    public void Flush()
    {
        Dictionary<string, GameStats> copy;
        lock (_gate)
        {
            copy = new Dictionary<string, GameStats>(_stats);
            _lastFlush = DateTime.Now;
        }

        JsonFile.Save(AppPaths.StatsFile, copy);
    }

    private GameStats GetOrAdd(string gameId)
    {
        if (!_stats.TryGetValue(gameId, out var stats))
        {
            stats = new GameStats();
            _stats[gameId] = stats;
        }

        return stats;
    }
}
