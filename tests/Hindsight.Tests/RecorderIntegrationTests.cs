using System.Diagnostics;
using Hindsight.Core;
using Hindsight.Core.Media;
using Hindsight.Core.Native;
using Hindsight.Core.Settings;
using Xunit.Abstractions;

namespace Hindsight.Tests;

/// <summary>
/// Records the real screen and system audio. Runs only with HINDSIGHT_INTEGRATION=1
/// (needs a desktop session, a GPU or Media Foundation encoder and ffmpeg on the machine).
/// </summary>
public class RecorderIntegrationTests(ITestOutputHelper output)
{
    private static bool Enabled => Environment.GetEnvironmentVariable("HINDSIGHT_INTEGRATION") == "1";

    [Fact]
    public async Task RecordsSavesClipAndManualRecording()
    {
        if (!Enabled)
        {
            return;
        }

        var root = Environment.GetEnvironmentVariable("HINDSIGHT_TEST_DIR") ?? Path.Combine(Path.GetTempPath(), "hs-it");
        Directory.CreateDirectory(root);
        AppPaths.OverrideDataRoot(root);

        var ffmpeg = FfmpegLocator.Find();
        Assert.NotNull(ffmpeg);
        var caps = await FfmpegCaps.ProbeAsync(ffmpeg!);
        Assert.NotNull(caps);
        output.WriteLine($"ffmpeg: {caps!.Version}; encoders: {string.Join(", ", caps.WorkingEncoders)}");

        var monitor = WindowInfo.GetPrimaryMonitor()!;
        var height = int.Parse(Environment.GetEnvironmentVariable("HINDSIGHT_TEST_HEIGHT") ?? "720");
        var fps = int.Parse(Environment.GetEnvironmentVariable("HINDSIGHT_TEST_FPS") ?? "60");
        var (w, h) = VideoMath.TargetSize(monitor.Width, monitor.Height, height);
        var encoders = EncoderCatalog.Plan(EncoderChoice.Auto, VideoCodec.H264, caps.Working);
        var request = new RecordingRequest(
            new CaptureSource(monitor.Handle, 0, monitor.Width, monitor.Height, "primary"),
            w, h, fps, VideoMath.VideoBitrateKbps(h, fps, QualityPreset.High, 0, VideoCodec.H264), encoders,
            new AudioOptions(true, 1, false, null, 1, 160, 0), 10, Path.Combine(root, "buffer"), RecorderPriority.Normal, true);

        await using var recorder = new ReplayRecorder(caps);
        var started = Stopwatch.StartNew();
        Assert.True(await recorder.StartAsync(request), recorder.LastError);
        output.WriteLine($"started in {started.ElapsedMilliseconds} ms with {recorder.Session!.Variant.Encoder.FfmpegName} [{recorder.Session.Variant.Label}] at {w}x{h}@{fps}");

        await Task.Delay(TimeSpan.FromSeconds(9));
        using (var p = Process.GetProcessById(recorder.Session.ProcessId!.Value))
        {
            output.WriteLine($"ffmpeg working set {p.WorkingSet64 / 1048576} MB, private {p.PrivateMemorySize64 / 1048576} MB, cpu {p.TotalProcessorTime.TotalSeconds:0.00}s over ~10s");
        }

        var clipPath = Path.Combine(root, "clip.mp4");
        var save = Stopwatch.StartNew();
        var clip = await recorder.SaveReplayAsync(8, clipPath);
        output.WriteLine($"clip saved in {save.ElapsedMilliseconds} ms (includes waiting for the current segment): {clip.DurationSeconds:0.00}s");

        var tools = new ClipTools(caps);
        var info = await tools.ProbeAsync(clipPath);
        Assert.NotNull(info);
        output.WriteLine($"probe: {info!.DurationSeconds:0.00}s {info.Width}x{info.Height} audio={info.HasAudio} size={new FileInfo(clipPath).Length / 1024} KB");
        Assert.InRange(info.DurationSeconds, 7.5, 11);
        Assert.Equal(w, info.Width);
        Assert.True(info.HasAudio);

        recorder.StartManualRecording();
        await Task.Delay(TimeSpan.FromSeconds(5));
        var manual = await recorder.StopManualRecordingAsync(Path.Combine(root, "manual.mp4"));
        Assert.NotNull(manual);
        var manualInfo = await tools.ProbeAsync(manual!.Path);
        output.WriteLine($"manual: {manualInfo!.DurationSeconds:0.00}s");
        Assert.InRange(manualInfo.DurationSeconds, 4, 10);

        Assert.True(await tools.ThumbnailAsync(clipPath, Path.Combine(root, "thumb.jpg"), 1));
        Assert.True(await tools.ScreenshotAsync(request.Source, Path.Combine(root, "shot.png")));

        await recorder.StopAsync();
        Assert.Equal(RecorderStatus.Idle, recorder.Status);
    }
}
