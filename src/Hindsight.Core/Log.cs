using System.Text;

namespace Hindsight.Core;

/// <summary>Tiny append-only log file (rotated at 1 MB). Never throws.</summary>
public static class Log
{
    private static readonly Lock Gate = new();
    private const long MaxBytes = 1024 * 1024;

    public static string FilePath => Path.Combine(AppPaths.LogsDir, "hindsight.log");

    public static void Info(string message) => Write("INFO", message);

    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message}: {ex.GetType().Name}: {ex.Message}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                var file = FilePath;
                var info = new FileInfo(file);
                if (info.Exists && info.Length > MaxBytes)
                {
                    File.Move(file, file + ".old", overwrite: true);
                }

                File.AppendAllText(file, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}", Encoding.UTF8);
            }
        }
        catch
        {
            // Logging must never break recording.
        }
    }
}
