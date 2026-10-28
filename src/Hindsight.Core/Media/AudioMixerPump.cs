using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Hindsight.Core.Media;

/// <summary>
/// Captures system audio (WASAPI loopback) and optionally a microphone, mixes them to 48 kHz stereo float
/// and streams the result to ffmpeg through a named pipe — ffmpeg itself cannot record WASAPI loopback.
/// The pump is paced by the clock and writes silence when nothing plays, so the audio track never stalls
/// and stays in step with the video. Each source keeps at most ~120 ms queued (no latency build-up).
/// </summary>
public sealed class AudioMixerPump : IDisposable
{
    public const int SampleRate = 48000;
    public const int Channels = 2;
    private static readonly Guid IeeeFloatSubtype = new("00000003-0000-0010-8000-00aa00389b71");

    private readonly AudioOptions _options;
    private readonly NamedPipeServerStream _pipe;
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly DeviceWatcher _watcher;
    private Source? _system;
    private Source? _microphone;
    private Thread? _thread;

    public AudioMixerPump(AudioOptions options)
    {
        _options = options;
        PipeName = $"hindsight-audio-{Environment.ProcessId}-{Guid.NewGuid():N}";
        _pipe = new NamedPipeServerStream(PipeName, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.None, 0, 1 << 16);
        _watcher = new DeviceWatcher(this);
    }

    public string PipeName { get; }

    public string PipePath => $@"\\.\pipe\{PipeName}";

    public void Start()
    {
        if (_options.SystemAudio)
        {
            _system = TryCreate(() => new WasapiLoopbackCapture(), "system", _options.SystemVolume);
        }

        if (_options.Microphone)
        {
            _microphone = TryCreate(CreateMicrophone, "microphone", _options.MicrophoneVolume);
        }

        try
        {
            _enumerator.RegisterEndpointNotificationCallback(_watcher);
        }
        catch (COMException)
        {
        }

        _thread = new Thread(Pump) { IsBackground = true, Name = "Hindsight audio pump", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    public static IReadOnlyList<(string Id, string Name)> ListMicrophones()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            return enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
                .Select(d => (d.ID, d.FriendlyName))
                .ToList();
        }
        catch (COMException)
        {
            return [];
        }
    }

    private WasapiCapture CreateMicrophone()
    {
        MMDevice? device = null;
        if (!string.IsNullOrWhiteSpace(_options.MicrophoneDeviceId))
        {
            try
            {
                device = _enumerator.GetDevice(_options.MicrophoneDeviceId);
            }
            catch (COMException)
            {
                device = null;
            }
        }

        device ??= _enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
        return new WasapiCapture(device, useEventSync: true, audioBufferMillisecondsLength: 50);
    }

    private static Source? TryCreate(Func<WasapiCapture> factory, string label, float volume)
    {
        try
        {
            return new Source(factory(), label, volume);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException)
        {
            Log.Warn($"Audio source '{label}' unavailable: {ex.Message}");
            return null;
        }
    }

    private void RestartSystemSource()
    {
        if (!_options.SystemAudio || _stop.IsCancellationRequested)
        {
            return;
        }

        lock (_gate)
        {
            _system?.Dispose();
            _system = TryCreate(() => new WasapiLoopbackCapture(), "system", _options.SystemVolume);
        }

        Log.Info("Default output device changed; system audio capture restarted.");
    }

    private void Pump()
    {
        try
        {
            _pipe.WaitForConnection();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            return;
        }

        const int MaxFrames = SampleRate / 10; // 100 ms per write at most
        var mix = new float[MaxFrames * Channels];
        var temp = new float[MaxFrames * Channels];
        var clock = Stopwatch.StartNew();
        long written = 0;

        while (!_stop.IsCancellationRequested)
        {
            var due = (long)(clock.Elapsed.TotalSeconds * SampleRate) - written;
            if (due < SampleRate / 200)
            {
                Thread.Sleep(4);
                continue;
            }

            var frames = (int)Math.Min(due, MaxFrames);
            var count = frames * Channels;
            Array.Clear(mix, 0, count);
            lock (_gate)
            {
                _system?.MixInto(mix, temp, count);
                _microphone?.MixInto(mix, temp, count);
            }

            for (var i = 0; i < count; i++)
            {
                mix[i] = Math.Clamp(mix[i], -1f, 1f);
            }

            try
            {
                _pipe.Write(MemoryMarshal.AsBytes(mix.AsSpan(0, count)));
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
            {
                return; // ffmpeg went away
            }

            written += frames;
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        try
        {
            _enumerator.UnregisterEndpointNotificationCallback(_watcher);
        }
        catch (COMException)
        {
        }

        try
        {
            _pipe.Dispose();
        }
        catch (IOException)
        {
        }

        _thread?.Join(500);
        lock (_gate)
        {
            _system?.Dispose();
            _microphone?.Dispose();
            _system = null;
            _microphone = null;
        }

        _enumerator.Dispose();
    }

    /// <summary>One capture device → bounded queue → 48 kHz stereo float.</summary>
    private sealed class Source : IDisposable
    {
        private readonly WasapiCapture _capture;
        private readonly BufferedWaveProvider _buffer;
        private readonly ISampleProvider _output;
        private readonly int _maxQueuedBytes;
        private readonly float _volume;
        private readonly byte[] _discard = new byte[16384];

        public Source(WasapiCapture capture, string label, float volume)
        {
            _capture = capture;
            _volume = volume;
            var format = Normalize(capture.WaveFormat);
            _buffer = new BufferedWaveProvider(format)
            {
                BufferDuration = TimeSpan.FromSeconds(1),
                DiscardOnBufferOverflow = true,
                ReadFully = true, // pads with silence when the device delivered nothing
            };
            _maxQueuedBytes = format.AverageBytesPerSecond * 120 / 1000;

            ISampleProvider samples = _buffer.ToSampleProvider();
            samples = samples.WaveFormat.Channels switch
            {
                1 => new MonoToStereoSampleProvider(samples),
                2 => samples,
                _ => new DownmixToStereo(samples),
            };
            if (samples.WaveFormat.SampleRate != SampleRate)
            {
                samples = new WdlResamplingSampleProvider(samples, SampleRate);
            }

            _output = samples;
            _capture.DataAvailable += (_, e) => _buffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
            _capture.RecordingStopped += (_, e) =>
            {
                if (e.Exception is not null)
                {
                    Log.Warn($"Audio source '{label}' stopped: {e.Exception.Message}");
                }
            };
            _capture.StartRecording();
            Log.Info($"Audio source '{label}': {capture.WaveFormat.SampleRate} Hz, {capture.WaveFormat.Channels} ch, {capture.WaveFormat.Encoding}");
        }

        public void MixInto(float[] mix, float[] temp, int count)
        {
            // Keep latency low: drop audio that queued up beyond ~120 ms (clock drift, hiccups).
            var excess = _buffer.BufferedBytes - _maxQueuedBytes;
            while (excess > 0)
            {
                var chunk = Math.Min(excess, _discard.Length);
                chunk -= chunk % _buffer.WaveFormat.BlockAlign;
                if (chunk <= 0)
                {
                    break;
                }

                _buffer.Read(_discard, 0, chunk);
                excess -= chunk;
            }

            var read = _output.Read(temp, 0, count);
            for (var i = 0; i < read; i++)
            {
                mix[i] += temp[i] * _volume;
            }
        }

        private static WaveFormat Normalize(WaveFormat format)
        {
            if (format is WaveFormatExtensible extensible)
            {
                return extensible.SubFormat == IeeeFloatSubtype
                    ? WaveFormat.CreateIeeeFloatWaveFormat(format.SampleRate, format.Channels)
                    : new WaveFormat(format.SampleRate, format.BitsPerSample, format.Channels);
            }

            return format;
        }

        public void Dispose()
        {
            try
            {
                _capture.StopRecording();
            }
            catch (Exception ex) when (ex is COMException or InvalidOperationException)
            {
            }

            _capture.Dispose();
        }
    }

    /// <summary>5.1 / 7.1 → stereo (front + centre); the order follows WAVE_FORMAT_EXTENSIBLE channel masks.</summary>
    private sealed class DownmixToStereo(ISampleProvider source) : ISampleProvider
    {
        private readonly int _channels = source.WaveFormat.Channels;
        private float[] _buffer = [];

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 2);

        public int Read(float[] buffer, int offset, int count)
        {
            var frames = count / 2;
            var needed = frames * _channels;
            if (_buffer.Length < needed)
            {
                _buffer = new float[needed];
            }

            var read = source.Read(_buffer, 0, needed) / _channels;
            for (var f = 0; f < read; f++)
            {
                var s = f * _channels;
                var centre = _channels > 2 ? _buffer[s + 2] * 0.7f : 0f;
                buffer[offset + (f * 2)] = _buffer[s] + centre;
                buffer[offset + (f * 2) + 1] = _buffer[s + 1] + centre;
            }

            return read * 2;
        }
    }

    private sealed class DeviceWatcher(AudioMixerPump owner) : IMMNotificationClient
    {
        private DateTime _last = DateTime.MinValue;

        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
        {
            if (flow != DataFlow.Render || role != Role.Multimedia || DateTime.UtcNow - _last < TimeSpan.FromSeconds(1))
            {
                return;
            }

            _last = DateTime.UtcNow;
            ThreadPool.QueueUserWorkItem(_ => owner.RestartSystemSource());
        }

        public void OnDeviceStateChanged(string deviceId, DeviceState newState)
        {
        }

        public void OnDeviceAdded(string pwstrDeviceId)
        {
        }

        public void OnDeviceRemoved(string deviceId)
        {
        }

        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key)
        {
        }
    }
}
