using System.Runtime.InteropServices;

namespace PCBoost.Platform.Interop;

internal static unsafe partial class Advapi32
{
    public const uint TOKEN_QUERY = 0x0008;

    // TOKEN_INFORMATION_CLASS
    public const int TokenUser = 1;
    public const int TokenElevation = 20;

    [StructLayout(LayoutKind.Sequential)]
    public struct TOKEN_ELEVATION
    {
        public uint TokenIsElevated;
    }

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool OpenProcessToken(nint processHandle, uint desiredAccess, out SafeKernelHandle tokenHandle);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool OpenProcessToken(SafeKernelHandle processHandle, uint desiredAccess, out SafeKernelHandle tokenHandle);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetTokenInformation(SafeKernelHandle tokenHandle, int tokenInformationClass, void* tokenInformation, uint tokenInformationLength, out uint returnLength);
}
