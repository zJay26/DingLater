using DingLater.App.ViewModels;
using DingLater.Core.Models;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;

namespace DingLater.App.Services;

internal sealed class TrayIconService : IDisposable
{
    private readonly TaskbarIcon _icon;
    private readonly MenuFlyoutItem _pauseItem;
    private Guid? _pendingMessageId;

    internal TrayIconService()
    {
        var menu = new MenuFlyout();
        var openItem = new MenuFlyoutItem { Text = "打开 DingLater" };
        openItem.Click += (_, _) => OpenPendingOrWindow();
        _pauseItem = new MenuFlyoutItem { Text = "暂停捕获" };
        _pauseItem.Click += (_, _) => PauseToggleRequested?.Invoke(this, EventArgs.Empty);
        var exitItem = new MenuFlyoutItem { Text = "退出" };
        exitItem.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);
        menu.Items.Add(openItem);
        menu.Items.Add(_pauseItem);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(exitItem);

        var openCommand = new XamlUICommand();
        openCommand.ExecuteRequested += (_, _) => OpenPendingOrWindow();

        _icon = new TaskbarIcon
        {
            IconSource = new BitmapImage(new Uri("ms-appx:///Assets/DingLater.ico")),
            ToolTipText = "DingLater · 捕获中",
            ContextMenuMode = ContextMenuMode.PopupMenu,
            ContextFlyout = menu,
            DoubleClickCommand = openCommand
        };
        _icon.ForceCreate(enablesEfficiencyMode: false);
        _icon.TrayIcon.MessageWindow.MouseEventReceived += MessageWindow_MouseEventReceived;
    }

    internal event EventHandler? OpenRequested;
    internal event EventHandler? PauseToggleRequested;
    internal event EventHandler? ExitRequested;
    internal event EventHandler<Guid>? MessageOpenRequested;

    internal void SetPaused(bool paused)
    {
        _pauseItem.Text = paused ? "继续捕获" : "暂停捕获";
        _icon.ToolTipText = paused ? "DingLater · 已暂停" : "DingLater · 捕获中";
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
        _icon.TrayIcon.MessageWindow.MouseEventReceived -= MessageWindow_MouseEventReceived;
        _icon.Dispose();
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
