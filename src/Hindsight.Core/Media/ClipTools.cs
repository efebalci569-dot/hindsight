using System.Globalization;
using System.Text.Json;
using Hindsight.Core.Settings;

namespace Hindsight.Core.Media;

public sealed record MediaInfo(double DurationSeconds, int Width, int Height, bool HasAudio);

/// <summary>One-shot ffmpeg jobs on saved clips. All run below normal priority so a game in progress is not disturbed.</summary>
public sealed class ClipTools(FfmpegCaps caps)
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public FfmpegCaps Caps => caps;

    public async Task<MediaInfo?> ProbeAsync(string path, CancellationToken cancellationToken = default)
    {
        var probe = FfmpegLocator.FindProbe(caps.FfmpegPath);
        if (probe is null)
        {
            return null;
        }

        var result = await FfmpegRunner.RunAsync(probe,
            ["-v", "error", "-show_entries", "format=duration:stream=codec_type,width,height", "-of", "json", path],
            cancellationToken, timeout: TimeSpan.FromSeconds(20)).ConfigureAwait(false);
        if (!result.Success)
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(result.Output);
            double duration = 0;
            if (doc.RootElement.TryGetProperty("format", out var format) &&
                format.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.String)
            {
                double.TryParse(d.GetString(), NumberStyles.Float, Inv, out duration);
            }

            int width = 0, height = 0;
            var hasAudio = false;
            if (doc.RootElement.TryGetProperty("streams", out var streams))
            {
                foreach (var stream in streams.EnumerateArray())
                {
                    var type = stream.TryGetProperty("codec_type", out var t) ? t.GetString() : null;
                    if (type == "video" && width == 0)
                    {
                        width = stream.TryGetProperty("width", out var w) ? w.GetInt32() : 0;
                        height = stream.TryGetProperty("height", out var h) ? h.GetInt32() : 0;
                    }
                    else if (type == "audio")
                    {
                        hasAudio = true;
                    }
                }
            }

            return new MediaInfo(duration, width, height, hasAudio);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async Task<bool> ThumbnailAsync(string video, string jpegPath, double atSeconds, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(jpegPath)!);
        var result = await FfmpegRunner.RunAsync(caps.FfmpegPath,
        [
            "-hide_banner", "-nostdin", "-loglevel", "error", "-y",
            "-ss", atSeconds.ToString("0.00", Inv), "-i", video,
            "-frames:v", "1", "-vf", "scale=384:-2:flags=bilinear", "-q:v", "5", jpegPath,
        ], cancellationToken, timeout: TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        return result.Success && File.Exists(jpegPath);
    }

    /// <summary>Cuts [start, end]. Re-encodes for a frame-exact cut (fast with a hardware encoder).</summary>
    public async Task<FfmpegResult> TrimAsync(string input, string output, double start, double end, EncoderInfo? encoder, int kbps, CancellationToken cancellationToken = default)
    {
        var duration = Math.Max(0.5, end - start);
        var args = new List<string>
        {
            "-hide_banner", "-nostdin", "-loglevel", "error", "-y",
            "-ss", start.ToString("0.000", Inv), "-i", input, "-t", duration.ToString("0.000", Inv),
        };
        args.AddRange(VideoEncodeArgs(encoder, kbps));
        args.AddRange(["-c:a", "aac", "-b:a", "160k", "-movflags", "+faststart", output]);
        return await FfmpegRunner.RunAsync(caps.FfmpegPath, args, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Re-encodes a clip so it fits a size limit (e.g. Discord's 10 MB), at most 720p.</summary>
    public async Task<FfmpegResult> CompressAsync(string input, string output, double targetMegabytes, double durationSeconds, EncoderInfo? encoder, CancellationToken cancellationToken = default)
    {
        const int AudioKbps = 96;
        var kbps = VideoMath.BitrateForTargetSize(targetMegabytes, durationSeconds, AudioKbps);
        var height = kbps < 1200 ? 480 : 720;
        var args = new List<string>
        {
            "-hide_banner", "-nostdin", "-loglevel", "error", "-y", "-i", input,
            "-vf", $"scale=-2:'min({height},ih)':flags=bicubic",
        };
        args.AddRange(VideoEncodeArgs(encoder, kbps, strict: true));
        args.AddRange(["-c:a", "aac", "-b:a", $"{AudioKbps}k", "-ac", "2", "-movflags", "+faststart", output]);
        return await FfmpegRunner.RunAsync(caps.FfmpegPath, args, cancellationToken).ConfigureAwait(false);
    }

    public async Task<FfmpegResult> GifAsync(string input, string output, double start, double seconds, CancellationToken cancellationToken = default)
    {
        string[] args =
        [
            "-hide_banner", "-nostdin", "-loglevel", "error", "-y",
            "-ss", start.ToString("0.000", Inv), "-t", Math.Clamp(seconds, 1, 15).ToString("0.000", Inv), "-i", input,
            "-vf", "fps=15,scale=480:-1:flags=lanczos,split[a][b];[a]palettegen=stats_mode=diff[p];[b][p]paletteuse=dither=bayer:bayer_scale=4",
            "-loop", "0", output,
        ];
        return await FfmpegRunner.RunAsync(caps.FfmpegPath, args, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A full-resolution PNG of a monitor or window (Windows Graphics Capture; Desktop Duplication fallback).</summary>
    public async Task<bool> ScreenshotAsync(CaptureSource source, string pngPath, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(pngPath)!);
        var attempts = new List<string[]>();
        if (caps.HasGfxCapture)
        {
            var target = source.IsWindow
                ? $"hwnd={(ulong)source.WindowHandle}:capture_border=0"
                : source.MonitorHandle != 0 ? $"hmonitor={(ulong)source.MonitorHandle}" : "monitor_idx=0";
            attempts.Add(
            [
                "-hide_banner", "-nostdin", "-loglevel", "error", "-y",
                "-f", "lavfi", "-i", $"gfxcapture={target}:max_framerate=30:capture_cursor=0:display_border=0",
                "-vf", "hwdownload,format=bgra,select=gte(n\\,2)", "-frames:v", "1", "-update", "1", pngPath,
            ]);
        }

        if (caps.HasDdaGrab)
        {
            attempts.Add(
            [
                "-hide_banner", "-nostdin", "-loglevel", "error", "-y",
                "-f", "lavfi", "-i", "ddagrab=output_idx=0:framerate=30:draw_mouse=0",
                "-vf", "hwdownload,format=bgra,select=gte(n\\,2)", "-frames:v", "1", "-update", "1", pngPath,
            ]);
        }

        foreach (var args in attempts)
        {
            var result = await FfmpegRunner.RunAsync(caps.FfmpegPath, args, cancellationToken,
                System.Diagnostics.ProcessPriorityClass.Normal, TimeSpan.FromSeconds(15)).ConfigureAwait(false);
            if (result.Success && File.Exists(pngPath))
            {
                return true;
            }

            Log.Warn($"Screenshot attempt failed: {result.ErrorTail}");
        }

        return false;
    }

    private IEnumerable<string> VideoEncodeArgs(EncoderInfo? encoder, int kbps, bool strict = false)
    {
        var b = kbps.ToString(Inv) + "k";
        var max = (strict ? kbps : kbps * 3 / 2).ToString(Inv) + "k";
        var buf = (kbps * 2).ToString(Inv) + "k";

        // Prefer an H.264 encoder that works here; H.264 plays everywhere (Discord, phones, browsers).
        var h264 = encoder?.Codec == VideoCodec.H264 && caps.Working.Contains(encoder.FfmpegName)
            ? encoder
            : EncoderCatalog.Plan(EncoderChoice.Auto, VideoCodec.H264, caps.Working).FirstOrDefault();

        return h264?.Vendor switch
        {
            EncoderVendor.Nvenc => ["-c:v", h264.FfmpegName, "-preset", "p5", "-rc", "vbr", "-b:v", b, "-maxrate", max, "-bufsize", buf, "-pix_fmt", "yuv420p"],
            EncoderVendor.Amf => ["-c:v", h264.FfmpegName, "-quality", "quality", "-rc", "vbr_peak", "-b:v", b, "-maxrate", max, "-pix_fmt", "yuv420p"],
            EncoderVendor.Qsv => ["-c:v", h264.FfmpegName, "-preset", "medium", "-b:v", b, "-maxrate", max, "-pix_fmt", "nv12"],
            EncoderVendor.MediaFoundation => ["-c:v", h264.FfmpegName, "-b:v", b, "-pix_fmt", "nv12"],
            EncoderVendor.X264 => ["-c:v", "libx264", "-preset", "veryfast", "-b:v", b, "-maxrate", max, "-bufsize", buf, "-pix_fmt", "yuv420p"],
            EncoderVendor.OpenH264 => ["-c:v", "libopenh264", "-b:v", b, "-pix_fmt", "yuv420p"],
            _ => ["-c:v", "mpeg4", "-b:v", b],
        };
    }
}
