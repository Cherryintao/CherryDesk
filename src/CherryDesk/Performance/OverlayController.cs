using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace CherryDesk.Performance;

internal sealed class OverlayController : IDisposable
{
    private readonly PerformanceMonitor monitor = new();
    private readonly OverlaySettingsStore? store;
    private readonly Dispatcher dispatcher;
    private readonly DispatcherTimer saveTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private bool disposed;
    private long enableRevision;
    public bool IsTransitioning { get; private set; }
    public bool IsSampling => monitor.IsRunning;
    public long ReadCount => monitor.ReadCount;
    public int ActiveReaders => monitor.ActiveReaders;
    public string? MonitoringWarning { get; private set; }
    public OverlayWindow Overlay { get; }
    public OverlaySettings Settings { get; private set; } = new();
    public PerformanceSnapshot? Latest { get; private set; }
    public string? SettingsWarning { get; private set; }
    public event Action? Changed;

    public OverlayController(bool transient = false)
    {
        dispatcher = Application.Current.Dispatcher;
        if (!transient)
        {
            store = new(Path.Combine(AppContext.BaseDirectory, "overlay-settings.json"));
            try { Settings = store.Load(); }
            catch (Exception ex) { SettingsWarning = "设置文件无法读取，已使用默认设置。"; App.Log(ex); }
        }
        Overlay = new OverlayWindow();
        new WindowInteropHelper(Overlay).EnsureHandle();
        Overlay.MoveToVisiblePosition(Settings.Left, Settings.Top);
        Settings = Settings with { Left = Overlay.Left, Top = Overlay.Top };
        Overlay.ApplySettings(Settings);
        if (Settings.Enabled && Settings.OverlayVisible) Overlay.Show();
        Overlay.ToggleVisibilityRequested += () => { if (Settings.Enabled) SetVisible(!Settings.OverlayVisible); };
        Overlay.ToggleLockRequested += () => { if (Settings.Enabled) SetLocked(!Settings.Locked); };
        Overlay.PositionChanged += (left, top) => Change(Settings with { Left = left, Top = top });
        saveTimer.Tick += (_, _) => { saveTimer.Stop(); Save(); };
        monitor.Updated += (generation, sample) =>
        {
            if (dispatcher.HasShutdownStarted) return;
            dispatcher.InvokeAsync(() =>
            {
                if (disposed || !Settings.Enabled || !monitor.IsRunning || monitor.Generation != generation) return;
                Latest = sample;
                Overlay.UpdateSnapshot(sample, PerformanceMath.SelectGpu(sample.Adapters, Settings.GpuKey));
                Changed?.Invoke();
            });
        };
        monitor.Failed += ex => dispatcher.InvokeAsync(() =>
        {
            if (disposed) return;
            MonitoringWarning = "采集暂不可用，请关闭后重新启用监控。";
            Changed?.Invoke();
        });
        _ = SetEnabledAsync(Settings.Enabled);
    }

    public async Task SetEnabledAsync(bool enabled)
    {
        if (disposed) return;
        var revision = ++enableRevision;
        IsTransitioning = true;
        MonitoringWarning = null;
        if (!enabled) Latest = null;
        Change(Settings with { Enabled = enabled });
        Changed?.Invoke();
        try { await monitor.SetEnabledAsync(enabled); }
        catch (ObjectDisposedException) when (disposed) { }
        catch (Exception ex) { MonitoringWarning = "监控状态切换失败，请重试。"; App.Log(ex); }
        finally
        {
            if (!disposed && revision == enableRevision) { IsTransitioning = false; Changed?.Invoke(); }
        }
    }

    public void SetVisible(bool visible) { if (Settings.Enabled) Change(Settings with { OverlayVisible = visible }); }
    public void SetLocked(bool locked) => Change(Settings with { Locked = locked });
    public void SetOpacity(double opacity) => Change((Settings with { Opacity = opacity }).Normalize());
    public void SelectGpu(string key) => Change(Settings with { GpuKey = key });
    public void ResetPosition()
    {
        Overlay.MoveToVisiblePosition(SystemParameters.WorkArea.Left + 24, SystemParameters.WorkArea.Top + 24);
        Change(Settings with { Left = Overlay.Left, Top = Overlay.Top });
    }

    private void Change(OverlaySettings value)
    {
        if (disposed || value == Settings) return;
        Settings = value;
        Overlay.ApplySettings(Settings);
        if (Settings.Enabled && Settings.OverlayVisible) { if (!Overlay.IsVisible) Overlay.Show(); }
        else Overlay.Hide();
        if (Latest is { } sample) Overlay.UpdateSnapshot(sample, PerformanceMath.SelectGpu(sample.Adapters, Settings.GpuKey));
        Changed?.Invoke();
        saveTimer.Stop();
        saveTimer.Start();
    }

    private void Save()
    {
        if (store is null) return;
        try { store.Save(Settings); SettingsWarning = null; }
        catch (Exception ex) { SettingsWarning = "设置暂时无法保存，本次使用仍然有效。"; App.Log(ex); }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        saveTimer.Stop();
        Save();
        monitor.Dispose();
        Overlay.AllowClose = true;
        Overlay.Close();
    }
}
