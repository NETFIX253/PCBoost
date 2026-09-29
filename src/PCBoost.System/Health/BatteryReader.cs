using System.Runtime.InteropServices;
using System.Text;
using PCBoost.Core.Common;
using PCBoost.Core.Models.Health;
using PCBoost.Platform.Interop;

namespace PCBoost.Platform.Health;

/// <summary>Capacité d'origine, capacité actuelle et cycles des batteries, lus auprès de leur pilote.</summary>
internal static unsafe class BatteryReader
{
    private const int MaxBatteries = 8;

    public static OperationResult<IReadOnlyList<BatteryInfo>> Read()
    {
        var guid = BatteryInterop.GuidDeviceBattery;
        nint devices;
        try
        {
            devices = BatteryInterop.SetupDiGetClassDevsW(in guid, 0, 0, BatteryInterop.DIGCF_PRESENT | BatteryInterop.DIGCF_DEVICEINTERFACE);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return OperationResult<IReadOnlyList<BatteryInfo>>.Fail(OperationErrorKind.NotSupported);
        }
        if (devices == 0 || devices == -1) return OperationResult<IReadOnlyList<BatteryInfo>>.Fail(OperationErrorKind.NotSupported);

        var batteries = new List<BatteryInfo>();
        try
        {
            for (uint index = 0; index < MaxBatteries; index++)
            {
                var data = new BatteryInterop.SP_DEVICE_INTERFACE_DATA { cbSize = (uint)sizeof(BatteryInterop.SP_DEVICE_INTERFACE_DATA) };
                if (!BatteryInterop.SetupDiEnumDeviceInterfaces(devices, 0, in guid, index, ref data)) break; // ERROR_NO_MORE_ITEMS
                if (DevicePath(devices, ref data) is not { } path) continue;
                if (ReadBattery(path) is { } battery) batteries.Add(battery);
            }
        }
        finally
        {
            BatteryInterop.SetupDiDestroyDeviceInfoList(devices);
        }
        return OperationResult<IReadOnlyList<BatteryInfo>>.Ok(batteries);
    }

    private static string? DevicePath(nint devices, ref BatteryInterop.SP_DEVICE_INTERFACE_DATA data)
    {
        BatteryInterop.SetupDiGetDeviceInterfaceDetailW(devices, ref data, 0, 0, out var required, 0);
        if (required is < 8 or > 32 * 1024) return null;
        var buffer = Marshal.AllocHGlobal((int)required);
        try
        {
            // SP_DEVICE_INTERFACE_DETAIL_DATA_W : cbSize vaut 8 en 64 bits (6 en 32 bits), puis le chemin UTF-16.
            Marshal.WriteInt32(buffer, IntPtr.Size == 8 ? 8 : 6);
            if (!BatteryInterop.SetupDiGetDeviceInterfaceDetailW(devices, ref data, buffer, required, out _, 0)) return null;
            return Marshal.PtrToStringUni(buffer + 4);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static BatteryInfo? ReadBattery(string path)
    {
        using var handle = BatteryInterop.CreateFileW(path, BatteryInterop.GENERIC_READ | BatteryInterop.GENERIC_WRITE,
            BatteryInterop.FILE_SHARE_READ | BatteryInterop.FILE_SHARE_WRITE, 0, BatteryInterop.OPEN_EXISTING, BatteryInterop.FILE_ATTRIBUTE_NORMAL, 0);
        if (handle.IsInvalid) return null;

        uint wait = 0, tag = 0;
        if (!BatteryInterop.DeviceIoControl(handle, BatteryInterop.IOCTL_BATTERY_QUERY_TAG, &wait, sizeof(uint), &tag, sizeof(uint), out _, 0) || tag == 0)
            return null;

        var query = new BatteryInterop.BATTERY_QUERY_INFORMATION { BatteryTag = tag, InformationLevel = BatteryInterop.InformationLevel.BatteryInformation };
        BatteryInterop.BATTERY_INFORMATION info;
        if (!BatteryInterop.DeviceIoControl(handle, BatteryInterop.IOCTL_BATTERY_QUERY_INFORMATION, &query, (uint)sizeof(BatteryInterop.BATTERY_QUERY_INFORMATION),
                &info, (uint)sizeof(BatteryInterop.BATTERY_INFORMATION), out _, 0))
            return null;

        var relative = (info.Capabilities & BatteryInterop.BATTERY_CAPACITY_RELATIVE) != 0;
        return new BatteryInfo(
            QueryString(handle, tag, BatteryInterop.InformationLevel.BatteryDeviceName),
            QueryString(handle, tag, BatteryInterop.InformationLevel.BatteryManufactureName),
            Chemistry(info.Chemistry),
            Capacity(info.DesignedCapacity),
            Capacity(info.FullChargedCapacity),
            info.CycleCount is > 0 and < 100_000 ? (int)info.CycleCount : null,
            relative);
    }

    /// <summary>0 et 0xFFFFFFFF (BATTERY_UNKNOWN_CAPACITY) signifient « inconnu ».</summary>
    private static long? Capacity(uint value) => value is 0 or uint.MaxValue ? null : value;

    private static string? Chemistry(byte* chemistry)
    {
        var bytes = new ReadOnlySpan<byte>(chemistry, 4);
        var text = Encoding.ASCII.GetString(bytes).TrimEnd('\0', ' ');
        return text.Length == 0 || text.Any(c => c < 0x20 || c > 0x7E) ? null : text;
    }

    private static string? QueryString(Microsoft.Win32.SafeHandles.SafeFileHandle handle, uint tag, BatteryInterop.InformationLevel level)
    {
        var query = new BatteryInterop.BATTERY_QUERY_INFORMATION { BatteryTag = tag, InformationLevel = level };
        const int capacity = 256;
        var buffer = stackalloc char[capacity];
        if (!BatteryInterop.DeviceIoControl(handle, BatteryInterop.IOCTL_BATTERY_QUERY_INFORMATION, &query, (uint)sizeof(BatteryInterop.BATTERY_QUERY_INFORMATION),
                buffer, capacity * sizeof(char), out var returned, 0) || returned < 2)
            return null;
        return HealthEventParsers.Clean(new string(buffer, 0, (int)Math.Min(returned / sizeof(char), capacity)).TrimEnd('\0'));
    }
}
