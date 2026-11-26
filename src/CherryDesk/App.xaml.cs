using System;
using System.IO;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CherryDesk.Audio;
using CherryDesk.Battery;
using CherryDesk.Performance;
using Forms = System.Windows.Forms;

namespace CherryDesk;

public partial class App : Application
{
    private Mutex? instanceMutex;
    private bool ownsMutex;
    private Forms.NotifyIcon? tray;
    private Forms.ContextMenuStrip? menu;
    private MainWindow? window;
    private OverlayController? performance;
    private BatteryController? battery;
    private BatteryTrayIcons? trayIcons;
    private readonly DispatcherTimer batteryBlink = new() { Interval = TimeSpan.FromMilliseconds(650) };
    private bool blinkPhase;
    private int blinkTicks;
    private readonly AudioSwitcher audio = new(new CoreAudioBackend());

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Contains("--battery-diagnose"))
        {
            var index = Array.IndexOf(e.Args, "--battery-diagnose");
            var path = index + 1 < e.Args.Length ? e.Args[index + 1] : "battery-diagnostics.json";
            try
            {
                var watch = Stopwatch.StartNew();
                var result = new WindowsBatteryReader().Read();
                File.WriteAllText(path, JsonSerializer.Serialize(new { CollectionMilliseconds = watch.Elapsed.TotalMilliseconds, Result = result }, new JsonSerializerOptions { WriteIndented = true }));
                Shutdown(result.Errors.Count == 0 ? 0 : 1);
            }
            catch (Exception ex) { Log(ex); Shutdown(1); }
            return;
        }
        if (e.Args.Contains("--performance-diagnose"))
        {
            DiagnosePerformance(e.Args);
            return;
        }
        if (e.Args.Contains("--diagnose") || e.Args.Contains("--verify-defaults"))
        {
            Diagnose(e.Args);
            return;
        }
        instanceMutex = new Mutex(true, @"Local\CherryDesk.Instance.v1", out ownsMutex);
        if (!ownsMutex)
        {
            MessageBox.Show("CherryDesk 已经运行，请双击任务栏右下角的托盘图标打开。", "CherryDesk");
            Shutdown();
            return;
        }
        DispatcherUnhandledException += (_, args) =>
        {
            Log(args.Exception);
            MessageBox.Show("发生错误：" + args.Exception.Message, "CherryDesk");
            performance?.Dispose();
            battery?.Dispose();
            if (window is not null) window.AllowClose = true;
            Shutdown(1);
            args.Handled = true;
        };

        var overlaySmoke = e.Args.Contains("--overlay-smoke-test");
        var batterySmoke = e.Args.Contains("--battery-smoke-test");
        var smoke = overlaySmoke || batterySmoke || e.Args.Contains("--smoke-test");
        performance = new OverlayController(smoke);
        battery = new BatteryController(batterySmoke ? new PreviewBatteryReader() : null, transient: smoke);
        window = new MainWindow(audio, performance, battery);
        MainWindow = window;
        menu = new Forms.ContextMenuStrip();
        menu.Opening += (_, _) => RebuildTrayMenu();
        tray = new Forms.NotifyIcon
        {
            Text = BatteryPresentation.Tooltip(Array.Empty<DeviceBattery>()),
            Icon = (trayIcons = new BatteryTrayIcons()).Normal,
            ContextMenuStrip = menu,
            Visible = true
        };
        tray.DoubleClick += (_, _) => ShowMainWindow();
        tray.MouseClick += (_, args) => { if (args.Button == Forms.MouseButtons.Left && battery.Alerts.Low.Count > 0) ShowBatteryPage(); };
        battery.Changed += RefreshBatteryTray;
        batteryBlink.Tick += (_, _) =>
        {
            blinkTicks++;
            blinkPhase = !blinkPhase;
            if (tray is not null && trayIcons is not null) tray.Icon = blinkPhase ? trayIcons.Low : trayIcons.Normal;
        };
        window.Show();
        battery.Start();
        SessionEnding += (_, _) =>
        {
            performance.Dispose();
            battery.Dispose();
            window.AllowClose = true;
        };
        if (overlaySmoke) _ = performance.SetEnabledAsync(true);
        if (overlaySmoke || e.Args.Contains("--show-performance")) window.SelectPage("performance");
        if (batterySmoke || e.Args.Contains("--show-battery")) window.SelectPage("battery");
        if (smoke)
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(overlaySmoke ? 6 : 3) };
            timer.Tick += async (_, _) =>
            {
                timer.Stop();
                try
                {
                    // Render our own WPF content for deterministic layout QA; no screen capture
                    // and no changes to actual endpoint preferences are required.
                    RebuildTrayMenu();
                    var nativeLockVerified = false;
                    var hideShowVerified = false;
                    var focusPreserved = false;
                    var idleSamplingVerified = !overlaySmoke && !performance.IsSampling && performance.ReadCount == 0 && performance.ActiveReaders == 0;
                    if (!overlaySmoke && !idleSamplingVerified) throw new InvalidOperationException("Default startup unexpectedly collects performance data");
                    if (overlaySmoke)
                    {
                        if (performance.Latest?.CpuPercent is null || performance.Latest.MemoryTotal is null)
                            throw new InvalidOperationException("CPU/memory sampling check failed");
                        var previousForeground = OverlayWindow.ForegroundHandle;
                        performance.SetLocked(false);
                        if (performance.Overlay.HasNativeClickThrough) throw new InvalidOperationException("Unlock check failed");
                        performance.SetLocked(true);
                        nativeLockVerified = performance.Overlay.HasNativeClickThrough && performance.Overlay.HasNativeNoActivate;
                        if (!nativeLockVerified) throw new InvalidOperationException("Native overlay styles missing");
                        performance.SetVisible(false);
                        hideShowVerified = !performance.Overlay.IsVisible;
                        performance.SetVisible(true);
                        hideShowVerified &= performance.Overlay.IsVisible;
                        if (!hideShowVerified) throw new InvalidOperationException("Overlay visibility check failed");
                        focusPreserved = OverlayWindow.ForegroundHandle == previousForeground;
                        if (!focusPreserved) throw new InvalidOperationException("Overlay stole foreground focus");
                        performance.SetOpacity(0.75);
                        if (Math.Abs(performance.Overlay.Opacity - 0.75) > 0.001) throw new InvalidOperationException("Opacity check failed");
                        performance.SetOpacity(0.9);
                    }
                    window.UpdateLayout();
                    var content = (FrameworkElement)window.Content;
                    var index = Array.IndexOf(e.Args, overlaySmoke ? "--overlay-smoke-test" : batterySmoke ? "--battery-smoke-test" : "--smoke-test");
                    var path = index + 1 < e.Args.Length ? e.Args[index + 1] : "ui-preview.png";
                    var trayAlertVerified = false;
                    if (batterySmoke)
                    {
                        await battery.RefreshAsync();
                        window.SelectPage("audio");
                        var ticksBefore = blinkTicks;
                        battery.SetThreshold(80); // The previously acknowledged fixture mouse becomes newly low.
                        if (!batteryBlink.IsEnabled || !tray.Text.Contains("WH-1000XM5")) throw new InvalidOperationException("Tray alert did not start or lacked device details");
                        await Task.Delay(1100);
                        if (blinkTicks <= ticksBefore) throw new InvalidOperationException("Tray icon timer did not tick");
                        window.SelectPage("battery");
                        battery.Acknowledge();
                        if (batteryBlink.IsEnabled || tray.Icon != trayIcons!.Low) throw new InvalidOperationException("Acknowledging the alert did not stop blinking");
                        battery.SetThreshold(20);
                        trayAlertVerified = true;
                    }
                    RenderPreview(window, path);
                    if (batterySmoke)
                    {
                        if (battery.Devices.Count != 3 || battery.Alerts.Low.Count != 2) throw new InvalidOperationException("Battery preview fixture failed");
                        window.SelectPage("audio");
                        RenderPreview(window, Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + "-audio.png"));
                    }
                    if (overlaySmoke)
                        RenderPreview(performance.Overlay, Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + "-overlay.png"));
                    var liveSnapshot = performance.Latest;
                    var disabledSamplingVerified = false;
                    if (overlaySmoke)
                    {
                        await performance.SetEnabledAsync(false);
                        var reads = performance.ReadCount;
                        await Task.Delay(1200);
                        disabledSamplingVerified = !performance.IsSampling && performance.ActiveReaders == 0
                            && performance.ReadCount == reads && performance.Latest is null && !performance.Overlay.IsVisible;
                        if (!disabledSamplingVerified) throw new InvalidOperationException("Disabled monitoring still samples or retains its collector");
                        RenderPreview(window, Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + "-disabled.png"));
                        window.SelectPage("audio");
                        RenderPreview(window, Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + "-audio.png"));
                        window.SelectPage("shutdown");
                        RenderPreview(window, Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + "-shutdown.png"));
                    }
                    File.WriteAllText(Path.ChangeExtension(path, ".json"), JsonSerializer.Serialize(new
                    {
                        Status = window.StatusText.Text,
                        Width = content.ActualWidth,
                        Height = content.ActualHeight,
                        OutputCount = window.OutputDevices.Items.Count,
                        InputCount = window.InputDevices.Items.Count,
                        OutputEnabled = window.OutputDevices.IsEnabled,
                        InputEnabled = window.InputDevices.IsEnabled,
                        Battery = battery.Devices,
                        BatteryUpdated = battery.LastUpdated,
                        BatteryErrors = battery.Errors,
                        BatteryLowCount = battery.Alerts.Low.Count,
                        UsesSimulatedBattery = batterySmoke,
                        TrayAlertVerified = trayAlertVerified,
                        TrayItems = menu!.Items.Count,
                        NativeLockVerified = nativeLockVerified,
                        HideShowVerified = hideShowVerified,
                        FocusPreserved = focusPreserved,
                        Performance = liveSnapshot,
                        DisabledSamplingVerified = disabledSamplingVerified,
                        IdleSamplingVerified = idleSamplingVerified,
                        ReadCount = performance.ReadCount,
                        ActiveReaders = performance.ActiveReaders,
                        HotkeyWarning = performance.Overlay.HotkeyWarning
                    }, new JsonSerializerOptions { WriteIndented = true }));
                    ExitApplication();
                }
                catch (Exception ex)
                {
                    Log(ex);
                    performance.Dispose();
                    battery.Dispose();
                    window.AllowClose = true;
                    Shutdown(1);
                }
            };
            timer.Start();
        }
    }

    private sealed class PreviewBatteryReader : IBatteryReader
    {
        public BatteryReadResult Read() => new(new[]
        {
            new DeviceBattery("preview:headset", "WH-1000XM5", "蓝牙 · 已连接", 15),
            new DeviceBattery("preview:mouse", "示例鼠标（仅用于界面验证）", "演示数据", 76),
            new DeviceBattery("preview:xbox", "Xbox 手柄 1", "无线 · XInput", null, BatteryLevel.Low)
        }, Array.Empty<string>());
    }

    private void RefreshBatteryTray()
    {
        if (battery is null || tray is null || trayIcons is null) return;
        tray.Text = BatteryPresentation.Tooltip(battery.Alerts.Low);
        if (battery.Alerts.ShouldBlink)
        {
            if (!batteryBlink.IsEnabled) { blinkPhase = true; tray.Icon = trayIcons.Low; batteryBlink.Start(); }
        }
        else
        {
            batteryBlink.Stop();
            tray.Icon = battery.Alerts.Low.Count > 0 ? trayIcons.Low : trayIcons.Normal;
        }
    }

    private void ShowBatteryPage()
    {
        if (window is null) return;
        window.SelectPage("battery");
        ShowMainWindow();
        battery?.Acknowledge();
    }

    private static void RenderPreview(Window target, string path)
    {
        target.UpdateLayout();
        var preview = new RenderTargetBitmap((int)Math.Ceiling(target.ActualWidth),
            (int)Math.Ceiling(target.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        preview.Render(target);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(preview));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private void DiagnosePerformance(string[] args)
    {
        var index = Array.IndexOf(args, "--performance-diagnose");
        var path = index + 1 < args.Length ? args[index + 1] : "performance-diagnostics.json";
        try
        {
            using var reader = new SystemPerformanceReader();
            var samples = new List<object>();
            for (var sample = 0; sample < 6; sample++)
            {
                var watch = Stopwatch.StartNew();
                var snapshot = reader.Read();
                samples.Add(new { CollectionMilliseconds = watch.Elapsed.TotalMilliseconds, Snapshot = snapshot });
                if (sample < 5) Thread.Sleep(1000);
            }
            File.WriteAllText(path, JsonSerializer.Serialize(samples, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
            Shutdown(0);
        }
        catch (Exception ex)
        {
            Log(ex);
            File.WriteAllText(path, JsonSerializer.Serialize(new { Error = ex.ToString() }), Encoding.UTF8);
            Shutdown(1);
        }
    }

    private void Diagnose(string[] args)
    {
        var verify = args.Contains("--verify-defaults");
        var index = Array.IndexOf(args, verify ? "--verify-defaults" : "--diagnose");
        var path = index + 1 < args.Length ? args[index + 1] : "audio-diagnostics.json";
        try
        {
            var verifiedWrites = 0;
            if (verify)
            {
                // Exercise the real PolicyConfig setter only with each role's existing value.
                // This validates the ABI without routing the user's sound to another device.
                var backend = new CoreAudioBackend();
                foreach (var flow in Enum.GetValues<AudioFlow>())
                foreach (var role in Enum.GetValues<AudioRole>())
                {
                    var original = backend.GetDefault(flow, role);
                    if (original is null) continue;
                    backend.SetDefault(original, role);
                    if (backend.GetDefault(flow, role) != original)
                        throw new InvalidOperationException("默认设备写回检查未通过，请检查声音设置。");
                    verifiedWrites++;
                }
            }
            var snapshot = new
            {
                Timestamp = DateTimeOffset.Now,
                OS = Environment.OSVersion.ToString(),
                Runtime = Environment.Version.ToString(),
                VerifiedDefaultWrites = verifiedWrites,
                Output = audio.Read(AudioFlow.Output),
                Input = audio.Read(AudioFlow.Input)
            };
            File.WriteAllText(path, JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
            Shutdown(0);
        }
        catch (Exception ex)
        {
            Log(ex);
            try { File.WriteAllText(path, JsonSerializer.Serialize(new { Error = ex.ToString() }), Encoding.UTF8); }
            catch { }
            Shutdown(1);
        }
    }

    private void RebuildTrayMenu()
    {
        if (menu is null || window is null) return;
        // Clear does not dispose old ToolStripItems and their event handlers.
        while (menu.Items.Count > 0) menu.Items[0].Dispose();
        menu.Items.Add("打开 CherryDesk", null, (_, _) => ShowMainWindow());
        menu.Items.Add(new Forms.ToolStripSeparator());
        try
        {
            AddDevices("声音输出", AudioFlow.Output);
            AddDevices("麦克风输入", AudioFlow.Input);
        }
        catch (Exception ex)
        {
            menu.Items.Add(new Forms.ToolStripMenuItem("读取设备失败，请打开窗口刷新") { Enabled = false });
            Log(ex);
        }
        var communication = new Forms.ToolStripMenuItem("同时切换默认通信设备")
        { Checked = window.IncludeCommunications, CheckOnClick = true };
        communication.Click += (_, _) => window.SyncCommunications.IsChecked = communication.Checked;
        menu.Items.Add(communication);
        if (battery is not null)
        {
            var group = new Forms.ToolStripMenuItem("设备电量");
            group.DropDownItems.Add("查看设备电量", null, (_, _) => ShowBatteryPage());
            foreach (var device in battery.Devices)
                group.DropDownItems.Add(new Forms.ToolStripMenuItem($"{device.Name.Replace("&", "&&")}  {device.ValueText}", null, (_, _) => ShowBatteryPage()));
            if (battery.Devices.Count == 0) group.DropDownItems.Add(new Forms.ToolStripMenuItem("暂未读取到设备电量") { Enabled = false });
            group.DropDownItems.Add(new Forms.ToolStripSeparator());
            var refresh = group.DropDownItems.Add(battery.IsRefreshing ? "正在更新…" : "立即更新", null, async (_, _) => await battery.RefreshAsync());
            refresh.Enabled = !battery.IsRefreshing;
            menu.Items.Add(group);
        }
        if (performance is not null)
        {
            var group = new Forms.ToolStripMenuItem("性能悬浮窗");
            var visible = new Forms.ToolStripMenuItem("显示悬浮窗")
            { Checked = performance.Settings.Enabled, ShortcutKeyDisplayString = "Ctrl+Shift+F10" };
            visible.Checked = performance.Settings.Enabled && performance.Settings.OverlayVisible;
            visible.Enabled = performance.Settings.Enabled;
            visible.Click += (_, _) => performance.SetVisible(!performance.Settings.OverlayVisible);
            var enabled = new Forms.ToolStripMenuItem("启用性能监控") { Checked = performance.Settings.Enabled };
            enabled.Click += async (_, _) => await performance.SetEnabledAsync(!performance.Settings.Enabled);
            var locked = new Forms.ToolStripMenuItem("锁定位置并穿透鼠标")
            { Checked = performance.Settings.Locked, ShortcutKeyDisplayString = "Ctrl+Shift+F11" };
            locked.Click += (_, _) => performance.SetLocked(!performance.Settings.Locked);
            locked.Enabled = performance.Settings.Enabled;
            group.DropDownItems.Add(enabled);
            group.DropDownItems.Add(visible);
            group.DropDownItems.Add(locked);
            var reset = group.DropDownItems.Add("重置位置", null, (_, _) => { performance.ResetPosition(); performance.SetVisible(true); });
            reset.Enabled = performance.Settings.Enabled;
            menu.Items.Add(group);
        }
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => ExitApplication());
    }

    private void AddDevices(string title, AudioFlow flow)
    {
        var state = audio.Read(flow);
        var group = new Forms.ToolStripMenuItem(title);
        if (state.Devices.Count == 0)
            group.DropDownItems.Add(new Forms.ToolStripMenuItem("没有可用设备") { Enabled = false });
        foreach (var device in state.Devices)
        {
            var item = new Forms.ToolStripMenuItem(device.Name.Replace("&", "&&"))
            { Checked = device.Id == state.Defaults[AudioRole.Multimedia] };
            item.Click += (_, _) => window!.ApplyDevice(device);
            group.DropDownItems.Add(item);
        }
        menu!.Items.Add(group);
    }

    private void ShowMainWindow()
    {
        if (window is null) return;
        window.RefreshDevices();
        window.Show();
        window.WindowState = WindowState.Normal;
        window.Activate();
    }

    private void ExitApplication()
    {
        performance?.Dispose();
        battery?.Dispose();
        if (window is not null) window.AllowClose = true;
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        performance?.Dispose();
        batteryBlink.Stop();
        if (battery is not null) { battery.Changed -= RefreshBatteryTray; battery.Dispose(); }
        if (tray is not null) { tray.Visible = false; tray.Dispose(); }
        trayIcons?.Dispose();
        menu?.Dispose();
        if (ownsMutex) instanceMutex?.ReleaseMutex();
        instanceMutex?.Dispose();
        base.OnExit(e);
    }

    internal static void Log(Exception ex)
    {
        try
        {
            var folder = Path.Combine(AppContext.BaseDirectory, "logs");
            Directory.CreateDirectory(folder);
            File.AppendAllText(Path.Combine(folder, "app.log"), $"{DateTimeOffset.Now:O} {ex}\n", Encoding.UTF8);
        }
        catch { /* A read-only location must not turn error reporting into another crash. */ }
    }
}
