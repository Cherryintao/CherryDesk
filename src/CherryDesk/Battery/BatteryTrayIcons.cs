using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace CherryDesk.Battery;

internal sealed class BatteryTrayIcons : IDisposable
{
    public Icon Normal { get; } = Create(false);
    public Icon Low { get; } = Create(true);

    private static Icon Create(bool low)
    {
        using var bitmap = new Bitmap(32, 32);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.Clear(Color.Transparent);
        using var background = new SolidBrush(low ? Color.FromArgb(229, 170, 57) : Color.FromArgb(0, 120, 212));
        graphics.FillEllipse(background, 1, 1, 30, 30);
        using var foreground = new SolidBrush(low ? Color.FromArgb(24, 24, 24) : Color.White);
        using var font = new Font("Segoe UI", low ? 21 : 19, FontStyle.Bold, GraphicsUnit.Pixel);
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        graphics.DrawString(low ? "!" : "C", font, foreground, new RectangleF(0, 0, 32, 31), format);
        var handle = bitmap.GetHicon();
        try { using var borrowed = Icon.FromHandle(handle); return (Icon)borrowed.Clone(); }
        finally { DestroyIcon(handle); }
    }

    public void Dispose() { Normal.Dispose(); Low.Dispose(); }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyIcon(IntPtr handle);
}
