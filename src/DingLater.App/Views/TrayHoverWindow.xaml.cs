using System.Runtime.InteropServices.WindowsRuntime;
using DingLater.App.Services;
using DingLater.App.ViewModels;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;
using Windows.Graphics.Imaging;
using Windows.System;
using WinRT.Interop;
using DrawingRectangle = System.Drawing.Rectangle;
using DrawingSize = System.Drawing.Size;

namespace DingLater.App.Views;

public sealed partial class TrayHoverWindow : Window
{
    private readonly nint _handle;
    private DrawingRectangle _anchor;
    private int _pendingCount;
    private bool _markingAll;

    public TrayHoverWindow()
    {
        InitializeComponent();
        Title = "DingLater · 待处理";
        _handle = WindowNative.GetWindowHandle(this);
        AppWindow.IsShownInSwitchers = false;
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsAlwaysOnTop = true;
            presenter.SetBorderAndTitleBar(false, false);
        }
        TrayHoverNative.RoundCorners(_handle);
        try
        {
            if (DesktopAcrylicController.IsSupported())
            {
                SystemBackdrop = new DesktopAcrylicBackdrop();
                Surface.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            }
        }
        catch
        {
            SystemBackdrop = null;
        }
        Activated += (_, args) =>
        {
            var wasActive = IsActive;
            IsActive = args.WindowActivationState != WindowActivationState.Deactivated;
            if (wasActive && !IsActive && IsVisible) HideFlyout();
        };
    }

    internal event EventHandler<Guid>? MessageOpenRequested;
    internal event EventHandler? InboxOpenRequested;
    internal Func<Task>? MarkAllHandledAsync { get; set; }
    internal bool IsVisible { get; private set; }
    internal bool IsActive { get; private set; }
    internal bool IsForeground => TrayHoverNative.ForegroundWindow == _handle;
    internal DrawingRectangle FlyoutBounds => new(AppWindow.Position.X, AppWindow.Position.Y, AppWindow.Size.Width, AppWindow.Size.Height);

    internal void VerifyRenderedState(int pendingCount, int contactCount)
    {
        if (!IsVisible || Surface.ActualWidth <= 0 || Surface.ActualHeight <= 0
            || ContactsList.Items.Count != contactCount || _pendingCount != pendingCount
            || MarkAllButton.IsEnabled != (pendingCount > 0) || MarkAllButton.ActualWidth <= 0
            || (contactCount > 0 && ContactsList.ActualHeight <= 0))
        {
            throw new InvalidOperationException("托盘浮层的渲染或待处理状态不符合预期。");
        }
    }

    internal async Task SaveLayoutPreviewAsync(string path)
    {
        // Render only the isolated smoke instance's XAML. Desktop acrylic is external to this visual tree.
        var background = Surface.Background;
        try
        {
            Surface.Background = (Brush)Application.Current.Resources["DingSurfaceRaisedBrush"];
            Surface.UpdateLayout();
            var bitmap = new RenderTargetBitmap();
            await bitmap.RenderAsync(Surface, (int)Math.Ceiling(Surface.ActualWidth * 2), (int)Math.Ceiling(Surface.ActualHeight * 2));
            var pixels = await bitmap.GetPixelsAsync();
            using var file = File.Create(path);
            using var stream = file.AsRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 192, 192, pixels.ToArray());
            await encoder.FlushAsync();
        }
        finally { Surface.Background = background; }
    }

    internal void Update(TrayInboxPresentation presentation, bool paused)
    {
        _pendingCount = presentation.PendingCount;
        PendingSummary.Text = $"{presentation.PendingCount} 条待处理";
        ContactSummary.Text = presentation.Contacts.Count > 0
            ? $"{presentation.Contacts.Count} 位联系人"
            : paused ? "已暂停" : "捕获中";
        if (ContactsList.ItemsSource is not IReadOnlyList<TrayContactViewModel> existing
            || !existing.SequenceEqual(presentation.Contacts))
        {
            ContactsList.ItemsSource = presentation.Contacts;
        }
        ContactsList.Visibility = presentation.Contacts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyState.Visibility = presentation.Contacts.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        EmptyTitle.Text = presentation.PendingCount == 0 ? "暂无待处理消息" : "消息预览已关闭";
        EmptyBody.Text = presentation.PendingCount == 0
            ? paused ? "捕获已暂停，可打开应用重新开始。" : "正在后台捕获新消息。"
            : "可在设置中开启消息预览。";
        MarkAllButton.IsEnabled = presentation.PendingCount > 0 && !_markingAll;
        if (IsVisible) ResizeAndPosition();
    }

    internal void ShowAt(DrawingRectangle anchor)
    {
        _anchor = anchor;
        ResizeAndPosition();
        AppWindow.Show(activateWindow: false);
        IsVisible = true;
    }

    internal void HideFlyout()
    {
        if (!IsVisible) return;
        IsVisible = false;
        IsActive = false;
        AppWindow.Hide();
    }

    private void ResizeAndPosition()
    {
        var display = DisplayArea.GetFromPoint(new PointInt32(_anchor.Left, _anchor.Top), DisplayAreaFallback.Nearest);
        var work = display.WorkArea;
        if (!IsVisible) AppWindow.Move(new PointInt32(_anchor.Left, work.Y));
        var scale = TrayHoverNative.GetScale(_handle);
        Surface.Width = Math.Min(392, Math.Max(240, work.Width / scale - 16));
        ContactsList.MaxHeight = Math.Max(72, Math.Min(360, work.Height / scale - 164));
        Surface.Measure(new Windows.Foundation.Size(Surface.Width, double.PositiveInfinity));
        var size = new DrawingSize((int)Math.Ceiling(Surface.DesiredSize.Width * scale), (int)Math.Ceiling(Surface.DesiredSize.Height * scale));
        var bounds = TrayHoverPlacement.Calculate(new DrawingRectangle(work.X, work.Y, work.Width, work.Height), _anchor, size, Math.Max(1, (int)Math.Round(8 * scale)));
        AppWindow.MoveAndResize(new RectInt32(bounds.X, bounds.Y, bounds.Width, bounds.Height));
    }

    private void ContactsList_ItemClick(object sender, ItemClickEventArgs args)
    {
        if (args.ClickedItem is not TrayContactViewModel contact) return;
        HideFlyout();
        MessageOpenRequested?.Invoke(this, contact.LatestMessageId);
    }

    private void OpenInbox_Click(object sender, RoutedEventArgs args)
    {
        HideFlyout();
        InboxOpenRequested?.Invoke(this, EventArgs.Empty);
    }

    private async void MarkAllButton_Click(object sender, RoutedEventArgs args) => await MarkAllAsync();

    internal async Task MarkAllAsync()
    {
        if (_markingAll || _pendingCount == 0 || MarkAllHandledAsync is null) return;
        _markingAll = true;
        MarkAllButton.IsEnabled = false;
        MarkAllLabel.Text = "处理中…";
        try { await MarkAllHandledAsync(); }
        finally
        {
            _markingAll = false;
            MarkAllLabel.Text = "全部已处理";
            MarkAllButton.IsEnabled = _pendingCount > 0;
        }
    }

    private void RootGrid_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key != VirtualKey.Escape) return;
        args.Handled = true;
        HideFlyout();
    }
}
