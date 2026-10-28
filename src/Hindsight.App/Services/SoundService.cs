using System.IO;
using System.Media;

namespace Hindsight.App.Services;

/// <summary>Short confirmation chimes, synthesized once at start-up (no audio files to ship).</summary>
public sealed class SoundService : IDisposable
{
    private readonly SoundPlayer _success = new(Chime([880, 1318.5], 0.11));
    private readonly SoundPlayer _start = new(Chime([660, 880], 0.08));
    private readonly SoundPlayer _error = new(Chime([392, 311], 0.13));

    public void Success() => Play(_success);

    public void Start() => Play(_start);

    public void Error() => Play(_error);

    private static void Play(SoundPlayer player)
    {
        try
        {
            player.Stream!.Position = 0;
            player.Play();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
        }
    }

    private static MemoryStream Chime(double[] notes, double noteSeconds)
    {
        const int Rate = 44100;
        var perNote = (int)(Rate * noteSeconds);
        var tail = (int)(Rate * 0.12);
        var total = (perNote * notes.Length) + tail;
        var samples = new short[total];
        for (var n = 0; n < notes.Length; n++)
        {
            var length = n == notes.Length - 1 ? perNote + tail : perNote;
            for (var i = 0; i < length; i++)
            {
                var t = i / (double)Rate;
                var attack = Math.Min(1, i / (Rate * 0.005));
                var decay = Math.Exp(-t * 18);
                var value = (Math.Sin(2 * Math.PI * notes[n] * t) + (0.25 * Math.Sin(4 * Math.PI * notes[n] * t))) * attack * decay;
                var index = (n * perNote) + i;
                if (index < total)
                {
                    samples[index] = (short)Math.Clamp(samples[index] + (value * 7000), short.MinValue, short.MaxValue);
                }
            }
        }

        var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            writer.Write("RIFF"u8);
            writer.Write(36 + (total * 2));
            writer.Write("WAVEfmt "u8);
            writer.Write(16);
            writer.Write((short)1);
            writer.Write((short)1);
            writer.Write(Rate);
            writer.Write(Rate * 2);
            writer.Write((short)2);
            writer.Write((short)16);
            writer.Write("data"u8);
            writer.Write(total * 2);
            foreach (var s in samples)
            {
                writer.Write(s);
            }
        }

        stream.Position = 0;
        return stream;
    }

    public void Dispose()
    {
        _success.Dispose();
        _start.Dispose();
        _error.Dispose();
    }
}
