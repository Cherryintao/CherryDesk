using CherryDesk.Audio;
using CherryDesk.Performance;
using CherryDesk.Battery;

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
    ("Temperatures convert driver units and reject absent or invalid values", () =>
    {
        Assert(TemperatureValues.FromGpuTenths(675) == 67.5);
        Assert(TemperatureValues.FromGpuTenths(0) is null);
        Assert(TemperatureValues.FromGpuTenths(uint.MaxValue) is null);
        Assert(TemperatureValues.Valid(double.NaN) is null && TemperatureValues.Valid(double.PositiveInfinity) is null);
        Assert(TemperatureValues.Valid(-1) is null && TemperatureValues.Valid(151) is null);
        Assert(TemperatureValues.Text(null) == "--" && TemperatureValues.Text(63) == "63°C");
    }),
    ("CPU package temperature takes priority over individual cores", () =>
    {
        Assert(TemperatureValues.SelectCpu([new("CPU Core #1", 80), new("CPU Package", 65)]) == 65);
        Assert(TemperatureValues.SelectCpu([new("CPU Core #1", 60), new("CPU Core #2", 70)]) == 70);
        Assert(TemperatureValues.SelectCpu([new("CPU (Tctl/Tdie)", 76), new("Core", 81)]) == 76);
        Assert(TemperatureValues.SelectCpu([new("CPU Package", 0)]) is null);
        Assert(TemperatureValues.SelectCpu(Array.Empty<CpuTemperatureSensor>()) is null);
    }),
    ("GPU temperature buffer matches the Windows x64 ABI", () =>
    {
        Assert(System.Runtime.InteropServices.Marshal.SizeOf<HardwareTemperatureReader.AdapterPerformance>() == 64);
        Assert(System.Runtime.InteropServices.Marshal.OffsetOf<HardwareTemperatureReader.AdapterPerformance>("Temperature").ToInt32() == 56);
    }),
    ("Overlay defaults to compact and rejects unknown saved modes", () =>
    {
        Assert(new OverlaySettings().Layout == OverlayLayout.Compact);
        Assert((new OverlaySettings { Layout = (OverlayLayout)999 }).Normalize().Layout == OverlayLayout.Compact);
        Assert((new OverlaySettings { Layout = OverlayLayout.Full }).Normalize().Layout == OverlayLayout.Full);
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
    ("Battery values preserve unknown data and controller level granularity", () =>
    {
        Assert(!new DeviceBattery("a", "A", "", null).HasReading);
        Assert(!new DeviceBattery("a", "A", "", 101).HasReading);
        Assert(new DeviceBattery("a", "A", "", 0).HasReading);
        var xbox = new DeviceBattery("x", "Xbox", "", null, BatteryLevel.Medium);
        Assert(xbox.HasReading && xbox.Percent is null && xbox.ValueText == "电量中等" && !xbox.IsLow(100));
        Assert((xbox with { Level = BatteryLevel.Low }).IsLow(1));
        Assert(new DeviceBattery("a", "A", "", 20).IsLow(20));
        Assert(!new DeviceBattery("a", "A", "", 21).IsLow(20));
    }),
    ("Bluetooth matching requires a connected address and joins the battery child by container", () =>
    {
        WindowsBatteryReader.BluetoothNode[] nodes =
        [new("BTHENUM\\DEV_AABBCCDDEEFF", "one", null), new("BTHENUM\\HFP-child", "one", 70),
         new("BTHENUM\\DEV_112233445566", "two", 5), new("BTHENUM\\random", null, 1)];
        var devices = WindowsBatteryReader.MatchBluetooth(nodes, new Dictionary<string, string> { ["aabbccddeeff"] = "Headphones" });
        Assert(devices.Count == 1 && devices[0].Name == "Headphones" && devices[0].Percent == 70);
        Assert(WindowsBatteryReader.MatchBluetooth(nodes, new Dictionary<string, string>()).Count == 0);
    }),
    ("Low battery acknowledgement survives refresh and rearms only after recovery", () =>
    {
        var alerts = new BatteryAlerts();
        var headset = new DeviceBattery("headset", "Headset", "", 15);
        alerts.Update([headset], 20);
        Assert(alerts.ShouldBlink);
        alerts.Acknowledge();
        alerts.Update([headset with { Percent = 10 }], 20);
        Assert(!alerts.ShouldBlink && alerts.Low.Count == 1);
        alerts.Update([headset, new("x", "Xbox", "", null, BatteryLevel.Low)], 20);
        Assert(alerts.ShouldBlink && alerts.Low.Count == 2);
        alerts.Acknowledge();
        alerts.Update([headset with { Percent = 80 }], 20);
        Assert(!alerts.ShouldBlink && alerts.Low.Count == 0);
        alerts.Update([headset], 20);
        Assert(alerts.ShouldBlink);
    }),
    ("Changing the battery threshold reevaluates current readings", () =>
    {
        var alerts = new BatteryAlerts();
        DeviceBattery[] devices = [new("a", "A", "", 30)];
        alerts.Update(devices, 20); Assert(!alerts.ShouldBlink);
        alerts.Update(devices, 30); Assert(alerts.ShouldBlink);
        alerts.Update(devices, 29); Assert(!alerts.ShouldBlink && alerts.Low.Count == 0);
    }),
    ("Battery timestamps are relative and tray text fits the native tooltip", () =>
    {
        var now = DateTimeOffset.Now;
        Assert(BatteryPresentation.Age(null, now) == "尚未更新");
        Assert(BatteryPresentation.Age(now.AddSeconds(-59), now) == "刚刚更新");
        Assert(BatteryPresentation.Age(now.AddMinutes(-5), now) == "5 分钟前更新");
        Assert(BatteryPresentation.Age(now.AddHours(-2), now) == "2 小时前更新");
        var tooltip = BatteryPresentation.Tooltip([new("a", new string('A', 200), "", 10)]);
        Assert(tooltip.Length <= 127 && tooltip.Contains("低电量"));
    }),
    ("Battery settings persist without touching performance preferences", () =>
    {
        var folder = Path.Combine(Path.GetTempPath(), "CherryDesk-battery-test-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(folder, "battery-settings.json");
        try
        {
            var store = new BatterySettingsStore(path);
            Assert(store.Load() == 20);
            store.Save(35); Assert(store.Load() == 35 && !File.Exists(path + ".tmp"));
            store.Save(101); Assert(store.Load() == 100);
            store.Save(0); Assert(store.Load() == 1);
        }
        finally { if (File.Exists(path)) File.Delete(path); if (Directory.Exists(folder)) Directory.Delete(folder); }
    }),
    ("Concurrent battery refreshes share one read and missing readings disappear", () =>
    {
        using var reader = new BatteryProbe { Block = true };
        using var controller = new BatteryController(reader, transient: true);
        var first = controller.RefreshAsync();
        Assert(reader.Entered.Wait(2000));
        Assert(ReferenceEquals(first, controller.RefreshAsync()));
        reader.Release.Set(); first.GetAwaiter().GetResult();
        Assert(reader.Reads == 1 && controller.Devices.Count == 1 && controller.LastUpdated is not null);
        reader.Result = new(Array.Empty<DeviceBattery>(), Array.Empty<string>());
        controller.RefreshAsync().GetAwaiter().GetResult();
        Assert(controller.Devices.Count == 0 && !controller.Alerts.ShouldBlink);
    }),
    ("Exiting discards an in-flight battery result", () =>
    {
        using var reader = new BatteryProbe { Block = true };
        using var controller = new BatteryController(reader, transient: true);
        var changes = 0;
        controller.Changed += () => changes++;
        var task = controller.RefreshAsync();
        Assert(reader.Entered.Wait(2000));
        controller.Dispose();
        reader.Release.Set(); task.GetAwaiter().GetResult();
        Assert(changes == 1 && controller.LastUpdated is null && controller.Devices.Count == 0);
    }),
    ("Audio dropdown applies immediately while refresh and rollback never switch again", () =>
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            System.Windows.Application? app = null;
            CherryDesk.MainWindow? window = null;
            try
            {
                app = new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
                app.Resources.MergedDictionaries.Add(new System.Windows.ResourceDictionary { Source = new Uri("pack://application:,,,/CherryDesk;component/Theme.xaml") });
                using var overlay = new OverlayController(true);
                var firstGpu = new GpuAdapter("a", "a", "First GPU", 100);
                var secondGpu = new GpuAdapter("b", "b", "Second GPU", 200);
                var snapshot = new PerformanceSnapshot(DateTimeOffset.Now, 20, 40, 100, [firstGpu, secondGpu],
                    new Dictionary<string, double> { ["a"] = 10, ["b"] = 90 }, null, null, null)
                {
                    CpuName = "Test CPU", CpuTemperature = 61,
                    GpuTemperatures = new Dictionary<string, double> { ["a"] = 45, ["b"] = 72 }
                };
                overlay.Overlay.UpdateSnapshot(snapshot, firstGpu);
                Assert(overlay.Overlay.Width == 160 && overlay.Overlay.Height == 74);
                Assert(overlay.Overlay.CompactGpuTemperature.Text == "45°C" && overlay.Overlay.CompactCpuTemperature.Text == "61°C");
                Assert(overlay.Overlay.FullPanel.Visibility == System.Windows.Visibility.Collapsed);
                overlay.Overlay.UpdateSnapshot(snapshot, secondGpu);
                Assert(overlay.Overlay.CompactGpuTemperature.Text == "72°C");
                overlay.SetLayout(OverlayLayout.Full);
                Assert(overlay.Overlay.Width == 360 && overlay.Overlay.FullPanel.Visibility == System.Windows.Visibility.Visible);
                Assert(overlay.Overlay.CpuName.Text.Contains("Test CPU") && overlay.Overlay.GpuName.Text.Contains("Second GPU"));
                overlay.SetLayout(OverlayLayout.Compact);
                overlay.Overlay.UpdateSnapshot(snapshot with { CpuTemperature = null, GpuTemperatures = new Dictionary<string, double> { ["a"] = 45 } }, secondGpu);
                Assert(overlay.Overlay.Width == 108 && overlay.Overlay.CompactGpuTemperature.Text == "");
                overlay.Overlay.ClearSnapshot();
                Assert(overlay.Overlay.CompactCpu.Text == "--" && overlay.Overlay.CompactCpuTemperature.Text == "");
                using var battery = new BatteryController(new BatteryProbe(), transient: true);
                var backend = new SelectionBackend();
                window = new CherryDesk.MainWindow(new AudioSwitcher(backend), overlay, battery);
                window.RefreshDevices();
                Assert(backend.Calls == 0 && (string)window.OutputDevices.SelectedValue == "old");
                window.OutputDevices.SelectedValue = "new";
                Assert(backend.Calls == 3 && backend.Defaults[(AudioFlow.Output, AudioRole.Multimedia)] == "new");
                window.InputDevices.SelectedValue = "new";
                Assert(backend.Calls == 6 && backend.Defaults[(AudioFlow.Input, AudioRole.Multimedia)] == "new");
                window.RefreshDevices(); window.RefreshDevices();
                Assert(backend.Calls == 6);
                foreach (var role in Enum.GetValues<AudioRole>()) backend.Defaults[(AudioFlow.Output, role)] = "old";
                window.RefreshDevices();
                Assert(backend.Calls == 6 && (string)window.OutputDevices.SelectedValue == "old");
                window.SyncCommunications.IsChecked = false;
                window.OutputDevices.SelectedValue = "new";
                Assert(backend.Calls == 8 && backend.Defaults[(AudioFlow.Output, AudioRole.Communications)] == "old");
                window.OutputDevices.SelectedValue = "old";
                backend.FailNew = true;
                window.OutputDevices.SelectedValue = "new";
                Assert((string)window.OutputDevices.SelectedValue == "old");
                Assert(backend.Defaults[(AudioFlow.Output, AudioRole.Multimedia)] == "old");
                var calls = backend.Calls;
                window.RefreshDevices(); Assert(backend.Calls == calls);
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                if (window is not null) { window.AllowClose = true; window.Close(); }
                app?.Shutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(10000)) throw new TimeoutException("WPF audio selection test timed out");
        if (failure is not null) throw failure;
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
            var settings = new OverlaySettings { Enabled = false, OverlayVisible = false, Locked = false, Left = -300, Top = 80, Opacity = 0.65, GpuKey = "amd", Layout = OverlayLayout.Full };
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

internal sealed class BatteryProbe : IBatteryReader, IDisposable
{
    public int Reads;
    public bool Block;
    public ManualResetEventSlim Entered { get; } = new(false);
    public ManualResetEventSlim Release { get; } = new(false);
    public BatteryReadResult Result = new(new[] { new DeviceBattery("a", "A", "", 10) }, Array.Empty<string>());
    public BatteryReadResult Read()
    {
        Interlocked.Increment(ref Reads);
        Entered.Set();
        if (Block && !Release.Wait(2000)) throw new TimeoutException("Battery fixture was not released");
        return Result;
    }
    public void Dispose() { Entered.Dispose(); Release.Dispose(); }
}

internal sealed class SelectionBackend : IAudioBackend
{
    public readonly Dictionary<(AudioFlow, AudioRole), string> Defaults = Enum.GetValues<AudioFlow>()
        .SelectMany(flow => Enum.GetValues<AudioRole>().Select(role => (flow, role))).ToDictionary(key => key, _ => "old");
    public int Calls;
    public bool FailNew;
    public IReadOnlyList<AudioDevice> GetDevices(AudioFlow flow) => [new("old", "Old", flow), new("new", "New", flow)];
    public string? GetDefault(AudioFlow flow, AudioRole role) { currentFlow = flow; return Defaults[(flow, role)]; }
    private AudioFlow currentFlow;
    // The test's devices have shared IDs; the flow was last resolved by GetDefault during Switch.
    public void SetDefault(string id, AudioRole role)
    {
        Calls++;
        Defaults[(currentFlow, role)] = id;
        if (FailNew && id == "new") throw new InvalidOperationException("Simulated endpoint failure");
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
