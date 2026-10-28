using Hindsight.Core;
using Hindsight.Core.Clips;
using Hindsight.Core.Games;
using Hindsight.Core.Settings;

namespace Hindsight.App.Services;

/// <summary>Long-lived services. They outlive the main window, which is created and destroyed on demand.</summary>
public sealed class AppServices
{
    public AppServices()
    {
        Settings = new SettingsStore();
        Games = new GameLibrary();
        Clips = new ClipLibrary(Settings.Current.EffectiveClipsFolder);
        Playtime = new PlaytimeStore();
        Detector = new GameDetector(Games, Settings);
        Capture = new CaptureController(Settings, Games, Clips, Playtime, Detector);
    }

    public SettingsStore Settings { get; }

    public GameLibrary Games { get; }

    public ClipLibrary Clips { get; }

    public PlaytimeStore Playtime { get; }

    public GameDetector Detector { get; }

    public CaptureController Capture { get; }
}
