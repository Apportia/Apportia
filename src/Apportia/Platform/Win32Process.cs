using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace Apportia.Platform;

[SupportedOSPlatform("windows")]
internal static partial class Win32Process
{
    public static int? TryGetParentPid(int pid)
    {
        try
        {
            using var proc = Process.GetProcessById(pid);
            var pbi = default(ProcessBasicInformationStruct);
            var size = Marshal.SizeOf<ProcessBasicInformationStruct>();
            var status = NtQueryInformationProcess(proc.Handle, 0 /* PROCESSINFOCLASS.ProcessBasicInformation */, ref pbi, size, out _);
            if (status != 0)
                return null;
            return pbi.InheritedFromUniqueProcessId.ToInt32();
        }
        catch
        {
            return null;
        }
    }

    public static bool IsProcessElevated(int pid)
    {
        var handle = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, (uint)pid);
        if (handle == IntPtr.Zero)
            return false;
        try
        {
            if (!OpenProcessToken(handle, 0x0008 /* TOKEN_QUERY */, out var token))
                return false;
            try
            {
                if (!GetTokenInformation(token, 20 /* TOKEN_INFORMATION_CLASS.TokenElevation */, out var elevation, sizeof(int), out _))
                    return false;
                return elevation != 0;
            }
            finally
            {
                CloseHandle(token);
            }
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    public unsafe static string? TryGetImagePath(int pid)
    {
        var handle = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, (uint)pid);
        if (handle == IntPtr.Zero)
            return null;
        try
        {
            Span<char> buffer = stackalloc char[1024];
            var size = buffer.Length;
            fixed (char* p = buffer)
            {
                if (!QueryFullProcessImageNameW(handle, 0, p, ref size))
                    return null;
                return new string(p, 0, size);
            }
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    public static bool IsCurrentProcessElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetTokenInformation(IntPtr tokenHandle, int tokenInformationClass, out int tokenInformation, int tokenInformationLength, out int returnLength);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial void CloseHandle(IntPtr handle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private unsafe static partial bool QueryFullProcessImageNameW(IntPtr process, uint flags, char* exeName, ref int size);

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
