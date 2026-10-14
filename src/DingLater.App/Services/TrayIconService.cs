using DingLater.App.ViewModels;
using DingLater.App.Views;
using DingLater.Core.Models;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.UI.ViewManagement;
using DrawingIcon = System.Drawing.Icon;

namespace DingLater.App.Services;

internal sealed class TrayIconService : IDisposable
{
    private static readonly Uri NormalIconUri = new("ms-appx:///Assets/DingLaterTray.ico");
    private static readonly Uri BadgeBackgroundUri = new("ms-appx:///Assets/DingLaterTrayBadge.png");
    private static readonly Uri WideBadgeBackgroundUri = new("ms-appx:///Assets/DingLaterTrayBadgeWide.png");
    private static readonly Uri TransparentIconUri = new("ms-appx:///Assets/DingLaterTrayTransparent.png");

    private readonly TaskbarIcon _icon;
    private readonly MenuFlyoutItem _pauseItem;
    private readonly DispatcherQueueTimer _attentionTimer;
    private readonly DispatcherQueueTimer _hoverTimer;
    private TrayHoverWindow? _hoverWindow;
    private TrayInboxPresentation _inboxPresentation = TrayInboxPresentationBuilder.Build([], true);
    private System.Drawing.Rectangle _hoverAnchor;
    private long? _hoverStartedAt;
    private long? _outsideSince;
    private readonly bool _animationsEnabled;
    private DrawingIcon? _pendingIcon;
    private DrawingIcon? _transparentIcon;
    private Guid? _pendingMessageId;
    private StoredMessage? _latestPendingMessage;
    private string _badgeText = string.Empty;
    private int _pendingCount;
    private bool _includePreview;
    private bool _paused;
    private bool _iconVisible;
    private bool _utilityNotificationVisible;

    internal TrayIconService()
    {
        var dispatcher = DispatcherQueue.GetForCurrentThread()
                         ?? throw new InvalidOperationException("托盘图标必须在 UI 线程创建。");
        var openCommand = CreateDeferredCommand(dispatcher, OpenPendingOrWindow);
        var pauseCommand = CreateDeferredCommand(
            dispatcher,
            () => PauseToggleRequested?.Invoke(this, EventArgs.Empty));
        var exitCommand = CreateDeferredCommand(
            dispatcher,
            () => ExitRequested?.Invoke(this, EventArgs.Empty));

        var menu = new MenuFlyout();
        var openItem = new MenuFlyoutItem { Text = "打开 DingLater", Command = openCommand };
        _pauseItem = new MenuFlyoutItem { Text = "暂停捕获", Command = pauseCommand };
        var exitItem = new MenuFlyoutItem { Text = "退出 DingLater", Command = exitCommand };
        menu.Items.Add(openItem);
        menu.Items.Add(_pauseItem);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(exitItem);

        _icon = new TaskbarIcon
        {
            IconSource = new BitmapImage(NormalIconUri),
            ToolTipText = "DingLater · 捕获中 · 暂无待处理消息",
            ContextMenuMode = ContextMenuMode.PopupMenu,
            ContextFlyout = menu,
            LeftClickCommand = openCommand
        };
        _icon.TrayIcon.UseStandardTooltip = false;
        _icon.ForceCreate(enablesEfficiencyMode: false);
        _icon.TrayIcon.MessageWindow.MouseEventReceived += MessageWindow_MouseEventReceived;

        _hoverTimer = dispatcher.CreateTimer();
        _hoverTimer.Interval = TimeSpan.FromMilliseconds(100);
        _hoverTimer.IsRepeating = true;
        _hoverTimer.Tick += HoverTimer_Tick;

        _attentionTimer = dispatcher.CreateTimer();
        _attentionTimer.Interval = TimeSpan.FromMilliseconds(600);
        _attentionTimer.IsRepeating = true;
        _attentionTimer.Tick += AttentionTimer_Tick;
        try
        {
            _animationsEnabled = new UISettings().AnimationsEnabled;
        }
        catch
        {
            _animationsEnabled = true;
        }

        ApplyPresentation();
    }

    internal event EventHandler? OpenRequested;
    internal event EventHandler? PauseToggleRequested;
    internal event EventHandler? ExitRequested;
    internal event EventHandler<Guid>? MessageOpenRequested;
    internal event EventHandler? UtilitySettingsRequested;
    internal event EventHandler? InboxOpenRequested;
    internal Func<Task>? MarkAllHandledAsync { get; set; }

    internal void SetPaused(bool paused)
    {
        _paused = paused;
        _pauseItem.Text = paused ? "开始捕获" : "暂停捕获";
        ApplyPresentation();
    }

    internal void SetPendingMessages(IReadOnlyList<StoredMessage> messages, bool includePreview)
    {
        _inboxPresentation = TrayInboxPresentationBuilder.Build(messages, includePreview);
        _pendingCount = _inboxPresentation.PendingCount;
        _latestPendingMessage = _inboxPresentation.LatestMessage;
        _includePreview = includePreview;
        ApplyPresentation();
    }

    internal void ShowReminder(StoredMessage message, bool includePreview)
    {
        _utilityNotificationVisible = false;
        _pendingMessageId = message.Id;
        var title = includePreview
            ? Trim($"DingLater · {ConversationPresentation.GetTitle([message])}", 63)
            : "DingLater";
        var text = includePreview
            ? Trim($"{message.Captured.Sender}\n{message.Captured.VisibleBody}", 255)
            : "一条稍后消息到时了。";
        _icon.ShowNotification(title, text, NotificationIcon.None);
    }

    internal void ShowUtilityError(string message)
    {
        _utilityNotificationVisible = true;
        _icon.ShowNotification("DingLater · 窗口置顶", Trim(message, 255), NotificationIcon.None, sound: false);
    }

    public void Dispose()
    {
        _attentionTimer.Stop();
        _attentionTimer.Tick -= AttentionTimer_Tick;
        _hoverTimer.Stop();
        _hoverTimer.Tick -= HoverTimer_Tick;
        _hoverWindow?.Close();
        _icon.TrayIcon.MessageWindow.MouseEventReceived -= MessageWindow_MouseEventReceived;
        _icon.Icon = null;
        _pendingIcon?.Dispose();
        _transparentIcon?.Dispose();
        _icon.Dispose();
    }

    private void ApplyPresentation()
    {
        var presentation = TrayIconPresentationBuilder.Build(
            _pendingCount,
            _latestPendingMessage,
            _includePreview,
            _paused);
        _icon.ToolTipText = presentation.ToolTipText;
        // Keep the accessible shell summary without a second, noninteractive tooltip.
        _icon.TrayIcon.UseStandardTooltip = false;
        _icon.TrayIcon.UpdateToolTip(presentation.ToolTipText);
        _hoverWindow?.Update(_inboxPresentation, _paused);

        if (_pendingCount == 0)
        {
            _attentionTimer.Stop();
            _iconVisible = false;
            _badgeText = string.Empty;
            _icon.Icon = null;
            _icon.IconSource = new BitmapImage(NormalIconUri);
            DisposeBadgedIcons();
            return;
        }

        if (!string.Equals(_badgeText, presentation.BadgeText, StringComparison.Ordinal))
        {
            _badgeText = presentation.BadgeText;
            RebuildBadgedIcons(presentation.BadgeText);
        }

        _iconVisible = true;
        _icon.IconSource = null;
        _icon.Icon = _pendingIcon;
        if (_animationsEnabled && !_attentionTimer.IsRunning)
        {
            _attentionTimer.Start();
        }
    }

    private void RebuildBadgedIcons(string text)
    {
        _attentionTimer.Stop();
        _icon.Icon = null;
        DisposeBadgedIcons();
        _pendingIcon = CreateBadgedIcon(text.Length == 1 ? BadgeBackgroundUri : WideBadgeBackgroundUri, text);
        _transparentIcon = CreateBadgedIcon(TransparentIconUri, string.Empty);
    }

    private static DrawingIcon CreateBadgedIcon(Uri backgroundUri, string text)
    {
        var textLength = text.Length;
        var source = new GeneratedIconSource
        {
            BackgroundSource = new BitmapImage(backgroundUri),
            Text = text,
            TextMargin = textLength == 1
                ? new Thickness(75, 1, 1, 71)
                : new Thickness(57, 1, 1, 69),
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
            FontFamily = new FontFamily("Segoe UI Variable Display"),
            FontWeight = FontWeights.Bold,
            FontSize = textLength switch
            {
                1 => 40,
                2 => 32,
                _ => 23
            },
            Size = 128
        };
        return source.ToIcon();
    }

    private void DisposeBadgedIcons()
    {
        _pendingIcon?.Dispose();
        _transparentIcon?.Dispose();
        _pendingIcon = null;
        _transparentIcon = null;
    }

    private void AttentionTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        if (_pendingCount == 0 || _pendingIcon is null || _transparentIcon is null)
        {
            sender.Stop();
            return;
        }

        if (_hoverWindow is { IsVisible: true })
        {
            _iconVisible = true;
            _icon.Icon = _pendingIcon;
            return;
        }

        _iconVisible = !_iconVisible;
        _icon.Icon = _iconVisible ? _pendingIcon : _transparentIcon;
    }

    private static XamlUICommand CreateDeferredCommand(DispatcherQueue dispatcher, Action execute)
    {
        var command = new XamlUICommand();
        command.ExecuteRequested += (_, _) => dispatcher.TryEnqueue(() => execute());
        return command;
    }

    private void MessageWindow_MouseEventReceived(
        object? sender,
        H.NotifyIcon.Core.MessageWindow.MouseEventReceivedEventArgs args)
    {
        if (args.MouseEvent == MouseEvent.MouseMove)
        {
            _hoverAnchor = TrayHoverNative.GetIconBounds(_icon.TrayIcon.WindowHandle, _icon.TrayIcon.Id, args.Point);
            _hoverStartedAt ??= Environment.TickCount64;
            if (!_hoverTimer.IsRunning) _hoverTimer.Start();
            return;
        }

        if (args.MouseEvent is MouseEvent.IconLeftMouseDown or MouseEvent.IconRightMouseDown)
        {
            DismissHover();
        }

        if (args.MouseEvent == MouseEvent.BalloonToolTipClicked)
        {
            if (_utilityNotificationVisible)
            {
                _utilityNotificationVisible = false;
                UtilitySettingsRequested?.Invoke(this, EventArgs.Empty);
                return;
            }

            OpenPendingOrWindow();
        }
    }

    private void HoverTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        if (!TrayHoverNative.TryGetPointer(out var pointer))
        {
            DismissHover();
            return;
        }
        var overIcon = System.Drawing.Rectangle.Inflate(_hoverAnchor, 4, 4).Contains(pointer);
        var overList = _hoverWindow is { IsVisible: true } window && window.FlyoutBounds.Contains(pointer);
        var now = Environment.TickCount64;
        if (overIcon || overList)
        {
            _outsideSince = null;
            if (overIcon && _hoverWindow is not { IsVisible: true } && now - (_hoverStartedAt ?? now) >= 350)
            {
                GetHoverWindow().ShowAt(_hoverAnchor);
            }
            return;
        }
        if (_hoverWindow is not { IsVisible: true })
        {
            DismissHover();
            return;
        }
        if (TrayHoverNative.IsMouseButtonDown)
        {
            DismissHover();
            return;
        }
        if (_hoverWindow.IsActive) return;
        _outsideSince ??= now;
        if (now - _outsideSince.Value >= 450) DismissHover();
    }

    private TrayHoverWindow GetHoverWindow()
    {
        if (_hoverWindow is not null) return _hoverWindow;
        _hoverWindow = new TrayHoverWindow();
        _hoverWindow.MessageOpenRequested += (_, id) => MessageOpenRequested?.Invoke(this, id);
        _hoverWindow.InboxOpenRequested += (_, _) => InboxOpenRequested?.Invoke(this, EventArgs.Empty);
        _hoverWindow.MarkAllHandledAsync = () => MarkAllHandledAsync?.Invoke() ?? Task.CompletedTask;
        _hoverWindow.Update(_inboxPresentation, _paused);
        return _hoverWindow;
    }

    internal TrayHoverWindow ShowHoverForSmokeTest(System.Drawing.Rectangle anchor)
    {
        var window = GetHoverWindow();
        window.ShowAt(anchor);
        return window;
    }

    private void DismissHover()
    {
        _hoverTimer.Stop();
        _hoverStartedAt = null;
        _outsideSince = null;
        _hoverWindow?.HideFlyout();
    }

    private void OpenPendingOrWindow()
    {
        if (_pendingMessageId is { } id)
        {
            _pendingMessageId = null;
            MessageOpenRequested?.Invoke(this, id);
            return;
        }

        OpenRequested?.Invoke(this, EventArgs.Empty);
    }

    private static string Trim(string value, int maximum) =>
        value.Length <= maximum ? value : value[..(maximum - 1)] + "…";

}
