using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hindsight.App.Services;
using Hindsight.Core;
using Hindsight.Core.Media;
using Hindsight.Core.Native;
using Hindsight.Core.Settings;

namespace Hindsight.App.ViewModels;

/// <summary>Every setting writes through immediately; the recorder restarts by itself when needed.</summary>
public sealed partial class SettingsViewModel : ObservableObject, IDisposable
{
    private readonly AppServices _services;

    public SettingsViewModel(AppServices services)
    {
        _services = services;
        _services.Settings.Changed += OnSettingsChanged;
        Microphones = [new Option<string?>(null, "Varsayılan mikrofon"), .. AudioMixerPump.ListMicrophones().Select(m => new Option<string?>(m.Id, m.Name))];
        RefreshDerived();
    }

    private AppSettings S => _services.Settings.Current;

    private void Set(Action<AppSettings> change, [CallerMemberName] string? property = null)
    {
        _services.Settings.Update(change);
        OnPropertyChanged(property);
        RefreshDerived();
    }

    private void OnSettingsChanged(object? sender, AppSettings e) =>
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(RefreshDerived);

    // ── Options ──────────────────────────────────────────────────────────────
    public IReadOnlyList<Option<RecordingMode>> Modes { get; } =
    [
        new(RecordingMode.GamesOnly, "Sadece oyun açıkken (önerilen)"),
        new(RecordingMode.Always, "Her zaman (masaüstü dahil)"),
        new(RecordingMode.Off, "Kapalı — sadece elle kayıt"),
    ];

    public IReadOnlyList<Option<int>> ClipLengths { get; } =
        AppSettings.ClipLengthChoices.Select(s => new Option<int>(s, s < 60 ? $"Son {s} saniye" : $"Son {s / 60.0:0.#} dakika")).ToList();

    public IReadOnlyList<Option<ResolutionPreset>> Resolutions { get; } =
    [
        new(ResolutionPreset.P1080, "1080p (Full HD)"),
        new(ResolutionPreset.P900, "900p"),
        new(ResolutionPreset.P720, "720p (HD)"),
        new(ResolutionPreset.P540, "540p"),
        new(ResolutionPreset.P480, "480p"),
    ];

    public IReadOnlyList<Option<int>> FpsOptions { get; } = [new(60, "60 FPS (akıcı)"), new(30, "30 FPS (daha hafif)")];

    public IReadOnlyList<Option<QualityPreset>> Qualities { get; } =
    [
        new(QualityPreset.High, "Yüksek"),
        new(QualityPreset.Medium, "Orta"),
        new(QualityPreset.Low, "Düşük (küçük dosya)"),
        new(QualityPreset.Custom, "Özel bit hızı"),
    ];

    public IReadOnlyList<Option<VideoCodec>> Codecs { get; } =
    [
        new(VideoCodec.H264, "H.264 — her yerde oynar (önerilen)"),
        new(VideoCodec.Hevc, "HEVC / H.265 — %30 daha küçük"),
        new(VideoCodec.Av1, "AV1 — en küçük (yeni ekran kartları)"),
    ];

    public IReadOnlyList<Option<CaptureTargetMode>> CaptureTargets { get; } =
    [
        new(CaptureTargetMode.GameMonitor, "Oyunun açık olduğu ekran"),
        new(CaptureTargetMode.GameWindow, "Sadece oyun penceresi"),
        new(CaptureTargetMode.PrimaryMonitor, "Her zaman ana ekran"),
    ];

    public IReadOnlyList<Option<RecorderPriority>> Priorities { get; } =
    [
        new(RecorderPriority.Low, "Düşük — oyuna öncelik ver"),
        new(RecorderPriority.Normal, "Normal (önerilen)"),
        new(RecorderPriority.High, "Yüksek — kare kaybını azalt"),
    ];

    public IReadOnlyList<Option<int>> AudioBitrates { get; } = [new(96, "96 kbps"), new(128, "128 kbps"), new(160, "160 kbps"), new(192, "192 kbps"), new(256, "256 kbps")];

    public IReadOnlyList<Option<EncoderChoice>> Encoders
    {
        get
        {
            var working = _services.Capture.Caps?.Working ?? new HashSet<string>();
            bool Has(EncoderVendor vendor) => EncoderCatalog.All.Any(e => e.Vendor == vendor && working.Contains(e.FfmpegName));
            var list = new List<Option<EncoderChoice>> { new(EncoderChoice.Auto, "Otomatik (en iyisini seç)") };
            if (Has(EncoderVendor.Nvenc)) list.Add(new(EncoderChoice.Nvenc, "NVIDIA NVENC"));
            if (Has(EncoderVendor.Amf)) list.Add(new(EncoderChoice.Amf, "AMD AMF"));
            if (Has(EncoderVendor.Qsv)) list.Add(new(EncoderChoice.Qsv, "Intel Quick Sync"));
            if (Has(EncoderVendor.MediaFoundation)) list.Add(new(EncoderChoice.MediaFoundation, "Windows Media Foundation"));
            if (Has(EncoderVendor.X264) || Has(EncoderVendor.OpenH264)) list.Add(new(EncoderChoice.Cpu, "İşlemci (CPU) — en ağır"));
            if (list.All(o => o.Value != S.Encoder)) list.Add(new(S.Encoder, EncoderChoiceName(S.Encoder) + " (bulunamadı)"));
            return list;
        }
    }

    public IReadOnlyList<Option<string?>> Microphones { get; }

    // ── Recording ────────────────────────────────────────────────────────────
    public RecordingMode Mode { get => S.Mode; set => Set(s => s.Mode = value); }

    public int ClipSeconds { get => S.ClipSeconds; set => Set(s => s.ClipSeconds = value); }

    public ResolutionPreset Resolution { get => S.Resolution; set => Set(s => s.Resolution = value); }

    public int Fps { get => S.Fps; set => Set(s => s.Fps = value); }

    public QualityPreset Quality
    {
        get => S.Quality;
        set
        {
            Set(s => s.Quality = value);
            OnPropertyChanged(nameof(IsCustomBitrate));
        }
    }

    public bool IsCustomBitrate => S.Quality == QualityPreset.Custom;

    public int CustomBitrateMbps { get => S.CustomBitrateMbps; set => Set(s => s.CustomBitrateMbps = value); }

    public EncoderChoice Encoder { get => S.Encoder; set => Set(s => s.Encoder = value); }

    public VideoCodec Codec { get => S.Codec; set => Set(s => s.Codec = value); }

    public CaptureTargetMode CaptureTarget { get => S.CaptureTarget; set => Set(s => s.CaptureTarget = value); }

    public bool CaptureCursor { get => S.CaptureCursor; set => Set(s => s.CaptureCursor = value); }

    public bool DetectUnknownFullscreen { get => S.DetectUnknownFullscreen; set => Set(s => s.DetectUnknownFullscreen = value); }

    // ── Audio ────────────────────────────────────────────────────────────────
    public bool RecordSystemAudio { get => S.RecordSystemAudio; set => Set(s => s.RecordSystemAudio = value); }

    public int SystemVolume { get => S.SystemVolume; set => Set(s => s.SystemVolume = value); }

    public bool RecordMicrophone { get => S.RecordMicrophone; set => Set(s => s.RecordMicrophone = value); }

    public string? MicrophoneDeviceId { get => S.MicrophoneDeviceId; set => Set(s => s.MicrophoneDeviceId = value); }

    public int MicrophoneVolume { get => S.MicrophoneVolume; set => Set(s => s.MicrophoneVolume = value); }

    public int AudioBitrateKbps { get => S.AudioBitrateKbps; set => Set(s => s.AudioBitrateKbps = value); }

    public int AvSyncOffsetMs { get => S.AvSyncOffsetMs; set => Set(s => s.AvSyncOffsetMs = value); }

    // ── Hotkeys ──────────────────────────────────────────────────────────────
    public string SaveClipHotkey { get => S.SaveClipHotkey; set => Set(s => s.SaveClipHotkey = value); }

    public string ToggleRecordingHotkey { get => S.ToggleRecordingHotkey; set => Set(s => s.ToggleRecordingHotkey = value); }

    public string ScreenshotHotkey { get => S.ScreenshotHotkey; set => Set(s => s.ScreenshotHotkey = value); }

    public string ToggleBufferHotkey { get => S.ToggleBufferHotkey; set => Set(s => s.ToggleBufferHotkey = value); }

    // ── Storage ──────────────────────────────────────────────────────────────
    public string ClipsFolder => S.EffectiveClipsFolder;

    public string BufferFolder => S.EffectiveBufferFolder;

    public bool StorageLimitEnabled { get => S.StorageLimitEnabled; set => Set(s => s.StorageLimitEnabled = value); }

    public int StorageLimitGb { get => S.StorageLimitGb; set => Set(s => s.StorageLimitGb = value); }

    // ── Performance & general ────────────────────────────────────────────────
    public RecorderPriority Priority { get => S.Priority; set => Set(s => s.Priority = value); }

    public bool TrimMemoryInTray { get => S.TrimMemoryInTray; set => Set(s => s.TrimMemoryInTray = value); }

    public bool StartWithWindows
    {
        get => S.StartWithWindows;
        set
        {
            Set(s => s.StartWithWindows = value);
            StartupRegistration.Apply(value);
        }
    }

    public bool StartMinimized { get => S.StartMinimized; set => Set(s => s.StartMinimized = value); }

    public bool CloseToTray { get => S.CloseToTray; set => Set(s => s.CloseToTray = value); }

    public bool ShowSaveToast { get => S.ShowSaveToast; set => Set(s => s.ShowSaveToast = value); }

    public bool PlaySaveSound { get => S.PlaySaveSound; set => Set(s => s.PlaySaveSound = value); }

    // ── Derived: estimates & info ────────────────────────────────────────────
    [ObservableProperty]
    public partial string OutputSizeText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string BitrateText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string RamEstimateText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string DiskEstimateText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ClipSizeText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string EncoderInfoText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string FfmpegText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsProbing { get; set; }

    private void RefreshDerived()
    {
        var s = S;
        var monitor = WindowInfo.GetPrimaryMonitor();
        var (w, h) = VideoMath.TargetSize(monitor?.Width ?? 1920, monitor?.Height ?? 1080, (int)s.Resolution);
        var caps = _services.Capture.Caps;
        var plan = caps is null ? [] : EncoderCatalog.Plan(s.Encoder, s.Codec, caps.Working);
        var encoder = plan.FirstOrDefault();
        var kbps = VideoMath.VideoBitrateKbps(h, s.Fps, s.Quality, s.CustomBitrateMbps, encoder?.Codec ?? s.Codec);
        var audio = s.RecordSystemAudio || s.RecordMicrophone ? s.AudioBitrateKbps : 0;

        OutputSizeText = $"{w}×{h} @ {s.Fps} FPS" + (monitor is not null && monitor.Height > h ? $"  (ekranın {monitor.Width}×{monitor.Height}, GPU'da küçültülür)" : string.Empty);
        BitrateText = $"{kbps / 1000.0:0.#} Mbps video" + (audio > 0 ? $" + {audio} kbps ses" : string.Empty);
        RamEstimateText = encoder is null
            ? "Kodlayıcı henüz algılanmadı"
            : encoder.IsHardware
                ? "≈ 100–150 MB · görüntü ekran kartında işlenir, RAM'e inmez"
                : encoder.Vendor == EncoderVendor.MediaFoundation
                    ? "≈ 200–260 MB · Windows kodlayıcısı"
                    : "≈ 250–350 MB · işlemciyle kodlama (daha ağır)";
        DiskEstimateText = $"≈ {VideoMath.BufferDiskMegabytes(kbps, audio, s.ClipSeconds):0} MB · arabellek diskte döner, RAM'de büyümez";
        ClipSizeText = $"≈ {VideoMath.Megabytes(kbps + audio, s.ClipSeconds):0} MB / {s.ClipSeconds} sn klip";
        EncoderInfoText = caps is null
            ? "Kontrol ediliyor…"
            : string.Join(" · ", EncoderCatalog.All.Where(e => caps.Working.Contains(e.FfmpegName))
                .GroupBy(e => e.Vendor)
                .Select(g => $"{EncoderInfo.VendorName(g.Key)} ({string.Join(", ", g.Select(e => EncoderInfo.CodecName(e.Codec)))})"));
        FfmpegText = caps is null
            ? _services.Capture.InitError ?? "Bekleniyor…"
            : $"{caps.Version.Replace("ffmpeg version ", string.Empty).Split(' ')[0]} · ekran yakalama: {(caps.HasGfxCapture ? "Windows Graphics Capture" : caps.HasDdaGrab ? "Desktop Duplication" : "yok")}";

        OnPropertyChanged(nameof(Encoders));
        OnPropertyChanged(nameof(Encoder));
        OnPropertyChanged(nameof(ClipsFolder));
        OnPropertyChanged(nameof(BufferFolder));
        OnPropertyChanged(nameof(IsCustomBitrate));
    }

    private static string EncoderChoiceName(EncoderChoice choice) => choice switch
    {
        EncoderChoice.Nvenc => "NVIDIA NVENC",
        EncoderChoice.Amf => "AMD AMF",
        EncoderChoice.Qsv => "Intel Quick Sync",
        EncoderChoice.MediaFoundation => "Windows Media Foundation",
        EncoderChoice.Cpu => "İşlemci (CPU)",
        _ => "Otomatik",
    };

    // ── Commands ─────────────────────────────────────────────────────────────
    [RelayCommand]
    private void BrowseClipsFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Kliplerin kaydedileceği klasör", InitialDirectory = ClipsFolder };
        if (dialog.ShowDialog() == true)
        {
            Set(s => s.ClipsFolder = dialog.FolderName, nameof(ClipsFolder));
        }
    }

    [RelayCommand]
    private void BrowseBufferFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Geçici kayıt arabelleği (hızlı bir SSD önerilir)" };
        if (dialog.ShowDialog() == true)
        {
            Set(s => s.BufferFolder = Path.Combine(dialog.FolderName, "Hindsight Buffer"), nameof(BufferFolder));
        }
    }

    [RelayCommand]
    private void ResetBufferFolder() => Set(s => s.BufferFolder = null, nameof(BufferFolder));

    [RelayCommand]
    private void OpenClipsFolder()
    {
        Directory.CreateDirectory(ClipsFolder);
        Process.Start(new ProcessStartInfo(ClipsFolder) { UseShellExecute = true })?.Dispose();
    }

    [RelayCommand]
    private void OpenLog()
    {
        if (File.Exists(Log.FilePath))
        {
            Process.Start(new ProcessStartInfo(Log.FilePath) { UseShellExecute = true })?.Dispose();
        }
    }

    [RelayCommand]
    private async Task ReprobeAsync()
    {
        IsProbing = true;
        try
        {
            File.Delete(AppPaths.EncoderCacheFile);
            if (FfmpegLocator.Find() is { } ffmpeg)
            {
                await FfmpegCaps.LoadOrProbeAsync(ffmpeg);
            }

            System.Windows.MessageBox.Show("Kodlayıcılar yeniden algılandı. Değişikliklerin geçerli olması için Hindsight'ı yeniden başlat.", "Hindsight");
        }
        finally
        {
            IsProbing = false;
        }
    }

    [RelayCommand]
    private void ResetDefaults()
    {
        if (!Views.DialogWindow.Confirm("Varsayılanlara dön", "Tüm kayıt, ses, kısayol ve performans ayarları sıfırlansın mı? Klip klasörün değişmez."))
        {
            return;
        }

        var keep = S;
        _services.Settings.Update(s =>
        {
            var fresh = new AppSettings
            {
                ClipsFolder = keep.ClipsFolder,
                FirstRunCompleted = true,
                ExcludedGames = [.. keep.ExcludedGames],
                StartWithWindows = keep.StartWithWindows,
            };
            foreach (var property in typeof(AppSettings).GetProperties().Where(p => p.CanWrite))
            {
                property.SetValue(s, property.GetValue(fresh));
            }
        });
        OnPropertyChanged(string.Empty);
    }

    public void Dispose() => _services.Settings.Changed -= OnSettingsChanged;
}
