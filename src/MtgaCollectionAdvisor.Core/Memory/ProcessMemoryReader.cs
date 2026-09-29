using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace MtgaCollectionAdvisor.Core.Memory;

public sealed record MemoryRegion(long BaseAddress, long Size);

/// <summary>
/// Read-only access to another process's memory (the running MTGA client), used to
/// locate the collection table that the current client no longer writes to Player.log.
/// Windows reads through a process handle; macOS through the Mach task port, which needs
/// this app signed with the debugger entitlement.
/// </summary>
public sealed class ProcessMemoryReader : IDisposable
{
    /// <summary>The Win32 process handle, or the Mach task port on macOS.</summary>
    private readonly IntPtr _handle;

    public int ProcessId { get; }

    private ProcessMemoryReader(IntPtr handle, int processId)
    {
        _handle = handle;
        ProcessId = processId;
    }

    public static ProcessMemoryReader Open(string processName)
    {
        var process = Process.GetProcessesByName(processName).FirstOrDefault()
            ?? throw new MemoryScanException(
                $"The \"{processName}\" process is not running. Start MTG Arena before scanning.");

        if (OperatingSystem.IsWindows()) return OpenWindows(process);
        if (OperatingSystem.IsMacOS()) return OpenMac(process);
        throw new PlatformNotSupportedException("Reading MTG Arena's memory is supported on Windows and macOS.");
    }

    /// <summary>
    /// Committed, writable regions only - the collection lives on the heap, and skipping
    /// read-only/image pages cuts the amount scanned by an order of magnitude.
    /// </summary>
    public IEnumerable<MemoryRegion> EnumerateWritableRegions()
    {
        if (OperatingSystem.IsWindows()) return EnumerateWritableRegionsWindows();
        if (OperatingSystem.IsMacOS()) return EnumerateWritableRegionsMac();
        throw new PlatformNotSupportedException();
    }

    public bool TryRead(long address, byte[] buffer, int count)
    {
        if (OperatingSystem.IsWindows()) return TryReadWindows(address, buffer, count);
        if (OperatingSystem.IsMacOS()) return TryReadMac(address, buffer, count);
        throw new PlatformNotSupportedException();
    }

    /// <summary>
    /// Reads whatever is readable at <paramref name="address"/>, returning how many bytes
    /// were actually retrieved (0 when the range is not readable at all).
    /// </summary>
    public int ReadPartial(long address, byte[] buffer, int count)
    {
        if (OperatingSystem.IsWindows()) return ReadPartialWindows(address, buffer, count);
        if (OperatingSystem.IsMacOS()) return ReadPartialMac(address, buffer, count);
        throw new PlatformNotSupportedException();
    }

    public void Dispose()
    {
        if (OperatingSystem.IsWindows()) DisposeWindows();
        else if (OperatingSystem.IsMacOS()) DisposeMac();
    }

    // ---- Windows ----

    [SupportedOSPlatform("windows")]
    private static ProcessMemoryReader OpenWindows(Process process)
    {
        var handle = NativeMethods.OpenProcess(
            NativeMethods.PROCESS_QUERY_INFORMATION | NativeMethods.PROCESS_VM_READ, false, process.Id);

        if (handle == IntPtr.Zero)
        {
            var error = Marshal.GetLastWin32Error();
            throw new MemoryScanException(
                "Could not open the MTG Arena process for reading " +
                $"({new Win32Exception(error).Message}). Try running this app as Administrator.");
        }

        return new ProcessMemoryReader(handle, process.Id);
    }

    [SupportedOSPlatform("windows")]
    private IEnumerable<MemoryRegion> EnumerateWritableRegionsWindows()
    {
        var address = 0L;
        var infoSize = (IntPtr)Marshal.SizeOf<NativeMethods.MEMORY_BASIC_INFORMATION>();
        const long maxUserAddress = 0x7FFFFFFFFFFF;

        while (address < maxUserAddress)
        {
            if (NativeMethods.VirtualQueryEx(_handle, (IntPtr)address, out var info, infoSize) == IntPtr.Zero)
            {
                break;
            }

            var regionSize = (long)info.RegionSize;
            if (regionSize <= 0) break;

            var isCommitted = info.State == NativeMethods.MEM_COMMIT;
            var isScannableType = info.Type is NativeMethods.MEM_PRIVATE or NativeMethods.MEM_MAPPED;
            var protect = info.Protect;
            var isGuarded = (protect & NativeMethods.PAGE_GUARD) != 0 || (protect & NativeMethods.PAGE_NOACCESS) != 0;
            var isWritable = (protect & (NativeMethods.PAGE_READWRITE | NativeMethods.PAGE_WRITECOPY |
                                         NativeMethods.PAGE_EXECUTE_READWRITE | NativeMethods.PAGE_EXECUTE_WRITECOPY)) != 0;

            if (isCommitted && isScannableType && isWritable && !isGuarded)
            {
                yield return new MemoryRegion((long)info.BaseAddress, regionSize);
            }

            address = (long)info.BaseAddress + regionSize;
        }
    }

    [SupportedOSPlatform("windows")]
    private bool TryReadWindows(long address, byte[] buffer, int count)
    {
        var ok = NativeMethods.ReadProcessMemory(_handle, (IntPtr)address, buffer, (IntPtr)count, out var read);
        return ok && (long)read == count;
    }

    [SupportedOSPlatform("windows")]
    private int ReadPartialWindows(long address, byte[] buffer, int count)
    {
        NativeMethods.ReadProcessMemory(_handle, (IntPtr)address, buffer, (IntPtr)count, out var read);
        return (int)Math.Clamp((long)read, 0, count);
    }

    [SupportedOSPlatform("windows")]
    private void DisposeWindows()
    {
        if (_handle != IntPtr.Zero) NativeMethods.CloseHandle(_handle);
    }

    // ---- macOS ----

    private uint TaskPort => (uint)_handle;

    [SupportedOSPlatform("macos")]
    private static ProcessMemoryReader OpenMac(Process process)
    {
        var result = MachNativeMethods.task_for_pid(MachNativeMethods.task_self_trap(), process.Id, out var task);

        if (result != MachNativeMethods.KERN_SUCCESS)
        {
            throw new MemoryScanException(
                $"Could not open the MTG Arena process for reading (Mach error {result}). The likely cause is " +
                "that this account has no developer tools access: in Terminal, add it to the _developer group " +
                "(sudo dseditgroup -o edit -a \"$USER\" -t user _developer) or run sudo DevToolsSecurity -enable, " +
                "then log out and back in. The other possible cause is that the app is not code-signed with the " +
                "com.apple.security.cs.debugger entitlement.");
        }

        return new ProcessMemoryReader((IntPtr)task, process.Id);
    }

    private const int VmProtRead = 1;  // VM_PROT_READ; here so the predicate is testable anywhere
    private const int VmProtWrite = 2;

    /// <summary>Readable and writable: the heap, where the collection lives.</summary>
    internal static bool IsScannableMacRegion(int protection) =>
        (protection & VmProtRead) != 0 && (protection & VmProtWrite) != 0;

    [SupportedOSPlatform("macos")]
    private IEnumerable<MemoryRegion> EnumerateWritableRegionsMac()
    {
        ulong address = 0;

        while (TryQueryMacRegion(ref address, out var size, out var protection))
        {
            if (IsScannableMacRegion(protection))
            {
                yield return new MemoryRegion((long)address, (long)size);
            }

            if (address + size < address) break;
            address += size;
        }
    }

    /// <summary>
    /// The region at or after <paramref name="address"/>, which is moved to its start;
    /// false past the last region.
    /// </summary>
    [SupportedOSPlatform("macos")]
    private bool TryQueryMacRegion(ref ulong address, out ulong size, out int protection)
    {
        var info = new int[MachNativeMethods.VM_REGION_BASIC_INFO_COUNT_64];
        uint infoCount = MachNativeMethods.VM_REGION_BASIC_INFO_COUNT_64;

        var result = MachNativeMethods.mach_vm_region(
            TaskPort, ref address, out size, MachNativeMethods.VM_REGION_BASIC_INFO_64,
            info, ref infoCount, out _);

        protection = info[0];
        return result == MachNativeMethods.KERN_SUCCESS && size > 0;
    }

    [SupportedOSPlatform("macos")]
    private bool TryReadMac(long address, byte[] buffer, int count) =>
        ReadMac(address, buffer, 0, count) == count;

    /// <summary>
    /// A Mach read fails whole when any page in the range is unreadable, so on failure this
    /// reads page by page and returns the bytes before the first unreadable page, as
    /// ReadProcessMemory reports them on Windows.
    /// </summary>
    [SupportedOSPlatform("macos")]
    private int ReadPartialMac(long address, byte[] buffer, int count)
    {
        count = Math.Clamp(count, 0, buffer.Length);
        if (ReadMac(address, buffer, 0, count) == count) return count;

        var pageSize = Environment.SystemPageSize;
        var read = 0;
        while (read < count)
        {
            var current = address + read;
            var toPageEnd = pageSize - (int)(current % pageSize);
            var chunk = Math.Min(toPageEnd, count - read);
            if (ReadMac(current, buffer, read, chunk) != chunk) break;
            read += chunk;
        }

        return read;
    }

    /// <summary>Bytes read into <paramref name="buffer"/> at <paramref name="offset"/>; 0 on failure.</summary>
    [SupportedOSPlatform("macos")]
    private int ReadMac(long address, byte[] buffer, int offset, int count)
    {
        if (count <= 0 || offset < 0 || offset + count > buffer.Length) return 0;

        var result = MachNativeMethods.mach_vm_read_overwrite(
            TaskPort, (ulong)address, (ulong)count, ref buffer[offset], out var read);
        return result == MachNativeMethods.KERN_SUCCESS && read == (ulong)count ? count : 0;
    }

    [SupportedOSPlatform("macos")]
    private void DisposeMac()
    {
        if (_handle != IntPtr.Zero) MachNativeMethods.mach_port_deallocate(MachNativeMethods.task_self_trap(), TaskPort);
    }
}

public sealed class MemoryScanException(string message) : Exception(message);
