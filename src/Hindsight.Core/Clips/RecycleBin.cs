using System.Runtime.InteropServices;

namespace Hindsight.Core.Clips;

/// <summary>Sends files to the Recycle Bin (SHFileOperation with FOF_ALLOWUNDO), so a deleted clip can be restored.</summary>
public static class RecycleBin
{
    private const uint FoDelete = 0x0003;
    private const ushort FofAllowUndo = 0x0040;
    private const ushort FofNoConfirmation = 0x0010;
    private const ushort FofSilent = 0x0004;
    private const ushort FofNoErrorUi = 0x0400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileOpStruct
    {
        public nint Hwnd;
        public uint Func;
        [MarshalAs(UnmanagedType.LPWStr)] public string From;
        [MarshalAs(UnmanagedType.LPWStr)] public string? To;
        public ushort Flags;
        public bool AnyOperationsAborted;
        public nint NameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? ProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref ShFileOpStruct operation);

    public static bool Send(string path)
    {
        if (!File.Exists(path))
        {
            return true;
        }

        var operation = new ShFileOpStruct
        {
            Func = FoDelete,
            From = path + "\0\0", // double-null terminated list
            Flags = FofAllowUndo | FofNoConfirmation | FofSilent | FofNoErrorUi,
        };
        return SHFileOperation(ref operation) == 0 && !operation.AnyOperationsAborted && !File.Exists(path);
    }
}
