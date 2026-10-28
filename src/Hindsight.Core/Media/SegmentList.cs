using System.Globalization;

namespace Hindsight.Core.Media;

/// <summary>One finished replay-buffer segment as listed by ffmpeg's segment muxer (file,start,end).</summary>
public sealed record Segment(int Index, string FileName, double Start, double End)
{
    public double Duration => Math.Max(0, End - Start);
}

public static class SegmentList
{
    /// <summary>Reads the finished segments. Reads only the tail of the file, so long sessions stay cheap.</summary>
    public static IReadOnlyList<Segment> Read(string csvPath, int maxEntries = 400)
    {
        try
        {
            if (!File.Exists(csvPath))
            {
                return [];
            }

            using var stream = new FileStream(csvPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            const int Tail = 64 * 1024;
            var skipped = false;
            if (stream.Length > Tail)
            {
                stream.Seek(-Tail, SeekOrigin.End);
                skipped = true;
            }

            using var reader = new StreamReader(stream);
            var text = reader.ReadToEnd();
            var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var lineOffset = 0;
            if (skipped && lines.Length > 0)
            {
                lines = lines[1..]; // first line may be cut in half
                lineOffset = -1;
            }

            return Parse(lines, maxEntries, lineOffset);
        }
        catch (IOException)
        {
            return [];
        }
    }

    internal static IReadOnlyList<Segment> Parse(IReadOnlyList<string> lines, int maxEntries = 400, int lineOffset = 0)
    {
        var result = new List<Segment>(Math.Min(lines.Count, maxEntries));
        var start = Math.Max(0, lines.Count - maxEntries);
        for (var i = start; i < lines.Count; i++)
        {
            var parts = lines[i].Trim().Split(',');
            if (parts.Length < 3)
            {
                continue;
            }

            if (double.TryParse(parts[^2], NumberStyles.Float, CultureInfo.InvariantCulture, out var s) &&
                double.TryParse(parts[^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var e))
            {
                var name = string.Join(',', parts[..^2]).Trim('"');
                result.Add(new Segment(i + lineOffset, name, s, e));
            }
        }

        return result;
    }

    /// <summary>
    /// Picks the newest consecutive segments covering at least <paramref name="seconds"/>.
    /// Only the last <paramref name="wrap"/> − 2 entries are safe: older names have been overwritten by the ring.
    /// </summary>
    public static IReadOnlyList<Segment> SelectLast(IReadOnlyList<Segment> segments, double seconds, int wrap)
    {
        var safe = Math.Max(1, wrap - 2);
        var picked = new List<Segment>();
        double total = 0;
        for (var i = segments.Count - 1; i >= 0 && picked.Count < safe; i--)
        {
            var segment = segments[i];
            if (picked.Count > 0 && Math.Abs(segment.End - picked[^1].Start) > 0.5)
            {
                break; // a gap means ffmpeg restarted; do not splice two sessions together
            }

            picked.Add(segment);
            total += segment.Duration;
            if (total >= seconds - 0.05)
            {
                break;
            }
        }

        picked.Reverse();
        return picked;
    }
}
