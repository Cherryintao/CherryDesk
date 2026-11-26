using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace CherryDesk.Performance;

internal sealed class PdhGpuReader : IDisposable
{
    private const uint MoreData = 0x800007D2;
    private IntPtr query;
    private readonly IntPtr counter;
    public int LastInstanceCount { get; private set; }

    public PdhGpuReader()
    {
        Check(PdhOpenQueryW(null, UIntPtr.Zero, out query));
        try
        {
            // English object/counter names are localized by PDH. Keep the wildcard so
            // new game process instances appear without reopening hundreds of counters.
            Check(PdhAddEnglishCounterW(query, @"\GPU Engine(*)\Utilization Percentage", UIntPtr.Zero, out counter));
            Check(PdhCollectQueryData(query)); // Prime the rate counter.
        }
        catch { Dispose(); throw; }
    }

    public IReadOnlyDictionary<string, double> Read()
    {
        Check(PdhCollectQueryData(query));
        // Instances can appear between sizing and retrieval. Requery from zero on MORE_DATA.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            uint bytes = 0;
            var status = PdhGetFormattedCounterArrayW(counter, 0x200, ref bytes, out _, IntPtr.Zero);
            if (status != MoreData) Check(status);
            if (bytes == 0) { LastInstanceCount = 0; return new Dictionary<string, double>(); }
            if (bytes > 64 * 1024 * 1024) throw new InvalidOperationException("GPU 计数器数据异常过大。");
            var buffer = Marshal.AllocHGlobal(checked((int)bytes));
            try
            {
                status = PdhGetFormattedCounterArrayW(counter, 0x200, ref bytes, out var count, buffer);
                if (status == MoreData) continue;
                Check(status);
                var size = Marshal.SizeOf<CounterItem>();
                if ((ulong)count * (uint)size > bytes) throw new InvalidOperationException("GPU 计数器数据长度不一致。");
                LastInstanceCount = checked((int)count);
                var samples = new List<GpuCounterSample>(LastInstanceCount);
                for (var index = 0; index < count; index++)
                {
                    var item = Marshal.PtrToStructure<CounterItem>(IntPtr.Add(buffer, index * size));
                    var instance = Marshal.PtrToStringUni(item.Name);
                    if (instance is not null) samples.Add(new(instance, item.Value.Value, item.Value.Status));
                }
                return PerformanceMath.AggregateGpu(samples);
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        throw new InvalidOperationException("GPU 计数器正在变化，等待下一次采样。");
    }

    public void Dispose()
    {
        if (query != IntPtr.Zero) { PdhCloseQuery(query); query = IntPtr.Zero; }
    }

    private static void Check(uint status)
    {
        if (status != 0) throw new InvalidOperationException($"GPU 性能计数器暂不可用（0x{status:X8}）。");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CounterValue { public uint Status; public double Value; }
    [StructLayout(LayoutKind.Sequential)]
    private struct CounterItem { public IntPtr Name; public CounterValue Value; }
    [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint PdhOpenQueryW(string? source, UIntPtr user, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint PdhAddEnglishCounterW(IntPtr query, string path, UIntPtr user, out IntPtr counter);
    [DllImport("pdh.dll", ExactSpelling = true)]
    private static extern uint PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll", ExactSpelling = true)]
    private static extern uint PdhCloseQuery(IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint bytes, out uint count, IntPtr buffer);
}
