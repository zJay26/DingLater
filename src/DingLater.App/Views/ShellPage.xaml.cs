using System.Collections.Specialized;
using System.ComponentModel;
using DingLater.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace DingLater.App.Views;

public sealed partial class ShellPage : Page
{
    private readonly MainViewModel _viewModel;
    private readonly InboxPage _inboxPage;
    private readonly SettingsPage _settingsPage;
    private readonly UpdateViewModel _updates;

    public ShellPage(MainViewModel viewModel, UpdateViewModel updates)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _updates = updates;
        DataContext = viewModel;
        _inboxPage = new InboxPage(viewModel);
        _settingsPage = new SettingsPage(viewModel, updates);
        Loaded += ShellPage_Loaded;
        Unloaded += ShellPage_Unloaded;
        SizeChanged += (_, _) => UpdateBannerInsets();
    }

    internal MainViewModel ViewModel => _viewModel;

    internal void OpenMessage(Guid id)
    {
        _viewModel.IsSettingsPage = false;
        _viewModel.SelectFromActivation(id);
        Navigation.SelectedItem = ItemForSection(_viewModel.Section);
        PageHost.Content = _inboxPage;
        _inboxPage.ShowSelectedMessage();
    }

    internal void OpenInbox()
    {
        _viewModel.IsSettingsPage = false;
        _viewModel.SearchText = string.Empty;
        _viewModel.Section = InboxSection.Inbox;
        Navigation.SelectedItem = ItemForSection(InboxSection.Inbox);
        PageHost.Content = _inboxPage;
    }

    internal void OpenSettings()
    {
        Navigation.SelectedItem = Navigation.SettingsItem;
        _settingsPage.ShowUpdates();
    }

    private void ShellPage_Loaded(object sender, RoutedEventArgs e)
    {
        _updates.PropertyChanged += Updates_PropertyChanged;
        _viewModel.ErrorOccurred += ViewModel_ErrorOccurred;
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
        _viewModel.HealthSources.CollectionChanged += HealthSources_CollectionChanged;
        RefreshHealthList();
        UpdateBannerInsets();
        RefreshUpdateNotice();
        RefreshCaptureStatus();
        RefreshNavigationCounts();
        if (_viewModel.IsSettingsPage)
        {
            Navigation.SelectedItem = Navigation.SettingsItem;
            PageHost.Content = _settingsPage;
            _settingsPage.RefreshControls();
            return;
        }

        Navigation.SelectedItem = ItemForSection(_viewModel.Section);
        PageHost.Content = _inboxPage;
        _inboxPage.RefreshFromViewModel();
    }

    private void ShellPage_Unloaded(object sender, RoutedEventArgs e)
    {
        _updates.PropertyChanged -= Updates_PropertyChanged;
        _viewModel.ErrorOccurred -= ViewModel_ErrorOccurred;
        _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
        _viewModel.HealthSources.CollectionChanged -= HealthSources_CollectionChanged;
    }

    private void HealthSources_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => RefreshHealthList();

    private void RefreshHealthList() =>
        HealthList.Visibility = _viewModel.HealthSources.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    private void Navigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.IsSettingsSelected)
        {
            _viewModel.IsSettingsPage = true;
            PageHost.Content = _settingsPage;
            _settingsPage.RefreshControls();
            return;
        }

        if (args.SelectedItemContainer?.Tag is not string tag)
        {
            return;
        }

        _viewModel.IsSettingsPage = false;
        _viewModel.Section = tag switch
        {
            "Snoozed" => InboxSection.Snoozed,
            "Handled" => InboxSection.Handled,
            _ => InboxSection.Inbox
        };
        PageHost.Content = _inboxPage;
        _inboxPage.RefreshFromViewModel();
    }

    private void SectionAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        Navigation.SelectedItem = sender.Key switch
        {
            VirtualKey.Number2 => SnoozedItem,
            VirtualKey.Number3 => HandledItem,
            _ => InboxItem
        };
        args.Handled = true;
    }

    private NavigationViewItem ItemForSection(InboxSection section) => section switch
    {
        InboxSection.Snoozed => SnoozedItem,
        InboxSection.Handled => HandledItem,
        _ => InboxItem
    };

    private void ViewModel_ErrorOccurred(object? sender, string message)
    {
        ErrorInfoBar.Message = message;
        SetBanner(ErrorInfoBar, true);
    }

    private async void CaptureToggleButton_Click(object sender, RoutedEventArgs e)
    {
        CaptureFlyout.Hide();
        if (sender is Control control)
        {
            control.IsEnabled = false;
        }

        try
        {
            await _viewModel.ToggleCaptureAsync();
        }
        finally
        {
            if (sender is Control finished)
            {
                finished.IsEnabled = true;
            }

            RefreshCaptureStatus();
        }
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.CapturePaused)
            or nameof(MainViewModel.CaptureActionText)
            or nameof(MainViewModel.CaptureStatus)
            or nameof(MainViewModel.CaptureStatusDetail)
            or nameof(MainViewModel.StatusText))
        {
            RefreshCaptureStatus();
        }

        if (e.PropertyName is nameof(MainViewModel.InboxCount)
            or nameof(MainViewModel.SnoozedCount)
            or nameof(MainViewModel.HandledCount))
        {
            RefreshNavigationCounts();
        }
    }

    private void RefreshCaptureStatus()
    {
        var paused = _viewModel.CapturePaused;
        var state = paused
            ? CaptureStatusKind.Paused
            : _viewModel.CaptureStatus;
        VisualStateManager.GoToState(this, state switch
        {
            CaptureStatusKind.Capturing => "CaptureCapturing",
            CaptureStatusKind.Waiting => "CaptureWaiting",
            CaptureStatusKind.Paused => "CapturePaused",
            _ => "CapturePreparing"
        }, false);
        CaptureStatusText.Text = paused ? "捕获已暂停" : _viewModel.StatusText;
        CaptureDetailText.Text = _viewModel.CaptureStatusDetail;
        CaptureToggleLabel.Text = _viewModel.CaptureActionText;
        ToolTipService.SetToolTip(CaptureStatusButton, _viewModel.CaptureStatusDetail);
        AutomationProperties.SetName(CaptureStatusButton, $"消息捕获：{CaptureStatusText.Text}");
        SetBanner(PausedInfoBar, paused);
    }

    private void RefreshNavigationCounts()
    {
        InboxBadge.Visibility = _viewModel.InboxCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        SnoozedCountText.Text = _viewModel.SnoozedCount.ToString();
        SnoozedCountText.Visibility = _viewModel.SnoozedCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        HandledCountText.Text = _viewModel.HandledCount.ToString();
        HandledCountText.Visibility = _viewModel.HandledCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetItemStatus(InboxItem, $"{_viewModel.InboxCount} 条");
        AutomationProperties.SetItemStatus(SnoozedItem, $"{_viewModel.SnoozedCount} 条");
        AutomationProperties.SetItemStatus(HandledItem, $"{_viewModel.HandledCount} 条");
    }

    private void Updates_PropertyChanged(object? sender, PropertyChangedEventArgs e) => RefreshUpdateNotice();

    private void RefreshUpdateNotice()
    {
        UpdateInfoBar.Title = _updates.AvailableVersionText;
        SetBanner(UpdateInfoBar, _updates.ShowNotice);
    }

    private void UpdateInfoBar_Closed(InfoBar sender, InfoBarClosedEventArgs args)
    {
        sender.Visibility = Visibility.Collapsed;
        _updates.DismissNotice();
    }

    private void Banner_Closed(InfoBar sender, InfoBarClosedEventArgs args) => sender.Visibility = Visibility.Collapsed;

    private void ViewUpdateButton_Click(object sender, RoutedEventArgs e) => OpenSettings();

    /// <summary>Closed InfoBars still reserve their margins, so collapse them as well.</summary>
    private static void SetBanner(InfoBar banner, bool open)
    {
        banner.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        banner.IsOpen = open;
    }

    private void UpdateBannerInsets()
    {
        var size = XamlRoot?.Size ?? new Windows.Foundation.Size(ActualWidth, ActualHeight);
        var padding = AdaptiveLayout.PagePadding(size.Width, size.Height);
        BannerHost.Padding = new Thickness(padding.Left, 0, padding.Right, 0);
    }
}
