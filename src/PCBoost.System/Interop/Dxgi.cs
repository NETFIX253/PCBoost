using System.Runtime.InteropServices;

namespace PCBoost.Platform.Interop;

/// <summary>
/// DXGI par appels directs dans la table virtuelle (pas d'interop COM intégrée).
/// Indices : IUnknown (0 QueryInterface, 1 AddRef, 2 Release) ; IDXGIObject (3 SetPrivateData, 4 SetPrivateDataInterface,
/// 5 GetPrivateData, 6 GetParent) ; IDXGIFactory (7 EnumAdapters, 8 MakeWindowAssociation, 9 GetWindowAssociation,
/// 10 CreateSwapChain, 11 CreateSoftwareAdapter) ; IDXGIFactory1 (12 EnumAdapters1, 13 IsCurrent) ;
/// IDXGIAdapter (7 EnumOutputs, 8 GetDesc, 9 CheckInterfaceSupport) ; IDXGIAdapter1 (10 GetDesc1).
/// </summary>
internal static unsafe partial class Dxgi
{
    public static readonly Guid IID_IDXGIFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");

    public const int DXGI_ERROR_NOT_FOUND = unchecked((int)0x887A0002);
    public const uint DXGI_ADAPTER_FLAG_SOFTWARE = 2;

    private const int SlotRelease = 2;
    private const int SlotFactoryEnumAdapters1 = 12;
    private const int SlotAdapterGetDesc1 = 10;

    /// <summary>DXGI_ADAPTER_DESC1 (312 octets en x64). LUID garde un alignement de 4 octets.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct DXGI_ADAPTER_DESC1
    {
        public fixed char Description[128];
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public nuint DedicatedVideoMemory;
        public nuint DedicatedSystemMemory;
        public nuint SharedSystemMemory;
        public uint AdapterLuidLowPart;
        public int AdapterLuidHighPart;
        public uint Flags;
    }

    [LibraryImport("dxgi.dll")]
    public static partial int CreateDXGIFactory1(Guid* riid, void** ppFactory);

    public static int FactoryEnumAdapters1(void* factory, uint index, void** adapter)
    {
        var vtable = *(void***)factory;
        var fn = (delegate* unmanaged[Stdcall]<void*, uint, void**, int>)vtable[SlotFactoryEnumAdapters1];
        return fn(factory, index, adapter);
    }

    public static int AdapterGetDesc1(void* adapter, DXGI_ADAPTER_DESC1* desc)
    {
        var vtable = *(void***)adapter;
        var fn = (delegate* unmanaged[Stdcall]<void*, DXGI_ADAPTER_DESC1*, int>)vtable[SlotAdapterGetDesc1];
        return fn(adapter, desc);
    }

    public static uint Release(void* unknown)
    {
        if (unknown == null) return 0;
        var vtable = *(void***)unknown;
        var fn = (delegate* unmanaged[Stdcall]<void*, uint>)vtable[SlotRelease];
        return fn(unknown);
    }
}
