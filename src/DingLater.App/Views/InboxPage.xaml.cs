using System.ComponentModel;
using DingLater.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.System;
using Windows.UI.ViewManagement;

namespace DingLater.App.Views;

public sealed partial class InboxPage : Page
{
    private readonly MainViewModel _viewModel;
    private readonly UISettings _uiSettings = new();
    private readonly XamlUICommand _quickSnoozeCommand = new();
    private bool _twoPane;
    private bool _showingDetail;
    private bool _subscribed = true;

    public InboxPage(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        _quickSnoozeCommand.CanExecuteRequested += QuickSnoozeCommand_CanExecuteRequested;
        _quickSnoozeCommand.ExecuteRequested += QuickSnoozeCommand_ExecuteRequested;
        QuickSnoozeButton.Command = _quickSnoozeCommand;
        QuickMinutesBox.Value = viewModel.QuickSnoozeMinutes;
        Loaded += InboxPage_Loaded;
        SizeChanged += InboxPage_SizeChanged;
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
        Unloaded += InboxPage_Unloaded;
    }

    internal void RefreshFromViewModel()
    {
        SearchBox.Text = _viewModel.SearchText;
        QuickMinutesBox.Value = _viewModel.QuickSnoozeMinutes;
        ConversationList.SelectedItem = _viewModel.SelectedConversation;
        MessageList.SelectedItem = _viewModel.SelectedMessage;
        RefreshVisualState();
    }

    internal void ShowSelectedMessage()
    {
        _showingDetail = _viewModel.SelectedConversation is not null;
        RefreshFromViewModel();
        UpdateResponsiveLayout();
    }

    private void InboxPage_Loaded(object sender, RoutedEventArgs e)
    {
        if (!_subscribed)
        {
            _viewModel.PropertyChanged += ViewModel_PropertyChanged;
            _subscribed = true;
        }

        RefreshFromViewModel();
        UpdateResponsiveLayout();
    }

    private void InboxPage_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateResponsiveLayout();

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.SelectedConversation)
            or nameof(MainViewModel.SelectedMessage)
            or nameof(MainViewModel.Section)
            or nameof(MainViewModel.IsEmpty)
            or nameof(MainViewModel.QuickSnoozeText))
        {
            RefreshVisualState();
        }
    }

    private void RefreshVisualState()
    {
        EmptyState.Visibility = _viewModel.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
        DetailEmptyState.Visibility = _viewModel.SelectedConversation is null
            ? Visibility.Visible
            : Visibility.Collapsed;
        QuickSnoozeButton.Content = _viewModel.QuickSnoozeText;
        var message = _viewModel.SelectedMessage;
        ActionBar.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
        var handled = _viewModel.Section == InboxSection.Handled;
        SnoozeActions.Visibility = handled ? Visibility.Collapsed : Visibility.Visible;
        RestoreButton.Visibility = handled && message is not null ? Visibility.Visible : Visibility.Collapsed;
        if (message is null)
        {
            return;
        }

        var now = DateTimeOffset.Now;
        var rememberedDue = now.AddMinutes(_viewModel.QuickSnoozeMinutes);
        QuickSnoozeButton.IsEnabled = message.ExpiresAt > now;
        ToolTipService.SetToolTip(
            QuickSnoozeButton,
            rememberedDue <= message.ExpiresAt
                ? null
                : $"当前常用时长超出清理时间 {message.ExpiresAt.LocalDateTime:M月d日 HH:mm}，可从箭头选择更短时长。");
        _quickSnoozeCommand.NotifyCanExecuteChanged();
        SetAvailability(Preset10Button, now.AddMinutes(10) <= message.ExpiresAt, "10 分钟后晚于消息清理时间。");
        SetAvailability(Preset15Button, now.AddMinutes(15) <= message.ExpiresAt, "15 分钟后晚于消息清理时间。");
        SetAvailability(Preset30Button, now.AddMinutes(30) <= message.ExpiresAt, "30 分钟后晚于消息清理时间。");
        SetAvailability(Preset60Button, now.AddMinutes(60) <= message.ExpiresAt, "60 分钟后晚于消息清理时间。");
        RefreshCustomMinutesAvailability();
        var tomorrow = SnoozeTimeFormatter.TomorrowAtNine(now);
        SetAvailability(
            TomorrowButton,
            tomorrow <= message.ExpiresAt,
            $"明早 9:00 晚于这条消息的清理时间 {message.ExpiresAt.LocalDateTime:M月d日 HH:mm}。");
        SetAvailability(
            ExactTimeButton,
            message.ExpiresAt > now,
            "这条消息已经到达清理时间，不能再设置稍后。");
        HandledButton.IsEnabled = message.CanHandle;
        if (ActionBar.Visibility == Visibility.Visible && _uiSettings.AnimationsEnabled)
        {
            FadeInActionBar();
        }
    }

    private void ConversationList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ConversationList.SelectedItem is not ConversationThreadViewModel conversation)
        {
            return;
        }

        _viewModel.SelectedConversation = conversation;
        MessageList.SelectedItem = _viewModel.SelectedMessage;
        if (!_twoPane)
        {
            _showingDetail = true;
            UpdateResponsiveLayout();
        }
    }

    private void MessageList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _viewModel.SelectedMessage = MessageList.SelectedItem as MessageCardViewModel;
        RefreshVisualState();
    }

    private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
        {
            sender.ItemsSource = _viewModel.GetSearchSuggestions(sender.Text);
            _viewModel.SearchText = sender.Text;
            RefreshFromViewModel();
        }
    }

    private void SearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        var value = args.ChosenSuggestion as string ?? args.QueryText;
        sender.Text = value;
        _viewModel.SearchText = value;
        RefreshFromViewModel();
    }

    private void QuickSnoozeCommand_CanExecuteRequested(XamlUICommand sender, CanExecuteRequestedEventArgs args)
    {
        var message = _viewModel.SelectedMessage;
        args.CanExecute = message is not null
                          && _viewModel.Section != InboxSection.Handled
                          && DateTimeOffset.Now.AddMinutes(_viewModel.QuickSnoozeMinutes) <= message.ExpiresAt;
    }

    private async void QuickSnoozeCommand_ExecuteRequested(XamlUICommand sender, ExecuteRequestedEventArgs args) =>
        await _viewModel.SnoozeRememberedMinutesAsync(_viewModel.SelectedMessage, _viewModel.QuickSnoozeMinutes);

    private async void PresetSnoozeButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string value } || !int.TryParse(value, out var minutes))
        {
            return;
        }

        QuickSnoozeButton.Flyout.Hide();
        await _viewModel.SnoozeRememberedMinutesAsync(_viewModel.SelectedMessage, minutes);
    }

    private void QuickMinutesBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args) =>
        RefreshCustomMinutesAvailability();

    private async void UseQuickMinutesButton_Click(object sender, RoutedEventArgs e)
    {
        if (double.IsNaN(QuickMinutesBox.Value))
        {
            return;
        }

        var minutes = Math.Clamp((int)Math.Round(QuickMinutesBox.Value), 1, 1440);
        QuickSnoozeButton.Flyout.Hide();
        await _viewModel.SnoozeRememberedMinutesAsync(_viewModel.SelectedMessage, minutes);
    }

    private async void TomorrowButton_Click(object sender, RoutedEventArgs e)
    {
        var error = await _viewModel.SnoozeAsync(
            _viewModel.SelectedMessage,
            SnoozeTimeFormatter.TomorrowAtNine(DateTimeOffset.Now));
        if (error is not null)
        {
            await ShowInlineErrorAsync(error);
        }
    }

    private async void ExactTimeButton_Click(object sender, RoutedEventArgs e)
    {
        var message = _viewModel.SelectedMessage;
        if (message is null)
        {
            return;
        }

        var now = DateTimeOffset.Now;
        var initial = now.AddMinutes(_viewModel.QuickSnoozeMinutes);
        if (initial > message.ExpiresAt)
        {
            initial = message.ExpiresAt;
        }

        var bodyFontSize = (double)Application.Current.Resources["DingBodyFontSize"];
        var secondaryFontSize = (double)Application.Current.Resources["DingSecondaryFontSize"];
        var textFontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Segoe UI Variable Text");
        var datePicker = new CalendarDatePicker
        {
            Header = "日期",
            Date = initial,
            MinDate = now,
            MaxDate = message.ExpiresAt,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            FontFamily = textFontFamily,
            FontSize = bodyFontSize
        };
        var timePicker = new TimePicker
        {
            Header = "时间",
            ClockIdentifier = "24HourClock",
            MinuteIncrement = 1,
            SelectedTime = initial.LocalDateTime.TimeOfDay,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            FontFamily = textFontFamily,
            FontSize = bodyFontSize
        };
        var errorText = new TextBlock
        {
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["DingDangerBrush"],
            FontFamily = textFontFamily,
            FontSize = secondaryFontSize,
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed
        };
        var content = new StackPanel { Spacing = 12, MinWidth = 320 };
        content.Children.Add(datePicker);
        content.Children.Add(timePicker);
        content.Children.Add(errorText);
        DateTimeOffset? selected = null;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "选择稍后时间",
            Content = content,
            PrimaryButtonText = "设为稍后",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };
        dialog.PrimaryButtonClick += (_, args) =>
        {
            if (datePicker.Date is not { } date || timePicker.SelectedTime is not { } time)
            {
                errorText.Text = "请选择日期和时间。";
                errorText.Visibility = Visibility.Visible;
                args.Cancel = true;
                return;
            }

            var local = DateTime.SpecifyKind(date.LocalDateTime.Date + time, DateTimeKind.Unspecified);
            var candidate = new DateTimeOffset(local);
            if (!SnoozeTimeFormatter.TryValidate(candidate, message.ExpiresAt, DateTimeOffset.Now, out var error))
            {
                errorText.Text = error;
                errorText.Visibility = Visibility.Visible;
                args.Cancel = true;
                return;
            }

            selected = candidate;
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary && selected is { } dueAt)
        {
            var error = await _viewModel.SnoozeAsync(message, dueAt);
            if (error is not null)
            {
                await ShowInlineErrorAsync(error);
            }
        }
    }

    private async void HandledButton_Click(object sender, RoutedEventArgs e) =>
        await _viewModel.MarkHandledAsync(_viewModel.SelectedMessage);

    private async void RestoreButton_Click(object sender, RoutedEventArgs e) =>
        await _viewModel.RestoreInboxAsync(_viewModel.SelectedMessage);

    private void MessageInfoButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: MessageCardViewModel message } anchor)
        {
            return;
        }

        var content = new StackPanel { Spacing = 7, Width = 300, Padding = new Thickness(4) };
        content.Children.Add(InfoLine("会话", message.Conversation));
        content.Children.Add(InfoLine("时间", message.ExactCapturedAt));
        content.Children.Add(InfoLine("类型", message.KindLabel));
        content.Children.Add(InfoLine("来源", message.SourceLabel));
        content.Children.Add(InfoLine("识别", message.ConfidenceText));
        content.Children.Add(InfoLine("清理", message.ExpiryText));
        if (!string.IsNullOrWhiteSpace(message.SnoozedUntilText))
        {
            content.Children.Add(InfoLine("提醒", message.SnoozedUntilText));
        }

        new Flyout { Content = content }.ShowAt(anchor);
    }

    private void BackButton_Click(object sender, RoutedEventArgs e) => GoBackToConversations();

    private void Back_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (!_twoPane && _showingDetail)
        {
            GoBackToConversations();
            args.Handled = true;
        }
    }

    private void FocusSearch_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        SearchBox.Focus(FocusState.Keyboard);
        args.Handled = true;
    }

    private void GoBackToConversations()
    {
        _showingDetail = false;
        UpdateResponsiveLayout();
        ConversationList.Focus(FocusState.Programmatic);
    }

    private void UpdateResponsiveLayout()
    {
        var width = XamlRoot?.Size.Width ?? ActualWidth;
        _twoPane = width >= 1008;
        var compactActions = width < 720;
        SnoozeActions.Orientation = compactActions ? Orientation.Vertical : Orientation.Horizontal;
        SnoozeActions.HorizontalAlignment = compactActions ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
        foreach (var control in new Control[] { QuickSnoozeButton, TomorrowButton, ExactTimeButton, HandledButton })
        {
            control.HorizontalAlignment = compactActions ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
        }

        if (_twoPane)
        {
            ConversationColumn.Width = new GridLength(340);
            DividerColumn.Width = new GridLength(1);
            DetailColumn.Width = new GridLength(1, GridUnitType.Star);
            ConversationPane.Visibility = Visibility.Visible;
            PaneDivider.Visibility = Visibility.Visible;
            DetailPane.Visibility = Visibility.Visible;
            BackButton.Visibility = Visibility.Collapsed;
            Grid.SetColumn(DetailPane, 2);
            Grid.SetColumnSpan(DetailPane, 1);
            DetailPane.CornerRadius = new CornerRadius(0, 8, 8, 0);
            DetailPane.BorderThickness = new Thickness(0, 1, 1, 1);
            return;
        }

        ConversationColumn.Width = new GridLength(1, GridUnitType.Star);
        DividerColumn.Width = new GridLength(0);
        DetailColumn.Width = new GridLength(1, GridUnitType.Star);
        PaneDivider.Visibility = Visibility.Collapsed;
        ConversationPane.Visibility = _showingDetail ? Visibility.Collapsed : Visibility.Visible;
        DetailPane.Visibility = _showingDetail ? Visibility.Visible : Visibility.Collapsed;
        BackButton.Visibility = _showingDetail ? Visibility.Visible : Visibility.Collapsed;
        Grid.SetColumn(DetailPane, 0);
        Grid.SetColumnSpan(DetailPane, 3);
        DetailPane.CornerRadius = new CornerRadius(8);
        DetailPane.BorderThickness = new Thickness(1);
    }

    private void FadeInActionBar()
    {
        ActionBar.Opacity = 0;
        var animation = new DoubleAnimation
        {
            To = 1,
            Duration = new Duration(TimeSpan.FromMilliseconds(140))
        };
        Storyboard.SetTarget(animation, ActionBar);
        Storyboard.SetTargetProperty(animation, "Opacity");
        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);
        storyboard.Begin();
    }

    private static void SetAvailability(Control control, bool available, string unavailableReason)
    {
        control.IsEnabled = available;
        ToolTipService.SetToolTip(control, available ? null : unavailableReason);
    }

    private void RefreshCustomMinutesAvailability()
    {
        var message = _viewModel.SelectedMessage;
        var validNumber = !double.IsNaN(QuickMinutesBox.Value);
        var minutes = validNumber ? Math.Clamp((int)Math.Round(QuickMinutesBox.Value), 1, 1440) : 0;
        var available = message is not null
                        && validNumber
                        && DateTimeOffset.Now.AddMinutes(minutes) <= message.ExpiresAt;
        SetAvailability(
            UseQuickMinutesButton,
            available,
            message is null ? "请先选择一条消息。" : "这个时长晚于消息清理时间。");
    }

    private static FrameworkElement InfoLine(string label, string value)
    {
        var bodyFontSize = (double)Application.Current.Resources["DingBodyFontSize"];
        var secondaryFontSize = (double)Application.Current.Resources["DingSecondaryFontSize"];
        var textFontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Segoe UI Variable Text");
        var panel = new StackPanel { Spacing = 2 };
        panel.Children.Add(new TextBlock
        {
            Text = label,
            FontFamily = textFontFamily,
            FontSize = secondaryFontSize,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["DingSecondaryTextBrush"]
        });
        panel.Children.Add(new TextBlock
        {
            Text = value,
            FontFamily = textFontFamily,
            FontSize = bodyFontSize,
            TextWrapping = TextWrapping.Wrap
        });
        return panel;
    }

    private async Task ShowInlineErrorAsync(string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "无法设置稍后时间",
            Content = message,
            CloseButtonText = "知道了"
        };
        await dialog.ShowAsync();
    }

    private void InboxPage_Unloaded(object sender, RoutedEventArgs e)
    {
        _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
        _subscribed = false;
    }
}
