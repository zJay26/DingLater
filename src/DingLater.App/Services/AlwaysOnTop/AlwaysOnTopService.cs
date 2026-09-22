using System.ComponentModel;
using System.Runtime.InteropServices;
using static DingLater.App.Services.AlwaysOnTop.TopmostNative;

namespace DingLater.App.Services.AlwaysOnTop;

internal sealed record TopmostStatus(string Message, bool IsError = false);

// All methods and native callbacks run on the host window's UI thread.
internal sealed class AlwaysOnTopService : IDisposable
{
    internal const int HotkeyId = 0x444C;
    private const nuint SubclassId = 0x444C4154;
    private readonly nint _host;
    private readonly Action<Action> _enqueue;
    private readonly SubclassProc _subclass;
    private readonly WinEventProc _windowEvent;
    private readonly uint _modifiers;
    private readonly uint _key;
    private readonly string _ownerProperty = $"DingLater.AlwaysOnTop.{Guid.NewGuid():N}";
    private readonly Dictionary<nint, TopmostBorder> _pins = [];
    private readonly HashSet<nint> _pendingRefresh = [];
    private readonly List<nint> _hooks = [];
    private bool _attached;
    private bool _disposed;
    private bool _orderCheckPending;

    internal AlwaysOnTopService(nint host, Action<Action> enqueue, uint modifiers = 0x0002 | 0x0008, uint key = 0x54)
    {
        _host = host;
        _enqueue = enqueue;
        _subclass = WindowMessage;
        _windowEvent = WindowEvent;
        _modifiers = modifiers | 0x4000;
        _key = key;
    }

    internal event EventHandler<TopmostStatus>? StatusChanged;
    internal bool IsRegistered { get; private set; }
    internal bool RestoreOnExit { get; set; } = true;
    internal int PinnedCount => _pins.Count;
    internal TopmostStatus Status { get; private set; } = new("窗口置顶已关闭。");

    internal void SetEnabled(bool enabled)
    {
        if (_disposed) return;
        if (!enabled)
        {
            if (IsRegistered) UnregisterHotKey(_host, HotkeyId);
            IsRegistered = false;
            var failures = ReleasePins(restore: true);
            Report(failures == 0 ? "窗口置顶已关闭。" : "窗口置顶已关闭，但有窗口未能取消置顶；可重新开启后对该窗口按快捷键。", failures != 0);
            return;
        }

        if (IsRegistered) return;
        if (!_attached)
        {
            _attached = SetWindowSubclass(_host, _subclass, SubclassId, 0);
            if (!_attached)
            {
                Report("无法初始化窗口置顶，请重启 DingLater 后重试。", true);
                return;
            }
        }

        // MOD_CONTROL | MOD_WIN | MOD_NOREPEAT: holding T must not repeatedly toggle.
        IsRegistered = RegisterHotKey(_host, HotkeyId, _modifiers, _key);
        if (!IsRegistered)
        {
            var error = Marshal.GetLastWin32Error();
            Report(error == 1409
                ? "Ctrl + Win + T 已被其他程序占用。请关闭 PowerToys 等程序的同名快捷键，再关闭并重新开启此开关。"
                : $"无法注册 Ctrl + Win + T（错误 {error}）。请关闭并重新开启此开关重试。", true);
            return;
        }

        Report("已就绪：Ctrl + Win + T 切换当前窗口置顶；置顶时显示描边。");
    }

    internal void ToggleWindow(nint window)
    {
        if (_disposed || !IsRegistered) return;
        if (!IsCandidate(window))
        {
            Report("请先选择一个普通应用窗口，再按 Ctrl + Win + T。", true);
            return;
        }

        if (IsHungAppWindow(window))
        {
            Report("当前窗口没有响应，请等待它恢复后重试。", true);
            return;
        }

        try
        {
            if (IsTopmost(window))
            {
                if (!SetTopmost(window, false) || IsTopmost(window))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                ForgetWindow(window);
                Report("已取消当前窗口置顶。");
                return;
            }

            ForgetWindow(window);
            // A per-instance window property prevents stale/reused HWNDs from being changed at exit.
            if (!SetProp(window, _ownerProperty, new nint(1)))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            try
            {
                if (!SetTopmost(window, true) || !IsTopmost(window))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                EnsureHooks();
                var border = new TopmostBorder(window);
                _pins.Add(window, border);
                border.Refresh();
            }
            catch
            {
                if (OwnsWindow(window)) SetTopmost(window, false);
                ForgetWindow(window);
                throw;
            }

            Report($"当前窗口已置顶；共 {_pins.Count} 个窗口由 DingLater 置顶。");
        }
        catch (Exception exception)
        {
            var detail = exception is Win32Exception { NativeErrorCode: 5 }
                ? "当前窗口的权限高于 DingLater，无法更改。"
                : "当前窗口不支持此次操作，或权限不足。";
            Report($"窗口置顶未完成：{detail}", true);
        }
    }

    private void EnsureHooks()
    {
        if (_hooks.Count != 0) return;
        try
        {
            // OUTOFCONTEXT observes window events without injecting into other processes.
            foreach (var (first, last) in new (uint, uint)[] { (0x8001, 0x800B), (0x8017, 0x8018), (0x0016, 0x0017) })
            {
                var hook = SetWinEventHook(first, last, 0, _windowEvent, 0, 0, 0);
                if (hook == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
                _hooks.Add(hook);
            }
        }
        catch
        {
            RemoveHooks();
            throw;
        }
    }

    private void WindowEvent(nint hook, uint eventType, nint window, int objectId, int childId, uint thread, uint time)
    {
        // Z-order changes can be reported against the desktop instead of the changed window.
        // Only prune stale pins here: moving our borders in response would create an event loop.
        if (!_disposed && eventType == 0x8004 && _pins.Count != 0
            && (objectId == 0 || window == GetDesktopWindow()) && !_orderCheckPending)
        {
            _orderCheckPending = true;
            try
            {
                _enqueue(() =>
                {
                    _orderCheckPending = false;
                    if (_disposed) return;
                    foreach (var target in _pins.Keys.ToArray())
                    {
                        if (!OwnsWindow(target) || !IsTopmost(target)) ForgetWindow(target);
                    }
                });
            }
            catch { _orderCheckPending = false; }
            return;
        }

        // Ignore control/content changes; coalesce window events on the existing UI queue.
        if (_disposed || objectId != 0 || childId != 0 || !_pins.ContainsKey(window)) return;
        if (eventType is 0x8001 or 0x8002 or 0x8003 or 0x800A or 0x800B or 0x8017 or 0x8018 or 0x0016 or 0x0017)
        {
            try { QueueRefresh(window); }
            catch { /* The UI queue can be unavailable during shutdown. */ }
        }
    }

    private void QueueRefresh(nint window)
    {
        if (!_pendingRefresh.Add(window)) return;
        _enqueue(() =>
        {
            _pendingRefresh.Remove(window);
            if (_disposed || !_pins.TryGetValue(window, out var border)) return;
            if (!OwnsWindow(window) || !IsTopmost(window))
            {
                ForgetWindow(window);
                return;
            }

            try { border.Refresh(); }
            catch
            {
                Report("置顶描边暂时无法更新；可对该窗口按快捷键取消置顶后重试。", true);
            }
        });
    }

    private nint WindowMessage(nint window, uint message, nuint wParam, nint lParam, nuint id, nuint data)
    {
        // Never allow a managed exception to cross a native window-procedure boundary.
        try
        {
            if (message == HotkeyMessage && wParam == HotkeyId)
            {
                ToggleWindow(GetForegroundWindow());
                return 0;
            }

            if (message == DestroyMessage)
            {
                Dispose();
            }
            else if (message is 0x007E or 0x0015 or 0x02E0 or 0x031A) // Display, system colors, DPI, theme.
            {
                foreach (var target in _pins.Keys.ToArray()) QueueRefresh(target);
            }
        }
        catch
        {
            // The host's own message processing must continue even if the utility fails.
        }

        return DefSubclassProc(window, message, wParam, lParam);
    }

    private bool OwnsWindow(nint window) => IsWindow(window) && GetProp(window, _ownerProperty) == new nint(1);

    private void ForgetWindow(nint window)
    {
        if (_pins.Remove(window, out var border)) border.Dispose();
        if (OwnsWindow(window)) RemoveProp(window, _ownerProperty);
        if (_pins.Count == 0) RemoveHooks();
    }

    private int ReleasePins(bool restore)
    {
        RemoveHooks();
        var failures = 0;
        foreach (var (window, border) in _pins)
        {
            border.Dispose();
            if (!OwnsWindow(window)) continue;
            if (restore && IsTopmost(window))
            {
                // Do not leave DingLater hanging on exit because another application is hung.
                var restored = IsHungAppWindow(window)
                    ? SetWindowPos(window, new nint(-2), 0, 0, 0, 0, NoMove | NoSize | NoActivate | NoOwnerOrder | 0x4000)
                    : SetTopmost(window, false);
                if (!restored) failures++;
            }

            RemoveProp(window, _ownerProperty);
        }

        _pins.Clear();
        _pendingRefresh.Clear();
        return failures;
    }

    private void RemoveHooks()
    {
        foreach (var hook in _hooks) UnhookWinEvent(hook);
        _hooks.Clear();
    }

    private void Report(string message, bool error = false)
    {
        var status = new TopmostStatus(message, error);
        if (status == Status) return;
        Status = status;
        StatusChanged?.Invoke(this, status);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (IsRegistered) UnregisterHotKey(_host, HotkeyId);
        IsRegistered = false;
        ReleasePins(RestoreOnExit);
        if (_attached)
        {
            RemoveWindowSubclass(_host, _subclass, SubclassId);
            _attached = false;
        }
    }
}
