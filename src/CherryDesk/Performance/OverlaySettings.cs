using System;
using System.IO;
using System.Text.Json;

namespace CherryDesk.Performance;

public enum OverlayLayout { Compact, Full }

public sealed record OverlaySettings
{
    public bool Enabled { get; init; } = false;
    public bool OverlayVisible { get; init; } = true;
    public bool Locked { get; init; } = true;
    public double Opacity { get; init; } = 0.9;
    public double Left { get; init; } = 24;
    public double Top { get; init; } = 24;
    public string GpuKey { get; init; } = "";
    public OverlayLayout Layout { get; init; } = OverlayLayout.Compact;

    public OverlaySettings Normalize() => this with
    {
        Opacity = double.IsFinite(Opacity) ? Math.Clamp(Opacity, 0.35, 1) : 0.9,
        Left = double.IsFinite(Left) ? Left : 24,
        Top = double.IsFinite(Top) ? Top : 24,
        GpuKey = GpuKey ?? "",
        Layout = Enum.IsDefined(Layout) ? Layout : OverlayLayout.Compact
    };
}

internal sealed class OverlaySettingsStore(string path)
{
    public OverlaySettings Load()
    {
        if (!File.Exists(path)) return new();
        var json = File.ReadAllText(path);
        var settings = (JsonSerializer.Deserialize<OverlaySettings>(json) ?? new()).Normalize();
        using var document = JsonDocument.Parse(json);
        // v0.2's Enabled meant overlay visibility, not consent to run the new collector.
        if (!document.RootElement.TryGetProperty("OverlayVisible", out _)
            && document.RootElement.TryGetProperty("Enabled", out var legacy))
            settings = settings with { Enabled = false, OverlayVisible = legacy.GetBoolean() };
        return settings;
    }

    public void Save(OverlaySettings settings)
    {
        var parent = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(parent);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(settings.Normalize(), new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, path, true);
    }
}
