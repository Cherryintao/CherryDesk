using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace CherryDesk.Audio;

internal sealed class CoreAudioBackend : IAudioBackend
{
    private const int NoDefaultEndpoint = unchecked((int)0x80070490);
    private static readonly Guid EnumeratorClass = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid PolicyClass = new("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9");

    public IReadOnlyList<AudioDevice> GetDevices(AudioFlow flow)
    {
        var enumerator = Create<IMMDeviceEnumerator>(EnumeratorClass);
        IMMDeviceCollection? collection = null;
        try
        {
            Check(enumerator.EnumAudioEndpoints(flow, 1, out collection));
            Check(collection.GetCount(out var count));
            var result = new List<AudioDevice>();
            for (uint i = 0; i < count; i++)
            {
                IMMDevice? device = null;
                IPropertyStore? properties = null;
                try
                {
                    Check(collection.Item(i, out device));
                    Check(device.GetId(out var id));
                    Check(device.OpenPropertyStore(0, out properties));
                    var key = new PropertyKey
                    {
                        FormatId = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), PropertyId = 14
                    };
                    Check(properties.GetValue(ref key, out var value));
                    string? name;
                    try { name = value.Type == 31 ? Marshal.PtrToStringUni(value.Pointer) : null; }
                    finally { Check(PropVariantClear(ref value)); }
                    result.Add(new AudioDevice(id, string.IsNullOrWhiteSpace(name) ? id : name, flow));
                }
                catch (COMException ex) when (ex.HResult == NoDefaultEndpoint)
                {
                    // A USB/Bluetooth endpoint may disappear between enumeration and reading it.
                }
                finally { Release(properties); Release(device); }
            }
            result.Sort((left, right) => StringComparer.CurrentCultureIgnoreCase.Compare(left.Name, right.Name));
            return result;
        }
        finally { Release(collection); Release(enumerator); }
    }

    public string? GetDefault(AudioFlow flow, AudioRole role)
    {
        var enumerator = Create<IMMDeviceEnumerator>(EnumeratorClass);
        IMMDevice? device = null;
        try
        {
            var hr = enumerator.GetDefaultAudioEndpoint(flow, role, out device);
            if (hr == NoDefaultEndpoint) return null;
            Check(hr);
            Check(device.GetId(out var id));
            return id;
        }
        finally { Release(device); Release(enumerator); }
    }

    public void SetDefault(string deviceId, AudioRole role)
    {
        // Windows has no documented public setter for system-wide default endpoints.
        // This widely used PolicyConfig COM interface is isolated here for compatibility fixes.
        var policy = Create<IPolicyConfig>(PolicyClass);
        try { Check(policy.SetDefaultEndpoint(deviceId, role)); }
        finally { Release(policy); }
    }

    private static T Create<T>(Guid clsid) => (T)Activator.CreateInstance(Type.GetTypeFromCLSID(clsid, true)!)!;
    private static void Check(int hr) => Marshal.ThrowExceptionForHR(hr);
    private static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value);
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant value);
}
