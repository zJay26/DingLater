using System.Runtime.InteropServices;
using System.Text;

namespace DingLater.App.Services.AlwaysOnTop;

// Window interaction is confined to this optional, user-invoked utility. Capture never calls it.
internal static class TopmostNative
{
    internal const uint HotkeyMessage = 0x0312;
    internal const uint DestroyMessage = 0x0082;
    internal const uint NoSize = 0x0001;
    internal const uint NoMove = 0x0002;
    internal const uint NoActivate = 0x0010;
    internal const uint Show = 0x0040;
    internal const uint Hide = 0x0080;
    internal const uint NoOwnerOrder = 0x0200;
    internal const uint Layered = 0x00080000;
    internal const uint Transparent = 0x00000020;
    internal const uint ToolWindow = 0x00000080;
    internal const uint NoActivateStyle = 0x08000000;
    internal const uint Popup = 0x80000000;
    internal const int ExtendedStyle = -20;
    internal const long TopmostStyle = 0x00000008;

    internal static bool IsTopmost(nint window) => (GetWindowLongPtr(window, ExtendedStyle).ToInt64() & TopmostStyle) != 0;

    internal static bool SetTopmost(nint window, bool enabled) => SetWindowPos(
        window, enabled ? new nint(-1) : new nint(-2), 0, 0, 0, 0,
        NoMove | NoSize | NoActivate | NoOwnerOrder);

    internal static bool IsCandidate(nint window)
    {
        if (window == 0 || !IsWindow(window) || !IsWindowVisible(window) || IsIconic(window)
            || window == GetDesktopWindow() || window == GetShellWindow() || GetAncestor(window, 2) != window)
        {
            return false;
        }

        var name = new StringBuilder(256);
        GetClassName(window, name, name.Capacity);
        return name.ToString() is not ("Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd"
            or "#32768" or "tooltips_class32" or TopmostBorder.ClassName);
    }

    internal static bool TryGetVisibleBounds(nint window, out Rect bounds)
    {
        bounds = default;
        if (!IsWindowVisible(window) || IsIconic(window)
            || (DwmGetInt(window, 14, out var cloaked, sizeof(int)) == 0 && cloaked != 0))
        {
            return false;
        }

        if (DwmGetRect(window, 9, out bounds, Marshal.SizeOf<Rect>()) != 0 && !GetWindowRect(window, out bounds))
        {
            return false;
        }

        return bounds.Right > bounds.Left && bounds.Bottom > bounds.Top;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate nint SubclassProc(nint window, uint message, nuint wParam, nint lParam, nuint id, nuint data);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate void WinEventProc(nint hook, uint eventType, nint window, int objectId, int childId, uint thread, uint time);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate nint WindowProc(nint window, uint message, nuint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect
    {
        internal int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct WindowClass
    {
        internal uint Size, Style;
        internal WindowProc Procedure;
        internal int ClassExtra, WindowExtra;
        internal nint Instance, Icon, Cursor, Background;
        internal string? MenuName;
        internal string ClassName;
        internal nint SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Paint
    {
        internal nint Dc;
        internal int Erase;
        internal Rect Bounds;
        internal int Restore, IncrementalUpdate;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        internal byte[] Reserved;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnregisterHotKey(nint window, int id);
    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowSubclass(nint window, SubclassProc callback, nuint id, nuint data);
    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool RemoveWindowSubclass(nint window, SubclassProc callback, nuint id);
    [DllImport("comctl32.dll")]
    internal static extern nint DefSubclassProc(nint window, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern nint SetWinEventHook(uint first, uint last, nint module, WinEventProc callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnhookWinEvent(nint hook);
    [DllImport("user32.dll")]
    internal static extern nint GetForegroundWindow();
    [DllImport("user32.dll")]
    internal static extern nint GetDesktopWindow();
    [DllImport("user32.dll")]
    internal static extern nint GetShellWindow();
    [DllImport("user32.dll")]
    internal static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetClassName(nint window, StringBuilder name, int maximum);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    internal static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindow(nint window);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsIconic(nint window);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsZoomed(nint window);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsHungAppWindow(nint window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetProp(nint window, string name, nint value);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern nint GetProp(nint window, string name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern nint RemoveProp(nint window, string name);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetWindowRect(nint window, out Rect bounds);
    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    private static extern int DwmGetRect(nint window, uint attribute, out Rect value, int size);
    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    private static extern int DwmGetInt(nint window, uint attribute, out int value, int size);
    [DllImport("user32.dll")]
    internal static extern uint GetDpiForWindow(nint window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern ushort RegisterClassEx(ref WindowClass windowClass);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    internal static extern nint GetModuleHandle(string? name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern nint CreateWindowEx(uint extendedStyle, string className, string title, uint style,
        int x, int y, int width, int height, nint owner, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern nint DefWindowProc(nint window, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetLayeredWindowAttributes(nint window, uint color, byte alpha, uint flags);
    [DllImport("user32.dll")]
    internal static extern int SetWindowRgn(nint window, nint region, [MarshalAs(UnmanagedType.Bool)] bool redraw);
    [DllImport("gdi32.dll")]
    internal static extern nint CreateRoundRectRgn(int left, int top, int right, int bottom, int ellipseWidth, int ellipseHeight);
    [DllImport("gdi32.dll")]
    internal static extern int CombineRgn(nint destination, nint first, nint second, int mode);
    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeleteObject(nint value);
    [DllImport("user32.dll")]
    internal static extern nint BeginPaint(nint window, out Paint paint);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EndPaint(nint window, ref Paint paint);
    [DllImport("user32.dll")]
    internal static extern int FillRect(nint dc, ref Rect bounds, nint brush);
    [DllImport("user32.dll")]
    internal static extern nint GetSysColorBrush(int index);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool InvalidateRect(nint window, nint bounds, [MarshalAs(UnmanagedType.Bool)] bool erase);
}
