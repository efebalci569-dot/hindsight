using System.Diagnostics;
using Hindsight.Core.Settings;
using Microsoft.Win32;

namespace Hindsight.Core.Media;

public enum EncoderVendor { Nvenc, Amf, Qsv, MediaFoundation, X264, OpenH264 }

public sealed record EncoderInfo(EncoderVendor Vendor, VideoCodec Codec, string FfmpegName)
{
    public bool IsHardware => Vendor is EncoderVendor.Nvenc or EncoderVendor.Amf or EncoderVendor.Qsv;

    public string DisplayName => $"{VendorName(Vendor)} · {CodecName(Codec)}";

    public static string VendorName(EncoderVendor vendor) => vendor switch
    {
        EncoderVendor.Nvenc => "NVIDIA NVENC",
        EncoderVendor.Amf => "AMD AMF",
        EncoderVendor.Qsv => "Intel Quick Sync",
        EncoderVendor.MediaFoundation => "Windows Media Foundation",
        EncoderVendor.X264 => "CPU (x264)",
        _ => "CPU (OpenH264)",
    };

    public static string CodecName(VideoCodec codec) => codec switch
    {
        VideoCodec.Hevc => "HEVC",
        VideoCodec.Av1 => "AV1",
        _ => "H.264",
    };
}

public static class EncoderCatalog
{
    public static readonly IReadOnlyList<EncoderInfo> All =
    [
        new(EncoderVendor.Nvenc, VideoCodec.H264, "h264_nvenc"),
        new(EncoderVendor.Nvenc, VideoCodec.Hevc, "hevc_nvenc"),
        new(EncoderVendor.Nvenc, VideoCodec.Av1, "av1_nvenc"),
        new(EncoderVendor.Amf, VideoCodec.H264, "h264_amf"),
        new(EncoderVendor.Amf, VideoCodec.Hevc, "hevc_amf"),
        new(EncoderVendor.Amf, VideoCodec.Av1, "av1_amf"),
        new(EncoderVendor.Qsv, VideoCodec.H264, "h264_qsv"),
        new(EncoderVendor.Qsv, VideoCodec.Hevc, "hevc_qsv"),
        new(EncoderVendor.Qsv, VideoCodec.Av1, "av1_qsv"),
        new(EncoderVendor.MediaFoundation, VideoCodec.H264, "h264_mf"),
        new(EncoderVendor.MediaFoundation, VideoCodec.Hevc, "hevc_mf"),
        new(EncoderVendor.X264, VideoCodec.H264, "libx264"),
        new(EncoderVendor.OpenH264, VideoCodec.H264, "libopenh264"),
    ];

    private static readonly EncoderVendor[] HardwareOrder = [EncoderVendor.Nvenc, EncoderVendor.Amf, EncoderVendor.Qsv];

    /// <summary>
    /// Encoders to try, best first. The user's choice leads; then the other GPUs, Media Foundation and the CPU,
    /// and finally H.264 if the chosen codec (HEVC/AV1) is not available anywhere.
    /// </summary>
    public static IReadOnlyList<EncoderInfo> Plan(EncoderChoice choice, VideoCodec codec, IReadOnlySet<string> working)
    {
        var order = new List<EncoderInfo>();

        void Add(EncoderVendor vendor, VideoCodec c)
        {
            var info = All.FirstOrDefault(e => e.Vendor == vendor && e.Codec == c);
            if (info is not null && working.Contains(info.FfmpegName) && !order.Contains(info))
            {
                order.Add(info);
            }
        }

        void AddChain(VideoCodec c)
        {
            switch (choice)
            {
                case EncoderChoice.Nvenc: Add(EncoderVendor.Nvenc, c); break;
                case EncoderChoice.Amf: Add(EncoderVendor.Amf, c); break;
                case EncoderChoice.Qsv: Add(EncoderVendor.Qsv, c); break;
                case EncoderChoice.MediaFoundation: Add(EncoderVendor.MediaFoundation, c); break;
                case EncoderChoice.Cpu:
                    Add(EncoderVendor.X264, c);
                    Add(EncoderVendor.OpenH264, c);
                    break;
            }

            foreach (var vendor in HardwareOrder)
            {
                Add(vendor, c);
            }

            Add(EncoderVendor.MediaFoundation, c);
            Add(EncoderVendor.X264, c);
            Add(EncoderVendor.OpenH264, c);
        }

        AddChain(codec);
        if (codec != VideoCodec.H264)
        {
            AddChain(VideoCodec.H264);
        }

        return order;
    }
}

/// <summary>What the local ffmpeg build can do on this PC. Probed once and cached per ffmpeg build + GPU.</summary>
public sealed class FfmpegCaps
{
    public const int ProbeVersion = 3;

    public string FfmpegPath { get; set; } = string.Empty;

    public string Version { get; set; } = string.Empty;

    public string CacheKey { get; set; } = string.Empty;

    public int Probe { get; set; }

    public bool HasGfxCapture { get; set; }

    public bool HasDdaGrab { get; set; }

    /// <summary>Encoders compiled into the build that also produced frames on this hardware.</summary>
    public List<string> WorkingEncoders { get; set; } = [];

    public IReadOnlySet<string> Working => WorkingEncoders.ToHashSet(StringComparer.OrdinalIgnoreCase);

    public bool HasHardwareEncoder => EncoderCatalog.All.Any(e => e.IsHardware && Working.Contains(e.FfmpegName));

    public static async Task<FfmpegCaps?> LoadOrProbeAsync(string ffmpegPath, CancellationToken cancellationToken = default)
    {
        var key = BuildCacheKey(ffmpegPath);
        var cached = JsonFile.Load<FfmpegCaps>(AppPaths.EncoderCacheFile);
        if (cached is not null && cached.CacheKey == key && cached.Probe == ProbeVersion && File.Exists(cached.FfmpegPath))
        {
            return cached;
        }

        var caps = await ProbeAsync(ffmpegPath, cancellationToken).ConfigureAwait(false);
        if (caps is not null)
        {
            caps.CacheKey = key;
            JsonFile.Save(AppPaths.EncoderCacheFile, caps);
        }

        return caps;
    }

    public static async Task<FfmpegCaps?> ProbeAsync(string ffmpegPath, CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        var version = await FfmpegRunner.RunAsync(ffmpegPath, ["-hide_banner", "-version"], cancellationToken, timeout: TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        if (!version.Success)
        {
            Log.Error($"ffmpeg did not start: {version.ErrorTail}");
            return null;
        }

        var encoders = await FfmpegRunner.RunAsync(ffmpegPath, ["-hide_banner", "-encoders"], cancellationToken, timeout: TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        var filters = await FfmpegRunner.RunAsync(ffmpegPath, ["-hide_banner", "-filters"], cancellationToken, timeout: TimeSpan.FromSeconds(15)).ConfigureAwait(false);

        var compiled = EncoderCatalog.All
            .Where(e => encoders.Output.Contains($" {e.FfmpegName} ", StringComparison.Ordinal))
            .ToList();

        var caps = new FfmpegCaps
        {
            FfmpegPath = ffmpegPath,
            Version = version.Output.Split('\n').FirstOrDefault()?.Trim() ?? string.Empty,
            Probe = ProbeVersion,
            HasGfxCapture = filters.Output.Contains(" gfxcapture ", StringComparison.Ordinal),
            HasDdaGrab = filters.Output.Contains(" ddagrab ", StringComparison.Ordinal),
        };

        // A compiled-in hardware encoder only works with matching GPU + driver: encode a few frames to find out.
        foreach (var encoder in compiled)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string[] args =
            [
                "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "color=c=black:s=1280x720:r=30",
                "-frames:v", "5", "-c:v", encoder.FfmpegName, "-f", "null", "-",
            ];
            var result = await FfmpegRunner.RunAsync(ffmpegPath, args, cancellationToken, ProcessPriorityClass.Normal, TimeSpan.FromSeconds(20)).ConfigureAwait(false);
            if (result.Success)
            {
                caps.WorkingEncoders.Add(encoder.FfmpegName);
            }
        }

        Log.Info($"ffmpeg probe ({sw.ElapsedMilliseconds} ms): {caps.Version}; gfxcapture={caps.HasGfxCapture}, ddagrab={caps.HasDdaGrab}; encoders: {string.Join(", ", caps.WorkingEncoders)}");
        return caps;
    }

    private static string BuildCacheKey(string ffmpegPath)
    {
        var file = new FileInfo(ffmpegPath);
        var dir = file.Directory;
        var codecDll = dir?.GetFiles("avcodec*.dll").FirstOrDefault();
        return string.Join('|', file.FullName, file.Length, file.LastWriteTimeUtc.Ticks,
            codecDll?.Length ?? 0, codecDll?.LastWriteTimeUtc.Ticks ?? 0, string.Join(',', GpuNames()));
    }

    /// <summary>Display adapter names from the registry (cheap; no WMI).</summary>
    public static IReadOnlyList<string> GpuNames()
    {
        var names = new List<string>();
        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            foreach (var sub in root?.GetSubKeyNames() ?? [])
            {
                if (!sub.All(char.IsAsciiDigit))
                {
                    continue;
                }

                using var key = root!.OpenSubKey(sub);
                if (key?.GetValue("DriverDesc") is string desc && !string.IsNullOrWhiteSpace(desc))
                {
                    names.Add($"{desc}@{key.GetValue("DriverVersion")}");
                }
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
        }

        return names;
    }
}
