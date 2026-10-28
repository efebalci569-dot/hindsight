using System.Diagnostics;
using Hindsight.Core.Clips;
using Hindsight.Core.Games;
using Hindsight.Core.Media;
using Hindsight.Core.Native;
using Hindsight.Core.Settings;

namespace Hindsight.Core;

public enum NoticeKind { Info, Success, Warning, Error }

public sealed record Notice(string Title, string Message, NoticeKind Kind, string? FilePath = null);

/// <summary>
/// Ties everything together: watches for games, runs the replay buffer only while it is wanted,
/// restarts it when settings or the game window change, and turns hotkey presses into clips.
/// All public methods are safe to call from any thread.
/// </summary>
public sealed class CaptureController : IAsyncDisposable
{
    private static readonly TimeSpan GameEndGrace = TimeSpan.FromSeconds(5);

    private readonly SettingsStore _settings;
    private readonly GameLibrary _games;
    private readonly ClipLibrary _clips;
    private readonly PlaytimeStore _playtime;
    private readonly GameDetector _detector;
    private readonly SemaphoreSlim _evaluate = new(1, 1);
    private readonly SemaphoreSlim _save = new(1, 1);
    private readonly List<DateTime> _failures = [];
    private CancellationTokenSource? _pendingEvaluate;
    private Timer? _playtimeTimer;
    private DateTime _lastPlaytimeTick = DateTime.Now;
    private string? _runningKey;
    private bool? _override;
    private bool _gaveUp;
    private bool _disposed;

    public CaptureController(SettingsStore settings, GameLibrary games, ClipLibrary clips, PlaytimeStore playtime, GameDetector detector)
    {
        _settings = settings;
        _games = games;
        _clips = clips;
        _playtime = playtime;
        _detector = detector;
    }

    public FfmpegCaps? Caps { get; private set; }

    public ClipTools? Tools { get; private set; }

    public ThumbnailService? Thumbnails { get; private set; }

    public ReplayRecorder? Recorder { get; private set; }

    public string? InitError { get; private set; }

    public bool IsReady => Recorder is not null;

    public GameSession? ActiveGame => _detector.Active;

    /// <summary>The game the running buffer belongs to (null = desktop).</summary>
    public GameEntry? BufferGame { get; private set; }

    public bool IsBuffering => Recorder?.Status == RecorderStatus.Buffering;

    public bool IsManualRecording => Recorder?.IsManualRecording == true;

    /// <summary>True when the user switched the buffer off by hand (hotkey or button).</summary>
    public bool IsPausedByUser => _override == false;

    public event EventHandler? StateChanged;

    public event EventHandler<Notice>? Notified;

    public event EventHandler<ClipItem>? ClipSaved;

    /// <param name="startRecording">False only for UI previews: probe the encoders but never capture.</param>
    public async Task InitializeAsync(bool startRecording = true, CancellationToken cancellationToken = default)
    {
        if (startRecording)
        {
            _detector.ActiveChanged += OnActiveGameChanged;
            _detector.WindowChanged += (_, _) => ScheduleEvaluate(TimeSpan.FromSeconds(1.5));
            _settings.Changed += OnSettingsChanged;
            _detector.Start();
            _playtimeTimer = new Timer(_ => PlaytimeTick(), null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));
        }

        var ffmpeg = FfmpegLocator.Find();
        if (ffmpeg is null)
        {
            InitError = "ffmpeg bulunamadı. Hindsight'ı kurulum dosyasıyla yeniden kurmak bunu düzeltir.";
            Notify("Kayıt motoru eksik", InitError, NoticeKind.Error);
            RaiseState();
            return;
        }

        Caps = await FfmpegCaps.LoadOrProbeAsync(ffmpeg, cancellationToken).ConfigureAwait(false);
        if (Caps is null)
        {
            InitError = "ffmpeg çalıştırılamadı (günlüğe bakın).";
            Notify("Kayıt motoru başlatılamadı", InitError, NoticeKind.Error);
            RaiseState();
            return;
        }

        Tools = new ClipTools(Caps);
        Thumbnails = new ThumbnailService(Tools);
        if (!startRecording)
        {
            RaiseState();
            return;
        }

        Recorder = new ReplayRecorder(Caps);
        Recorder.StatusChanged += OnRecorderStatusChanged;
        RaiseState();
        await EvaluateAsync().ConfigureAwait(false);
    }

    // ── What should be running? ──────────────────────────────────────────────

    private bool ShouldRun()
    {
        if (_override is { } forced)
        {
            return forced;
        }

        return _settings.Current.Mode switch
        {
            RecordingMode.Always => true,
            RecordingMode.GamesOnly => _detector.Active is not null,
            _ => false,
        };
    }

    public async Task EvaluateAsync()
    {
        if (Recorder is null || Caps is null || _disposed)
        {
            return;
        }

        await _evaluate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!ShouldRun())
            {
                if (Recorder.Status is RecorderStatus.Buffering or RecorderStatus.Failed or RecorderStatus.Starting)
                {
                    await FinishManualRecordingAsync().ConfigureAwait(false);
                    await Recorder.StopAsync().ConfigureAwait(false);
                    _runningKey = null;
                }

                RaiseState();
                return;
            }

            if (_gaveUp)
            {
                return;
            }

            var session = _detector.Active;
            var request = BuildRequest(session);
            var key = RequestKey(request);
            if (Recorder.Status == RecorderStatus.Buffering && key == _runningKey)
            {
                // Same screen, same settings: keep recording, but clips now belong to the game in front.
                if (!ReferenceEquals(BufferGame, session?.Game))
                {
                    BufferGame = session?.Game;
                    RaiseState();
                }

                return;
            }

            await FinishManualRecordingAsync().ConfigureAwait(false);
            BufferGame = session?.Game;
            _runningKey = null; // a failed start is counted here, not again by the status handler
            if (await Recorder.StartAsync(request).ConfigureAwait(false))
            {
                _runningKey = key;
            }
            else
            {
                _runningKey = null;
                RegisterFailure();
                Notify("Arka plan kaydı başlatılamadı", Recorder.LastError ?? "Bilinmeyen hata", NoticeKind.Error);
            }

            RaiseState();
        }
        catch (Exception ex)
        {
            Log.Error("Evaluate failed", ex);
        }
        finally
        {
            _evaluate.Release();
        }
    }

    private void ScheduleEvaluate(TimeSpan delay)
    {
        var cts = new CancellationTokenSource();
        Interlocked.Exchange(ref _pendingEvaluate, cts)?.Cancel();
        _ = Task.Delay(delay, cts.Token).ContinueWith(t =>
        {
            if (!t.IsCanceled)
            {
                _ = EvaluateAsync();
            }
        }, TaskScheduler.Default);
    }

    public RecordingRequest BuildRequest(GameSession? session)
    {
        var s = _settings.Current;
        var source = BuildSource(session, s.CaptureTarget);
        var (width, height) = VideoMath.TargetSize(source.SourceWidth, source.SourceHeight, (int)s.Resolution);
        var encoders = EncoderCatalog.Plan(s.Encoder, s.Codec, Caps?.Working ?? new HashSet<string>());
        var kbps = VideoMath.VideoBitrateKbps(height, s.Fps, s.Quality, s.CustomBitrateMbps, encoders.FirstOrDefault()?.Codec ?? VideoCodec.H264);
        var audio = new AudioOptions(s.RecordSystemAudio, s.SystemVolume / 100f, s.RecordMicrophone, s.MicrophoneDeviceId,
            s.MicrophoneVolume / 100f, s.AudioBitrateKbps, s.AvSyncOffsetMs);
        return new RecordingRequest(source, width, height, s.Fps, kbps, encoders, audio, s.ClipSeconds,
            s.EffectiveBufferFolder, s.Priority, s.CaptureCursor);
    }

    private static CaptureSource BuildSource(GameSession? session, CaptureTargetMode mode)
    {
        if (session is not null && mode == CaptureTargetMode.GameWindow && session.Window.Handle != 0 &&
            WindowInfo.GetWindowSize(session.Window.Handle) is { } size)
        {
            return new CaptureSource(session.Window.Monitor, session.Window.Handle, size.Width, size.Height, $"pencere: {session.Game.Name}");
        }

        var monitor = session is not null && mode != CaptureTargetMode.PrimaryMonitor
            ? WindowInfo.GetMonitor(session.Window.Monitor) ?? WindowInfo.GetPrimaryMonitor()
            : WindowInfo.GetPrimaryMonitor();
        return monitor is null
            ? new CaptureSource(0, 0, 1920, 1080, "ekran 1")
            : new CaptureSource(monitor.Handle, 0, monitor.Width, monitor.Height, $"ekran {monitor.Device.TrimStart('\\', '.')} ({monitor.Width}×{monitor.Height})");
    }

    private static string RequestKey(RecordingRequest r) => string.Join('|',
        r.Source.MonitorHandle, r.Source.WindowHandle, r.Width, r.Height, r.Fps, r.VideoKbps,
        string.Join(',', r.Encoders.Select(e => e.FfmpegName)), r.Audio, r.ClipSeconds, r.BufferRoot, r.Priority, r.CaptureCursor);

    // ── Events ───────────────────────────────────────────────────────────────

    private void OnActiveGameChanged(object? sender, GameSession? session)
    {
        if (session is not null)
        {
            _playtime.BeginSession(session.Game.Id);
            _lastPlaytimeTick = DateTime.Now;
            if (_override == false)
            {
                _override = null; // a new game: auto mode again
            }

            _gaveUp = false;
            ScheduleEvaluate(TimeSpan.FromMilliseconds(300));
        }
        else
        {
            _playtime.Flush();
            if (_override == false)
            {
                _override = null;
            }

            // Wait a moment: some games restart their own process right after start.
            ScheduleEvaluate(GameEndGrace);
        }

        RaiseState();
    }

    private void OnSettingsChanged(object? sender, AppSettings settings)
    {
        if (!string.Equals(_clips.Root, settings.EffectiveClipsFolder, StringComparison.OrdinalIgnoreCase))
        {
            _clips.SetRoot(settings.EffectiveClipsFolder);
        }

        _gaveUp = false;
        _failures.Clear();
        ScheduleEvaluate(TimeSpan.FromMilliseconds(900));
    }

    private void OnRecorderStatusChanged(object? sender, EventArgs e)
    {
        if (Recorder?.Status == RecorderStatus.Failed && _runningKey is not null)
        {
            // ffmpeg died while buffering (driver reset, display mode change, …): try again with a back-off.
            _runningKey = null;
            var delay = RegisterFailure();
            if (!_gaveUp)
            {
                ScheduleEvaluate(delay);
            }
        }

        RaiseState();
    }

    private TimeSpan RegisterFailure()
    {
        var now = DateTime.Now;
        _failures.RemoveAll(f => now - f > TimeSpan.FromMinutes(3));
        _failures.Add(now);
        if (_failures.Count >= 5)
        {
            _gaveUp = true;
            Notify("Arka plan kaydı durduruldu", "Kayıt art arda başarısız oldu. Ayarlar > Kayıt bölümünden kodlayıcıyı veya çözünürlüğü değiştirmeyi dene.", NoticeKind.Error);
        }

        return TimeSpan.FromSeconds(Math.Min(30, 2 * Math.Pow(2, _failures.Count - 1)));
    }

    private void PlaytimeTick()
    {
        var now = DateTime.Now;
        var elapsed = (now - _lastPlaytimeTick).TotalSeconds;
        _lastPlaytimeTick = now;
        if (_detector.Active is { } session)
        {
            _playtime.Add(session.Game.Id, elapsed);
        }
    }

    // ── User actions ─────────────────────────────────────────────────────────

    /// <summary>Hotkey / button: switch the background buffer on or off regardless of the mode.</summary>
    public async Task ToggleBufferAsync()
    {
        var turnOn = !(IsBuffering || Recorder?.Status == RecorderStatus.Starting);
        _override = turnOn;
        _gaveUp = false;
        _failures.Clear();
        await EvaluateAsync().ConfigureAwait(false);

        // Report what actually happened (a failed start already showed its own error).
        if (!turnOn)
        {
            Notify("Arka plan kaydı kapalı", "Kısayolla tekrar açabilirsin.", NoticeKind.Info);
        }
        else if (IsBuffering)
        {
            Notify("Arka plan kaydı açık", $"Son {_settings.Current.ClipSeconds} saniye tutuluyor.", NoticeKind.Info);
        }
    }

    /// <summary>
    /// Settings → "Test recording": records 4 s of the primary monitor with the current settings in a
    /// throw-away folder, saves a 2 s clip and checks it. Returns a human-readable verdict.
    /// </summary>
    public async Task<(bool Ok, string Message)> TestRecordingAsync()
    {
        if (Caps is null)
        {
            return (false, InitError ?? "Kayıt motoru hazır değil.");
        }

        var root = Path.Combine(AppPaths.LocalDir, "selftest");
        try
        {
            var request = BuildRequest(null) with { BufferRoot = Path.Combine(root, "buffer"), ClipSeconds = 10 };
            await using var recorder = new ReplayRecorder(Caps);
            if (!await recorder.StartAsync(request).ConfigureAwait(false))
            {
                return (false, "Kayıt başlatılamadı. Ayrıntılar günlük dosyasında.");
            }

            var variant = recorder.Session!.Variant;
            await Task.Delay(TimeSpan.FromSeconds(4)).ConfigureAwait(false);
            var clip = await recorder.SaveReplayAsync(2, Path.Combine(root, "test.mp4")).ConfigureAwait(false);
            var info = Tools is null ? null : await Tools.ProbeAsync(clip.Path).ConfigureAwait(false);
            await recorder.StopAsync().ConfigureAwait(false);
            if (info is null || info.DurationSeconds < 1)
            {
                return (false, "Kayıt başladı ama klip okunamadı. Ayrıntılar günlük dosyasında.");
            }

            var audio = request.Audio.Enabled ? (info.HasAudio ? " · ses var" : " · ses YOK") : string.Empty;
            return (true, $"Çalışıyor: {variant.Encoder.DisplayName} ({(variant.Label.StartsWith("gpu-zero-copy", StringComparison.Ordinal) ? "tamamen GPU'da" : variant.Label)}) · {info.Width}×{info.Height}{audio}");
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return (false, ex.Message);
        }
        finally
        {
            try
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>Back to "follow the recording mode" after a manual toggle.</summary>
    public async Task ResetOverrideAsync()
    {
        _override = null;
        await EvaluateAsync().ConfigureAwait(false);
    }

    public async Task<ClipItem?> SaveClipAsync()
    {
        if (Recorder?.Session is null)
        {
            Notify("Kaydedilecek görüntü yok", InitError ?? "Arka plan kaydı çalışmıyor. Bir oyun açınca otomatik başlar.", NoticeKind.Warning);
            return null;
        }

        if (!await _save.WaitAsync(0).ConfigureAwait(false))
        {
            return null; // already saving
        }

        try
        {
            var game = BufferGame;
            var name = game?.Name ?? "Masaüstü";
            var path = _clips.NewClipPath(name, ClipKind.Clip);
            var seconds = _settings.Current.ClipSeconds;
            var result = await Recorder.SaveReplayAsync(seconds, path).ConfigureAwait(false);
            var item = _clips.Add(path, new ClipMeta
            {
                GameId = game?.Id,
                GameName = name,
                Kind = ClipKind.Clip,
                DurationSeconds = result.DurationSeconds,
                Width = result.Width,
                Height = result.Height,
                CreatedAt = DateTime.Now,
            });
            Notify("Klip kaydedildi", $"{name} · {Math.Round(result.DurationSeconds)} sn", NoticeKind.Success, path);
            ClipSaved?.Invoke(this, item);
            _ = AfterSaveAsync(item);
            return item;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            Notify("Klip kaydedilemedi", ex.Message, NoticeKind.Error);
            return null;
        }
        finally
        {
            _save.Release();
        }
    }

    public async Task ToggleManualRecordingAsync()
    {
        if (Recorder is null)
        {
            Notify("Kayıt motoru hazır değil", InitError ?? string.Empty, NoticeKind.Error);
            return;
        }

        if (Recorder.IsManualRecording)
        {
            await FinishManualRecordingAsync().ConfigureAwait(false);
            if (_override == true && _settings.Current.Mode == RecordingMode.Off)
            {
                _override = null;
                await EvaluateAsync().ConfigureAwait(false);
            }

            return;
        }

        if (!IsBuffering)
        {
            _override = true;
            await EvaluateAsync().ConfigureAwait(false);
        }

        try
        {
            Recorder.StartManualRecording();
            Notify("Kayıt başladı", "Durdurmak için kısayola tekrar bas.", NoticeKind.Info);
        }
        catch (InvalidOperationException ex)
        {
            Notify("Kayıt başlatılamadı", ex.Message, NoticeKind.Error);
        }

        RaiseState();
    }

    private async Task FinishManualRecordingAsync()
    {
        if (Recorder is not { IsManualRecording: true })
        {
            return;
        }

        var game = BufferGame;
        var name = game?.Name ?? "Masaüstü";
        var path = _clips.NewClipPath(name, ClipKind.Recording);
        try
        {
            if (await Recorder.StopManualRecordingAsync(path).ConfigureAwait(false) is { } result)
            {
                var item = _clips.Add(path, new ClipMeta
                {
                    GameId = game?.Id,
                    GameName = name,
                    Kind = ClipKind.Recording,
                    DurationSeconds = result.DurationSeconds,
                    Width = result.Width,
                    Height = result.Height,
                    CreatedAt = DateTime.Now,
                });
                Notify("Kayıt kaydedildi", $"{name} · {VideoMath.FormatDuration(result.DurationSeconds)}", NoticeKind.Success, path);
                ClipSaved?.Invoke(this, item);
                _ = AfterSaveAsync(item);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            Notify("Kayıt kaydedilemedi", ex.Message, NoticeKind.Error);
        }

        RaiseState();
    }

    public async Task<ClipItem?> TakeScreenshotAsync()
    {
        if (Tools is null)
        {
            Notify("Kayıt motoru hazır değil", InitError ?? string.Empty, NoticeKind.Error);
            return null;
        }

        var session = _detector.Active;
        var source = BuildSource(session, _settings.Current.CaptureTarget);
        var name = session?.Game.Name ?? "Masaüstü";
        var path = _clips.NewClipPath(name, ClipKind.Screenshot);
        if (!await Tools.ScreenshotAsync(source, path).ConfigureAwait(false))
        {
            Notify("Ekran görüntüsü alınamadı", "Günlük dosyasında ayrıntı var.", NoticeKind.Error);
            return null;
        }

        var item = _clips.Add(path, new ClipMeta
        {
            GameId = session?.Game.Id,
            GameName = name,
            Kind = ClipKind.Screenshot,
            Width = source.SourceWidth,
            Height = source.SourceHeight,
            CreatedAt = DateTime.Now,
        });
        Notify("Ekran görüntüsü kaydedildi", name, NoticeKind.Success, path);
        ClipSaved?.Invoke(this, item);
        return item;
    }

    private async Task AfterSaveAsync(ClipItem item)
    {
        try
        {
            if (Thumbnails is not null)
            {
                await Thumbnails.GetAsync(item).ConfigureAwait(false);
            }

            var s = _settings.Current;
            if (s.StorageLimitEnabled)
            {
                _clips.EnforceLimit((long)s.StorageLimitGb * 1024 * 1024 * 1024);
            }
        }
        catch (Exception ex)
        {
            Log.Error("After-save work failed", ex);
        }
    }

    /// <summary>Live resource use of the recorder process (for the home page).</summary>
    public (long WorkingSetBytes, TimeSpan CpuTime)? RecorderUsage()
    {
        if (Recorder?.Session?.ProcessId is not { } pid)
        {
            return null;
        }

        try
        {
            using var process = Process.GetProcessById(pid);
            return (process.WorkingSet64, process.TotalProcessorTime);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private void Notify(string title, string message, NoticeKind kind, string? path = null) =>
        Notified?.Invoke(this, new Notice(title, message, kind, path));

    private void RaiseState() => StateChanged?.Invoke(this, EventArgs.Empty);

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        _playtimeTimer?.Dispose();
        _detector.Dispose();
        _playtime.Flush();
        if (Recorder is not null)
        {
            try
            {
                await FinishManualRecordingAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Error("Could not finish the manual recording on exit", ex);
            }

            await Recorder.DisposeAsync().ConfigureAwait(false);
        }
    }
}
