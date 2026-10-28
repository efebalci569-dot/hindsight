using Hindsight.Core.Settings;

namespace Hindsight.Core.Media;

/// <summary>What to capture: a monitor (by HMONITOR) or one window (by HWND).</summary>
public sealed record CaptureSource(nint MonitorHandle, nint WindowHandle, int SourceWidth, int SourceHeight, string Description)
{
    public bool IsWindow => WindowHandle != 0;
}

public sealed record AudioOptions(
    bool SystemAudio,
    float SystemVolume,
    bool Microphone,
    string? MicrophoneDeviceId,
    float MicrophoneVolume,
    int BitrateKbps,
    int OffsetMs)
{
    public bool Enabled => SystemAudio || Microphone;
}

/// <summary>Everything the replay recorder needs for one ffmpeg run.</summary>
public sealed record RecordingRequest(
    CaptureSource Source,
    int Width,
    int Height,
    int Fps,
    int VideoKbps,
    IReadOnlyList<EncoderInfo> Encoders,
    AudioOptions Audio,
    int ClipSeconds,
    string BufferRoot,
    RecorderPriority Priority,
    bool CaptureCursor);

/// <summary>
/// One concrete way to run the capture → encoder chain. Several are tried until one starts.
/// <paramref name="MonitorFallback"/>: the request asked for a window, but this variant records the window's monitor instead.
/// </summary>
public sealed record PipelineVariant(EncoderInfo Encoder, bool UseGfxCapture, string? VideoFilter, string Label, bool MonitorFallback = false);
