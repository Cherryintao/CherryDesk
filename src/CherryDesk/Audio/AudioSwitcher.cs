using System;
using System.Collections.Generic;
using System.Linq;

namespace CherryDesk.Audio;

public enum AudioFlow { Output = 0, Input = 1 }
public enum AudioRole { Console = 0, Multimedia = 1, Communications = 2 }

public sealed record AudioDevice(string Id, string Name, AudioFlow Flow);
public sealed record AudioState(IReadOnlyList<AudioDevice> Devices,
    IReadOnlyDictionary<AudioRole, string?> Defaults);

internal interface IAudioBackend
{
    IReadOnlyList<AudioDevice> GetDevices(AudioFlow flow);
    string? GetDefault(AudioFlow flow, AudioRole role);
    void SetDefault(string deviceId, AudioRole role);
}

// No game or audio streams are opened: only the user's endpoint preferences change.
internal sealed class AudioSwitcher(IAudioBackend backend)
{
    public AudioState Read(AudioFlow flow) => new(backend.GetDevices(flow),
        Enum.GetValues<AudioRole>().ToDictionary(role => role, role => backend.GetDefault(flow, role)));

    public void Switch(AudioFlow flow, string deviceId, bool includeCommunications)
    {
        if (!backend.GetDevices(flow).Any(device => device.Id == deviceId))
            throw new InvalidOperationException("设备已经断开，请刷新后重新选择。");

        AudioRole[] roles = includeCommunications
            ? [AudioRole.Console, AudioRole.Multimedia, AudioRole.Communications]
            : [AudioRole.Console, AudioRole.Multimedia];
        var previous = roles.ToDictionary(role => role, role => backend.GetDefault(flow, role));
        var changed = new List<AudioRole>();
        try
        {
            foreach (var role in roles)
            {
                if (previous[role] == deviceId) continue;
                // Include a failing call in rollback: it may have applied before reporting failure.
                changed.Add(role);
                backend.SetDefault(deviceId, role);
            }
            if (roles.Any(role => backend.GetDefault(flow, role) != deviceId))
                throw new InvalidOperationException("系统返回的默认设备与选择不一致。");
        }
        catch (Exception failure)
        {
            var rollbackFailed = false;
            foreach (var role in changed.AsEnumerable().Reverse())
            {
                try
                {
                    if (previous[role] is not { } oldId) { rollbackFailed = true; continue; }
                    backend.SetDefault(oldId, role);
                    if (backend.GetDefault(flow, role) != oldId) rollbackFailed = true;
                }
                catch { rollbackFailed = true; }
            }
            throw new InvalidOperationException(rollbackFailed
                ? "切换失败，部分默认设备未能恢复，请检查系统声音设置。"
                : "切换失败，已保留或恢复原来的默认设备。", failure);
        }
    }
}
