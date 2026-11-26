using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace CherryDesk.Battery;

// Reads Windows' existing Bluetooth battery property and XInput's battery state.
// No radio discovery, audio streams, pairing changes, or vendor HID commands are issued.
internal sealed class WindowsBatteryReader : IBatteryReader
{
    public BatteryReadResult Read()
    {
        var devices = new List<DeviceBattery>();
        var errors = new List<string>();
        try { devices.AddRange(ReadBluetooth()); }
        catch (Exception ex) { errors.Add("蓝牙电量暂不可用"); App.Log(ex); }
        try { devices.AddRange(ReadXbox()); }
        catch (Exception ex) { errors.Add("手柄电量暂不可用"); App.Log(ex); }
        return new(devices, errors);
    }

    private static IEnumerable<DeviceBattery> ReadXbox()
    {
        for (uint index = 0; index < 4; index++)
        {
            // Some adapters answer battery queries even for empty slots.
            if (XInputGetState(index, out _) != 0 || XInputGetBatteryInformation(index, 0, out var info) != 0) continue;
            if (info.Type is not (2 or 3) || info.Level > 3) continue; // wired/unknown has no valid battery reading
            yield return new("xinput:" + index, $"Xbox 手柄 {index + 1}", "无线 · XInput", null, (BatteryLevel)info.Level);
        }
    }

    private static IEnumerable<DeviceBattery> ReadBluetooth()
    {
        var connected = ConnectedBluetoothDevices();
        if (connected.Count == 0) return Array.Empty<DeviceBattery>();
        var nodes = new List<BluetoothNode>();
        var set = SetupDiGetClassDevsW(IntPtr.Zero, null, IntPtr.Zero, 6); // PRESENT | ALLCLASSES
        if (set == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            for (uint index = 0; ; index++)
            {
                var info = new DeviceInfo { Size = (uint)Marshal.SizeOf<DeviceInfo>() };
                if (!SetupDiEnumDeviceInfo(set, index, ref info))
                {
                    if (Marshal.GetLastWin32Error() != 259) throw new Win32Exception(Marshal.GetLastWin32Error());
                    break;
                }
                var id = new StringBuilder(1024);
                if (!SetupDiGetDeviceInstanceIdW(set, ref info, id, id.Capacity, out _)) continue;
                var instance = id.ToString();
                if (!instance.StartsWith("BTH", StringComparison.OrdinalIgnoreCase)) continue;
                var container = Property(set, ref info, ContainerKey, 13);
                var battery = Property(set, ref info, BatteryKey, 3); // DEVPROP_TYPE_BYTE
                int? percent = battery is { Length: >= 1 } && battery[0] <= 100 ? battery[0] : null;
                nodes.Add(new(instance, container is { Length: 16 } ? new Guid(container).ToString() : null, percent));
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        return MatchBluetooth(nodes, connected);
    }

    // Match by Bluetooth address / Windows container, never by a possibly duplicated device name.
    internal static IReadOnlyList<DeviceBattery> MatchBluetooth(IReadOnlyList<BluetoothNode> nodes, IReadOnlyDictionary<string, string> connected)
    {
        var result = new List<DeviceBattery>();
        foreach (var device in connected)
        {
            var direct = nodes.Where(node => node.Instance.Contains(device.Key, StringComparison.OrdinalIgnoreCase)).ToArray();
            var containers = direct.Where(node => node.Container is not null).Select(node => node.Container).ToHashSet();
            var reading = direct.Concat(nodes.Where(node => node.Container is not null && containers.Contains(node.Container)))
                .FirstOrDefault(node => node.Percent is >= 0 and <= 100);
            if (reading is not null) result.Add(new("bluetooth:" + device.Key, device.Value, "蓝牙 · 已连接", reading.Percent));
        }
        return result;
    }

    internal sealed record BluetoothNode(string Instance, string? Container, int? Percent);

    private static Dictionary<string, string> ConnectedBluetoothDevices()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var radioSearch = new RadioSearch { Size = (uint)Marshal.SizeOf<RadioSearch>() };
        var radioFind = BluetoothFindFirstRadio(ref radioSearch, out var radio);
        if (radioFind == IntPtr.Zero)
        {
            var error = Marshal.GetLastWin32Error();
            if (error is not (0 or 259 or 1168)) throw new Win32Exception(error);
            return result;
        }
        CloseHandle(radio);
        BluetoothFindRadioClose(radioFind);
        var search = new BluetoothSearch { Size = (uint)Marshal.SizeOf<BluetoothSearch>(), ReturnConnected = true };
        var info = new BluetoothInfo { Size = (uint)Marshal.SizeOf<BluetoothInfo>(), Name = "" };
        var handle = BluetoothFindFirstDevice(ref search, ref info);
        if (handle == IntPtr.Zero)
        {
            var error = Marshal.GetLastWin32Error();
            if (error is not (0 or 259 or 1168)) throw new Win32Exception(error);
            return result;
        }
        try
        {
            do
            {
                var isGamepad = (info.Class & 0x1F00) == 0x500 && (info.Class & 0x3C) is 4 or 8;
                // XInput owns gamepads, avoiding a second Bluetooth entry for the same controller.
                if (info.Connected && !isGamepad) result[info.Address.ToString("X12")] = string.IsNullOrWhiteSpace(info.Name) ? "蓝牙设备" : info.Name;
                info.Size = (uint)Marshal.SizeOf<BluetoothInfo>();
            } while (BluetoothFindNextDevice(handle, ref info));
            var error = Marshal.GetLastWin32Error();
            if (error != 259) throw new Win32Exception(error);
        }
        finally { BluetoothFindDeviceClose(handle); }
        return result;
    }

    // Windows' Bluetooth battery property is not a documented compatibility contract.
    // Keep it isolated here; absent or differently typed values are never guessed.
    private static readonly PropertyKey BatteryKey = new(new Guid("104EA319-6EE2-4701-BD47-8DDBF425BBE5"), 2);
    private static readonly PropertyKey ContainerKey = new(new Guid("8C7ED206-3F8A-4827-B3AB-AE9E1FAEFC6C"), 2);
    private static byte[]? Property(IntPtr set, ref DeviceInfo info, PropertyKey key, uint expectedType)
    {
        var buffer = new byte[256];
        return SetupDiGetDevicePropertyW(set, ref info, ref key, out var type, buffer, (uint)buffer.Length, out var size, 0)
            && type == expectedType && size <= buffer.Length ? buffer[..(int)size] : null;
    }

    [StructLayout(LayoutKind.Sequential)] private struct PropertyKey(Guid format, uint id) { public Guid Format = format; public uint Id = id; }
    [StructLayout(LayoutKind.Sequential)] private struct DeviceInfo { public uint Size; public Guid ClassGuid; public uint DevInst; public UIntPtr Reserved; }
    [StructLayout(LayoutKind.Sequential)] private struct XboxBattery { public byte Type, Level; }
    [StructLayout(LayoutKind.Sequential)] private struct XboxState { public uint Packet; public ushort Buttons; public byte LeftTrigger, RightTrigger; public short LX, LY, RX, RY; }
    [StructLayout(LayoutKind.Sequential)] private struct RadioSearch { public uint Size; }
    [StructLayout(LayoutKind.Sequential)] private struct BluetoothSearch
    {
        public uint Size;
        [MarshalAs(UnmanagedType.Bool)] public bool ReturnAuthenticated;
        [MarshalAs(UnmanagedType.Bool)] public bool ReturnRemembered;
        [MarshalAs(UnmanagedType.Bool)] public bool ReturnUnknown;
        [MarshalAs(UnmanagedType.Bool)] public bool ReturnConnected;
        [MarshalAs(UnmanagedType.Bool)] public bool Inquiry;
        public byte Timeout;
        public IntPtr Radio;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct BluetoothInfo
    {
        public uint Size;
        public ulong Address;
        public uint Class;
        [MarshalAs(UnmanagedType.Bool)] public bool Connected;
        [MarshalAs(UnmanagedType.Bool)] public bool Remembered;
        [MarshalAs(UnmanagedType.Bool)] public bool Authenticated;
        public SystemTime LastSeen, LastUsed;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 248)] public string Name;
    }
    [StructLayout(LayoutKind.Sequential)] private struct SystemTime { public ushort Year, Month, DayOfWeek, Day, Hour, Minute, Second, Milliseconds; }

    [DllImport("xinput1_4.dll")] private static extern uint XInputGetState(uint index, out XboxState state);
    [DllImport("xinput1_4.dll")] private static extern uint XInputGetBatteryInformation(uint index, byte type, out XboxBattery battery);
    [DllImport("bthprops.cpl", SetLastError = true)] private static extern IntPtr BluetoothFindFirstDevice(ref BluetoothSearch search, ref BluetoothInfo info);
    [DllImport("bthprops.cpl", SetLastError = true)] private static extern IntPtr BluetoothFindFirstRadio(ref RadioSearch search, out IntPtr radio);
    [DllImport("bthprops.cpl")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool BluetoothFindRadioClose(IntPtr handle);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("bthprops.cpl", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool BluetoothFindNextDevice(IntPtr handle, ref BluetoothInfo info);
    [DllImport("bthprops.cpl")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool BluetoothFindDeviceClose(IntPtr handle);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr SetupDiGetClassDevsW(IntPtr guid, string? enumerator, IntPtr parent, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref DeviceInfo info);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiGetDeviceInstanceIdW(IntPtr set, ref DeviceInfo info, StringBuilder id, int size, out uint required);
    [DllImport("setupapi.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiGetDevicePropertyW(IntPtr set, ref DeviceInfo info, ref PropertyKey key, out uint type, byte[] buffer, uint size, out uint required, uint flags);
    [DllImport("setupapi.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
}
