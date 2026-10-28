namespace Hindsight.Core.Media;

/// <summary>Finds ffmpeg: the copy bundled next to Hindsight first, then the usual install places.</summary>
public static class FfmpegLocator
{
    public static string? Find()
    {
        var candidates = new List<string>
        {
            Path.Combine(AppPaths.AppDirectory, "ffmpeg", "ffmpeg.exe"),
            Path.Combine(AppPaths.AppDirectory, "ffmpeg.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Hindsight", "ffmpeg", "ffmpeg.exe"),
        };

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                candidates.Add(Path.Combine(dir.Trim().Trim('"'), "ffmpeg.exe"));
            }
            catch (ArgumentException)
            {
            }
        }

        candidates.Add(@"C:\ffmpeg\bin\ffmpeg.exe");
        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>ffprobe next to ffmpeg (same build), if present.</summary>
    public static string? FindProbe(string ffmpegPath)
    {
        var probe = Path.Combine(Path.GetDirectoryName(ffmpegPath)!, "ffprobe.exe");
        return File.Exists(probe) ? probe : null;
    }
}
