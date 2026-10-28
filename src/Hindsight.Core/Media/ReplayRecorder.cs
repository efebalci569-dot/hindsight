using System.Diagnostics;
using System.Text;
using Hindsight.Core.Native;
using Hindsight.Core.Settings;

namespace Hindsight.Core.Media;

public enum RecorderStatus { Idle, Starting, Buffering, Failed }

public sealed record ClipResult(string Path, double DurationSeconds, int Width, int Height);

/// <summary>A running (or just stopped) replay-buffer session: one ffmpeg process writing segments into <see cref="Directory"/>.</summary>
public sealed class ReplaySession
{
    internal ReplaySession(string directory, RecordingRequest request, PipelineVariant variant)
    {
        Directory = directory;
        Request = request;
        Variant = variant;
        StartedAt = DateTime.Now;
    }

    public string Directory { get; }

    public RecordingRequest Request { get; }

    public PipelineVariant Variant { get; }

    public DateTime StartedAt { get; }

    public string ListPath => Path.Combine(Directory, FfmpegArgs.SegmentList);

    internal Process? Process { get; set; }

    internal AudioMixerPump? Audio { get; set; }

    public int? ProcessId
    {
        get
        {
            try
            {
                return Process is { HasExited: false } p ? p.Id : null;
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }
    }

    public bool IsRunning => ProcessId is not null;

    /// <summary>Seconds of footage currently available for a clip.</summary>
    public double BufferedSeconds
    {
        get
        {
            var segments = SegmentList.Read(ListPath);
            var picked = SegmentList.SelectLast(segments, Request.ClipSeconds, VideoMath.SegmentWrap(Request.ClipSeconds));
            return picked.Sum(s => s.Duration);
        }
    }
}

/// <summary>
/// The background "instant replay" recorder. It keeps the last N seconds of gameplay as a ring of
/// small encoded files on disk and turns them into an MP4 on demand — the same idea as SteelSeries
/// Moments or ShadowPlay, but with RAM use that does not grow with the clip length.
/// </summary>
public sealed class ReplayRecorder : IAsyncDisposable
{
    private readonly FfmpegCaps _caps;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly Queue<string> _stderr = new();
    private ManualRecording? _manual;
    private bool _stopping;

    public ReplayRecorder(FfmpegCaps caps)
    {
        _caps = caps;
    }

    public RecorderStatus Status { get; private set; }

    public ReplaySession? Session { get; private set; }

    public string? LastError { get; private set; }

    public bool IsManualRecording => _manual is not null;

    public DateTime? ManualStartedAt => _manual?.StartedAt;

    /// <summary>Raised on any status change, including an unexpected ffmpeg exit (status becomes Failed).</summary>
    public event EventHandler? StatusChanged;

    public async Task<bool> StartAsync(RecordingRequest request, CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
            SetStatus(RecorderStatus.Starting);
            CleanOldSessions(request.BufferRoot);

            var variants = FfmpegArgs.Variants(request, _caps);
            if (variants.Count == 0)
            {
                LastError = _caps.HasGfxCapture || _caps.HasDdaGrab
                    ? "Bu bilgisayarda çalışan bir video kodlayıcı bulunamadı."
                    : "ffmpeg sürümü ekran yakalamayı desteklemiyor (gfxcapture/ddagrab yok).";
                SetStatus(RecorderStatus.Failed);
                return false;
            }

            var errors = new StringBuilder();
            string? firstFailure = null;
            foreach (var variant in variants)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var directory = Path.Combine(request.BufferRoot, $"session-{DateTime.Now:yyyyMMdd-HHmmss-fff}");
                Directory.CreateDirectory(directory);
                var session = new ReplaySession(directory, request, variant);
                if (await TryLaunchAsync(session, cancellationToken).ConfigureAwait(false))
                {
                    Session = session;
                    LastError = null;
                    Log.Info($"Replay buffer started: {variant.Encoder.DisplayName} [{variant.Label}] {request.Width}x{request.Height}@{request.Fps} {request.VideoKbps} kbps, {request.ClipSeconds}s, {request.Source.Description}" +
                             (variant.MonitorFallback ? " (window capture failed; recording its monitor)" : string.Empty));
                    SetStatus(RecorderStatus.Buffering);
                    return true;
                }

                errors.AppendLine($"  {variant.Encoder.FfmpegName} [{variant.Label}]: {LastStderrLine()}");
                // The first failure carries the real cause; keep ffmpeg's full output for the log.
                firstFailure ??= string.Join(Environment.NewLine, RecentLog().Select(l => "    " + l));
                TryDeleteDirectory(directory);
            }

            LastError = "Kayıt başlatılamadı. Ayrıntılar günlük dosyasında (Ayarlar > Sistem ve hakkında).";
            Log.Error($"Recording could not start. Tried:{Environment.NewLine}{errors}First attempt's ffmpeg output:{Environment.NewLine}{firstFailure}");
            SetStatus(RecorderStatus.Failed);
            return false;
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    private async Task<bool> TryLaunchAsync(ReplaySession session, CancellationToken cancellationToken)
    {
        var request = session.Request;
        AudioMixerPump? audio = null;
        if (request.Audio.Enabled)
        {
            audio = new AudioMixerPump(request.Audio);
        }

        var args = FfmpegArgs.Replay(request, session.Variant, session.Directory, audio?.PipePath);
        var process = new Process { StartInfo = FfmpegRunner.CreateStartInfo(_caps.FfmpegPath, args), EnableRaisingEvents = true };
        lock (_stderr)
        {
            _stderr.Clear();
        }

        process.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data))
            {
                return;
            }

            lock (_stderr)
            {
                _stderr.Enqueue(e.Data);
                if (_stderr.Count > 40)
                {
                    _stderr.Dequeue();
                }
            }
        };

        try
        {
            audio?.Start();
            process.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            audio?.Dispose();
            lock (_stderr)
            {
                _stderr.Enqueue(ex.Message);
            }

            return false;
        }

        ChildProcessJob.Add(process);
        FfmpegRunner.TrySetPriority(process, request.Priority switch
        {
            RecorderPriority.Low => ProcessPriorityClass.BelowNormal,
            RecorderPriority.High => ProcessPriorityClass.AboveNormal,
            _ => ProcessPriorityClass.Normal,
        });
        process.BeginErrorReadLine();
        session.Process = process;
        session.Audio = audio;

        // Started = the first segment file is receiving data and ffmpeg is still alive a moment later.
        var firstSegment = Path.Combine(session.Directory, "seg000.ts");
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(8))
        {
            await Task.Delay(150, cancellationToken).ConfigureAwait(false);
            if (process.HasExited)
            {
                break;
            }

            if (File.Exists(firstSegment) && new FileInfo(firstSegment).Length > 0 && deadline.Elapsed > TimeSpan.FromMilliseconds(1200))
            {
                process.Exited += (_, _) => OnProcessExited(session);
                if (process.HasExited)
                {
                    break;
                }

                return true;
            }
        }

        FfmpegRunner.TryKill(process);
        process.WaitForExit(2000);
        audio?.Dispose();
        process.Dispose();
        session.Process = null;
        session.Audio = null;
        return false;
    }

    private void OnProcessExited(ReplaySession session)
    {
        if (_stopping || !ReferenceEquals(session, Session))
        {
            return;
        }

        session.Audio?.Dispose();
        session.Audio = null;
        LastError = "Kayıt beklenmedik şekilde durdu; yeniden deneniyor.";
        Log.Warn($"ffmpeg exited while buffering: {string.Join(" | ", RecentLog().TakeLast(6))}");
        SetStatus(RecorderStatus.Failed);
    }

    public async Task StopAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
            SetStatus(RecorderStatus.Idle);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    private async Task StopCoreAsync()
    {
        var session = Session;
        if (session?.Process is not { } process)
        {
            return;
        }

        _stopping = true;
        try
        {
            if (_manual is not null)
            {
                _manual.Cancel();
                _manual = null;
            }

            try
            {
                if (!process.HasExited)
                {
                    // 'q' lets ffmpeg finish the current segment cleanly; kill if it does not react.
                    await process.StandardInput.WriteAsync('q').ConfigureAwait(false);
                    await process.StandardInput.FlushAsync().ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
            {
            }

            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4)))
            {
                try
                {
                    await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    Log.Warn("ffmpeg did not stop in time; killing it.");
                    FfmpegRunner.TryKill(process);
                    process.WaitForExit(3000);
                }
            }

            session.Audio?.Dispose();
            session.Audio = null;
            process.Dispose();
            session.Process = null;
            Log.Info("Replay buffer stopped.");
        }
        finally
        {
            _stopping = false;
        }
    }

    /// <summary>
    /// Saves the last <paramref name="seconds"/> seconds. While recording, waits for the segment that was being
    /// written at the moment of the request (≤ 2 s) so the clip includes everything up to the key press.
    /// Also works right after the buffer stopped (e.g. the game just closed or crashed).
    /// </summary>
    public async Task<ClipResult> SaveReplayAsync(int seconds, string outputPath, CancellationToken cancellationToken = default)
    {
        var session = Session ?? throw new InvalidOperationException("Arka plan kaydı çalışmıyor; kaydedilecek görüntü yok.");
        if (session.IsRunning)
        {
            await WaitForNextSegmentAsync(session, cancellationToken).ConfigureAwait(false);
        }

        var wrap = VideoMath.SegmentWrap(session.Request.ClipSeconds);
        var picked = SegmentList.SelectLast(SegmentList.Read(session.ListPath), seconds, wrap)
            .Where(s => File.Exists(Path.Combine(session.Directory, s.FileName)))
            .ToList();
        if (picked.Count == 0)
        {
            throw new InvalidOperationException("Arabellek henüz boş — birkaç saniye sonra tekrar dene.");
        }

        var files = picked.Select(s => Path.Combine(session.Directory, s.FileName)).ToList();

        // Copy first: the ring keeps overwriting the oldest files while ffmpeg concatenates.
        var staging = Path.Combine(session.Directory, $"save-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        try
        {
            var staged = new List<string>();
            for (var i = 0; i < files.Count; i++)
            {
                var target = Path.Combine(staging, $"{i:D4}.ts");
                File.Copy(files[i], target);
                staged.Add(target);
            }

            await ConcatAsync(staged, outputPath, staging, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            TryDeleteDirectory(staging);
        }

        var duration = picked.Sum(s => s.Duration);
        Log.Info($"Clip saved ({duration:0.0}s, {picked.Count} segments): {outputPath}");
        return new ClipResult(outputPath, duration, session.Request.Width, session.Request.Height);
    }

    private async Task WaitForNextSegmentAsync(ReplaySession session, CancellationToken cancellationToken)
    {
        // Segments are identified by their stream time: line numbers shift once the list is read from its tail.
        var endAtPress = SegmentList.Read(session.ListPath).LastOrDefault()?.End ?? -1;
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(VideoMath.SegmentSeconds + 3) && session.IsRunning)
        {
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            if ((SegmentList.Read(session.ListPath).LastOrDefault()?.End ?? -1) > endAtPress + 0.001)
            {
                return;
            }
        }
    }

    private async Task ConcatAsync(IReadOnlyList<string> files, string outputPath, string workDirectory, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        var list = Path.Combine(workDirectory, "concat.txt");
        await File.WriteAllLinesAsync(list, files.Select(FfmpegRunner.ConcatLine), new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        var partial = outputPath + ".part.mp4";
        var result = await FfmpegRunner.RunAsync(_caps.FfmpegPath, FfmpegArgs.Concat(list, partial), cancellationToken, timeout: TimeSpan.FromMinutes(30)).ConfigureAwait(false);
        if (!result.Success || !File.Exists(partial))
        {
            TryDelete(partial);
            throw new InvalidOperationException("Klip birleştirilemedi: " + result.ErrorTail);
        }

        File.Move(partial, outputPath, overwrite: true);
    }

    // ── Manual (unlimited) recording on top of the running buffer ───────────────

    public void StartManualRecording()
    {
        var session = Session;
        if (session is null || !session.IsRunning)
        {
            throw new InvalidOperationException("Önce arka plan kaydının çalışması gerekiyor.");
        }

        if (_manual is not null)
        {
            return;
        }

        // The segment being written right now starts where the last finished one ended.
        var lastEnd = SegmentList.Read(session.ListPath).LastOrDefault()?.End ?? 0;
        _manual = new ManualRecording(session, lastEnd);
        Log.Info("Manual recording started.");
    }

    public async Task<ClipResult?> StopManualRecordingAsync(string outputPath, CancellationToken cancellationToken = default)
    {
        var manual = _manual;
        if (manual is null)
        {
            return null;
        }

        _manual = null;
        if (manual.Session.IsRunning)
        {
            await WaitForNextSegmentAsync(manual.Session, cancellationToken).ConfigureAwait(false);
        }

        await manual.StopAsync().ConfigureAwait(false);
        try
        {
            if (manual.Parts.Count == 0)
            {
                throw new InvalidOperationException("Kayıt çok kısa.");
            }

            await ConcatAsync(manual.Parts, outputPath, manual.Directory, cancellationToken).ConfigureAwait(false);
            var r = manual.Session.Request;
            Log.Info($"Manual recording saved ({manual.Duration:0.0}s): {outputPath}");
            return new ClipResult(outputPath, manual.Duration, r.Width, r.Height);
        }
        finally
        {
            TryDeleteDirectory(manual.Directory);
        }
    }

    /// <summary>Copies every finished segment out of the ring as soon as it appears, so nothing is overwritten.</summary>
    private sealed class ManualRecording
    {
        private readonly CancellationTokenSource _cancel = new();
        private readonly Task _loop;
        private double _nextStart;
        private int _partNumber;

        public ManualRecording(ReplaySession session, double firstStart)
        {
            Session = session;
            _nextStart = firstStart;
            Directory = Path.Combine(session.Directory, $"manual-{DateTime.Now:HHmmss}");
            System.IO.Directory.CreateDirectory(Directory);
            _loop = Task.Run(LoopAsync);
        }

        public ReplaySession Session { get; }

        public string Directory { get; }

        public DateTime StartedAt { get; } = DateTime.Now;

        public List<string> Parts { get; } = [];

        public double Duration { get; private set; }

        private async Task LoopAsync()
        {
            while (!_cancel.IsCancellationRequested)
            {
                CopyNew();
                try
                {
                    await Task.Delay(700, _cancel.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        private void CopyNew()
        {
            foreach (var segment in SegmentList.Read(Session.ListPath).Where(s => s.Start >= _nextStart - 0.01))
            {
                var source = Path.Combine(Session.Directory, segment.FileName);
                var target = Path.Combine(Directory, $"{_partNumber++:D7}.ts");
                try
                {
                    File.Copy(source, target, overwrite: true);
                    Parts.Add(target);
                    Duration += segment.Duration;
                }
                catch (IOException ex)
                {
                    Log.Warn($"Manual recording: could not copy {segment.FileName}: {ex.Message}");
                }

                _nextStart = segment.End;
            }
        }

        public async Task StopAsync()
        {
            _cancel.Cancel();
            await _loop.ConfigureAwait(false);
            CopyNew();
        }

        public void Cancel() => _cancel.Cancel();
    }

    // ── helpers ─────────────────────────────────────────────────────────────────

    private string LastStderrLine()
    {
        lock (_stderr)
        {
            var useful = _stderr.Reverse().FirstOrDefault(l => l.Contains("rror", StringComparison.Ordinal) || l.Contains("ailed", StringComparison.Ordinal));
            return useful ?? _stderr.LastOrDefault() ?? "(ffmpeg çıktı vermedi)";
        }
    }

    public IReadOnlyList<string> RecentLog()
    {
        lock (_stderr)
        {
            return [.. _stderr];
        }
    }

    private void SetStatus(RecorderStatus status)
    {
        Status = status;
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private void CleanOldSessions(string bufferRoot)
    {
        try
        {
            Directory.CreateDirectory(bufferRoot);
            foreach (var dir in Directory.EnumerateDirectories(bufferRoot, "session-*"))
            {
                TryDeleteDirectory(dir);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Could not clean the buffer folder: {ex.Message}");
        }

        Session = null;
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        if (Session is not null)
        {
            TryDeleteDirectory(Session.Directory);
            Session = null;
        }
    }
}
