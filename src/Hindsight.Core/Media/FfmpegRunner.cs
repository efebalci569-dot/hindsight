using System.Diagnostics;
using Hindsight.Core.Native;

namespace Hindsight.Core.Media;

public sealed record FfmpegResult(int ExitCode, string ErrorTail, string Output)
{
    public bool Success => ExitCode == 0;
}

/// <summary>Runs short ffmpeg/ffprobe jobs (concat, thumbnails, trims) with no window and low priority.</summary>
public static class FfmpegRunner
{
    public static ProcessStartInfo CreateStartInfo(string exe, IEnumerable<string> arguments)
    {
        var info = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            RedirectStandardInput = true,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        return info;
    }

    public static async Task<FfmpegResult> RunAsync(
        string exe,
        IEnumerable<string> arguments,
        CancellationToken cancellationToken = default,
        ProcessPriorityClass priority = ProcessPriorityClass.BelowNormal,
        TimeSpan? timeout = null)
    {
        using var process = new Process { StartInfo = CreateStartInfo(exe, arguments) };
        var errors = new Queue<string>();
        var output = new System.Text.StringBuilder();
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                return;
            }

            lock (errors)
            {
                errors.Enqueue(e.Data);
                if (errors.Count > 30)
                {
                    errors.Dequeue();
                }
            }
        };
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                lock (output)
                {
                    output.AppendLine(e.Data);
                }
            }
        };

        process.Start();
        ChildProcessJob.Add(process);
        TrySetPriority(process, priority);
        process.BeginErrorReadLine();
        process.BeginOutputReadLine();

        using var timeoutSource = new CancellationTokenSource(timeout ?? TimeSpan.FromMinutes(10));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            return new FfmpegResult(-1, "Zaman aşımı", string.Empty);
        }

        process.WaitForExit(); // flush the async readers
        string tail;
        lock (errors)
        {
            tail = string.Join(Environment.NewLine, errors);
        }

        return new FfmpegResult(process.ExitCode, tail, output.ToString());
    }

    public static void TrySetPriority(Process process, ProcessPriorityClass priority)
    {
        try
        {
            process.PriorityClass = priority;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }

    public static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }

    /// <summary>Quotes a path for an ffmpeg concat list (single quotes, with ' escaped).</summary>
    public static string ConcatLine(string path) => $"file '{path.Replace("'", @"'\''")}'";
}
