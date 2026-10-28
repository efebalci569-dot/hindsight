using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hindsight.App.Services;
using Hindsight.Core;
using Hindsight.Core.Media;
using Hindsight.Core.Settings;

namespace Hindsight.App.ViewModels;

/// <summary>Shell: navigation and the recording status shown in the sidebar. Lives only while the window is open.</summary>
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly AppServices _services;
    private readonly DispatcherTimer _timer;
    private GamesViewModel? _games;
    private ClipsViewModel? _clips;
    private SettingsViewModel? _settings;

    public MainViewModel(AppServices services, ClipActions actions, Action<HotkeyAction> runAction)
    {
        _services = services;
        Actions = actions;
        RunAction = runAction;
        Home = new HomeViewModel(services, this);
        CurrentPage = Home;
        _services.Capture.StateChanged += OnCaptureStateChanged;
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        Tick();
    }

    public ClipActions Actions { get; }

    public Action<HotkeyAction> RunAction { get; }

    public HomeViewModel Home { get; }

    public GamesViewModel Games => _games ??= new GamesViewModel(_services, this);

    public ClipsViewModel Clips => _clips ??= new ClipsViewModel(_services, this);

    public SettingsViewModel Settings => _settings ??= new SettingsViewModel(_services);

    [ObservableProperty]
    public partial object CurrentPage { get; set; }

    [ObservableProperty]
    public partial string CurrentKey { get; set; } = "home";

    [ObservableProperty]
    public partial string StatusTitle { get; set; } = "Hazırlanıyor…";

    [ObservableProperty]
    public partial string StatusDetail { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsBuffering { get; set; }

    [ObservableProperty]
    public partial bool IsManualRecording { get; set; }

    [ObservableProperty]
    public partial bool HasError { get; set; }

    [ObservableProperty]
    public partial double BufferFill { get; set; }

    public string SaveHotkey => _services.Settings.Current.SaveClipHotkey;

    [RelayCommand]
    public void Navigate(string key)
    {
        CurrentKey = key;
        CurrentPage = key switch
        {
            "games" => Games,
            "clips" => Clips,
            "settings" => Settings,
            _ => Home,
        };

        if (CurrentPage is ClipsViewModel clips)
        {
            clips.Activate();
        }
        else if (CurrentPage is GamesViewModel games)
        {
            games.Activate();
        }
    }

    public void ShowClipsFor(string? gameName)
    {
        Navigate("clips");
        Clips.SelectGame(gameName);
    }

    [RelayCommand]
    private void SaveClip() => RunAction(HotkeyAction.SaveClip);

    [RelayCommand]
    private void ToggleRecording() => RunAction(HotkeyAction.ToggleRecording);

    [RelayCommand]
    private void Screenshot() => RunAction(HotkeyAction.Screenshot);

    [RelayCommand]
    private void ToggleBuffer() => RunAction(HotkeyAction.ToggleBuffer);

    private void OnCaptureStateChanged(object? sender, EventArgs e) =>
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(Tick);

    private void Tick()
    {
        var capture = _services.Capture;
        var settings = _services.Settings.Current;
        var recorder = capture.Recorder;
        IsBuffering = capture.IsBuffering;
        IsManualRecording = capture.IsManualRecording;
        HasError = recorder?.Status == RecorderStatus.Failed || capture.InitError is not null;
        OnPropertyChanged(nameof(SaveHotkey));

        var gameName = capture.BufferGame?.Name ?? (settings.Mode == RecordingMode.Always ? "Masaüstü" : null);
        if (capture.InitError is not null)
        {
            StatusTitle = "Kayıt motoru yok";
            StatusDetail = capture.InitError;
        }
        else if (!capture.IsReady)
        {
            StatusTitle = "Hazırlanıyor…";
            StatusDetail = "Ekran kartı ve kodlayıcılar kontrol ediliyor";
        }
        else if (capture.IsManualRecording && recorder?.ManualStartedAt is { } started)
        {
            StatusTitle = "Kayıt yapılıyor";
            StatusDetail = $"{VideoMath.FormatDuration((DateTime.Now - started).TotalSeconds)} · {gameName ?? "Masaüstü"}";
        }
        else if (capture.IsBuffering)
        {
            var buffered = recorder?.Session?.BufferedSeconds ?? 0;
            BufferFill = Math.Clamp(buffered / Math.Max(1, settings.ClipSeconds), 0, 1);
            StatusTitle = "Arka plan kaydı açık";
            StatusDetail = $"{gameName ?? "Masaüstü"} · {Math.Min(buffered, settings.ClipSeconds):0}/{settings.ClipSeconds} sn hazır";
        }
        else if (recorder?.Status == RecorderStatus.Starting)
        {
            StatusTitle = "Başlatılıyor…";
            StatusDetail = capture.ActiveGame?.Game.Name ?? string.Empty;
        }
        else if (recorder?.Status == RecorderStatus.Failed)
        {
            StatusTitle = "Kayıt hatası";
            StatusDetail = recorder.LastError ?? string.Empty;
        }
        else if (capture.IsPausedByUser)
        {
            StatusTitle = "Duraklatıldı";
            StatusDetail = $"{settings.ToggleBufferHotkey} ile tekrar aç";
        }
        else
        {
            StatusTitle = settings.Mode == RecordingMode.Off ? "Arka plan kaydı kapalı" : "Oyun bekleniyor";
            StatusDetail = settings.Mode == RecordingMode.Off ? "Ayarlar > Kayıt modundan açabilirsin" : "Bir oyun açınca kayıt kendiliğinden başlar";
        }

        if (!IsBuffering)
        {
            BufferFill = 0;
        }

        if (ReferenceEquals(CurrentPage, Home))
        {
            Home.Tick();
        }
    }

    public void Dispose()
    {
        _timer.Stop();
        _services.Capture.StateChanged -= OnCaptureStateChanged;
        Home.Dispose();
        _games?.Dispose();
        _clips?.Dispose();
        _settings?.Dispose();
    }
}
