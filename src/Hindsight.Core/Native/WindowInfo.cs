namespace Hindsight.Core.Native;

/// <summary>A snapshot of the foreground window: who owns it and whether it covers its whole monitor.</summary>
public sealed record ForegroundWindow(
    nint Handle,
    int ProcessId,
    string? ExePath,
    string ProcessName,
    string Title,
    nint Monitor,
    bool IsFullscreen);

public sealed record MonitorDetails(nint Handle, string Device, int Left, int Top, int Width, int Height, bool IsPrimary);

public static class WindowInfo
{
    private static readonly HashSet<string> ShellClasses = new(StringComparer.Ordinal)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Windows.UI.Core.CoreWindow",
    };

    public static ForegroundWindow? GetForeground()
    {
        var hwnd = Win32.GetForegroundWindow();
        if (hwnd == 0 || !Win32.IsWindowVisible(hwnd) || Win32.IsIconic(hwnd))
        {
            return null;
        }

        Win32.GetWindowThreadProcessId(hwnd, out var pid);
        if (pid <= 0 || pid == Environment.ProcessId)
        {
            return null;
        }

        var exe = Win32.GetProcessPath(pid);
        string name;
        if (exe is not null)
        {
            name = Path.GetFileNameWithoutExtension(exe);
        }
        else
        {
            // Protected processes (some anti-cheat games) hide their path; the name is still visible.
            try
            {
                using var process = System.Diagnostics.Process.GetProcessById(pid);
                name = process.ProcessName;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                return null;
            }
        }

        var monitor = Win32.MonitorFromWindow(hwnd, Win32.MonitorDefaultToNearest);
        return new ForegroundWindow(hwnd, pid, exe, name, Win32.GetWindowTitle(hwnd), monitor, IsFullscreen(hwnd, monitor));
    }

    public static bool IsFullscreen(nint hwnd, nint monitor)
    {
        if (ShellClasses.Contains(Win32.GetClassName(hwnd)))
        {
            return false;
        }

        if (Win32.DwmGetWindowAttribute(hwnd, Win32.DwmwaCloaked, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
        {
            return false;
        }

        if (!Win32.GetWindowRect(hwnd, out var rect) || GetMonitor(monitor) is not { } m)
        {
            return false;
        }

        return rect.Left <= m.Left && rect.Top <= m.Top && rect.Right >= m.Left + m.Width && rect.Bottom >= m.Top + m.Height;
    }

    public static MonitorDetails? GetMonitor(nint monitor)
    {
        if (monitor == 0)
        {
            return null;
        }

        var info = new Win32.MonitorInfoEx { Size = System.Runtime.InteropServices.Marshal.SizeOf<Win32.MonitorInfoEx>() };
        if (!Win32.GetMonitorInfo(monitor, ref info))
        {
            return null;
        }

        return new MonitorDetails(monitor, info.Device, info.Monitor.Left, info.Monitor.Top,
            info.Monitor.Width, info.Monitor.Height, (info.Flags & 1) != 0);
    }

    public static IReadOnlyList<MonitorDetails> GetMonitors()
    {
        var list = new List<MonitorDetails>();
        Win32.EnumDisplayMonitors(0, 0, (nint handle, nint _, ref Win32.Rect _, nint _) =>
        {
            if (GetMonitor(handle) is { } m)
            {
                list.Add(m);
            }

            return true;
        }, 0);
        return list;
    }

    public static MonitorDetails? GetPrimaryMonitor() =>
        GetMonitors().FirstOrDefault(m => m.IsPrimary) ?? GetMonitors().FirstOrDefault();

    /// <summary>Size of the window's visible frame (without the invisible resize border).</summary>
    public static (int Width, int Height)? GetWindowSize(nint hwnd)
    {
        if (Win32.DwmGetWindowAttribute(hwnd, Win32.DwmwaExtendedFrameBounds, out Win32.Rect frame, System.Runtime.InteropServices.Marshal.SizeOf<Win32.Rect>()) == 0
            && frame.Width > 0 && frame.Height > 0)
        {
            return (frame.Width, frame.Height);
        }

        return Win32.GetWindowRect(hwnd, out var rect) && rect.Width > 0 ? (rect.Width, rect.Height) : null;
    }

    /// <summary>Gives unused memory pages back to Windows (the working set refills on demand).</summary>
    public static void TrimWorkingSet() => Win32.SetProcessWorkingSetSize(Win32.GetCurrentProcess(), -1, -1);
}

/// <summary>A handle on a running process that tells reliably (no pid reuse) when it has exited.</summary>
public sealed class ProcessWatch : IDisposable
{
    private nint _handle;

    public ProcessWatch(int processId)
    {
        ProcessId = processId;
        _handle = Win32.OpenProcess(Win32.Synchronize | Win32.ProcessQueryLimitedInformation, false, processId);
    }

    public int ProcessId { get; }

    public bool HasExited
    {
        get
        {
            if (_handle == 0)
            {
                try
                {
                    using var p = System.Diagnostics.Process.GetProcessById(ProcessId);
                    return p.HasExited;
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    return true;
                }
            }

            return Win32.WaitForSingleObject(_handle, 0) == 0;
        }
    }

    public void Dispose()
    {
        if (_handle != 0)
        {
            Win32.CloseHandle(_handle);
            _handle = 0;
        }
    }
}
