using System;
using System.Collections.Generic;
using System.Linq;

namespace CherryDesk.Battery;

public enum BatteryLevel { Empty, Low, Medium, Full }

public sealed record DeviceBattery(string Id, string Name, string Connection, int? Percent, BatteryLevel? Level = null)
{
    public string ValueText => Percent is { } value ? $"{value}%" : Level switch
    {
        BatteryLevel.Empty => "即将耗尽", BatteryLevel.Low => "电量低",
        BatteryLevel.Medium => "电量中等", BatteryLevel.Full => "电量充足", _ => ""
    };
    public bool HasReading => Percent is >= 0 and <= 100 || Percent is null && Level is not null;
    public bool IsLow(int threshold) => Percent is { } value ? value <= threshold : Level is BatteryLevel.Empty or BatteryLevel.Low;
}

internal sealed record BatteryReadResult(IReadOnlyList<DeviceBattery> Devices, IReadOnlyList<string> Errors);
internal interface IBatteryReader { BatteryReadResult Read(); }

internal static class BatteryPresentation
{
    public static string Age(DateTimeOffset? updated, DateTimeOffset now)
    {
        if (updated is null) return "尚未更新";
        var elapsed = now - updated.Value;
        if (elapsed.TotalSeconds < 60) return "刚刚更新";
        if (elapsed.TotalMinutes < 60) return $"{(int)elapsed.TotalMinutes} 分钟前更新";
        if (elapsed.TotalHours < 24) return $"{(int)elapsed.TotalHours} 小时前更新";
        return $"{(int)elapsed.TotalDays} 天前更新";
    }

    public static string Tooltip(IEnumerable<DeviceBattery> low)
    {
        var devices = low.ToArray();
        var text = devices.Length == 0 ? "CherryDesk · 音频、设备电量与性能" :
            "低电量 · " + string.Join("；", devices.Select(device => $"{device.Name} {device.ValueText}"));
        // NotifyIcon's tooltip has a 127 UTF-16 code-unit limit. Full names remain in its menu.
        if (text.Length <= 127) return text;
        var length = char.IsHighSurrogate(text[125]) ? 125 : 126;
        return text[..length] + "…";
    }
}

internal sealed class BatteryAlerts
{
    private readonly HashSet<string> acknowledged = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<DeviceBattery> Low { get; private set; } = Array.Empty<DeviceBattery>();
    public bool ShouldBlink => Low.Any(device => !acknowledged.Contains(device.Id));

    public void Update(IEnumerable<DeviceBattery> devices, int threshold)
    {
        Low = devices.Where(device => device.HasReading && device.IsLow(threshold)).ToArray();
        acknowledged.IntersectWith(Low.Select(device => device.Id));
    }

    public void Acknowledge() => acknowledged.UnionWith(Low.Select(device => device.Id));
}
