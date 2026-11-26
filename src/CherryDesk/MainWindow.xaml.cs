using System;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using CherryDesk.Audio;
using CherryDesk.Performance;

namespace CherryDesk;

public partial class MainWindow : Window
{
    private readonly AudioSwitcher audio;
    private readonly OverlayController performance;
    private readonly DispatcherTimer refreshTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private bool initialized;
    private bool refreshing;
    private bool syncingPerformance;
    private string activePage = "audio";
    private string lastAudioStatus = "正在读取音频设备…";
    internal bool AllowClose { get; set; }
    internal bool IncludeCommunications => SyncCommunications.IsChecked == true;
    internal event Action? AudioChanged;

    internal MainWindow(AudioSwitcher audio, OverlayController performance)
    {
        this.audio = audio;
        this.performance = performance;
        InitializeComponent();
        MinHeight = Math.Min(MinHeight, Math.Max(400, SystemParameters.WorkArea.Height - 32));
        Height = Math.Max(MinHeight, Math.Min(Height, SystemParameters.WorkArea.Height - 32));
        initialized = true;
        SelectPage("audio");
        performance.Changed += RefreshPerformance;
        RefreshPerformance();
        refreshTimer.Tick += (_, _) => RefreshDevices();
        Loaded += (_, _) => { RefreshDevices(); refreshTimer.Start(); };
        Closing += OnClosing;
        Closed += (_, _) => { refreshTimer.Stop(); performance.Changed -= RefreshPerformance; };
    }

    internal void RefreshDevices(bool reportSuccess = false)
    {
        try
        {
            refreshing = true;
            var output = audio.Read(AudioFlow.Output);
            var input = audio.Read(AudioFlow.Input);
            UpdateDevices(OutputDevices, output);
            UpdateDevices(InputDevices, input);
            OutputCurrent.Text = "当前输出  " + DefaultName(output, AudioRole.Multimedia);
            InputCurrent.Text = "当前麦克风  " + DefaultName(input, AudioRole.Multimedia);
            OutputCommunication.Text = "默认通信输出  " + DefaultName(output, AudioRole.Communications);
            InputCommunication.Text = "默认通信输入  " + DefaultName(input, AudioRole.Communications);
            ApplyOutput.IsEnabled = output.Devices.Count > 0;
            ApplyInput.IsEnabled = input.Devices.Count > 0;
            OutputDevices.IsEnabled = ApplyOutput.IsEnabled;
            InputDevices.IsEnabled = ApplyInput.IsEnabled;
            LastRefresh.Text = $"更新于 {DateTime.Now:HH:mm:ss} · 每 3 秒自动检测";
            if (reportSuccess || lastAudioStatus == "正在读取音频设备…")
                ShowStatus(output.Devices.Count + input.Devices.Count == 0
                    ? "未发现可用的音频设备，请连接耳机或麦克风。"
                    : "设备已就绪，可在窗口或托盘菜单中切换。", false);
        }
        catch (Exception ex)
        {
            ApplyOutput.IsEnabled = ApplyInput.IsEnabled = false;
            ShowStatus("读取设备失败：" + ex.Message, true);
            App.Log(ex);
        }
        finally { refreshing = false; }
    }

    private static string DefaultName(AudioState state, AudioRole role) =>
        state.Devices.FirstOrDefault(device => device.Id == state.Defaults[role])?.Name
        ?? (state.Defaults[role] is null ? "未设置" : "设备暂不可用");

    private static void UpdateDevices(ComboBox combo, AudioState state)
    {
        var old = (AudioDevice?)combo.SelectedItem;
        var existing = combo.ItemsSource as System.Collections.Generic.IReadOnlyList<AudioDevice>;
        if (existing is null || !existing.SequenceEqual(state.Devices))
        {
            combo.ItemsSource = state.Devices;
            combo.SelectedValue = state.Devices.Any(device => device.Id == old?.Id)
                ? old!.Id : state.Defaults[AudioRole.Multimedia];
            if (combo.SelectedIndex < 0 && state.Devices.Count > 0) combo.SelectedIndex = 0;
        }
    }

    internal void ApplyDevice(AudioDevice device)
    {
        try
        {
            audio.Switch(device.Flow, device.Id, IncludeCommunications);
            // Keep selection consistent when applying from the tray.
            var combo = device.Flow == AudioFlow.Output ? OutputDevices : InputDevices;
            combo.SelectedValue = device.Id;
            RefreshDevices();
            ShowStatus($"已切换{(device.Flow == AudioFlow.Output ? "输出" : "麦克风")}：{device.Name}", false);
            AudioChanged?.Invoke();
        }
        catch (Exception ex)
        {
            RefreshDevices();
            ShowStatus(ex.Message, true);
            App.Log(ex);
            Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
        }
    }

    private void ShowStatus(string message, bool error)
    {
        lastAudioStatus = message;
        if (activePage != "audio") return;
        StatusText.Text = message;
        StatusText.Foreground = new SolidColorBrush(error ? Color.FromRgb(248, 81, 73) : Color.FromRgb(204, 204, 204));
    }

    private void SwitchOutput(object sender, RoutedEventArgs e)
    { if (OutputDevices.SelectedItem is AudioDevice device) ApplyDevice(device); }
    private void SwitchInput(object sender, RoutedEventArgs e)
    { if (InputDevices.SelectedItem is AudioDevice device) ApplyDevice(device); }
    private void ManualRefresh(object sender, RoutedEventArgs e) => RefreshDevices(true);
    private void SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (initialized && !refreshing) ShowStatus("已选择设备，点击切换按钮后生效。", false);
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (AllowClose) return;
        e.Cancel = true;
        Hide();
    }

    private sealed record GpuChoice(string Key, string Name);

    private void RefreshPerformance()
    {
        syncingPerformance = true;
        try
        {
            var settings = performance.Settings;
            MonitoringEnabled.IsChecked = settings.Enabled;
            MonitoringEnabled.IsEnabled = !performance.IsTransitioning;
            PerformanceSettingsPanel.IsEnabled = settings.Enabled && !performance.IsTransitioning;
            MonitoringDisabledPanel.Visibility = settings.Enabled ? Visibility.Collapsed : Visibility.Visible;
            LivePanel.Visibility = settings.Enabled ? Visibility.Visible : Visibility.Collapsed;
            DisabledNote.Text = performance.IsTransitioning ? "正在停止监控…" : "采集已停止。启用后查看 CPU、GPU 和内存占用。";
            MonitoringIndicator.Fill = new SolidColorBrush(settings.Enabled ? Color.FromRgb(0, 120, 212) : Color.FromRgb(97, 97, 97));
            ShowOverlay.IsChecked = settings.OverlayVisible;
            LockOverlay.IsChecked = settings.Locked;
            OverlayOpacity.Value = settings.Opacity * 100;
            OpacityValue.Text = $"{settings.Opacity:P0}";
            LiveCpu.Text = performance.Latest is null ? "--" : performance.Overlay.CpuText.Text;
            LiveGpu.Text = performance.Latest is null ? "--" : performance.Overlay.GpuText.Text;
            LiveMemory.Text = performance.Latest is null ? "--" : performance.Overlay.MemoryText.Text;
            LiveMemoryDetail.Text = performance.Latest is null ? "正在采样…" : performance.Overlay.MemoryDetail.Text;
            var choices = new[] { new GpuChoice("", "自动选择（优先独立显卡）") }
                .Concat(performance.Latest?.Adapters.Select(adapter => new GpuChoice(adapter.Key, adapter.DisplayName))
                    ?? Enumerable.Empty<GpuChoice>()).ToArray();
            if (GpuDevices.ItemsSource is not GpuChoice[] old || !old.SequenceEqual(choices))
                GpuDevices.ItemsSource = choices;
            GpuDevices.SelectedValue = choices.Any(choice => choice.Key == settings.GpuKey) ? settings.GpuKey : "";
            var note = settings.Enabled ? "正在采样…" : "";
            if (performance.Latest is { } snapshot)
            {
                var adapter = PerformanceMath.SelectGpu(snapshot.Adapters, settings.GpuKey);
                var error = snapshot.CpuError ?? snapshot.MemoryError ?? snapshot.GpuError;
                if (error is not null) note = error;
                else if (adapter is null || !snapshot.GpuUsage.ContainsKey(adapter.Id)) note = "当前显卡暂无有效使用率，显示 --。";
                else note = $"{adapter.DisplayName} · 每秒更新";
            }
            PerformanceNote.Text = string.Join("\n", new[] { note, performance.MonitoringWarning,
                settings.Enabled ? performance.Overlay.HotkeyWarning : null, performance.SettingsWarning }
                .Where(value => !string.IsNullOrWhiteSpace(value)));
            PerformanceNote.Visibility = string.IsNullOrWhiteSpace(PerformanceNote.Text) ? Visibility.Collapsed : Visibility.Visible;
            RefreshStatusBar();
        }
        finally { syncingPerformance = false; }
    }

    private void OverlayVisibilityChanged(object sender, RoutedEventArgs e)
    { if (initialized && !syncingPerformance) performance.SetVisible(ShowOverlay.IsChecked == true); }
    private void OverlayLockChanged(object sender, RoutedEventArgs e)
    { if (initialized && !syncingPerformance) performance.SetLocked(LockOverlay.IsChecked == true); }
    private void OpacityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    { if (initialized && !syncingPerformance) performance.SetOpacity(e.NewValue / 100); }
    private void GpuSelectionChanged(object sender, SelectionChangedEventArgs e)
    { if (initialized && !syncingPerformance && GpuDevices.SelectedItem is GpuChoice choice) performance.SelectGpu(choice.Key); }
    private void ResetOverlayPosition(object sender, RoutedEventArgs e) => performance.ResetPosition();
    private async void MonitoringEnabledChanged(object sender, RoutedEventArgs e)
    {
        if (initialized && !syncingPerformance) await performance.SetEnabledAsync(MonitoringEnabled.IsChecked == true);
    }

    private void NavigationChanged(object sender, RoutedEventArgs e)
    {
        if (!initialized) return;
        SelectPage(ReferenceEquals(sender, PerformanceNavigation) ? "performance" : ReferenceEquals(sender, ShutdownNavigation) ? "shutdown" : "audio");
    }

    internal void SelectPage(string page)
    {
        activePage = page;
        AudioNavigation.IsChecked = page == "audio";
        PerformanceNavigation.IsChecked = page == "performance";
        ShutdownNavigation.IsChecked = page == "shutdown";
        AudioPage.Visibility = page == "audio" ? Visibility.Visible : Visibility.Collapsed;
        PerformancePage.Visibility = page == "performance" ? Visibility.Visible : Visibility.Collapsed;
        ShutdownPage.Visibility = page == "shutdown" ? Visibility.Visible : Visibility.Collapsed;
        SectionPath.Text = "设置  /  " + (page == "performance" ? "性能监控" : page == "shutdown" ? "关机助手" : "音频设备");
        RefreshStatusBar();
    }

    private void RefreshStatusBar()
    {
        StatusText.Foreground = new SolidColorBrush(Color.FromRgb(204, 204, 204));
        StatusText.Text = activePage switch
        {
            "performance" => performance.IsTransitioning ? "正在切换监控状态…"
                : !performance.Settings.Enabled ? "性能监控已关闭 · 未采集数据"
                : !performance.IsSampling ? "性能采集暂不可用"
                : performance.Settings.OverlayVisible ? "监控运行中 · 每秒更新" : "监控运行中 · 悬浮窗已隐藏",
            "shutdown" => "关机助手尚未启用",
            _ => lastAudioStatus
        };
    }

    private void MinimizeWindow(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void MaximizeWindow(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void CloseWindow(object sender, RoutedEventArgs e) => Close();
}
