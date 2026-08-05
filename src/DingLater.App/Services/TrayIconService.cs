using DingLater.App.ViewModels;
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
    private static readonly Uri AttentionBadgeBackgroundUri = new("ms-appx:///Assets/DingLaterTrayBadgeAttention.png");

    private readonly TaskbarIcon _icon;
    private readonly MenuFlyoutItem _pauseItem;
    private readonly DispatcherQueueTimer _attentionTimer;
    private readonly TextBlock _toolTipTitle;
    private readonly TextBlock _toolTipBody;
    private readonly TextBlock _toolTipFooter;
    private readonly bool _animationsEnabled;
    private DrawingIcon? _pendingIcon;
    private DrawingIcon? _attentionIcon;
    private Guid? _pendingMessageId;
    private StoredMessage? _latestPendingMessage;
    private string _badgeText = string.Empty;
    private int _pendingCount;
    private bool _includePreview;
    private bool _paused;
    private bool _attentionPhase;

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

        _toolTipTitle = new TextBlock
        {
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap
        };
        _toolTipBody = new TextBlock
        {
            MaxWidth = 320,
            MaxLines = 4,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        _toolTipFooter = new TextBlock
        {
            FontSize = 12,
            Opacity = 0.68,
            TextWrapping = TextWrapping.Wrap
        };
        var toolTipContent = new StackPanel
        {
            Width = 320,
            Spacing = 6,
            Margin = new Thickness(4),
            Children =
            {
                _toolTipTitle,
                _toolTipBody,
                _toolTipFooter
            }
        };

        _icon = new TaskbarIcon
        {
            IconSource = new BitmapImage(NormalIconUri),
            ToolTipText = "DingLater · 捕获中 · 暂无待处理消息",
            TrayToolTip = toolTipContent,
            ContextMenuMode = ContextMenuMode.PopupMenu,
            ContextFlyout = menu,
            DoubleClickCommand = openCommand
        };
        _icon.ForceCreate(enablesEfficiencyMode: false);
        _icon.TrayIcon.MessageWindow.MouseEventReceived += MessageWindow_MouseEventReceived;

        _attentionTimer = dispatcher.CreateTimer();
        _attentionTimer.Interval = TimeSpan.FromMilliseconds(750);
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

    internal void SetPaused(bool paused)
    {
        _paused = paused;
        _pauseItem.Text = paused ? "开始捕获" : "暂停捕获";
        ApplyPresentation();
    }

    internal void SetPendingMessages(int count, StoredMessage? latestMessage, bool includePreview)
    {
        _pendingCount = Math.Max(0, count);
        _latestPendingMessage = latestMessage;
        _includePreview = includePreview;
        ApplyPresentation();
    }

    internal void ShowReminder(StoredMessage message, bool includePreview)
    {
        _pendingMessageId = message.Id;
        var title = includePreview
            ? Trim($"DingLater · {ConversationPresentation.GetTitle([message])}", 63)
            : "DingLater";
        var text = includePreview
            ? Trim($"{message.Captured.Sender}\n{message.Captured.VisibleBody}", 255)
            : "一条稍后消息到时了。";
        _icon.ShowNotification(title, text, NotificationIcon.None);
    }

    public void Dispose()
    {
        _attentionTimer.Stop();
        _attentionTimer.Tick -= AttentionTimer_Tick;
        _icon.TrayIcon.MessageWindow.MouseEventReceived -= MessageWindow_MouseEventReceived;
        _icon.Icon = null;
        _pendingIcon?.Dispose();
        _attentionIcon?.Dispose();
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
        _toolTipTitle.Text = presentation.Title;
        _toolTipBody.Text = presentation.Body;
        _toolTipFooter.Text = presentation.Footer;

        if (_pendingCount == 0)
        {
            _attentionTimer.Stop();
            _attentionPhase = false;
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

        _attentionPhase = false;
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
        _pendingIcon = CreateBadgedIcon(BadgeBackgroundUri, text);
        _attentionIcon = CreateBadgedIcon(AttentionBadgeBackgroundUri, text);
    }

    private static DrawingIcon CreateBadgedIcon(Uri backgroundUri, string text)
    {
        var textLength = text.Length;
        var source = new GeneratedIconSource
        {
            BackgroundSource = new BitmapImage(backgroundUri),
            Text = text,
            TextMargin = new Thickness(77, 2, 1, 79),
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
            FontFamily = new FontFamily("Segoe UI Variable Display"),
            FontWeight = FontWeights.Bold,
            FontSize = textLength switch
            {
                1 => 34,
                2 => 27,
                _ => 20
            },
            Size = 128
        };
        return source.ToIcon();
    }

    private void DisposeBadgedIcons()
    {
        _pendingIcon?.Dispose();
        _attentionIcon?.Dispose();
        _pendingIcon = null;
        _attentionIcon = null;
    }

    private void AttentionTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        if (_pendingCount == 0 || _pendingIcon is null || _attentionIcon is null)
        {
            sender.Stop();
            return;
        }

        _attentionPhase = !_attentionPhase;
        _icon.Icon = _attentionPhase ? _attentionIcon : _pendingIcon;
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
        if (args.MouseEvent == MouseEvent.BalloonToolTipClicked)
        {
            OpenPendingOrWindow();
        }
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
