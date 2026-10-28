using Hindsight.Core.Native;
using Hindsight.Core.Settings;

namespace Hindsight.Core.Games;

/// <summary>A game that is running right now.</summary>
public sealed class GameSession : IDisposable
{
    internal GameSession(GameEntry game, ForegroundWindow window)
    {
        Game = game;
        Window = window;
        Watch = new ProcessWatch(window.ProcessId);
    }

    public GameEntry Game { get; }

    /// <summary>The game's main window (updated while it stays in front).</summary>
    public ForegroundWindow Window { get; internal set; }

    public DateTime StartedAt { get; } = DateTime.Now;

    internal ProcessWatch Watch { get; private set; }

    /// <summary>The same game moved to another process (launcher → game exe): follow the new one.</summary>
    internal void Follow(ForegroundWindow window)
    {
        Watch.Dispose();
        Watch = new ProcessWatch(window.ProcessId);
        Window = window;
    }

    public void Dispose() => Watch.Dispose();
}

/// <summary>
/// Looks at the foreground window every 2 seconds (one cheap Win32 call, no process enumeration)
/// and reports when a game starts or its process ends. Alt-tabbing out does not end a session.
/// </summary>
public sealed class GameDetector : IDisposable
{
    /// <summary>Full-screen apps that are never games: browsers, players, launchers, overlays, screenshot tools.</summary>
    public static readonly HashSet<string> NotGames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Lightshot", "ShareX", "Greenshot", "Snagit32", "SnagitEditor", "SnippingTool", "ScreenClippingHost", "ScreenSketch",
        "Gyazo", "GyazoReplay", "Flameshot", "PicPick", "obs-browser-page", "msedgewebview2", "CefSharp.BrowserSubprocess",
        "LockApp", "LogonUI", "consent", "mmc", "WinStore.App", "Widgets", "PowerToys", "PowerToys.PowerLauncher", "rundll32",
        "explorer", "chrome", "msedge", "firefox", "opera", "opera_gx", "brave", "vivaldi", "iexplore", "arc",
        "vlc", "mpc-hc64", "mpc-hc", "mpc-be64", "potplayer", "potplayermini64", "wmplayer", "Video.UI", "Microsoft.Media.Player",
        "ApplicationFrameHost", "TextInputHost", "LockApp", "SearchHost", "StartMenuExperienceHost", "ShellExperienceHost",
        "discord", "spotify", "obs64", "obs32", "POWERPNT", "mstsc", "vmconnect", "Hindsight", "steam", "steamwebhelper",
        "EpicGamesLauncher", "GalaxyClient", "upc", "EADesktop", "Battle.net", "RiotClientUx", "RiotClientServices",
        "XboxPcApp", "Teams", "ms-teams", "Zoom", "devenv", "Code", "WindowsTerminal", "Taskmgr", "photos", "PhotosApp",
        "Microsoft.Photos", "NVIDIA Share", "nvcontainer", "GameBar", "SteelSeriesGG", "Medal", "Overwolf",
    };

    private readonly GameLibrary _library;
    private readonly SettingsStore _settings;
    private readonly Lock _gate = new();
    private (int ProcessId, int Ticks) _fullscreenCandidate;
    private Timer? _timer;
    private int _ticking;

    public GameDetector(GameLibrary library, SettingsStore settings)
    {
        _library = library;
        _settings = settings;
    }

    public GameSession? Active { get; private set; }

    /// <summary>Raised on a thread-pool thread with the new session (or null when the game ended).</summary>
    public event EventHandler<GameSession?>? ActiveChanged;

    /// <summary>Raised when the active game's window moved to another monitor or changed.</summary>
    public event EventHandler<GameSession>? WindowChanged;

    public void Start()
    {
        // Older versions could mistake tools (e.g. Lightshot) for games; drop those entries.
        _library.RemoveWhere(g => g.Source == GameSource.Detected &&
                                  g.ProcessNames.Any(p => NotGames.Contains(Path.GetFileNameWithoutExtension(p))));
        _timer ??= new Timer(_ => Tick(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2));
    }

    public void Tick()
    {
        if (Interlocked.Exchange(ref _ticking, 1) == 1)
        {
            return;
        }

        try
        {
            TickCore();
        }
        catch (Exception ex)
        {
            Log.Error("Game detection tick failed", ex);
        }
        finally
        {
            Volatile.Write(ref _ticking, 0);
        }
    }

    private void TickCore()
    {
        GameSession? ended = null;
        GameSession? started = null;
        GameSession? moved = null;

        var foreground = WindowInfo.GetForeground();
        lock (_gate)
        {
            if (Active is not null && Active.Watch.HasExited)
            {
                ended = Active;
                Active = null;
            }

            if (foreground is not null)
            {
                if (Active is not null && Active.Window.ProcessId == foreground.ProcessId)
                {
                    if (Active.Window.Handle != foreground.Handle || Active.Window.Monitor != foreground.Monitor)
                    {
                        Active.Window = foreground;
                        moved = Active;
                    }
                }
                else if (Identify(foreground) is not { } game)
                {
                    // Not a game: an alt-tab does not end the running session.
                }
                else if (Active is not null && Active.Game.Id == game.Id)
                {
                    Active.Follow(foreground);
                    moved = Active;
                }
                else
                {
                    if (Active is not null)
                    {
                        ended ??= Active;
                    }

                    Active = new GameSession(game, foreground);
                    started = Active;
                }
            }
        }

        if (ended is not null)
        {
            Log.Info($"Game ended: {ended.Game.Name}");
            ActiveChanged?.Invoke(this, started);
            ended.Dispose();
            if (started is not null)
            {
                Log.Info($"Game started: {started.Game.Name} (pid {started.Window.ProcessId})");
            }

            return;
        }

        if (started is not null)
        {
            Log.Info($"Game started: {started.Game.Name} (pid {started.Window.ProcessId})");
            ActiveChanged?.Invoke(this, started);
        }
        else if (moved is not null)
        {
            WindowChanged?.Invoke(this, moved);
        }
    }

    private GameEntry? Identify(ForegroundWindow window)
    {
        if (NotGames.Contains(window.ProcessName))
        {
            return null;
        }

        var game = _library.Match(window.ExePath, window.ProcessName);
        if (game is null && _settings.Current.DetectUnknownFullscreen && window.IsFullscreen && !IsSystemPath(window.ExePath))
        {
            // A screenshot overlay or a splash screen is full screen for a moment; a game stays.
            _fullscreenCandidate = _fullscreenCandidate.ProcessId == window.ProcessId
                ? (window.ProcessId, _fullscreenCandidate.Ticks + 1)
                : (window.ProcessId, 1);
            if (_fullscreenCandidate.Ticks < 3)
            {
                return null;
            }

            game = _library.AddDetected(window.ExePath, window.ProcessName, window.Title);
        }

        if (game is null || game.Hidden || _settings.Current.ExcludedGames.Contains(game.Id, StringComparer.OrdinalIgnoreCase))
        {
            return null;
        }

        return game;
    }

    private static bool IsSystemPath(string? exePath)
    {
        if (exePath is null)
        {
            return false;
        }

        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return exePath.StartsWith(windows, StringComparison.OrdinalIgnoreCase) ||
               exePath.Contains(@"\WindowsApps\Microsoft.", StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
        Active?.Dispose();
    }
}
