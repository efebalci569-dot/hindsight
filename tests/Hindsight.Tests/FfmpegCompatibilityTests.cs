using Hindsight.Core;
using Hindsight.Core.Media;
using Hindsight.Core.Settings;
using Xunit.Abstractions;

namespace Hindsight.Tests;

/// <summary>
/// Runs every command line Hindsight gives ffmpeg against the ffmpeg build that ships (HINDSIGHT_FFMPEG, the bundled
/// copy, or one on PATH), with a test pattern in place of the screen. FFmpeg changes its option rules between builds
/// (0.1.0 broke on one), so the release workflow runs this before publishing. Skipped when no ffmpeg is found.
/// </summary>
public class FfmpegCompatibilityTests(ITestOutputHelper output)
{
    [Fact]
    public async Task ShippedFfmpegAcceptsEveryCommandHindsightRuns()
    {
        var ffmpeg = FfmpegLocator.Find();
        if (ffmpeg is null)
        {
            output.WriteLine("No ffmpeg found; skipped.");
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), "hindsight-compat-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        AppPaths.OverrideDataRoot(root);
        try
        {
            var caps = await FfmpegCaps.ProbeAsync(ffmpeg);
            Assert.NotNull(caps);
            output.WriteLine($"{caps!.Version} — working encoders: {string.Join(", ", caps.WorkingEncoders)}");
            Assert.True(caps.HasGfxCapture, "gfxcapture filter missing");
            Assert.True(caps.HasDdaGrab, "ddagrab filter missing");

            var encoder = EncoderCatalog.Plan(EncoderChoice.Auto, VideoCodec.H264, caps.Working).First();
            var session = Directory.CreateDirectory(Path.Combine(root, "session")).FullName;
            var audio = Path.Combine(root, "silence.f32");
            await File.WriteAllBytesAsync(audio, new byte[48000 * 2 * 4 * 7]); // 7 s of 48 kHz stereo float

            var request = new RecordingRequest(new CaptureSource(0, 0, 1280, 720, "test"), 1280, 720, 30, 4000, [encoder],
                new AudioOptions(true, 1, false, null, 1, 128, 0), 10, root, RecorderPriority.Normal, false);
            var variant = new PipelineVariant(encoder, true, null, "compat-test");

            // The real capture filters must at least parse (they cannot capture on a headless build agent).
            foreach (var filter in new[] { FfmpegArgs.CaptureFilter(request, true), FfmpegArgs.CaptureFilter(request, false) })
            {
                var name = filter[..filter.IndexOf('=')];
                var help = await FfmpegRunner.RunAsync(ffmpeg, ["-hide_banner", "-h", "filter=" + name]);
                foreach (var option in filter[(filter.IndexOf('=') + 1)..].Split(':').Select(o => o.Split('=')[0]))
                {
                    Assert.True(help.Output.Contains($" {option} ", StringComparison.Ordinal), $"{name} has no option '{option}'");
                }
            }

            // Replay buffer: same arguments, a test pattern instead of the screen, a file instead of the audio pipe.
            var args = FfmpegArgs.Replay(request, variant, session, audio, captureOverride: "testsrc2=size=1280x720:rate=30").ToList();
            args.Insert(args.Count - 1, "-t");
            args.Insert(args.Count - 1, "6");
            var replay = await FfmpegRunner.RunAsync(ffmpeg, args, timeout: TimeSpan.FromMinutes(2));
            Assert.True(replay.Success, "replay buffer command failed: " + replay.ErrorTail);
            var segments = SegmentList.Read(Path.Combine(session, FfmpegArgs.SegmentList));
            Assert.True(segments.Count >= 2, $"expected segments, got {segments.Count}");

            // Saving a clip (concat, no re-encode).
            var list = Path.Combine(root, "concat.txt");
            await File.WriteAllLinesAsync(list, segments.Select(s => FfmpegRunner.ConcatLine(Path.Combine(session, s.FileName))));
            var clip = Path.Combine(root, "clip.mp4");
            var concat = await FfmpegRunner.RunAsync(ffmpeg, FfmpegArgs.Concat(list, clip));
            Assert.True(concat.Success, "concat failed: " + concat.ErrorTail);

            var tools = new ClipTools(caps);
            var info = await tools.ProbeAsync(clip);
            Assert.NotNull(info);
            output.WriteLine($"clip: {info!.DurationSeconds:0.00}s {info.Width}x{info.Height} audio={info.HasAudio}");
            Assert.InRange(info.DurationSeconds, 3.5, 7);
            Assert.True(info.HasAudio);

            Assert.True(await tools.ThumbnailAsync(clip, Path.Combine(root, "thumb.jpg"), 1), "thumbnail failed");
            var trim = await tools.TrimAsync(clip, Path.Combine(root, "trim.mp4"), 1, 3, encoder, 4000);
            Assert.True(trim.Success, "trim failed: " + trim.ErrorTail);
            var compress = await tools.CompressAsync(clip, Path.Combine(root, "small.mp4"), 10, info.DurationSeconds, encoder);
            Assert.True(compress.Success, "compress failed: " + compress.ErrorTail);
            var gif = await tools.GifAsync(clip, Path.Combine(root, "clip.gif"), 0, 2);
            Assert.True(gif.Success, "gif failed: " + gif.ErrorTail);
        }
        finally
        {
            try
            {
                Directory.Delete(root, true);
            }
            catch (IOException)
            {
            }
        }
    }
}
