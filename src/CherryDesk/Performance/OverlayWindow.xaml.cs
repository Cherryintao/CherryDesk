using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Forms = System.Windows.Forms;

namespace CherryDesk.Performance;

public partial class OverlayWindow : Window
{
    private const int ExtendedStyle = -20, TransparentStyle = 0x20, ToolWindowStyle = 0x80, NoActivateStyle = 0x08000000;
    private const int VisibilityHotkey = 70, LockHotkey = 71;
    private IntPtr handle;
    private bool locked = true, visibilityHotkeyRegistered, lockHotkeyRegistered;
    internal bool AllowClose { get; set; }
    internal string? HotkeyWarning { get; private set; }
    internal event Action? ToggleVisibilityRequested;
    internal event Action? ToggleLockRequested;
    internal event Action<double, double>? PositionChanged;

    public OverlayWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            handle = new WindowInteropHelper(this).Handle;
            HwndSource.FromHwnd(handle)!.AddHook(WindowMessage);
            ApplyNativeStyle();
            visibilityHotkeyRegistered = RegisterHotKey(handle, VisibilityHotkey, 0x4006, 0x79); // Ctrl+Shift+F10
            lockHotkeyRegistered = RegisterHotKey(handle, LockHotkey, 0x4006, 0x7A); // Ctrl+Shift+F11
            if (!visibilityHotkeyRegistered || !lockHotkeyRegistered)
                HotkeyWarning = "部分快捷键被其他程序占用，仍可使用托盘或主窗口控制悬浮窗。";
        };
        Closing += (_, e) => { if (!AllowClose) { e.Cancel = true; Hide(); ToggleVisibilityRequested?.Invoke(); } };
        Closed += (_, _) =>
        {
            if (visibilityHotkeyRegistered) UnregisterHotKey(handle, VisibilityHotkey);
            if (lockHotkeyRegistered) UnregisterHotKey(handle, LockHotkey);
        };
    }

    internal void ApplySettings(OverlaySettings settings)
    {
        locked = settings.Locked;
        Opacity = settings.Opacity;
        ModeText.Text = locked ? "已锁定 · 鼠标穿透" : "已解锁 · 拖动调整位置";
        Panel.Cursor = locked ? Cursors.Arrow : Cursors.SizeAll;
        ApplyNativeStyle();
    }

    internal void MoveToVisiblePosition(double left, double top)
    {
        // WPF's system-aware desktop coordinates are DIPs, Screen rectangles are pixels.
        var scaleX = SystemParameters.PrimaryScreenWidth / Forms.Screen.PrimaryScreen!.Bounds.Width;
        var scaleY = SystemParameters.PrimaryScreenHeight / Forms.Screen.PrimaryScreen.Bounds.Height;
        var proposed = new Rect(left, top, Width, Height);
        var bestArea = SystemParameters.WorkArea;
        var bestOverlap = 0.0;
        foreach (var screen in Forms.Screen.AllScreens)
        {
            var work = screen.WorkingArea;
            var area = new Rect(work.Left * scaleX, work.Top * scaleY, work.Width * scaleX, work.Height * scaleY);
            var overlap = Rect.Intersect(proposed, area);
            var size = overlap.IsEmpty ? 0 : overlap.Width * overlap.Height;
            if (size > bestOverlap) { bestOverlap = size; bestArea = area; }
        }
        var x = bestOverlap == 0 ? bestArea.Left + 24 : left;
        var y = bestOverlap == 0 ? bestArea.Top + 24 : top;
        Left = Math.Clamp(x, bestArea.Left, Math.Max(bestArea.Left, bestArea.Right - Width));
        Top = Math.Clamp(y, bestArea.Top, Math.Max(bestArea.Top, bestArea.Bottom - Height));
    }

    internal void UpdateSnapshot(PerformanceSnapshot snapshot, GpuAdapter? adapter)
    {
        SetMetric(CpuText, CpuBar, snapshot.CpuPercent);
        double? gpuUsage = adapter is not null && snapshot.GpuUsage.TryGetValue(adapter.Id, out var usage) ? usage : null;
        SetMetric(GpuText, GpuBar, gpuUsage);
        var memoryUsage = snapshot.MemoryTotal is > 0 && snapshot.MemoryUsed is { } used
            ? (double)used / snapshot.MemoryTotal.Value * 100 : (double?)null;
        SetMetric(MemoryText, MemoryBar, memoryUsage);
        MemoryDetail.Text = snapshot.MemoryUsed is { } usedBytes && snapshot.MemoryTotal is { } totalBytes
            ? $"物理内存  {usedBytes / 1073741824.0:F1} / {totalBytes / 1073741824.0:F1} GiB"
            : "物理内存暂不可用";
        GpuName.Text = adapter?.DisplayName ?? "未识别到硬件显卡";
        GpuName.ToolTip = snapshot.GpuError ?? adapter?.Name;
    }

    private static void SetMetric(System.Windows.Controls.TextBlock text, System.Windows.Controls.ProgressBar bar, double? value)
    {
        text.Text = value is { } number ? $"{number:F0}%" : "--";
        bar.Value = value ?? 0;
        bar.Opacity = value.HasValue ? 1 : 0.3;
    }

    private void DragPanel(object sender, MouseButtonEventArgs e)
    {
        if (locked || e.LeftButton != MouseButtonState.Pressed) return;
        try { DragMove(); }
        catch (InvalidOperationException) { return; }
        MoveToVisiblePosition(Left, Top);
        PositionChanged?.Invoke(Left, Top);
    }

    private void ApplyNativeStyle()
    {
        if (handle == IntPtr.Zero) return;
        var style = GetWindowLongPtrW(handle, ExtendedStyle).ToInt64() | ToolWindowStyle | NoActivateStyle;
        style = locked ? style | TransparentStyle : style & ~TransparentStyle;
        var previous = SetWindowLongPtrW(handle, ExtendedStyle, new IntPtr(style));
        if (previous == IntPtr.Zero && Marshal.GetLastPInvokeError() != 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
    }

    internal bool HasNativeClickThrough => handle != IntPtr.Zero
        && (GetWindowLongPtrW(handle, ExtendedStyle).ToInt64() & (TransparentStyle | 0x80000)) == (TransparentStyle | 0x80000);
    internal bool HasNativeNoActivate => handle != IntPtr.Zero && (GetWindowLongPtrW(handle, ExtendedStyle).ToInt64() & NoActivateStyle) != 0;
    internal static IntPtr ForegroundHandle => GetForegroundWindow();

    private IntPtr WindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x21) { handled = true; return new IntPtr(3); } // WM_MOUSEACTIVATE -> MA_NOACTIVATE
        if (message == 0x312)
        {
            if (wParam.ToInt32() == VisibilityHotkey) { ToggleVisibilityRequested?.Invoke(); handled = true; }
            else if (wParam.ToInt32() == LockHotkey) { ToggleLockRequested?.Invoke(); handled = true; }
        }
        return IntPtr.Zero;
    }

    [DllImport("user32.dll", SetLastError = true, ExactSpelling = true)]
    private static extern IntPtr GetWindowLongPtrW(IntPtr window, int index);
    [DllImport("user32.dll", SetLastError = true, ExactSpelling = true)]
    private static extern IntPtr SetWindowLongPtrW(IntPtr window, int index, IntPtr value);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr window, int id);
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
}
