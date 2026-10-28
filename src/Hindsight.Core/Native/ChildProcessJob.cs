using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Hindsight.Core.Native;

/// <summary>
/// A Windows job object with KILL_ON_JOB_CLOSE: every ffmpeg process Hindsight starts is put in it,
/// so if Hindsight exits or crashes, Windows ends the recorder too — no orphaned ffmpeg eating the GPU.
/// </summary>
public static class ChildProcessJob
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint KillOnJobClose = 0x2000;
    private static readonly Lazy<nint> Job = new(Create);

    public static void Add(Process process)
    {
        try
        {
            if (Job.Value != 0)
            {
                Win32.AssignProcessToJobObject(Job.Value, process.Handle);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // The process already ended; nothing to clean up.
        }
    }

    private static nint Create()
    {
        var job = Win32.CreateJobObject(0, null);
        if (job == 0)
        {
            return 0;
        }

        var info = new Win32.JobExtendedLimitInformation();
        info.BasicLimitInformation.LimitFlags = KillOnJobClose;
        if (!Win32.SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref info, Marshal.SizeOf<Win32.JobExtendedLimitInformation>()))
        {
            Log.Warn("Could not configure the child-process job object.");
        }

        return job;
    }
}
