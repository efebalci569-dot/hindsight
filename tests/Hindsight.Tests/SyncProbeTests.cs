using System.Globalization;
using System.Text.RegularExpressions;
using Hindsight.Core;
using Hindsight.Core.Media;
using Hindsight.Core.Native;
using Hindsight.Core.Settings;
using Xunit.Abstractions;

namespace Hindsight.Tests;

/// <summary>
/// Measures audio/video offset: flashes a full-screen window white while playing a click, records it,
/// then finds the brightness jumps and the loudness jumps in the clip. Runs only with HINDSIGHT_SYNC=1.
/// </summary>
public partial class SyncProbeTests(ITestOutputHelper output)
{
    [Fact]
    public async Task MeasureAvOffset()
    {
        if (Environment.GetEnvironmentVariable("HINDSIGHT_SYNC") != "1")
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), "hs-sync");
        Directory.CreateDirectory(root);
        AppPaths.OverrideDataRoot(root);
        var caps = (await FfmpegCaps.ProbeAsync(FfmpegLocator.Find()!))!;
        var monitor = WindowInfo.GetPrimaryMonitor()!;
        var offset = int.Parse(Environment.GetEnvironmentVariable("HINDSIGHT_SYNC_OFFSET") ?? "0");
        var request = new RecordingRequest(new CaptureSource(monitor.Handle, 0, monitor.Width, monitor.Height, "primary"),
            1280, 720, 60, 10000, EncoderCatalog.Plan(EncoderChoice.Auto, VideoCodec.H264, caps.Working),
            new AudioOptions(true, 1, false, null, 1, 160, offset), 15, Path.Combine(root, "buffer"), RecorderPriority.Normal, false);

        await using var recorder = new ReplayRecorder(caps);
        Assert.True(await recorder.StartAsync(request), recorder.LastError);
        await Task.Delay(1500);

        var wav = Path.Combine(root, "click.wav");
        WriteClick(wav);
        var done = new TaskCompletionSource();
        var thread = new Thread(() =>
        {
            using var player = new System.Media.SoundPlayer(wav);
            player.Load();
            using var form = new System.Windows.Forms.Form
            {
                FormBorderStyle = System.Windows.Forms.FormBorderStyle.None,
                WindowState = System.Windows.Forms.FormWindowState.Maximized,
                BackColor = System.Drawing.Color.Black,
                TopMost = true,
            };
            var timer = new System.Windows.Forms.Timer { Interval = 50 };
            var ticks = 0;
            timer.Tick += (_, _) =>
            {
                ticks++;
                var phase = ticks % 30; // 1.5 s period
                if (phase == 10)
                {
                    form.BackColor = System.Drawing.Color.White;
                    form.Refresh();
                    player.Play();
                }
                else if (phase == 14)
                {
                    form.BackColor = System.Drawing.Color.Black;
                    form.Refresh();
                }

                if (ticks > 30 * 5)
                {
                    timer.Stop();
                    form.Close();
                }
            };
            form.Shown += (_, _) => timer.Start();
            System.Windows.Forms.Application.Run(form);
            done.SetResult();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await done.Task;

        var clip = Path.Combine(root, "sync.mp4");
        await recorder.SaveReplayAsync(10, clip);
        await recorder.StopAsync();

        var video = await Events(caps.FfmpegPath, clip, "signalstats,metadata=print:key=lavfi.signalstats.YAVG", isAudio: false, threshold: 128);
        var audio = await Events(caps.FfmpegPath, clip, "bandpass=f=1000:width_type=q:w=8,asetnsamples=240,astats=metadata=1:reset=1,ametadata=print:key=lavfi.astats.Overall.Peak_level", isAudio: true, threshold: -12);
        output.WriteLine($"video flashes: {string.Join(", ", video.Select(v => v.ToString("0.000")))}");
        output.WriteLine($"audio clicks:  {string.Join(", ", audio.Select(a => a.ToString("0.000")))}");
        var pairs = video.Select(v => audio.Where(a => a > v - 0.3 && a < v + 0.5).Select(a => a - v).DefaultIfEmpty(double.NaN).First())
            .Where(d => !double.IsNaN(d)).ToList();
        output.WriteLine($"audio − video (ms): {string.Join(", ", pairs.Select(p => (p * 1000).ToString("0")))}; median {(pairs.Count > 0 ? pairs.Order().ElementAt(pairs.Count / 2) * 1000 : double.NaN):0} ms");
    }

    private static async Task<List<double>> Events(string ffmpeg, string clip, string filter, bool isAudio, double threshold)
    {
        var args = new List<string> { "-hide_banner", "-nostdin", "-i", clip, isAudio ? "-af" : "-vf", filter, isAudio ? "-vn" : "-an", "-f", "null", "-" };
        // metadata=print writes to stderr, and every line is needed (RunAsync keeps only the tail).
        var info = FfmpegRunner.CreateStartInfo(ffmpeg, args);
        using var process = System.Diagnostics.Process.Start(info)!;
        var text = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        var events = new List<double>();
        double? time = null;
        var above = false;
        foreach (var line in text.Split('\n'))
        {
            var t = TimeRegex().Match(line);
            if (t.Success)
            {
                time = double.Parse(t.Groups[1].Value, CultureInfo.InvariantCulture);
                continue;
            }

            var v = ValueRegex().Match(line);
            if (v.Success && time is not null && double.TryParse(v.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                var now = value > threshold;
                if (now && !above)
                {
                    events.Add(time.Value);
                }

                above = now;
            }
        }

        return events;
    }

    private static void WriteClick(string path)
    {
        const int rate = 48000;
        var samples = rate / 20; // 50 ms burst
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write("RIFF"u8);
        writer.Write(36 + (samples * 2));
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(rate);
        writer.Write(rate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(samples * 2);
        for (var i = 0; i < samples; i++)
        {
            writer.Write((short)(Math.Sin(2 * Math.PI * 1000 * i / rate) * 30000));
        }
    }

    [GeneratedRegex(@"pts_time:([0-9.]+)")]
    private static partial Regex TimeRegex();

    [GeneratedRegex(@"lavfi\.(?:signalstats\.YAVG|astats\.Overall\.Peak_level)=(-?[0-9.]+|-inf)")]
    private static partial Regex ValueRegex();
}
