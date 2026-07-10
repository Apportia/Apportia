using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Apportia.Platform;

[SupportedOSPlatform("windows")]
internal static partial class WindowsCommandLine
{
    public static string? TryGet(int pid)
    {
        var handle = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION*/ | 0x0010 /* PROCESS_VM_READ */, false, (uint)pid);
        if (handle == IntPtr.Zero)
            return null;
        try
        {
            var info = new ProcessBasicInformation();
            if (NtQueryInformationProcess(handle, 0, ref info, (uint)Marshal.SizeOf<ProcessBasicInformation>(), out _) != 0)
                return null;
            if (info.PebBaseAddress == IntPtr.Zero)
                return null;

            var pebParamsOffset = IntPtr.Size == 8 ? 0x20 : 0x10;
            if (!ReadPointer(handle, info.PebBaseAddress + pebParamsOffset, out var paramsAddr) || paramsAddr == IntPtr.Zero)
                return null;

            var cmdOffset = IntPtr.Size == 8 ? 0x70 : 0x40;
            return !ReadUnicodeString(handle, paramsAddr + cmdOffset, out var cmd) ? null : cmd;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private unsafe static bool ReadPointer(IntPtr handle, IntPtr addr, out IntPtr value)
    {
        value = IntPtr.Zero;
        Span<byte> buf = stackalloc byte[IntPtr.Size];
        fixed (byte* p = buf)
        {
            if (!ReadProcessMemory(handle, addr, (IntPtr)p, (uint)buf.Length, out var read) || read != (nuint)buf.Length)
                return false;
        }

        value = (IntPtr)(IntPtr.Size == 8 ? BitConverter.ToInt64(buf) : BitConverter.ToInt32(buf));
        return true;
    }

    private unsafe static bool ReadUnicodeString(IntPtr handle, IntPtr addr, out string result)
    {
        result = string.Empty;
        Span<byte> header = stackalloc byte[IntPtr.Size == 8 ? 16 : 8];
        fixed (byte* p = header)
        {
            if (!ReadProcessMemory(handle, addr, (IntPtr)p, (uint)header.Length, out var read) || read != (nuint)header.Length)
                return false;
        }

        var length = BitConverter.ToUInt16(header[..2]);
        if (length is 0 or > 32 * 1024)
            return false;

        var bufferPtrOffset = IntPtr.Size == 8 ? 8 : 4;
        var bufferAddr = IntPtr.Size == 8
            ? (IntPtr)BitConverter.ToInt64(header[bufferPtrOffset..])
            : BitConverter.ToInt32(header[bufferPtrOffset..]);
        if (bufferAddr == IntPtr.Zero)
            return false;

        var buf = new byte[length];
        fixed (byte* p = buf)
        {
            if (!ReadProcessMemory(handle, bufferAddr, (IntPtr)p, length, out var read) || read != length)
                return false;
        }

        result = Encoding.Unicode.GetString(buf);
        return true;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwProcessId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial void CloseHandle(IntPtr hObject);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, IntPtr lpBuffer, uint nSize, out nuint lpNumberOfBytesRead);

    [LibraryImport("ntdll.dll")]
    private static partial int NtQueryInformationProcess(IntPtr processHandle, int processInformationClass, ref ProcessBasicInformation processInformation, uint processInformationLength, out uint returnLength);

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        public IntPtr Reserved1;
        public IntPtr PebBaseAddress;
        public IntPtr Reserved2_0;
        public IntPtr Reserved2_1;
        public IntPtr UniqueProcessId;
        public IntPtr Reserved3;
    }
}
