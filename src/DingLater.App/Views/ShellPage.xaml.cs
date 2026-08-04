using System.ComponentModel;
using DingLater.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DingLater.App.Views;

public sealed partial class ShellPage : Page
{
    private readonly MainViewModel _viewModel;
    private readonly InboxPage _inboxPage;
    private readonly SettingsPage _settingsPage;

    public ShellPage(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        _inboxPage = new InboxPage(viewModel);
        _settingsPage = new SettingsPage(viewModel);
        _viewModel.ErrorOccurred += ViewModel_ErrorOccurred;
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
        Unloaded += ShellPage_Unloaded;
        Loaded += ShellPage_Loaded;
    }

    internal void OpenMessage(Guid id)
    {
        _viewModel.IsSettingsPage = false;
        _viewModel.SelectFromActivation(id);
        Navigation.SelectedItem = ItemForSection(_viewModel.Section);
        PageHost.Content = _inboxPage;
        _inboxPage.ShowSelectedMessage();
    }

    private void ShellPage_Loaded(object sender, RoutedEventArgs e)
    {
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

    private NavigationViewItem ItemForSection(InboxSection section) => section switch
    {
        InboxSection.Snoozed => SnoozedItem,
        InboxSection.Handled => HandledItem,
        _ => InboxItem
    };

    private void ViewModel_ErrorOccurred(object? sender, string message)
    {
        ErrorInfoBar.Message = message;
        ErrorInfoBar.IsOpen = true;
    }

    private async void CaptureToggleButton_Click(object sender, RoutedEventArgs e)
    {
        CaptureToggleButton.IsEnabled = false;
        try
        {
            await _viewModel.ToggleCaptureAsync();
        }
        finally
        {
            CaptureToggleButton.IsEnabled = true;
            RefreshCaptureStatus();
        }
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.CapturePaused)
            or nameof(MainViewModel.CaptureActionText)
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
        CaptureStatusText.Text = _viewModel.StatusText;
        CaptureToggleButton.Content = _viewModel.CaptureActionText;
        CaptureToggleButton.Style = (Style)Application.Current.Resources[
            _viewModel.CapturePaused ? "DingPrimaryButtonStyle" : "DingQuietButtonStyle"];
        CaptureStatusPanel.Background = (Brush)Application.Current.Resources[
            _viewModel.CapturePaused ? "DingAccentSoftBrush" : "DingSurfaceBrush"];
        var statusBrush = _viewModel.CapturePaused
            ? "DingAccentBrush"
            : _viewModel.StatusText == "捕获中"
                ? "DingSuccessBrush"
                : "DingSecondaryTextBrush";
        CaptureStatusDot.Background = (Brush)Application.Current.Resources[statusBrush];
    }

    private void RefreshNavigationCounts()
    {
        InboxBadge.Visibility = _viewModel.InboxCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        SnoozedCountText.Text = _viewModel.SnoozedCount.ToString();
        SnoozedCountText.Visibility = _viewModel.SnoozedCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        HandledCountText.Text = _viewModel.HandledCount.ToString();
        HandledCountText.Visibility = _viewModel.HandledCount > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShellPage_Unloaded(object sender, RoutedEventArgs e)
    {
        _viewModel.ErrorOccurred -= ViewModel_ErrorOccurred;
        _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
    }
}
