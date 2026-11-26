using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace CherryDesk.Performance;

public sealed record GpuAdapter(string Id, string Key, string Name, ulong DedicatedMemory, int Index = 0)
{
    public string DisplayName => $"GPU {Index} · {Name}";
}
public sealed record GpuCounterSample(string Instance, double Value, uint Status);
public sealed record PerformanceSnapshot(DateTimeOffset Timestamp, double? CpuPercent,
    ulong? MemoryUsed, ulong? MemoryTotal, IReadOnlyList<GpuAdapter> Adapters,
    IReadOnlyDictionary<string, double> GpuUsage, string? CpuError, string? MemoryError, string? GpuError)
{
    public string? CpuName { get; init; }
    public double? CpuTemperature { get; init; }
    public IReadOnlyDictionary<string, double> GpuTemperatures { get; init; } = new Dictionary<string, double>();
    public string? CpuTemperatureSource { get; init; }
}

internal static partial class PerformanceMath
{
    // Kernel includes idle time. A first sample/reset/zero interval is unknown, not 0%.
    public static double? CpuUsage(ulong previousIdle, ulong previousKernel, ulong previousUser,
        ulong idle, ulong kernel, ulong user)
    {
        if (idle < previousIdle || kernel < previousKernel || user < previousUser) return null;
        var total = (double)(kernel - previousKernel) + (user - previousUser);
        var idleDelta = idle - previousIdle;
        if (total <= 0 || idleDelta > total) return null;
        return Math.Clamp((total - idleDelta) / total * 100, 0, 100);
    }

    [GeneratedRegex(@"(?:^|_)luid_0x([0-9a-f]+)_0x([0-9a-f]+)_phys_(\d+)_eng_(\d+)(?:_|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex InstancePattern();

    public static IReadOnlyDictionary<string, double> AggregateGpu(IEnumerable<GpuCounterSample> samples)
    {
        // Sum processes sharing one physical engine; take the busiest engine per adapter.
        // Summing different engines would exceed 100% and averaging would hide saturation.
        var engines = new Dictionary<(string Adapter, string Physical, string Engine), double>();
        foreach (var sample in samples)
        {
            if (sample.Status > 1 || !double.IsFinite(sample.Value) || sample.Value < 0) continue;
            var match = InstancePattern().Match(sample.Instance);
            if (!match.Success || !uint.TryParse(match.Groups[1].Value, NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture, out var high)
                || !uint.TryParse(match.Groups[2].Value, NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture, out var low)) continue;
            var key = ($"{high:x8}_{low:x8}", match.Groups[3].Value, match.Groups[4].Value);
            engines.TryGetValue(key, out var value);
            engines[key] = Math.Min(100, value + Math.Min(100, sample.Value));
        }
        return engines.GroupBy(pair => pair.Key.Adapter)
            .ToDictionary(group => group.Key, group => group.Max(pair => pair.Value));
    }

    public static GpuAdapter? SelectGpu(IReadOnlyList<GpuAdapter> adapters, string? preferredKey) =>
        adapters.FirstOrDefault(adapter => adapter.Key == preferredKey)
        ?? adapters.OrderByDescending(adapter => adapter.DedicatedMemory).FirstOrDefault();
}
