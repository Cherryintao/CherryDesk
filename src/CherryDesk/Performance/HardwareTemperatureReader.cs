using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;

namespace CherryDesk.Performance;

internal sealed record CpuTemperatureSensor(string Name, double Value);

internal static class TemperatureValues
{
    // Zero is commonly an unsupported-sensor sentinel. Never display it as a real reading.
    public static double? Valid(double value) => double.IsFinite(value) && value > 0 && value <= 150 ? value : null;
    public static double? FromGpuTenths(uint value) => Valid(value / 10.0);
    public static string Text(double? value) => value is { } number && Valid(number) is { } valid ? $"{valid:F0}°C" : "--";

    public static double? SelectCpu(IEnumerable<CpuTemperatureSensor> sensors)
    {
        var valid = sensors.Where(sensor => Valid(sensor.Value) is not null).ToArray();
        var package = valid.Where(sensor => sensor.Name.Contains("Package", StringComparison.OrdinalIgnoreCase)
            || sensor.Name.Contains("Tctl/Tdie", StringComparison.OrdinalIgnoreCase)
            || sensor.Name.Contains("Die (average)", StringComparison.OrdinalIgnoreCase)).ToArray();
        return (package.Length > 0 ? package : valid).Select(sensor => (double?)sensor.Value).Max();
    }
}

// Uses the installed display driver. CPU sensors are optional: only an already-running
// LibreHardwareMonitor/OpenHardwareMonitor WMI provider is read. No driver is installed/loaded.
internal sealed class HardwareTemperatureReader
{
    private DateTimeOffset nextRead;
    private DateTimeOffset nextCpuProbe;
    private string? cpuNamespace;
    public double? Cpu { get; private set; }
    public string? CpuSource { get; private set; }
    public IReadOnlyDictionary<string, double> Gpus { get; private set; } = new Dictionary<string, double>();

    public void Update(IReadOnlyList<GpuAdapter> adapters, DateTimeOffset now)
    {
        if (now < nextRead) return;
        nextRead = now.AddSeconds(5);
        var temperatures = new Dictionary<string, double>();
        foreach (var adapter in adapters)
        {
            try { if (ReadGpu(adapter.Id) is { } temperature) temperatures[adapter.Id] = temperature; }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or ExternalException) { }
        }
        Gpus = temperatures;
        if (cpuNamespace is null && now < nextCpuProbe) return;
        Cpu = null;
        CpuSource = null;
        var namespaces = cpuNamespace is null ? new[] { "LibreHardwareMonitor", "OpenHardwareMonitor" } : new[] { cpuNamespace };
        foreach (var name in namespaces)
        {
            try
            {
                var temperature = ReadCpu(name);
                if (temperature is null) continue;
                Cpu = temperature;
                CpuSource = name;
                cpuNamespace = name;
                return;
            }
            catch (Exception) { /* An optional sensor provider must never stop CPU/GPU usage sampling. */ }
        }
        cpuNamespace = null;
        nextCpuProbe = now.AddSeconds(30);
    }

    private static double? ReadCpu(string provider)
    {
        object? locator = null, service = null, hardware = null, sensors = null;
        try
        {
            var type = Type.GetTypeFromProgID("WbemScripting.SWbemLocator");
            if (type is null) return null;
            locator = Activator.CreateInstance(type);
            service = ((dynamic)locator!).ConnectServer(".", @"root\" + provider);
            hardware = ((dynamic)service).ExecQuery("SELECT Identifier, HardwareType FROM Hardware", "WQL", 0);
            var parents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var count = (int)((dynamic)hardware).Count;
            for (var index = 0; index < count; index++)
            {
                object? item = null;
                try
                {
                    item = ((dynamic)hardware).ItemIndex(index);
                    if (string.Equals((string)((dynamic)item).HardwareType, "Cpu", StringComparison.OrdinalIgnoreCase))
                        parents.Add((string)((dynamic)item).Identifier);
                }
                finally { Release(item); }
            }
            if (parents.Count == 0) return null;
            sensors = ((dynamic)service).ExecQuery("SELECT Name, Value, Parent FROM Sensor WHERE SensorType='Temperature'", "WQL", 0);
            var readings = new List<CpuTemperatureSensor>();
            count = (int)((dynamic)sensors).Count;
            for (var index = 0; index < count; index++)
            {
                object? item = null;
                try
                {
                    item = ((dynamic)sensors).ItemIndex(index);
                    if (parents.Contains((string)((dynamic)item).Parent))
                        readings.Add(new((string)((dynamic)item).Name, Convert.ToDouble(((dynamic)item).Value, CultureInfo.InvariantCulture)));
                }
                finally { Release(item); }
            }
            return TemperatureValues.SelectCpu(readings);
        }
        finally { Release(sensors); Release(hardware); Release(service); Release(locator); }
    }

    private static void Release(object? value) { if (value is not null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value); }

    private static double? ReadGpu(string id)
    {
        var parts = id.Split('_');
        if (parts.Length != 2 || !uint.TryParse(parts[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var high)
            || !uint.TryParse(parts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var low)) return null;
        var open = new OpenAdapter { Low = low, High = unchecked((int)high) };
        if (D3DKMTOpenAdapterFromLuid(ref open) < 0) return null;
        IntPtr buffer = IntPtr.Zero;
        try
        {
            var size = Marshal.SizeOf<AdapterPerformance>();
            buffer = Marshal.AllocHGlobal(size);
            Marshal.StructureToPtr(new AdapterPerformance(), buffer, false);
            var query = new QueryAdapter { Adapter = open.Adapter, Type = 62, Data = buffer, Size = (uint)size }; // KMTQAITYPE_ADAPTERPERFDATA
            return D3DKMTQueryAdapterInfo(ref query) < 0 ? null : TemperatureValues.FromGpuTenths(Marshal.PtrToStructure<AdapterPerformance>(buffer).Temperature);
        }
        finally
        {
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
            var close = new CloseAdapter { Adapter = open.Adapter };
            D3DKMTCloseAdapter(ref close);
        }
    }

    // SDK ABI: performance buffer 64 bytes, Temperature at offset 56; temperature is 0.1°C.
    [StructLayout(LayoutKind.Sequential)] internal struct AdapterPerformance
    {
        public uint PhysicalAdapterIndex;
        public ulong MemoryFrequency, MaxMemoryFrequency, MaxMemoryFrequencyOC, MemoryBandwidth, PCIEBandwidth;
        public uint FanRPM, Power, Temperature;
        public byte PowerStateOverride;
    }
    [StructLayout(LayoutKind.Sequential)] private struct OpenAdapter { public uint Low; public int High; public uint Adapter; }
    [StructLayout(LayoutKind.Sequential)] private struct CloseAdapter { public uint Adapter; }
    [StructLayout(LayoutKind.Sequential)] private struct QueryAdapter { public uint Adapter, Type; public IntPtr Data; public uint Size; }
    [DllImport("gdi32.dll", ExactSpelling = true)] private static extern int D3DKMTOpenAdapterFromLuid(ref OpenAdapter value);
    [DllImport("gdi32.dll", ExactSpelling = true)] private static extern int D3DKMTQueryAdapterInfo(ref QueryAdapter value);
    [DllImport("gdi32.dll", ExactSpelling = true)] private static extern int D3DKMTCloseAdapter(ref CloseAdapter value);
}
