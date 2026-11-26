using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace CherryDesk.Battery;

internal sealed class BatteryController : IDisposable
{
    private readonly IBatteryReader reader;
    private readonly BatterySettingsStore? store;
    private readonly DispatcherTimer timer;
    private Task? refreshTask;
    private bool disposed;
    public IReadOnlyList<DeviceBattery> Devices { get; private set; } = Array.Empty<DeviceBattery>();
    public DateTimeOffset? LastUpdated { get; private set; }
    public IReadOnlyList<string> Errors { get; private set; } = Array.Empty<string>();
    public string? SettingsWarning { get; private set; }
    public int Threshold { get; private set; } = 20;
    public bool IsRefreshing { get; private set; }
    public BatteryAlerts Alerts { get; } = new();
    public event Action? Changed;

    public BatteryController(IBatteryReader? reader = null, string? settingsPath = null, bool transient = false)
    {
        this.reader = reader ?? new WindowsBatteryReader();
        timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(5) };
        timer.Tick += OnTick;
        if (!transient)
        {
            store = new BatterySettingsStore(settingsPath ?? Path.Combine(AppContext.BaseDirectory, "battery-settings.json"));
            try { Threshold = store.Load(); }
            catch (Exception ex) { SettingsWarning = "无法读取提醒设置，暂用 20%。"; App.Log(ex); }
        }
    }

    public void Start() { if (!disposed) { timer.Start(); _ = RefreshAsync(); } }
    private void OnTick(object? sender, EventArgs e) => _ = RefreshAsync();

    // Called from the UI dispatcher. Overlapping manual/timed requests share one read.
    public Task RefreshAsync()
    {
        if (disposed) return Task.CompletedTask;
        if (refreshTask is { IsCompleted: false }) return refreshTask;
        return refreshTask = RefreshCoreAsync();
    }

    private async Task RefreshCoreAsync()
    {
        IsRefreshing = true;
        Changed?.Invoke();
        try
        {
            var result = await Task.Run(reader.Read);
            if (disposed) return;
            Devices = result.Devices.Where(device => device.HasReading).DistinctBy(device => device.Id, StringComparer.OrdinalIgnoreCase)
                .OrderBy(device => device.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
            Errors = result.Errors;
            LastUpdated = DateTimeOffset.Now;
            Alerts.Update(Devices, Threshold);
        }
        catch (Exception ex)
        {
            if (disposed) return;
            Devices = Array.Empty<DeviceBattery>();
            Errors = new[] { "读取设备电量失败，将在下次刷新时重试。" };
            Alerts.Update(Devices, Threshold);
            App.Log(ex);
        }
        finally
        {
            IsRefreshing = false;
            if (!disposed) Changed?.Invoke();
        }
    }

    public void SetThreshold(int threshold)
    {
        Threshold = Math.Clamp(threshold, 1, 100);
        try { store?.Save(Threshold); SettingsWarning = null; }
        catch (Exception ex) { SettingsWarning = "提醒阈值暂时无法保存，当前会话仍然生效。"; App.Log(ex); }
        Alerts.Update(Devices, Threshold);
        Changed?.Invoke();
    }

    public void Acknowledge() { Alerts.Acknowledge(); Changed?.Invoke(); }
    public void Dispose() { disposed = true; timer.Stop(); timer.Tick -= OnTick; }
}
