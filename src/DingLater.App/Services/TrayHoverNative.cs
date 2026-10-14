using System.Drawing;
using System.Runtime.InteropServices;

namespace DingLater.App.Services;

internal static class TrayHoverNative
{
    internal static Rectangle GetIconBounds(nint window, Guid iconId, Point fallback)
    {
        var identifier = new NotifyIconIdentifier
        {
            Size = (uint)Marshal.SizeOf<NotifyIconIdentifier>(),
            Window = window,
            IconId = iconId
        };
        return Shell_NotifyIconGetRect(ref identifier, out var bounds) == 0
            ? Rectangle.FromLTRB(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom)
            : new Rectangle(fallback.X - 12, fallback.Y - 12, 24, 24);
    }

    internal static bool TryGetPointer(out Point point)
    {
        var succeeded = GetCursorPos(out var position);
        point = new Point(position.X, position.Y);
        return succeeded;
    }

    internal static bool IsMouseButtonDown =>
        ((GetAsyncKeyState(1) | GetAsyncKeyState(2)) & 0x8000) != 0;

    internal static nint ForegroundWindow => GetForegroundWindow();

    internal static double GetScale(nint window) => Math.Max(96, GetDpiForWindow(window)) / 96.0;

    internal static void RoundCorners(nint window)
    {
        const int cornerPreference = 33;
        var round = 2;
        _ = DwmSetWindowAttribute(window, cornerPreference, ref round, sizeof(int));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NotifyIconIdentifier
    {
        public uint Size;
        public nint Window;
        public uint Id;
        public Guid IconId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("shell32.dll")]
    private static extern int Shell_NotifyIconGetRect(ref NotifyIconIdentifier identifier, out NativeRect bounds);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint window);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint window, int attribute, ref int value, int size);
}
