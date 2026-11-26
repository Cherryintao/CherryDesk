using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace CherryDesk.Performance;

internal sealed class SystemPerformanceReader : IPerformanceReader
{
    private (ulong Idle, ulong Kernel, ulong User)? previous;
    private IReadOnlyList<GpuAdapter> adapters = Array.Empty<GpuAdapter>();
    private PdhGpuReader? gpu;
    private string? gpuError;
    private DateTimeOffset lastInitialization = DateTimeOffset.MinValue;

    public PerformanceSnapshot Read()
    {
        var now = DateTimeOffset.Now;
        double? cpu = null;
        ulong? memoryUsed = null, memoryTotal = null;
        string? cpuError = null, memoryError = null;
        if (GetSystemTimes(out var idle, out var kernel, out var user))
        {
            if (previous is { } old) cpu = PerformanceMath.CpuUsage(old.Idle, old.Kernel, old.User, idle, kernel, user);
            previous = (idle, kernel, user);
        }
        else { previous = null; cpuError = new Win32Exception(Marshal.GetLastWin32Error()).Message; }

        var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        if (GlobalMemoryStatusEx(ref memory) && memory.TotalPhysical > 0 && memory.AvailablePhysical <= memory.TotalPhysical)
        { memoryTotal = memory.TotalPhysical; memoryUsed = memory.TotalPhysical - memory.AvailablePhysical; }
        else memoryError = "物理内存状态暂不可用。";

        IReadOnlyDictionary<string, double> usage = new Dictionary<string, double>();
        if (gpu is null && now - lastInitialization >= TimeSpan.FromSeconds(30))
        {
            lastInitialization = now;
            try
            {
                adapters = DxgiAdapters.Read();
                gpu = new PdhGpuReader();
                gpuError = "GPU 正在采样，稍候显示。";
            }
            catch (Exception ex) { gpuError = ex.Message; }
        }
        else if (gpu is not null)
        {
            try
            {
                usage = gpu.Read();
                gpuError = usage.Count == 0 ? "驱动未提供有效 GPU 使用率，或计数器正在预热。" : null;
            }
            catch (Exception ex)
            {
                gpuError = ex.Message;
                gpu.Dispose();
                gpu = null;
            }
        }
        return new(now, cpu, memoryUsed, memoryTotal, adapters, usage, cpuError, memoryError, gpuError);
    }

    public void Dispose() => gpu?.Dispose();

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length, Load;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile,
            TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
}
