using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using PCBoost.Platform.Interop;

namespace PCBoost.System.Tests;

/// <summary>
/// Tailles et décalages des structures natives, comparés aux valeurs du SDK Windows pour les processus 64 bits
/// (x64 et ARM64 partagent les règles d'alignement ; ces tests s'exécutent sur tout hôte 64 bits).
/// </summary>
public sealed class NativeLayoutTests
{
    private static bool Is64Bit => nint.Size == 8;

    [Fact]
    public void Kernel32_StructureSizes()
    {
        if (!Is64Bit) return;
        Assert.Equal(568, Unsafe.SizeOf<Kernel32.PROCESSENTRY32W>());
        Assert.Equal(48, Unsafe.SizeOf<Kernel32.IO_COUNTERS>());
        Assert.Equal(12, Unsafe.SizeOf<Kernel32.PROCESS_POWER_THROTTLING_STATE>());
        Assert.Equal(64, Unsafe.SizeOf<Kernel32.MEMORYSTATUSEX>());
        Assert.Equal(104, Unsafe.SizeOf<Kernel32.PERFORMANCE_INFORMATION>());
        Assert.Equal(12, Unsafe.SizeOf<Kernel32.SYSTEM_POWER_STATUS>());
        Assert.Equal(52, Unsafe.SizeOf<Kernel32.BY_HANDLE_FILE_INFORMATION>());
        Assert.Equal(44, (int)Marshal.OffsetOf<Kernel32.PROCESSENTRY32W>(nameof(Kernel32.PROCESSENTRY32W.szExeFile)));
        Assert.Equal(16, (int)Marshal.OffsetOf<Kernel32.PROCESSENTRY32W>(nameof(Kernel32.PROCESSENTRY32W.th32DefaultHeapID)));
        Assert.Equal(40, (int)Marshal.OffsetOf<Kernel32.BY_HANDLE_FILE_INFORMATION>(nameof(Kernel32.BY_HANDLE_FILE_INFORMATION.nNumberOfLinks)));
    }

    [Fact]
    public void WinTrust_StructureSizes()
    {
        if (!Is64Bit) return;
        Assert.Equal(88, Unsafe.SizeOf<WinTrust.WINTRUST_DATA>());
        Assert.Equal(32, Unsafe.SizeOf<WinTrust.WINTRUST_FILE_INFO>());
        Assert.Equal(72, Unsafe.SizeOf<WinTrust.WINTRUST_CATALOG_INFO>());
        Assert.Equal(524, Unsafe.SizeOf<WinTrust.CATALOG_INFO>());
        Assert.Equal(40, (int)Marshal.OffsetOf<WinTrust.WINTRUST_DATA>(nameof(WinTrust.WINTRUST_DATA.pUnion)));
        Assert.Equal(56, (int)Marshal.OffsetOf<WinTrust.WINTRUST_DATA>(nameof(WinTrust.WINTRUST_DATA.hWVTStateData)));
        Assert.Equal(64, (int)Marshal.OffsetOf<WinTrust.WINTRUST_CATALOG_INFO>(nameof(WinTrust.WINTRUST_CATALOG_INFO.hCatAdmin)));
    }

    [Fact]
    public void Pdh_Dxgi_Shell_User32_StructureSizes()
    {
        if (!Is64Bit) return;
        Assert.Equal(16, Unsafe.SizeOf<Pdh.PDH_FMT_COUNTERVALUE>());
        Assert.Equal(24, Unsafe.SizeOf<Pdh.PDH_FMT_COUNTERVALUE_ITEM_W>());
        Assert.Equal(312, Unsafe.SizeOf<Dxgi.DXGI_ADAPTER_DESC1>());
        Assert.Equal(304, (int)Marshal.OffsetOf<Dxgi.DXGI_ADAPTER_DESC1>(nameof(Dxgi.DXGI_ADAPTER_DESC1.Flags)));
        Assert.Equal(272, (int)Marshal.OffsetOf<Dxgi.DXGI_ADAPTER_DESC1>(nameof(Dxgi.DXGI_ADAPTER_DESC1.DedicatedVideoMemory)));
        Assert.Equal(24, Unsafe.SizeOf<Shell32.SHQUERYRBINFO>());
        Assert.Equal(20, Unsafe.SizeOf<Shell32.SHQUERYRBINFO32>());
        // SHFILEOPSTRUCTW en 64 bits : hwnd 0, wFunc 8, pFrom 16, pTo 24, fFlags 32, fAnyOperationsAborted 36, hNameMappings 40, titre 48.
        Assert.Equal(56, Unsafe.SizeOf<Shell32.SHFILEOPSTRUCTW>());
        Assert.Equal(32, (int)Marshal.OffsetOf<Shell32.SHFILEOPSTRUCTW>(nameof(Shell32.SHFILEOPSTRUCTW.fFlags)));
        Assert.Equal(36, (int)Marshal.OffsetOf<Shell32.SHFILEOPSTRUCTW>(nameof(Shell32.SHFILEOPSTRUCTW.fAnyOperationsAborted)));
        Assert.Equal(40, Unsafe.SizeOf<User32.MONITORINFO>());
        Assert.Equal(8, Unsafe.SizeOf<User32.ANIMATIONINFO>());
    }
}
