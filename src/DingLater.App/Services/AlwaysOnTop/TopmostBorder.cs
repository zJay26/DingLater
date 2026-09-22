using System.ComponentModel;
using System.Runtime.InteropServices;
using static DingLater.App.Services.AlwaysOnTop.TopmostNative;

namespace DingLater.App.Services.AlwaysOnTop;

internal sealed class TopmostBorder : IDisposable
{
    internal const string ClassName = "DingLater.AlwaysOnTop.Border";
    private static readonly WindowProc Procedure = WindowProcedure;
    private static readonly object ClassLock = new();
    private static bool _classRegistered;
    private readonly nint _target;
    private readonly string _identity = $"DingLater.Border.{Guid.NewGuid():N}";
    private nint _window;
    private (int Width, int Height, int Thickness, int Radius) _shape;

    internal TopmostBorder(nint target)
    {
        EnsureWindowClass();
        _target = target;
        // An owned, hollow, layered tool window: no taskbar entry, no focus, mouse passes through.
        _window = CreateWindowEx(Layered | Transparent | ToolWindow | NoActivateStyle,
            ClassName, string.Empty, Popup, 0, 0, 0, 0, target, 0, GetModuleHandle(null), 0);
        if (_window == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        if (!SetProp(_window, _identity, new nint(1)))
        {
            var error = Marshal.GetLastWin32Error();
            DestroyWindow(_window);
            _window = 0;
            throw new Win32Exception(error);
        }

        if (!SetLayeredWindowAttributes(_window, 0, 255, 2))
        {
            var error = Marshal.GetLastWin32Error();
            Dispose();
            throw new Win32Exception(error);
        }
    }

    internal nint Handle => _window;

    internal void Refresh()
    {
        if (_window == 0 || GetProp(_window, _identity) != new nint(1))
        {
            return;
        }

        if (!TryGetVisibleBounds(_target, out var bounds))
        {
            SetWindowPos(_window, 0, 0, 0, 0, 0, NoMove | NoSize | NoActivate | Hide | 0x0004);
            return;
        }

        var width = bounds.Right - bounds.Left;
        var height = bounds.Bottom - bounds.Top;
        var scale = Math.Max(1d, GetDpiForWindow(_target) / 96d);
        var thickness = Math.Max(2, (int)Math.Round(3 * scale));
        var radius = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000) && !IsZoomed(_target)
            ? (int)Math.Round(8 * scale) : 0;
        var shape = (width, height, thickness, radius);
        if (_shape != shape)
        {
            var outer = CreateRoundRectRgn(0, 0, width + 1, height + 1, radius * 2, radius * 2);
            var innerRadius = Math.Max(0, radius - thickness);
            var inner = CreateRoundRectRgn(thickness, thickness, width - thickness + 1, height - thickness + 1,
                innerRadius * 2, innerRadius * 2);
            try
            {
                if (outer == 0 || inner == 0 || CombineRgn(outer, outer, inner, 4) == 0 || SetWindowRgn(_window, outer, true) == 0)
                {
                    throw new Win32Exception("无法创建窗口置顶描边。");
                }

                outer = 0; // Windows owns the region after a successful SetWindowRgn.
                _shape = shape;
            }
            finally
            {
                if (outer != 0) DeleteObject(outer);
                if (inner != 0) DeleteObject(inner);
            }
        }

        if (!SetWindowPos(_window, new nint(-1), bounds.Left, bounds.Top, width, height, NoActivate | NoOwnerOrder | Show))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        InvalidateRect(_window, 0, false);
    }

    public void Dispose()
    {
        if (_window != 0)
        {
            if (GetProp(_window, _identity) == new nint(1)) DestroyWindow(_window);
            _window = 0;
        }
    }

    private static void EnsureWindowClass()
    {
        lock (ClassLock)
        {
            if (_classRegistered) return;
            var windowClass = new WindowClass
            {
                Size = (uint)Marshal.SizeOf<WindowClass>(),
                Procedure = Procedure,
                Instance = GetModuleHandle(null),
                ClassName = ClassName
            };
            if (RegisterClassEx(ref windowClass) == 0)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            _classRegistered = true;
        }
    }

    private static nint WindowProcedure(nint window, uint message, nuint wParam, nint lParam)
    {
        switch (message)
        {
            case 0x000F: // WM_PAINT; the window region clips painting to the hollow border.
                var dc = BeginPaint(window, out var paint);
                FillRect(dc, ref paint.Bounds, GetSysColorBrush(13)); // System highlight also supports High Contrast.
                EndPaint(window, ref paint);
                return 0;
            case 0x0014: return 1; // WM_ERASEBKGND
            case 0x0084: return -1; // HTTRANSPARENT
            case 0x0021: return 3; // MA_NOACTIVATE
            case 0x0015: // WM_SYSCOLORCHANGE
            case 0x031A: // WM_THEMECHANGED
                InvalidateRect(window, 0, false);
                break;
        }

        return DefWindowProc(window, message, wParam, lParam);
    }
}
