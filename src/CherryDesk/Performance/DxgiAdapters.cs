using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace CherryDesk.Performance;

internal static class DxgiAdapters
{
    public static IReadOnlyList<GpuAdapter> Read()
    {
        var iid = typeof(IDXGIFactory1).GUID;
        Marshal.ThrowExceptionForHR(CreateDXGIFactory1(ref iid, out var factory));
        try
        {
            var result = new List<GpuAdapter>();
            var keys = new Dictionary<string, int>();
            for (uint index = 0; ; index++)
            {
                var hr = factory.EnumAdapters1(index, out var adapter);
                if (hr == unchecked((int)0x887A0002)) break; // DXGI_ERROR_NOT_FOUND
                Marshal.ThrowExceptionForHR(hr);
                try
                {
                    Marshal.ThrowExceptionForHR(adapter.GetDesc1(out var desc));
                    if ((desc.Flags & 2) != 0) continue; // Microsoft software renderer
                    var id = $"{unchecked((uint)desc.AdapterLuid.HighPart):x8}_{desc.AdapterLuid.LowPart:x8}";
                    var stableKey = $"{desc.VendorId:x4}:{desc.DeviceId:x4}:{desc.SubsystemId:x8}:{desc.Description}";
                    keys.TryGetValue(stableKey, out var ordinal);
                    keys[stableKey] = ordinal + 1;
                    result.Add(new GpuAdapter(id, $"{stableKey}:{ordinal}", desc.Description, desc.DedicatedVideoMemory.ToUInt64(), (int)index));
                }
                finally { Marshal.ReleaseComObject(adapter); }
            }
            return result;
        }
        finally { Marshal.ReleaseComObject(factory); }
    }

    [DllImport("dxgi.dll", ExactSpelling = true)]
    private static extern int CreateDXGIFactory1(ref Guid iid, out IDXGIFactory1 factory);

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid { public uint LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct AdapterDescription
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public uint VendorId, DeviceId, SubsystemId, Revision;
        public UIntPtr DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
        public Luid AdapterLuid;
        public uint Flags;
    }

    // Inherited IDXGIObject/IDXGIFactory slots are included in their native order.
    [ComImport, Guid("770AAE78-F26F-4DBA-A829-253C83D1B387"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIFactory1
    {
        [PreserveSig] int SetPrivateData(ref Guid name, uint size, IntPtr data);
        [PreserveSig] int SetPrivateDataInterface(ref Guid name, IntPtr unknown);
        [PreserveSig] int GetPrivateData(ref Guid name, ref uint size, IntPtr data);
        [PreserveSig] int GetParent(ref Guid iid, out IntPtr parent);
        [PreserveSig] int EnumAdapters(uint index, out IntPtr adapter);
        [PreserveSig] int MakeWindowAssociation(IntPtr window, uint flags);
        [PreserveSig] int GetWindowAssociation(out IntPtr window);
        [PreserveSig] int CreateSwapChain(IntPtr device, IntPtr desc, out IntPtr chain);
        [PreserveSig] int CreateSoftwareAdapter(IntPtr module, out IntPtr adapter);
        [PreserveSig] int EnumAdapters1(uint index, out IDXGIAdapter1 adapter);
        [PreserveSig]
        [return: MarshalAs(UnmanagedType.Bool)] bool IsCurrent();
    }

    [ComImport, Guid("29038F61-3839-4626-91FD-086879011A05"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIAdapter1
    {
        [PreserveSig] int SetPrivateData(ref Guid name, uint size, IntPtr data);
        [PreserveSig] int SetPrivateDataInterface(ref Guid name, IntPtr unknown);
        [PreserveSig] int GetPrivateData(ref Guid name, ref uint size, IntPtr data);
        [PreserveSig] int GetParent(ref Guid iid, out IntPtr parent);
        [PreserveSig] int EnumOutputs(uint index, out IntPtr output);
        [PreserveSig] int GetDesc(IntPtr desc);
        [PreserveSig] int CheckInterfaceSupport(ref Guid name, out long version);
        [PreserveSig] int GetDesc1(out AdapterDescription desc);
    }
}
