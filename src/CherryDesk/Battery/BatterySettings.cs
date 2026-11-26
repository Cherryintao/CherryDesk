using System;
using System.IO;
using System.Text.Json;

namespace CherryDesk.Battery;

internal sealed class BatterySettingsStore(string path)
{
    private sealed record Settings(int LowThreshold = 20);

    public int Load() => File.Exists(path)
        ? Math.Clamp((JsonSerializer.Deserialize<Settings>(File.ReadAllText(path)) ?? new()).LowThreshold, 1, 100) : 20;

    public void Save(int threshold)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(new Settings(Math.Clamp(threshold, 1, 100))));
        File.Move(temporary, path, true);
    }
}
