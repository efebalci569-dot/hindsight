using Hindsight.Core.Media;
using Hindsight.Core.Settings;

namespace Hindsight.Tests;

public class VideoMathTests
{
    [Theory]
    [InlineData(1920, 1080, 1080, 1920, 1080)]
    [InlineData(1920, 1080, 720, 1280, 720)]
    [InlineData(2560, 1440, 1080, 1920, 1080)]
    [InlineData(1280, 720, 1080, 1280, 720)] // never upscales
    [InlineData(3440, 1440, 1080, 2560, 1072)] // ultrawide: width capped, aspect kept, even
    [InlineData(1366, 768, 720, 1280, 720)]
    public void TargetSizeKeepsAspectAndCapsAt1080p(int sw, int sh, int max, int ew, int eh)
    {
        Assert.Equal((ew, eh), VideoMath.TargetSize(sw, sh, max));
    }

    [Fact]
    public void TargetSizeIsAlwaysEven()
    {
        for (var w = 801; w < 2000; w += 37)
        {
            var (tw, th) = VideoMath.TargetSize(w, 601, 1080);
            Assert.Equal(0, tw % 2);
            Assert.Equal(0, th % 2);
        }
    }

    [Fact]
    public void BitrateScalesWithResolutionFpsAndCodec()
    {
        var hi1080p60 = VideoMath.VideoBitrateKbps(1080, 60, QualityPreset.High, 0, VideoCodec.H264);
        var hi720p30 = VideoMath.VideoBitrateKbps(720, 30, QualityPreset.High, 0, VideoCodec.H264);
        var hevc = VideoMath.VideoBitrateKbps(1080, 60, QualityPreset.High, 0, VideoCodec.Hevc);
        Assert.Equal(20000, hi1080p60);
        Assert.True(hi720p30 < hi1080p60 / 2);
        Assert.True(hevc < hi1080p60);
        Assert.Equal(15000, VideoMath.VideoBitrateKbps(480, 30, QualityPreset.Custom, 15, VideoCodec.H264));
        Assert.True(VideoMath.VideoBitrateKbps(480, 30, QualityPreset.Low, 0, VideoCodec.Av1) >= 1500);
    }

    [Fact]
    public void DiscordTargetLeavesHeadroom()
    {
        var kbps = VideoMath.BitrateForTargetSize(10, 30, 96);
        var megabytes = (kbps + 96) * 30 / 8.0 / 1024; // MiB
        Assert.InRange(megabytes, 8.5, 10);
    }

    [Fact]
    public void BufferDiskEstimateCoversWrap()
    {
        Assert.Equal(23, VideoMath.SegmentWrap(30));
        Assert.InRange(VideoMath.BufferDiskMegabytes(20000, 160, 30), 110, 120);
    }
}

public class SegmentListTests
{
    [Fact]
    public void ParsesFfmpegCsv()
    {
        var segments = SegmentList.Parse(["seg000.ts,0.000000,2.050000", "seg001.ts,2.050000,4.050000", "garbage"]);
        Assert.Equal(2, segments.Count);
        Assert.Equal("seg001.ts", segments[1].FileName);
        Assert.Equal(2.0, segments[1].Duration, 3);
    }

    [Fact]
    public void SelectsNewestSegmentsCoveringTheClip()
    {
        var lines = Enumerable.Range(0, 30).Select(i => $"seg{i % 23:D3}.ts,{i * 2}.000000,{(i + 1) * 2}.000000").ToList();
        var picked = SegmentList.SelectLast(SegmentList.Parse(lines), 10, 23);
        Assert.Equal(5, picked.Count);
        Assert.Equal(50, picked[0].Start);
        Assert.Equal(60, picked[^1].End);
    }

    [Fact]
    public void NeverReachesIntoOverwrittenSegments()
    {
        var lines = Enumerable.Range(0, 40).Select(i => $"seg{i % 10:D3}.ts,{i * 2},{(i + 1) * 2}").ToList();
        var picked = SegmentList.SelectLast(SegmentList.Parse(lines), 300, 10);
        Assert.Equal(8, picked.Count); // wrap − 2
    }

    [Fact]
    public void StopsAtAGapBetweenSessions()
    {
        var picked = SegmentList.SelectLast(SegmentList.Parse(["a.ts,0,2", "b.ts,2,4", "c.ts,100,102", "d.ts,102,104"]), 30, 20);
        Assert.Equal(["c.ts", "d.ts"], picked.Select(p => p.FileName));
    }
}

public class FfmpegArgsTests
{
    private static readonly EncoderInfo Nvenc = EncoderCatalog.All.First(e => e.FfmpegName == "h264_nvenc");

    private static RecordingRequest Request(nint hwnd = 0, bool audio = true) => new(
        new CaptureSource(12345, hwnd, 1920, 1080, "test"), 1280, 720, 60, 10000, [Nvenc],
        new AudioOptions(audio, 1, false, null, 1, 160, 0), 30, @"C:\buf", RecorderPriority.Normal, true);

    [Fact]
    public void MonitorCaptureScalesOnTheGpu()
    {
        var filter = FfmpegArgs.CaptureFilter(Request(), gfxCapture: true);
        Assert.Contains("gfxcapture=hmonitor=12345", filter);
        Assert.Contains("width=1280:height=720", filter);
        Assert.Contains("max_framerate=60", filter);
    }

    [Fact]
    public void WindowCaptureUsesTheHwnd()
    {
        Assert.Contains("hwnd=777", FfmpegArgs.CaptureFilter(Request(hwnd: 777), gfxCapture: true));
    }

    [Fact]
    public void ReplayWritesARingOfSegments()
    {
        var variant = new PipelineVariant(Nvenc, true, null, "gpu-zero-copy");
        var args = FfmpegArgs.Replay(Request(), variant, @"C:\buf\s1", @"\\.\pipe\x");
        var joined = string.Join(' ', args);
        Assert.Contains("-segment_wrap 23", joined);
        Assert.Contains("-segment_time 2", joined);
        Assert.Contains(@"-i \\.\pipe\x", joined);
        Assert.Contains("-c:a aac", joined);
        Assert.Contains("-g 120", joined);
        Assert.DoesNotContain("-vf", args); // zero-copy: no filter, frames stay on the GPU
    }

    [Fact]
    public void NoAudioMeansNoAudioInput()
    {
        var variant = new PipelineVariant(Nvenc, true, null, "gpu-zero-copy");
        var args = FfmpegArgs.Replay(Request(audio: false), variant, @"C:\buf\s1", null);
        Assert.DoesNotContain("-c:a", args);
        Assert.DoesNotContain("1:a:0", args);
    }

    [Fact]
    public void VariantsPreferZeroCopyThenFallBack()
    {
        var caps = new FfmpegCaps { HasGfxCapture = true, HasDdaGrab = true };
        var variants = FfmpegArgs.Variants(Request(), caps);
        Assert.Equal("gpu-zero-copy", variants[0].Label);
        Assert.Contains(variants, v => v.Label == "ddagrab+cpu-scale");
    }
}

public class EncoderPlanTests
{
    private static readonly HashSet<string> NvidiaBox = ["h264_nvenc", "hevc_nvenc", "av1_nvenc", "h264_mf", "libx264"];

    [Fact]
    public void AutoPicksTheGpuFirst()
    {
        var plan = EncoderCatalog.Plan(EncoderChoice.Auto, VideoCodec.H264, NvidiaBox);
        Assert.Equal("h264_nvenc", plan[0].FfmpegName);
        Assert.Equal("libx264", plan[^1].FfmpegName);
    }

    [Fact]
    public void UnavailableCodecFallsBackToH264()
    {
        var plan = EncoderCatalog.Plan(EncoderChoice.Auto, VideoCodec.Av1, new HashSet<string> { "h264_amf", "h264_mf" });
        Assert.Equal(["h264_amf", "h264_mf"], plan.Select(p => p.FfmpegName));
    }

    [Fact]
    public void ExplicitCpuChoiceComesFirst()
    {
        Assert.Equal("libx264", EncoderCatalog.Plan(EncoderChoice.Cpu, VideoCodec.H264, NvidiaBox)[0].FfmpegName);
    }
}

public class HotkeyTests
{
    [Theory]
    [InlineData("Alt+F10")]
    [InlineData("Ctrl+Alt+F10")]
    [InlineData("Ctrl+Shift+S")]
    [InlineData("Alt+PrintScreen")]
    public void RoundTrips(string text) => Assert.Equal(text, Hotkey.Parse(text).ToString());

    [Fact]
    public void ParsesModifiersAndKey()
    {
        var hotkey = Hotkey.Parse("alt + f10");
        Assert.Equal(HotkeyModifiers.Alt, hotkey.Modifiers);
        Assert.Equal(0x79, hotkey.VirtualKey);
    }

    [Fact]
    public void InvalidIsEmpty()
    {
        Assert.True(Hotkey.Parse("Alt+Nope").IsEmpty);
        Assert.True(Hotkey.Parse("").IsEmpty);
        Assert.Equal("Yok", Hotkey.None.ToString());
    }
}
