using Microsoft.Win32.SafeHandles;

namespace PCBoost.Platform.Interop;

/// <summary>Handle noyau générique (processus, jeton, instantané Toolhelp) libéré par CloseHandle.</summary>
internal sealed class SafeKernelHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public SafeKernelHandle()
        : base(ownsHandle: true)
    {
    }

    protected override bool ReleaseHandle() => Kernel32.CloseHandle(handle);
}

/// <summary>Requête PDH libérée par PdhCloseQuery (les compteurs de la requête sont libérés avec elle).</summary>
internal sealed class SafePdhQueryHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public SafePdhQueryHandle()
        : base(ownsHandle: true)
    {
    }

    protected override bool ReleaseHandle() => Pdh.PdhCloseQuery(handle) == 0;
}
