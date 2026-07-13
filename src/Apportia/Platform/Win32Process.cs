using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Apportia.Platform;

[SupportedOSPlatform("windows")]
internal static partial class Win32Process
{
    private const int ProcessBasicInformation = 0;

    public static int? TryGetParentPid(int pid)
    {
        try
        {
            using var proc = Process.GetProcessById(pid);
            var pbi = default(ProcessBasicInformationStruct);
            var size = Marshal.SizeOf<ProcessBasicInformationStruct>();
            var status = NtQueryInformationProcess(proc.Handle, ProcessBasicInformation, ref pbi, size, out _);
            if (status != 0)
                return null;
            return pbi.InheritedFromUniqueProcessId.ToInt32();
        }
        catch
        {
            return null;
        }
    }

    [LibraryImport("ntdll.dll")]
    private static partial int NtQueryInformationProcess(
        IntPtr processHandle,
        int processInformationClass,
        ref ProcessBasicInformationStruct processInformation,
        int processInformationLength,
        out int returnLength);

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformationStruct
    {
        public IntPtr Reserved1;
        public IntPtr PebBaseAddress;
        public IntPtr Reserved20;
        public IntPtr Reserved21;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }
}
