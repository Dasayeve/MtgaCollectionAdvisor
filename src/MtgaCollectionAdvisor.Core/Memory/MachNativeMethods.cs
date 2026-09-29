using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace MtgaCollectionAdvisor.Core.Memory;

/// <summary>
/// The Mach calls that read another process's memory on macOS. <c>task_for_pid</c> only
/// succeeds when this binary is signed with the <c>com.apple.security.cs.debugger</c>
/// entitlement (see Entitlements.plist in the Web project).
/// </summary>
[SupportedOSPlatform("macos")]
internal static class MachNativeMethods
{
    private const string LibSystem = "libSystem.dylib";

    public const int KERN_SUCCESS = 0;

    /// <summary>VM_REGION_BASIC_INFO_64: an int[9] whose first element is the protection.</summary>
    public const int VM_REGION_BASIC_INFO_64 = 9;
    public const int VM_REGION_BASIC_INFO_COUNT_64 = 9;

    [DllImport(LibSystem)]
    public static extern uint task_self_trap();

    [DllImport(LibSystem)]
    public static extern int task_for_pid(uint target, int pid, out uint task);

    [DllImport(LibSystem)]
    public static extern int mach_vm_region(
        uint task, ref ulong address, out ulong size, int flavor, [Out] int[] info, ref uint infoCnt, out uint objectName);

    /// <summary><paramref name="data"/> is the destination; the marshaller pins it and passes its address.</summary>
    [DllImport(LibSystem)]
    public static extern int mach_vm_read_overwrite(uint task, ulong address, ulong size, ref byte data, out ulong outsize);

    [DllImport(LibSystem)]
    public static extern int mach_port_deallocate(uint task, uint name);
}
