using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hindsight.App.Services;
using Hindsight.Core.Media;

namespace Hindsight.App.ViewModels;

public sealed partial class HomeViewModel : ObservableObject, IDisposable
{
    private readonly AppServices _services;
    private readonly MainViewModel _main;
    private readonly Process _self = Process.GetCurrentProcess();
    private TimeSpan _lastCpu;
    private DateTime _lastCpuAt = DateTime.UtcNow;
    private int _tick;

    public HomeViewModel(AppServices services, MainViewModel main)
    {
        _services = services;
        _main = main;
        _services.Clips.Changed += OnClipsChanged;
        ShowWelcome = !services.Settings.Current.FirstRunCompleted;
        LoadRecent();
    }

    public MainViewModel Main => _main;

    public ObservableCollection<ClipCardViewModel> Recent { get; } = [];

    [ObservableProperty]
    public partial bool ShowWelcome { get; set; }

    [ObservableProperty]
    public partial string ProfileText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string EncoderText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string AppMemoryText { get; set; } = "—";

    [ObservableProperty]
    public partial string RecorderMemoryText { get; set; } = "—";

    [ObservableProperty]
    public partial string RecorderCpuText { get; set; } = "—";

    [ObservableProperty]
    public partial string DiskBufferText { get; set; } = "—";

    [ObservableProperty]
    public partial string ClipCountText { get; set; } = "0";

    [ObservableProperty]
    public partial string StorageText { get; set; } = "0 MB";

    [ObservableProperty]
    public partial string GameCountText { get; set; } = "0";

    [ObservableProperty]
    public partial string WeekPlaytimeText { get; set; } = "0 sa";

    public string SaveHotkey => _services.Settings.Current.SaveClipHotkey;

    public string RecordHotkey => _services.Settings.Current.ToggleRecordingHotkey;

    public string ScreenshotHotkey => _services.Settings.Current.ScreenshotHotkey;

    public string BufferHotkey => _services.Settings.Current.ToggleBufferHotkey;

    public bool HasRecent => Recent.Count > 0;

    public void Tick()
    {
        var capture = _services.Capture;
        var s = _services.Settings.Current;
        OnPropertyChanged(nameof(SaveHotkey));
        OnPropertyChanged(nameof(RecordHotkey));
        OnPropertyChanged(nameof(ScreenshotHotkey));
        OnPropertyChanged(nameof(BufferHotkey));

        if (capture.Recorder?.Session is { } session && capture.IsBuffering)
        {
            var r = session.Request;
            EncoderText = session.Variant.Encoder.DisplayName + (session.Variant.Label == "gpu-zero-copy" ? " · tamamen GPU'da" : string.Empty);
            ProfileText = $"{r.Width}×{r.Height} · {r.Fps} FPS · {r.VideoKbps / 1000.0:0.#} Mbps · son {r.ClipSeconds} sn";
        }
        else
        {
            var caps = capture.Caps;
            var plan = caps is null ? [] : EncoderCatalog.Plan(s.Encoder, s.Codec, caps.Working);
            EncoderText = plan.Count > 0 ? plan[0].DisplayName : capture.IsReady ? "Kodlayıcı bulunamadı" : "Kontrol ediliyor…";
            ProfileText = $"{(int)s.Resolution}p · {s.Fps} FPS · son {s.ClipSeconds} sn";
        }

        _self.Refresh();
        AppMemoryText = VideoMath.FormatSize(_self.WorkingSet64);
        if (capture.RecorderUsage() is { } usage)
        {
            RecorderMemoryText = VideoMath.FormatSize(usage.WorkingSetBytes);
            var now = DateTime.UtcNow;
            var elapsed = (now - _lastCpuAt).TotalSeconds;
            if (_lastCpu != TimeSpan.Zero && elapsed > 0 && usage.CpuTime >= _lastCpu)
            {
                var percent = (usage.CpuTime - _lastCpu).TotalSeconds / elapsed / Environment.ProcessorCount * 100;
                RecorderCpuText = $"%{percent:0.0}";
            }

            _lastCpu = usage.CpuTime;
            _lastCpuAt = now;
            if (_tick++ % 3 == 0)
            {
                DiskBufferText = VideoMath.FormatSize(BufferBytes(capture.Recorder?.Session?.Directory));
            }
        }
        else
        {
            RecorderMemoryText = "Kapalı";
            RecorderCpuText = "—";
            DiskBufferText = "—";
            _lastCpu = TimeSpan.Zero;
        }

        if (_tick % 5 == 0)
        {
            UpdateStats();
        }
    }

    private static long BufferBytes(string? directory)
    {
        try
        {
            return directory is null || !Directory.Exists(directory)
                ? 0
                : new DirectoryInfo(directory).EnumerateFiles("*.ts").Sum(f => f.Length);
        }
        catch (IOException)
        {
            return 0;
        }
    }

    private void UpdateStats()
    {
        var items = _services.Clips.Items;
        ClipCountText = items.Count.ToString();
        StorageText = VideoMath.FormatSize(items.Sum(i => i.SizeBytes));
        GameCountText = _services.Games.Games.Count(g => !g.Hidden).ToString();
        var week = _services.Playtime.SecondsThisWeek();
        WeekPlaytimeText = week >= 3600 ? $"{week / 3600:0.#} sa" : $"{week / 60:0} dk";
    }

    private void OnClipsChanged(object? sender, EventArgs e) =>
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(LoadRecent);

    private void LoadRecent()
    {
        Recent.Clear();
        foreach (var item in _services.Clips.Items.Take(8))
        {
            Recent.Add(new ClipCardViewModel(item, _services.Capture.Thumbnails));
        }

        OnPropertyChanged(nameof(HasRecent));
        UpdateStats();
    }

    [RelayCommand]
    private void DismissWelcome()
    {
        ShowWelcome = false;
        _services.Settings.Update(s => s.FirstRunCompleted = true);
    }

    [RelayCommand]
    private void OpenClip(ClipCardViewModel? card) => _main.Actions.Open(card);

    [RelayCommand]
    private void OpenClipsFolder() => _main.Actions.OpenFolder(_services.Clips.Root);

    [RelayCommand]
    private void GoToClips() => _main.Navigate("clips");

    [RelayCommand]
    private void GoToSettings() => _main.Navigate("settings");

    public void Dispose()
    {
        _services.Clips.Changed -= OnClipsChanged;
        _self.Dispose();
    }
}
