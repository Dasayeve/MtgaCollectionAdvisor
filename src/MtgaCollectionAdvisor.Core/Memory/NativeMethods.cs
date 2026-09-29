using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace MtgaCollectionAdvisor.Core.Memory;

[SupportedOSPlatform("windows")]
internal static class NativeMethods
{
    public const int PROCESS_QUERY_INFORMATION = 0x0400;
    public const int PROCESS_VM_READ = 0x0010;

    public const int MEM_COMMIT = 0x1000;
    public const int MEM_PRIVATE = 0x20000;
    public const int MEM_MAPPED = 0x40000;

    public const int PAGE_NOACCESS = 0x01;
    public const int PAGE_GUARD = 0x100;
    public const int PAGE_READWRITE = 0x04;
    public const int PAGE_WRITECOPY = 0x08;
    public const int PAGE_EXECUTE_READWRITE = 0x40;
    public const int PAGE_EXECUTE_WRITECOPY = 0x80;

    [StructLayout(LayoutKind.Sequential)]
    public struct MEMORY_BASIC_INFORMATION
    {
        public IntPtr BaseAddress;
        public IntPtr AllocationBase;
        public uint AllocationProtect;
        public int __alignment1;
        public IntPtr RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
        public int __alignment2;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(int dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ReadProcessMemory(
        IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, IntPtr dwSize, out IntPtr lpNumberOfBytesRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr VirtualQueryEx(
        IntPtr hProcess, IntPtr lpAddress, out MEMORY_BASIC_INFORMATION lpBuffer, IntPtr dwLength);
}
