using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using DingLater.App.Services.AlwaysOnTop;
using DingLater.Core.Models;
using static DingLater.App.Services.AlwaysOnTop.TopmostNative;

namespace DingLater.Tests;

[TestClass]
[DoNotParallelize]
public sealed class AlwaysOnTopTests
{
    [TestMethod]
    public void OldSettings_EnableTopmostAndRestoreOnExit_AndChoicesRoundTrip()
    {
        var old = JsonSerializer.Deserialize<AppSettings>("{\"RetentionDays\":14}")!.Normalize();
        Assert.IsTrue(old.AlwaysOnTopEnabled);
        Assert.IsTrue(old.RestoreTopmostOnExit);
        var saved = old with { AlwaysOnTopEnabled = false, RestoreTopmostOnExit = false };
        Assert.AreEqual(saved, JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(saved))!.Normalize());
    }

    [TestMethod]
    public Task Toggle_PinsTwoWindowsAndUnpinsOnlyTheSelectedWindow() => OnWindowThread(context =>
    {
        var first = context.Window();
        var second = context.Window();
        context.Service.ToggleWindow(first);
        context.Service.ToggleWindow(second);
        Assert.IsTrue(IsTopmost(first));
        Assert.IsTrue(IsTopmost(second));
        Assert.AreEqual(2, context.Service.PinnedCount);
        context.Service.ToggleWindow(first);
        Assert.IsFalse(IsTopmost(first));
        Assert.IsTrue(IsTopmost(second));
        Assert.AreEqual(1, context.Service.PinnedCount);
        Assert.AreEqual(nint.Zero, FindBorder(first));
    });

    [TestMethod]
    public Task Border_IsHollowClickThroughAndTracksMoveResizeMinimizeRestore() => OnWindowThread(context =>
    {
        var target = context.Window();
        context.Service.ToggleWindow(target);
        Assert.IsFalse(context.Service.Status.IsError, context.Service.Status.Message);
        var border = FindBorder(target);
        Assert.AreNotEqual(nint.Zero, border);
        var style = GetWindowLongPtr(border, ExtendedStyle).ToInt64();
        Assert.AreEqual((long)(Layered | Transparent | ToolWindow | NoActivateStyle),
            style & (Layered | Transparent | ToolWindow | NoActivateStyle));
        var region = CreateRoundRectRgn(0, 0, 1, 1, 0, 0);
        try
        {
            Assert.AreNotEqual(0, GetWindowRgn(border, region));
            Assert.IsFalse(PtInRegion(region, 180, 100));
            Assert.IsTrue(PtInRegion(region, 1, 100));
        }
        finally { DeleteObject(region); }

        Assert.IsTrue(SetWindowPos(target, 0, 310, 290, 510, 310, NoActivate | 0x0004));
        context.PumpUntil(() => BoundsEqual(target, border));
        ShowWindow(target, 6);
        context.PumpUntil(() => !IsWindowVisible(border));
        ShowWindow(target, 4);
        context.PumpUntil(() => IsWindowVisible(border) && BoundsEqual(target, border));
        Assert.IsTrue(IsTopmost(target));
    });

    [TestMethod]
    public Task Disable_RestoresOnlyWindowsPinnedByThisService_AndReleasesHotkey() => OnWindowThread(context =>
    {
        var pinned = context.Window();
        var originallyTopmost = context.Window();
        Assert.IsTrue(SetTopmost(originallyTopmost, true));
        context.Service.ToggleWindow(pinned);
        context.Service.RestoreOnExit = false; // Disabling still explicitly cancels the utility's pins.
        context.Service.SetEnabled(false);
        Assert.IsFalse(IsTopmost(pinned));
        Assert.IsTrue(IsTopmost(originallyTopmost));
        Assert.AreEqual(nint.Zero, FindBorder(pinned));
        Assert.IsFalse(context.Service.IsRegistered);
        context.Service.SetEnabled(true);
        Assert.IsTrue(context.Service.IsRegistered);
    });

    [TestMethod]
    public Task Dispose_RespectsExitPreferenceAndAlwaysRemovesBorders() => OnWindowThread(context =>
    {
        var target = context.Window();
        context.Service.ToggleWindow(target);
        context.Service.RestoreOnExit = false;
        context.Service.Dispose();
        Assert.IsTrue(IsTopmost(target));
        Assert.AreEqual(nint.Zero, FindBorder(target));
        Assert.IsTrue(RegisterHotKey(context.Host, 42, 0x0007, 0x87));
        UnregisterHotKey(context.Host, 42);
    });

    [TestMethod]
    public Task DefaultDispose_RestoresPins_AndToleratesRepeatedDispose() => OnWindowThread(context =>
    {
        var target = context.Window();
        context.Service.ToggleWindow(target);
        context.Service.Dispose();
        context.Service.Dispose();
        Assert.IsFalse(IsTopmost(target));
        Assert.AreEqual(nint.Zero, FindBorder(target));
    });

    [TestMethod]
    public Task DestroyedTarget_RemovesTrackingAndBorder_WithoutPolling() => OnWindowThread(context =>
    {
        var target = context.Window();
        context.Service.ToggleWindow(target);
        var border = FindBorder(target);
        Assert.AreNotEqual(nint.Zero, border);
        Assert.IsTrue(DestroyWindow(target));
        context.PumpUntil(() => context.Service.PinnedCount == 0);
        Assert.IsFalse(IsWindow(border));
    });

    [TestMethod]
    public Task ExternalUnpin_RemovesStaleBorder_AndInvalidTargetsAreRejected() => OnWindowThread(context =>
    {
        var target = context.Window();
        context.Service.ToggleWindow(target);
        Assert.IsTrue(SetTopmost(target, false));
        context.PumpUntil(() => context.Service.PinnedCount == 0);
        Assert.AreEqual(nint.Zero, FindBorder(target));
        context.Service.ToggleWindow(0);
        Assert.IsTrue(context.Service.Status.IsError);
        context.Service.ToggleWindow(GetDesktopWindow());
        Assert.AreEqual(0, context.Service.PinnedCount);
    });

    [TestMethod]
    public Task HotkeyConflict_IsReportedAndCanBeRetried() => OnWindowThread(context =>
    {
        var host = context.Window(visible: false);
        using var competing = new AlwaysOnTopService(host, context.Enqueue, 0x0007, 0x87);
        competing.SetEnabled(true);
        Assert.IsFalse(competing.IsRegistered);
        Assert.IsTrue(competing.Status.IsError);
        StringAssert.Contains(competing.Status.Message, "占用");
        context.Service.SetEnabled(false);
        competing.SetEnabled(true);
        Assert.IsTrue(competing.IsRegistered);
    });

    private static async Task OnWindowThread(Action<WindowContext> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                using (var context = new WindowContext()) action(context);
                completion.SetResult();
            }
            catch (Exception exception) { completion.SetException(exception); }
        })
        { IsBackground = true, Name = "DingLater isolated window tests" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }

    private static bool BoundsEqual(nint target, nint border)
    {
        if (!TryGetVisibleBounds(target, out var expected) || !GetWindowRect(border, out var actual)) return false;
        return expected.Left == actual.Left && expected.Top == actual.Top && expected.Right == actual.Right && expected.Bottom == actual.Bottom;
    }

    private static nint FindBorder(nint target)
    {
        nint result = 0;
        EnumWindows((window, _) =>
        {
            var name = new StringBuilder(256);
            GetClassName(window, name, name.Capacity);
            if (name.ToString() == TopmostBorder.ClassName && GetWindow(window, 4) == target) result = window;
            return true;
        }, 0);
        return result;
    }

    private sealed class WindowContext : IDisposable
    {
        private readonly List<nint> _windows = [];
        private readonly Queue<Action> _queue = [];
        internal nint Host { get; }
        internal AlwaysOnTopService Service { get; }

        internal WindowContext()
        {
            Host = Window(visible: false);
            // Use Ctrl+Alt+Shift+F24 so tests do not consume the user's Ctrl+Win+T.
            Service = new AlwaysOnTopService(Host, Enqueue, 0x0007, 0x87);
            Service.SetEnabled(true);
            Assert.IsTrue(Service.IsRegistered, Service.Status.Message);
        }

        internal nint Window(bool visible = true)
        {
            var window = CreateWindowEx(NoActivateStyle | ToolWindow, "STATIC", "DingLater isolated window test",
                0x00CF0000, 260, 240, 420, 260, 0, 0, GetModuleHandle(null), 0);
            Assert.AreNotEqual(nint.Zero, window);
            _windows.Add(window);
            if (visible) ShowWindow(window, 4);
            return window;
        }

        internal void Enqueue(Action action) => _queue.Enqueue(action);

        internal void PumpUntil(Func<bool> condition)
        {
            var deadline = Environment.TickCount64 + 3500;
            do
            {
                while (PeekMessage(out var message, 0, 0, 0, 1))
                {
                    TranslateMessage(ref message);
                    DispatchMessage(ref message);
                }
                while (_queue.TryDequeue(out var action)) action();
                if (condition()) return;
                Thread.Sleep(10);
            } while (Environment.TickCount64 < deadline);
            Assert.Fail("Window event did not produce the expected state within 3.5 seconds.");
        }

        public void Dispose()
        {
            Service.Dispose();
            foreach (var window in _windows) if (IsWindow(window)) DestroyWindow(window);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public nint Window;
        public uint Id;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public int X, Y;
        public uint Private;
    }

    private delegate bool EnumWindowProc(nint window, nint data);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowProc callback, nint data);
    [DllImport("user32.dll")]
    private static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll")]
    private static extern int GetWindowRgn(nint window, nint region);
    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PtInRegion(nint region, int x, int y);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessage(out Message message, nint window, uint first, uint last, uint remove);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint DispatchMessage(ref Message message);
}
