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
    private static readonly Uri WideBadgeBackgroundUri = new("ms-appx:///Assets/DingLaterTrayBadgeWide.png");
    private static readonly Uri TransparentIconUri = new("ms-appx:///Assets/DingLaterTrayTransparent.png");

    private readonly TaskbarIcon _icon;
    private readonly MenuFlyoutItem _pauseItem;
    private readonly DispatcherQueueTimer _attentionTimer;
    private readonly TextBlock _toolTipTitle;
    private readonly TextBlock _toolTipCount;
    private readonly TextBlock _toolTipBody;
    private readonly TextBlock _toolTipFooter;
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
            Text = "DingLater",
            VerticalAlignment = VerticalAlignment.Center,
            FontFamily = new FontFamily("Segoe UI Variable Display"),
            FontSize = 16,
            FontWeight = FontWeights.SemiBold
        };
        _toolTipCount = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            FontFamily = new FontFamily("Segoe UI Variable Text"),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = ResourceBrush("DingAccentBrush")
        };
        _toolTipBody = new TextBlock
        {
            MaxWidth = 310,
            MaxLines = 3,
            FontFamily = new FontFamily("Segoe UI Variable Text"),
            FontSize = 14,
            LineHeight = 20,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        _toolTipFooter = new TextBlock
        {
            FontSize = 12,
            Opacity = 0.68,
            FontFamily = new FontFamily("Segoe UI Variable Text"),
            TextWrapping = TextWrapping.Wrap
        };
        var iconTile = new Border
        {
            Width = 28,
            Height = 28,
            Background = ResourceBrush("DingAccentSoftBrush"),
            CornerRadius = new CornerRadius(7),
            Child = new FontIcon
            {
                Glyph = "\uE823",
                FontSize = 15,
                Foreground = ResourceBrush("DingAccentBrush")
            }
        };
        var header = new Grid { ColumnSpacing = 9 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(iconTile);
        Grid.SetColumn(_toolTipTitle, 1);
        header.Children.Add(_toolTipTitle);
        var countChip = new Border
        {
            Padding = new Thickness(8, 3, 8, 3),
            Background = ResourceBrush("DingAccentSoftBrush"),
            CornerRadius = new CornerRadius(6),
            Child = _toolTipCount
        };
        Grid.SetColumn(countChip, 2);
        header.Children.Add(countChip);
        var messagePanel = new Border
        {
            Padding = new Thickness(10, 8, 10, 8),
            Background = ResourceBrush("DingAccentSoftBrush"),
            CornerRadius = new CornerRadius(7),
            Child = _toolTipBody
        };
        var contentStack = new StackPanel
        {
            Spacing = 9,
            Children =
            {
                header,
                messagePanel,
                _toolTipFooter
            }
        };
        var toolTipContent = new Border
        {
            Width = 334,
            Padding = new Thickness(12),
            Background = ResourceBrush("DingSurfaceRaisedBrush"),
            BorderBrush = ResourceBrush("DingDividerBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Child = contentStack
        };

        _icon = new TaskbarIcon
        {
            IconSource = new BitmapImage(NormalIconUri),
            ToolTipText = "DingLater · 捕获中 · 暂无待处理消息",
            TrayToolTip = toolTipContent,
            ContextMenuMode = ContextMenuMode.PopupMenu,
            ContextFlyout = menu,
            LeftClickCommand = openCommand
        };
        _icon.ForceCreate(enablesEfficiencyMode: false);
        _icon.TrayIcon.MessageWindow.MouseEventReceived += MessageWindow_MouseEventReceived;

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
        _toolTipCount.Text = presentation.Title;
        _toolTipBody.Text = presentation.Body;
        _toolTipFooter.Text = presentation.Footer;

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

    private static Brush ResourceBrush(string key) =>
        (Brush)Application.Current.Resources[key];
}
