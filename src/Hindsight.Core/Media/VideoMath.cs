using Hindsight.Core.Settings;

namespace Hindsight.Core.Media;

/// <summary>Pure size / bitrate / disk math used by the recorder and by the settings page estimates.</summary>
public static class VideoMath
{
    /// <summary>Length of one replay-buffer segment. A clip is assembled from whole segments.</summary>
    public const int SegmentSeconds = 2;

    /// <summary>
    /// Output size for a capture source: never taller than <paramref name="maxHeight"/> (Hindsight caps at 1080p),
    /// never upscaled, aspect ratio kept, even numbers (required by H.264/HEVC 4:2:0).
    /// </summary>
    public static (int Width, int Height) TargetSize(int sourceWidth, int sourceHeight, int maxHeight)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0)
        {
            sourceWidth = 1920;
            sourceHeight = 1080;
        }

        maxHeight = Math.Clamp(maxHeight, 240, 1080);
        var height = Math.Min(maxHeight, sourceHeight);
        var width = (int)Math.Round(sourceWidth * (double)height / sourceHeight);
        const int MaxWidth = 2560; // 21:9 and 32:9 screens stay within what every hardware encoder accepts
        if (width > MaxWidth)
        {
            width = MaxWidth;
            height = (int)Math.Round(sourceHeight * (double)MaxWidth / sourceWidth);
        }

        return (Even(width), Even(height));
    }

    public static int Even(int value) => Math.Max(2, value - (value & 1));

    /// <summary>Video bitrate in kbit/s. 1080p60 "High" H.264 = 20 Mbit/s; smaller sizes and 30 fps scale down.</summary>
    public static int VideoBitrateKbps(int height, int fps, QualityPreset quality, int customMbps, VideoCodec codec)
    {
        if (quality == QualityPreset.Custom)
        {
            return Math.Clamp(customMbps, 2, 50) * 1000;
        }

        double baseKbps = quality switch
        {
            QualityPreset.Low => 6000,
            QualityPreset.Medium => 12000,
            _ => 20000,
        };

        var pixelRatio = Math.Pow(height / 1080.0, 2);
        var fpsFactor = fps >= 50 ? 1.0 : 0.65;
        var codecFactor = codec switch
        {
            VideoCodec.Hevc => 0.7,
            VideoCodec.Av1 => 0.6,
            _ => 1.0,
        };

        var kbps = baseKbps * Math.Pow(pixelRatio, 0.85) * fpsFactor * codecFactor;
        return Math.Max(1500, (int)(Math.Round(kbps / 500) * 500));
    }

    /// <summary>Megabytes (10^6) for a stream of <paramref name="kbps"/> lasting <paramref name="seconds"/>.</summary>
    public static double Megabytes(int kbps, double seconds) => kbps / 8.0 * seconds / 1000.0;

    /// <summary>Number of segment files kept on disk: one clip's worth plus a safety margin.</summary>
    public static int SegmentWrap(int clipSeconds) => (int)Math.Ceiling(clipSeconds / (double)SegmentSeconds) + 8;

    /// <summary>Disk space the rolling buffer uses at most.</summary>
    public static double BufferDiskMegabytes(int videoKbps, int audioKbps, int clipSeconds) =>
        Megabytes(videoKbps + audioKbps, SegmentWrap(clipSeconds) * SegmentSeconds);

    /// <summary>Bitrate that makes a clip of <paramref name="seconds"/> fit in <paramref name="targetMegabytes"/> (MiB, Discord-style).</summary>
    public static int BitrateForTargetSize(double targetMegabytes, double seconds, int audioKbps)
    {
        seconds = Math.Max(1, seconds);
        var totalKbps = targetMegabytes * 1024 * 1024 * 8 / 1000.0 / seconds * 0.92; // 8 % container + rate-control headroom
        return Math.Max(150, (int)(totalKbps - audioKbps));
    }

    public static string FormatDuration(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
    }

    public static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0} MB",
        >= 1L << 10 => $"{bytes / 1024.0:0} KB",
        _ => $"{bytes} B",
    };
}
