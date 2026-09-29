using PCBoost.Platform.Interop;

namespace PCBoost.Platform;

internal sealed record DxgiAdapter(string Description, uint VendorId, uint DeviceId, long DedicatedVideoMemory, long SharedSystemMemory, bool IsSoftware);

/// <summary>Énumère les cartes graphiques via DXGI 1.1 (CreateDXGIFactory1 → EnumAdapters1 → GetDesc1).</summary>
internal static unsafe class DxgiAdapterReader
{
    public static List<DxgiAdapter> Enumerate()
    {
        var adapters = new List<DxgiAdapter>();
        void* factory = null;
        var iid = Dxgi.IID_IDXGIFactory1;
        if (Dxgi.CreateDXGIFactory1(&iid, &factory) < 0 || factory == null)
            return adapters;

        try
        {
            for (uint index = 0; index < 16; index++)
            {
                void* adapter = null;
                var hr = Dxgi.FactoryEnumAdapters1(factory, index, &adapter);
                if (hr == Dxgi.DXGI_ERROR_NOT_FOUND) break;
                if (hr < 0 || adapter == null) continue;
                try
                {
                    Dxgi.DXGI_ADAPTER_DESC1 desc;
                    if (Dxgi.AdapterGetDesc1(adapter, &desc) < 0) continue;
                    var description = NativeStrings.FromFixedBuffer(desc.Description, 128).Trim();
                    adapters.Add(new DxgiAdapter(
                        description,
                        desc.VendorId,
                        desc.DeviceId,
                        ToInt64(desc.DedicatedVideoMemory),
                        ToInt64(desc.SharedSystemMemory),
                        (desc.Flags & Dxgi.DXGI_ADAPTER_FLAG_SOFTWARE) != 0 || IsBasicRenderDriver(desc.VendorId, desc.DeviceId)));
                }
                finally
                {
                    Dxgi.Release(adapter);
                }
            }
        }
        finally
        {
            Dxgi.Release(factory);
        }
        return adapters;
    }

    private static long ToInt64(nuint value) => (ulong)value > long.MaxValue ? long.MaxValue : (long)value;

    /// <summary>« Microsoft Basic Render Driver » (WARP) : VEN_1414 DEV_008C.</summary>
    public static bool IsBasicRenderDriver(uint vendorId, uint deviceId)
        => vendorId == HardwareClassification.VendorMicrosoft && deviceId == 0x8C;
}
