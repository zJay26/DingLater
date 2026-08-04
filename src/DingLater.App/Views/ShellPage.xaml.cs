using DingLater.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

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

    private void ShellPage_Unloaded(object sender, RoutedEventArgs e) =>
        _viewModel.ErrorOccurred -= ViewModel_ErrorOccurred;
}
