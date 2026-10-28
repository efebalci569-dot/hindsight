using System.Globalization;

namespace Hindsight.Core.Media;

/// <summary>
/// Builds the ffmpeg command line for the replay buffer.
/// Capture (Windows Graphics Capture) and scaling run on the GPU and the frames go straight into the
/// hardware encoder, so video never passes through system RAM. The encoded stream is cut into 2-second
/// MPEG-TS files that overwrite each other in a ring (<c>-segment_wrap</c>): RAM use stays flat no matter
/// how long the clip length is, and disk use is capped at one clip plus a few segments.
/// </summary>
public static class FfmpegArgs
{
    public const string SegmentList = "segments.csv";
    public const string SegmentPattern = "seg%03d.ts";

    /// <summary>Capture/encoder combinations to try, most efficient first.</summary>
    public static IReadOnlyList<PipelineVariant> Variants(RecordingRequest request, FfmpegCaps caps)
    {
        var variants = new List<PipelineVariant>();
        foreach (var encoder in request.Encoders)
        {
            if (caps.HasGfxCapture)
            {
                foreach (var (filter, label) in GpuFrameFilters(encoder))
                {
                    variants.Add(new PipelineVariant(encoder, true, filter, label));
                }
            }

            if (caps.HasDdaGrab && !request.Source.IsWindow)
            {
                variants.Add(DdaGrab(request, encoder));
            }
        }

        // Some games cannot be captured as a window (exclusive full screen, protected surfaces):
        // record the monitor the window is on rather than nothing at all.
        if (request.Source.IsWindow && request.Source.MonitorHandle != 0)
        {
            foreach (var encoder in request.Encoders.Take(2))
            {
                if (caps.HasGfxCapture)
                {
                    var (filter, label) = GpuFrameFilters(encoder).First();
                    variants.Add(new PipelineVariant(encoder, true, filter, label + "+monitor", MonitorFallback: true));
                }

                if (caps.HasDdaGrab)
                {
                    variants.Add(DdaGrab(request, encoder) with { MonitorFallback = true });
                }
            }
        }

        return variants;
    }

    /// <summary>Desktop Duplication cannot scale on the GPU in current builds, so scaling happens on the CPU.</summary>
    private static PipelineVariant DdaGrab(RecordingRequest request, EncoderInfo encoder)
    {
        var filter = $"hwdownload,format=bgra,scale={request.Width}:{request.Height}:flags=fast_bilinear,format={SoftwarePixelFormat(encoder)}";
        return new PipelineVariant(encoder, false, filter, "ddagrab+cpu-scale");
    }

    private static IEnumerable<(string? Filter, string Label)> GpuFrameFilters(EncoderInfo encoder)
    {
        switch (encoder.Vendor)
        {
            case EncoderVendor.Nvenc:
            case EncoderVendor.Amf:
                yield return (null, "gpu-zero-copy");
                yield return ("hwdownload,format=bgra", "gpu-capture+download");
                break;
            case EncoderVendor.Qsv:
                yield return ("hwmap=derive_device=qsv,format=qsv", "gpu-zero-copy");
                yield return ("hwdownload,format=bgra,format=nv12", "gpu-capture+download");
                break;
            default:
                yield return ($"hwdownload,format=bgra,format={SoftwarePixelFormat(encoder)}", "gpu-capture+cpu-encode");
                break;
        }
    }

    private static string SoftwarePixelFormat(EncoderInfo encoder) =>
        encoder.Vendor is EncoderVendor.X264 or EncoderVendor.OpenH264 ? "yuv420p" : "nv12";

    public static string CaptureFilter(RecordingRequest request, bool gfxCapture, bool monitorFallback = false)
    {
        var inv = CultureInfo.InvariantCulture;
        if (!gfxCapture)
        {
            return string.Format(inv, "ddagrab=output_idx=0:framerate={0}:draw_mouse={1}", request.Fps, request.CaptureCursor ? 1 : 0);
        }

        var target = request.Source.IsWindow && !monitorFallback
            ? string.Format(inv, "hwnd={0}:capture_border=0", (ulong)request.Source.WindowHandle)
            : request.Source.MonitorHandle != 0
                ? string.Format(inv, "hmonitor={0}", (ulong)request.Source.MonitorHandle)
                : "monitor_idx=0";

        return string.Format(inv,
            "gfxcapture={0}:max_framerate={1}:width={2}:height={3}:resize_mode=scale_aspect:scale_mode=bilinear:capture_cursor={4}:display_border=0",
            target, request.Fps, request.Width, request.Height, request.CaptureCursor ? 1 : 0);
    }

    public static IReadOnlyList<string> EncoderArguments(EncoderInfo encoder, int kbps, int fps)
    {
        var inv = CultureInfo.InvariantCulture;
        var b = kbps.ToString(inv) + "k";
        var max = (kbps * 3 / 2).ToString(inv) + "k";
        var buf = (kbps * 2).ToString(inv) + "k";
        var gop = (fps * VideoMath.SegmentSeconds).ToString(inv);
        var args = new List<string> { "-c:v", encoder.FfmpegName };
        switch (encoder.Vendor)
        {
            case EncoderVendor.Nvenc:
                args.AddRange(["-preset", "p4", "-tune", "hq", "-rc", "vbr", "-b:v", b, "-maxrate", max, "-bufsize", buf, "-forced-idr", "1"]);
                break;
            case EncoderVendor.Amf:
                args.AddRange(["-usage", "transcoding", "-quality", "balanced", "-rc", "vbr_peak", "-b:v", b, "-maxrate", max, "-bufsize", buf]);
                break;
            case EncoderVendor.Qsv:
                args.AddRange(["-preset", "veryfast", "-b:v", b, "-maxrate", max, "-bufsize", buf, "-look_ahead", "0"]);
                break;
            case EncoderVendor.MediaFoundation:
                args.AddRange(["-b:v", b, "-rate_control", "u_vbr", "-scenario", "display_remoting"]);
                break;
            case EncoderVendor.X264:
                args.AddRange(["-preset", "superfast", "-tune", "zerolatency", "-b:v", b, "-maxrate", max, "-bufsize", buf,
                    "-keyint_min", gop, "-sc_threshold", "0"]);
                break;
            case EncoderVendor.OpenH264:
                args.AddRange(["-b:v", b]);
                break;
        }

        // A key frame at every segment boundary makes each 2 s file independently decodable.
        args.AddRange(["-g", gop, "-force_key_frames", $"expr:gte(t,n_forced*{VideoMath.SegmentSeconds})"]);
        return args;
    }

    /// <param name="captureOverride">Tests only: a lavfi source (e.g. testsrc2) in place of the screen.</param>
    public static IReadOnlyList<string> Replay(RecordingRequest request, PipelineVariant variant, string sessionDirectory, string? audioPipe, string? captureOverride = null)
    {
        var inv = CultureInfo.InvariantCulture;
        // No -nostdin here: the recorder stops ffmpeg cleanly by writing 'q' to its stdin.
        var args = new List<string> { "-hide_banner", "-loglevel", "warning", "-y" };

        args.AddRange(["-f", "lavfi", "-i", captureOverride ?? CaptureFilter(request, variant.UseGfxCapture, variant.MonitorFallback)]);
        if (audioPipe is not null)
        {
            if (request.Audio.OffsetMs != 0)
            {
                args.AddRange(["-itsoffset", (request.Audio.OffsetMs / 1000.0).ToString("0.000", inv)]);
            }

            // Keep input options to the long-standing ones: newer FFmpeg builds reject -thread_queue_size on inputs.
            args.AddRange(["-f", "f32le", "-ar", "48000", "-ac", "2", "-i", audioPipe]);
        }

        args.AddRange(["-map", "0:v:0"]);
        if (variant.VideoFilter is not null)
        {
            args.AddRange(["-vf", variant.VideoFilter]);
        }

        args.AddRange(EncoderArguments(variant.Encoder, request.VideoKbps, request.Fps));

        if (audioPipe is not null)
        {
            args.AddRange(["-map", "1:a:0", "-c:a", "aac", "-b:a", request.Audio.BitrateKbps.ToString(inv) + "k", "-ar", "48000"]);
        }

        args.AddRange(
        [
            "-max_muxing_queue_size", "1024",
            "-f", "segment",
            "-segment_time", VideoMath.SegmentSeconds.ToString(inv),
            "-segment_wrap", VideoMath.SegmentWrap(request.ClipSeconds).ToString(inv),
            "-segment_format", "mpegts",
            "-segment_format_options", "mpegts_flags=+resend_headers",
            "-segment_list", Path.Combine(sessionDirectory, SegmentList),
            "-segment_list_type", "csv",
            "-reset_timestamps", "0",
            "-muxdelay", "0",
            "-muxpreload", "0",
            Path.Combine(sessionDirectory, SegmentPattern),
        ]);
        return args;
    }

    /// <summary>Joins segment files into one MP4 without re-encoding (takes a fraction of a second).</summary>
    public static IReadOnlyList<string> Concat(string listFile, string output) =>
    [
        "-hide_banner", "-nostdin", "-loglevel", "error", "-y",
        "-f", "concat", "-safe", "0", "-i", listFile,
        "-map", "0", "-c", "copy", "-avoid_negative_ts", "make_zero", "-movflags", "+faststart",
        output,
    ];
}
