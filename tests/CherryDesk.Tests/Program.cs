using CherryDesk.Audio;
using CherryDesk.Performance;

// Dependency-free regression tests. A fake endpoint backend never changes Windows audio.
var tests = new (string Name, Action Run)[]
{
    ("Switches output across all requested roles", () =>
    {
        var fake = new FakeBackend();
        new AudioSwitcher(fake).Switch(AudioFlow.Output, "new", true);
        Assert(fake.Defaults.Values.All(value => value == "new"));
    }),
    ("Leaves communication preference unchanged when unchecked", () =>
    {
        var fake = new FakeBackend();
        new AudioSwitcher(fake).Switch(AudioFlow.Input, "new", false);
        Assert(fake.Defaults[AudioRole.Console] == "new");
        Assert(fake.Defaults[AudioRole.Multimedia] == "new");
        Assert(fake.Defaults[AudioRole.Communications] == "chat");
        Assert(fake.LastFlow == AudioFlow.Input);
    }),
    ("Rejects disconnected devices before making any change", () =>
    {
        var fake = new FakeBackend();
        Throws(() => new AudioSwitcher(fake).Switch(AudioFlow.Output, "missing", true));
        Assert(fake.SetCalls == 0);
    }),
    ("Restores each original role after a partial write failure", () =>
    {
        var fake = new FakeBackend { FailRole = AudioRole.Multimedia };
        Throws(() => new AudioSwitcher(fake).Switch(AudioFlow.Output, "new", true));
        Assert(fake.Defaults[AudioRole.Console] == "old");
        Assert(fake.Defaults[AudioRole.Multimedia] == "music");
        Assert(fake.Defaults[AudioRole.Communications] == "chat");
    }),
    ("Readback mismatch fails and restores original defaults", () =>
    {
        var fake = new FakeBackend { IgnoreRole = AudioRole.Multimedia };
        Throws(() => new AudioSwitcher(fake).Switch(AudioFlow.Output, "new", true));
        Assert(fake.Defaults[AudioRole.Console] == "old");
        Assert(fake.Defaults[AudioRole.Multimedia] == "music");
        Assert(fake.Defaults[AudioRole.Communications] == "chat");
    }),
    ("Reports incomplete recovery when rollback fails", () =>
    {
        var fake = new FakeBackend { FailRole = AudioRole.Multimedia, FailRollback = true };
        var error = Throws(() => new AudioSwitcher(fake).Switch(AudioFlow.Output, "new", true));
        Assert(error.Message.Contains("未能恢复"));
    }),
    ("Already selected roles do not cause redundant writes", () =>
    {
        var fake = new FakeBackend();
        foreach (var role in Enum.GetValues<AudioRole>()) fake.Defaults[role] = "new";
        new AudioSwitcher(fake).Switch(AudioFlow.Output, "new", true);
        Assert(fake.SetCalls == 0);
    }),
    ("Null defaults do not prevent the first device being selected", () =>
    {
        var fake = new FakeBackend();
        foreach (var role in Enum.GetValues<AudioRole>()) fake.Defaults[role] = null;
        new AudioSwitcher(fake).Switch(AudioFlow.Output, "new", true);
        Assert(fake.Defaults.Values.All(value => value == "new"));
    }),
    ("CPU excludes idle time already included in kernel", () =>
    {
        var cpu = PerformanceMath.CpuUsage(100, 200, 100, 150, 270, 130);
        Assert(cpu == 50);
    }),
    ("CPU reset and empty intervals are unknown", () =>
    {
        Assert(PerformanceMath.CpuUsage(100, 200, 100, 50, 270, 130) is null);
        Assert(PerformanceMath.CpuUsage(100, 200, 100, 100, 200, 100) is null);
        Assert(PerformanceMath.CpuUsage(0, 0, 0, 200, 100, 0) is null);
    }),
    ("GPU sums processes on one engine and takes the busiest engine", () =>
    {
        var gpu = PerformanceMath.AggregateGpu(new[]
        {
            new GpuCounterSample("pid_1_luid_0x0_0xAB_phys_0_eng_0_engtype_3D", 60, 0),
            new GpuCounterSample("pid_2_luid_0x0_0xAB_phys_0_eng_0_engtype_3D", 30, 1),
            new GpuCounterSample("pid_1_luid_0x0_0xAB_phys_0_eng_1_engtype_Copy", 40, 0),
            new GpuCounterSample("pid_4_luid_0x0_0xCD_phys_0_eng_0_engtype_3D", 25, 0)
        });
        Assert(gpu["00000000_000000ab"] == 90);
        Assert(gpu["00000000_000000cd"] == 25);
    }),
    ("GPU invalid statuses, names and numbers do not appear as 0 percent", () =>
    {
        var prefix = "pid_1_luid_0x0_0xAB_phys_0_eng_0_engtype_3D";
        var gpu = PerformanceMath.AggregateGpu(new[]
        {
            new GpuCounterSample(prefix, 20, 0xC0000BBA),
            new GpuCounterSample(prefix, double.NaN, 0),
            new GpuCounterSample(prefix, double.PositiveInfinity, 0),
            new GpuCounterSample(prefix, -1, 0),
            new GpuCounterSample("unexpected-instance", 20, 0)
        });
        Assert(gpu.Count == 0);
    }),
    ("GPU caps totals per engine without mixing physical nodes", () =>
    {
        var gpu = PerformanceMath.AggregateGpu(new[]
        {
            new GpuCounterSample("pid_1_luid_0x0_0xAB_phys_0_eng_0_engtype_3D", 70, 0),
            new GpuCounterSample("pid_2_luid_0x0_0xAB_phys_0_eng_0_engtype_3D", 60, 0),
            new GpuCounterSample("pid_3_luid_0x0_0xCD_phys_0_eng_0_engtype_3D", 40, 0),
            new GpuCounterSample("pid_4_luid_0x0_0xCD_phys_1_eng_0_engtype_3D", 50, 0)
        });
        Assert(gpu["00000000_000000ab"] == 100);
        Assert(gpu["00000000_000000cd"] == 50);
    }),
    ("GPU selection prefers dedicated memory and falls back across PCs", () =>
    {
        GpuAdapter[] adapters = [new("a", "integrated", "Integrated", 128), new("b", "discrete", "Discrete", 16384)];
        Assert(PerformanceMath.SelectGpu(adapters, "")?.Id == "b");
        Assert(PerformanceMath.SelectGpu(adapters, "integrated")?.Id == "a");
        Assert(PerformanceMath.SelectGpu(adapters, "previous-computer")?.Id == "b");
        Assert(PerformanceMath.SelectGpu(Array.Empty<GpuAdapter>(), "") is null);
    }),
    ("Overlay settings reject nonfinite coordinates and unsafe opacity", () =>
    {
        var settings = new OverlaySettings { Left = double.NaN, Top = double.PositiveInfinity, Opacity = 0 }.Normalize();
        Assert(settings.Left == 24 && settings.Top == 24 && settings.Opacity == 0.35);
        Assert((new OverlaySettings { Opacity = 5 }).Normalize().Opacity == 1);
    }),
    ("Disabled monitoring does not create any collector", () =>
    {
        using var probe = new ReaderProbe();
        using var monitor = new PerformanceMonitor(probe.Create, TimeSpan.FromMilliseconds(20));
        monitor.SetEnabledAsync(false).GetAwaiter().GetResult();
        Thread.Sleep(80);
        Assert(probe.Created == 0 && monitor.ReadCount == 0 && !monitor.IsRunning);
    }),
    ("Disabling releases the collector and stops subsequent sampling", () =>
    {
        using var probe = new ReaderProbe();
        using var monitor = new PerformanceMonitor(probe.Create, TimeSpan.FromMilliseconds(20));
        monitor.SetEnabledAsync(true).GetAwaiter().GetResult();
        Assert(probe.Sampled.Wait(2000));
        monitor.SetEnabledAsync(false).GetAwaiter().GetResult();
        var reads = probe.Reads;
        Thread.Sleep(100);
        Assert(reads > 0 && probe.Reads == reads && probe.Active == 0 && probe.Disposed == 1);
        Assert(!monitor.IsRunning && monitor.ActiveReaders == 0);
    }),
    ("Re-enabling creates a fresh collector", () =>
    {
        using var probe = new ReaderProbe();
        using var monitor = new PerformanceMonitor(probe.Create, TimeSpan.FromMilliseconds(20));
        monitor.SetEnabledAsync(true).GetAwaiter().GetResult();
        Assert(probe.Sampled.Wait(2000));
        monitor.SetEnabledAsync(false).GetAwaiter().GetResult();
        probe.Sampled.Reset();
        monitor.SetEnabledAsync(true).GetAwaiter().GetResult();
        Assert(probe.Sampled.Wait(2000));
        monitor.SetEnabledAsync(false).GetAwaiter().GetResult();
        Assert(probe.Created == 2 && probe.Disposed == 2 && probe.MaxActive == 1);
    }),
    ("Rapid toggles never overlap collectors", () =>
    {
        using var probe = new ReaderProbe();
        using var monitor = new PerformanceMonitor(probe.Create, TimeSpan.FromMilliseconds(20));
        Task.WhenAll(Enumerable.Range(0, 20).Select(index => monitor.SetEnabledAsync(index % 2 == 0))).GetAwaiter().GetResult();
        monitor.SetEnabledAsync(false).GetAwaiter().GetResult();
        Assert(probe.Active == 0 && probe.Created == probe.Disposed && probe.MaxActive <= 1);
    }),
    ("Stopping waits for in-flight reads and discards their updates", () =>
    {
        using var probe = new ReaderProbe { BlockRead = true };
        using var monitor = new PerformanceMonitor(probe.Create, TimeSpan.FromMilliseconds(20));
        var published = 0;
        monitor.Updated += (_, _) => Interlocked.Increment(ref published);
        monitor.SetEnabledAsync(true).GetAwaiter().GetResult();
        try
        {
            Assert(probe.Entered.Wait(2000));
            var stop = monitor.SetEnabledAsync(false);
            Assert(!monitor.IsRunning && !stop.IsCompleted);
            probe.Release.Set();
            stop.GetAwaiter().GetResult();
            Assert(published == 0 && probe.Disposed == 1 && monitor.ActiveReaders == 0);
        }
        finally { probe.Release.Set(); }
    }),
    ("Legacy visibility does not silently enable the new monitoring feature", () =>
    {
        var folder = Path.Combine(Path.GetTempPath(), "CherryDesk-legacy-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "settings.json");
        try
        {
            File.WriteAllText(path, "{\"Enabled\":true,\"Locked\":false,\"Opacity\":0.7}");
            var settings = new OverlaySettingsStore(path).Load();
            Assert(!settings.Enabled && settings.OverlayVisible && !settings.Locked && settings.Opacity == 0.7);
            File.WriteAllText(path, "{\"Enabled\":false}");
            Assert(!new OverlaySettingsStore(path).Load().OverlayVisible);
        }
        finally { File.Delete(path); Directory.Delete(folder); }
    }),
    ("Overlay preferences round-trip with atomic replacement", () =>
    {
        var folder = Path.Combine(Path.GetTempPath(), "CherryDesk-overlay-test-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(folder, "settings.json");
        try
        {
            var store = new OverlaySettingsStore(path);
            Assert(store.Load() == new OverlaySettings());
            Assert(!store.Load().Enabled);
            var settings = new OverlaySettings { Enabled = false, OverlayVisible = false, Locked = false, Left = -300, Top = 80, Opacity = 0.65, GpuKey = "amd" };
            store.Save(settings);
            Assert(store.Load() == settings);
            store.Save(settings with { Enabled = true });
            Assert(store.Load().Enabled && !File.Exists(path + ".tmp"));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (Directory.Exists(folder)) Directory.Delete(folder);
        }
    })
};

var failures = 0;
foreach (var (name, run) in tests)
{
    try { run(); Console.WriteLine($"PASS {name}"); }
    catch (Exception ex) { failures++; Console.Error.WriteLine($"FAIL {name}: {ex.Message}"); }
}

Console.WriteLine($"{tests.Length - failures}/{tests.Length} passed");
return failures == 0 ? 0 : 1;

static void Assert(bool condition) { if (!condition) throw new Exception("Assertion failed"); }
static Exception Throws(Action action)
{
    try { action(); } catch (InvalidOperationException ex) { return ex; }
    throw new Exception("Expected an error, but the switch succeeded");
}

internal sealed class ReaderProbe : IDisposable
{
    public int Created, Disposed, Active, MaxActive, Reads;
    public bool BlockRead { get; init; }
    public ManualResetEventSlim Sampled { get; } = new(false);
    public ManualResetEventSlim Entered { get; } = new(false);
    public ManualResetEventSlim Release { get; } = new(false);
    public IPerformanceReader Create()
    {
        lock (this) { Created++; Active++; MaxActive = Math.Max(MaxActive, Active); }
        return new ProbeReader(this);
    }
    public void Dispose() { Sampled.Dispose(); Entered.Dispose(); Release.Dispose(); }
    private sealed class ProbeReader(ReaderProbe probe) : IPerformanceReader
    {
        private bool disposed;
        public PerformanceSnapshot Read()
        {
            probe.Entered.Set();
            if (probe.BlockRead && !probe.Release.Wait(2000)) throw new TimeoutException("Test fixture did not release read");
            Interlocked.Increment(ref probe.Reads);
            probe.Sampled.Set();
            return new(DateTimeOffset.Now, 20, 10, 100, Array.Empty<GpuAdapter>(), new Dictionary<string, double>(), null, null, null);
        }
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            lock (probe) { probe.Disposed++; probe.Active--; }
        }
    }
}
internal sealed class FakeBackend : IAudioBackend
{
    public Dictionary<AudioRole, string?> Defaults { get; } = new()
    { [AudioRole.Console] = "old", [AudioRole.Multimedia] = "music", [AudioRole.Communications] = "chat" };
    public AudioRole? FailRole { get; init; }
    public AudioRole? IgnoreRole { get; init; }
    public bool FailRollback { get; init; }
    public int SetCalls { get; private set; }
    public AudioFlow LastFlow { get; private set; }
    public IReadOnlyList<AudioDevice> GetDevices(AudioFlow flow)
    {
        LastFlow = flow;
        return [new("new", "New headset", flow)];
    }
    public string? GetDefault(AudioFlow flow, AudioRole role) => Defaults[role];
    public void SetDefault(string deviceId, AudioRole role)
    {
        SetCalls++;
        if (deviceId != "new" && FailRollback) throw new InvalidOperationException("Rollback unavailable");
        if (deviceId == "new" && role == IgnoreRole) return;
        Defaults[role] = deviceId;
        // Deliberately apply before throwing, to test conservative recovery.
        if (deviceId == "new" && role == FailRole) throw new InvalidOperationException("Endpoint unavailable");
    }
}
