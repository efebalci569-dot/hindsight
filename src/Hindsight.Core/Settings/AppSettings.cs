namespace Hindsight.Core.Settings;

public enum RecordingMode
{
    /// <summary>The replay buffer runs only while a game is running (lowest resource use).</summary>
    GamesOnly,
    /// <summary>The replay buffer always runs, also on the desktop.</summary>
    Always,
    /// <summary>No background buffer; only manual recordings and screenshots.</summary>
    Off,
}

public enum ResolutionPreset { P1080 = 1080, P900 = 900, P720 = 720, P540 = 540, P480 = 480 }

public enum QualityPreset { Low, Medium, High, Custom }

public enum EncoderChoice { Auto, Nvenc, Amf, Qsv, MediaFoundation, Cpu }

public enum VideoCodec { H264, Hevc, Av1 }

public enum CaptureTargetMode
{
    /// <summary>The monitor the game is on (falls back to the primary monitor).</summary>
    GameMonitor,
    /// <summary>Only the game window — overlays and notifications stay out of the clip.</summary>
    GameWindow,
    /// <summary>Always the primary monitor.</summary>
    PrimaryMonitor,
}

public enum RecorderPriority { Low, Normal, High }

public sealed class AppSettings
{
    // ── Recording ────────────────────────────────────────────────────────────
    public RecordingMode Mode { get; set; } = RecordingMode.GamesOnly;

    /// <summary>Length of a saved clip (and of the replay buffer), in seconds.</summary>
    public int ClipSeconds { get; set; } = 30;

    public ResolutionPreset Resolution { get; set; } = ResolutionPreset.P1080;

    /// <summary>30 or 60.</summary>
    public int Fps { get; set; } = 60;

    public QualityPreset Quality { get; set; } = QualityPreset.High;

    public int CustomBitrateMbps { get; set; } = 15;

    public EncoderChoice Encoder { get; set; } = EncoderChoice.Auto;

    public VideoCodec Codec { get; set; } = VideoCodec.H264;

    public CaptureTargetMode CaptureTarget { get; set; } = CaptureTargetMode.GameMonitor;

    public bool CaptureCursor { get; set; } = true;

    /// <summary>Treat full-screen apps that are not in the library as games (browsers and players excluded).</summary>
    public bool DetectUnknownFullscreen { get; set; } = true;

    // ── Audio ────────────────────────────────────────────────────────────────
    public bool RecordSystemAudio { get; set; } = true;

    public int SystemVolume { get; set; } = 100;

    public bool RecordMicrophone { get; set; }

    /// <summary>WASAPI endpoint id; null = default communications/recording device.</summary>
    public string? MicrophoneDeviceId { get; set; }

    public int MicrophoneVolume { get; set; } = 100;

    public int AudioBitrateKbps { get; set; } = 160;

    /// <summary>Positive values delay the audio, negative values advance it.</summary>
    public int AvSyncOffsetMs { get; set; }

    // ── Hotkeys ──────────────────────────────────────────────────────────────
    // Defaults avoid Alt+F9/F10/F1, which the NVIDIA overlay (ShadowPlay) already uses.
    public string SaveClipHotkey { get; set; } = "Alt+F8";

    public string ToggleRecordingHotkey { get; set; } = "Alt+F7";

    public string ScreenshotHotkey { get; set; } = "Alt+F12";

    public string ToggleBufferHotkey { get; set; } = "Ctrl+Alt+F8";

    // ── Storage ──────────────────────────────────────────────────────────────
    public string? ClipsFolder { get; set; }

    public string? BufferFolder { get; set; }

    public bool StorageLimitEnabled { get; set; }

    public int StorageLimitGb { get; set; } = 50;

    // ── Performance ──────────────────────────────────────────────────────────
    public RecorderPriority Priority { get; set; } = RecorderPriority.Normal;

    /// <summary>Unload the window and give memory back to Windows while Hindsight sits in the tray.</summary>
    public bool TrimMemoryInTray { get; set; } = true;

    // ── General ──────────────────────────────────────────────────────────────
    public bool StartWithWindows { get; set; }

    public bool StartMinimized { get; set; } = true;

    public bool CloseToTray { get; set; } = true;

    public bool ShowSaveToast { get; set; } = true;

    public bool PlaySaveSound { get; set; } = true;

    public bool FirstRunCompleted { get; set; }

    /// <summary>Game ids the user does not want recorded.</summary>
    public List<string> ExcludedGames { get; set; } = [];

    public string EffectiveClipsFolder => string.IsNullOrWhiteSpace(ClipsFolder) ? AppPaths.DefaultClipsDir : ClipsFolder!;

    public string EffectiveBufferFolder => string.IsNullOrWhiteSpace(BufferFolder) ? AppPaths.DefaultBufferDir : BufferFolder!;

    public static readonly int[] ClipLengthChoices = [15, 30, 45, 60, 90, 120, 180, 300];

    public static readonly int[] FpsChoices = [30, 60];

    /// <summary>Clamps values that came from an edited or old settings file.</summary>
    public AppSettings Normalize()
    {
        ClipSeconds = Math.Clamp(ClipSeconds, 10, 600);
        Fps = Fps >= 45 ? 60 : 30;
        CustomBitrateMbps = Math.Clamp(CustomBitrateMbps, 2, 50);
        SystemVolume = Math.Clamp(SystemVolume, 0, 200);
        MicrophoneVolume = Math.Clamp(MicrophoneVolume, 0, 200);
        AudioBitrateKbps = Math.Clamp(AudioBitrateKbps, 64, 320);
        AvSyncOffsetMs = Math.Clamp(AvSyncOffsetMs, -1000, 1000);
        StorageLimitGb = Math.Clamp(StorageLimitGb, 1, 4096);
        if (!Enum.IsDefined(Resolution))
        {
            Resolution = ResolutionPreset.P1080;
        }

        ExcludedGames ??= [];
        return this;
    }

    public AppSettings Clone()
    {
        var copy = (AppSettings)MemberwiseClone();
        copy.ExcludedGames = [.. ExcludedGames];
        return copy;
    }
}
