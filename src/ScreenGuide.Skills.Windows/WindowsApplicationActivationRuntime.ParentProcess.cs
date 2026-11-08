using System.Runtime.InteropServices;

namespace ScreenGuide.Skills.Windows;

internal sealed partial class WindowsApplicationActivationRuntime
{
    // Toolhelp is read-only OS process metadata. Keep the child's original handle
    // and creation time bound while sampling its parent; never combine a reused PID.
    private static ApplicationProcessSnapshot? ReadParentIdentity(IntPtr childHandle, int childId, DateTimeOffset childStarted)
    {
        if (!GetProcessTimes(childHandle, out var creation, out _, out _, out _)
            || DateTimeOffset.FromFileTime(creation) != childStarted) return null;
        var snapshot = CreateToolhelp32Snapshot(0x00000002, 0); // TH32CS_SNAPPROCESS
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1)) return null;
        try
        {
            var entry = new ProcessEntry32 { Size = (uint)Marshal.SizeOf<ProcessEntry32>(), ExeFile = string.Empty };
            if (!Process32First(snapshot, ref entry)) return null;
            do
            {
                if (entry.ProcessId != childId) continue;
                if (entry.ParentProcessId == 0 || entry.ParentProcessId > int.MaxValue || entry.ParentProcessId == childId) return null;
                var parent = ReadProcess((int)entry.ParentProcessId, includeParent: false);
                if (parent is null || !parent.IsRunning || parent.ProcessStartTimeUtc > childStarted
                    || !GetProcessTimes(childHandle, out var afterCreation, out _, out _, out _)
                    || afterCreation != creation) return null;
                return parent;
            } while (Process32Next(snapshot, ref entry));
            return null;
        }
        finally { _ = CloseHandle(snapshot); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public UIntPtr DefaultHeapId;
        public uint ModuleId;
        public uint ThreadCount;
        public uint ParentProcessId;
        public int BasePriority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", EntryPoint = "Process32NextW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);
}
