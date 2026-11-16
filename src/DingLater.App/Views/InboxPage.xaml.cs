using System.ComponentModel;
using DingLater.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.ApplicationModel.DataTransfer;
using Windows.UI.ViewManagement;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace DingLater.App.Views;

public sealed partial class InboxPage : Page
{
    private readonly MainViewModel _viewModel;
    private readonly UISettings _uiSettings = new();
    private readonly XamlUICommand _quickSnoozeCommand = new();
    private readonly DispatcherQueueTimer _searchTimer;
    private readonly MenuFlyout _messageMenu = new();
    private readonly MenuFlyoutItem _copyMenuItem;
    private MessageCardViewModel? _menuMessage;
    private FrameworkElement? _menuAnchor;
    private Guid? _lastAnimatedMessageId;
    private bool _twoPane;
    private bool _showingDetail;
    private bool _compactActionsLayout;

    public InboxPage(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        _searchTimer = DispatcherQueue.CreateTimer();
        _searchTimer.Interval = TimeSpan.FromMilliseconds(180);
        _searchTimer.IsRepeating = false;
        _searchTimer.Tick += (_, _) => ApplySearch();
        _quickSnoozeCommand.CanExecuteRequested += QuickSnoozeCommand_CanExecuteRequested;
        _quickSnoozeCommand.ExecuteRequested += QuickSnoozeCommand_ExecuteRequested;
        QuickSnoozeButton.Command = _quickSnoozeCommand;
        QuickMinutesBox.Value = viewModel.QuickSnoozeMinutes;

        _copyMenuItem = new MenuFlyoutItem
        {
            Text = "复制正文",
            Icon = new FontIcon { Glyph = "" },
            KeyboardAcceleratorTextOverride = "Ctrl+C"
        };
        _copyMenuItem.Click += (_, _) => CopyMessage(_menuMessage);
        var infoMenuItem = new MenuFlyoutItem { Text = "消息信息", Icon = new FontIcon { Glyph = "" } };
        infoMenuItem.Click += (_, _) =>
        {
            if (_menuMessage is not null && _menuAnchor is not null)
            {
                ShowMessageInfo(_menuMessage, _menuAnchor);
            }
        };
        _messageMenu.Items.Add(_copyMenuItem);
        _messageMenu.Items.Add(infoMenuItem);

        Loaded += InboxPage_Loaded;
        Unloaded += InboxPage_Unloaded;
        SizeChanged += InboxPage_SizeChanged;
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

    public static Visibility BoolToVisibility(bool value) =>
        value ? Visibility.Visible : Visibility.Collapsed;

    private void InboxPage_Loaded(object sender, RoutedEventArgs e)
    {
        // Subscribe only while in the tree: shells rebuilt for a new font scale must not leave pages listening.
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
        RefreshFromViewModel();
        UpdateResponsiveLayout();
    }

    private void InboxPage_Unloaded(object sender, RoutedEventArgs e)
    {
        _searchTimer.Stop();
        _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
    }

    private void InboxPage_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateResponsiveLayout();

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.SelectedConversation)
            or nameof(MainViewModel.SelectedMessage)
            or nameof(MainViewModel.Section)
            or nameof(MainViewModel.IsEmpty)
            or nameof(MainViewModel.QuickSnoozeText)
            or nameof(MainViewModel.InboxCount)
            or nameof(MainViewModel.SnoozedCount)
            or nameof(MainViewModel.HandledCount)
            or nameof(MainViewModel.IsBusy))
        {
            RefreshVisualState();
        }
    }

    private void RefreshVisualState()
    {
        RefreshEmptyState();
        RefreshBulkAction();
        RefreshDetailHeader();
        QuickSnoozeLabel.Text = _viewModel.QuickSnoozeText;
        var message = _viewModel.SelectedMessage;
        ActionBar.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
        var handled = _viewModel.Section == InboxSection.Handled;
        SnoozeActions.Visibility = handled ? Visibility.Collapsed : Visibility.Visible;
        HandledActions.Visibility = handled && message is not null ? Visibility.Visible : Visibility.Collapsed;
        UpdateResponsiveLayout();
        if (message is null)
        {
            return;
        }

        var now = DateTimeOffset.Now;
        var rememberedDue = now.AddMinutes(_viewModel.QuickSnoozeMinutes);
        QuickSnoozeButton.IsEnabled = message.ExpiresAt > now;
        var quickReason = rememberedDue < message.ExpiresAt
            ? null
            : $"当前常用时长超出清理时间 {message.ExpiresAt.LocalDateTime:M月d日 HH:mm}，可从箭头选择更短时长。";
        ToolTipService.SetToolTip(QuickSnoozeButton, quickReason);
        AutomationProperties.SetHelpText(QuickSnoozeButton, quickReason ?? string.Empty);
        _quickSnoozeCommand.NotifyCanExecuteChanged();
        SetAvailability(Preset10Button, now.AddMinutes(10) < message.ExpiresAt, "10 分钟后晚于消息清理时间。");
        SetAvailability(Preset15Button, now.AddMinutes(15) < message.ExpiresAt, "15 分钟后晚于消息清理时间。");
        SetAvailability(Preset30Button, now.AddMinutes(30) < message.ExpiresAt, "30 分钟后晚于消息清理时间。");
        SetAvailability(Preset60Button, now.AddMinutes(60) < message.ExpiresAt, "1 小时后晚于消息清理时间。");
        RefreshCustomMinutesAvailability();
        var tomorrow = SnoozeTimeFormatter.TomorrowAtNine(now);
        SetAvailability(
            TomorrowButton,
            tomorrow < message.ExpiresAt,
            $"明早 9:00 晚于这条消息的清理时间 {message.ExpiresAt.LocalDateTime:M月d日 HH:mm}。");
        SetAvailability(
            ExactTimeButton,
            message.ExpiresAt > now,
            "这条消息已经到达清理时间，不能再设置稍后。");
        HandledButton.IsEnabled = message.CanHandle;
        if (ActionBar.Visibility == Visibility.Visible && _uiSettings.AnimationsEnabled
            && _lastAnimatedMessageId != message.Id)
        {
            FadeInActionBar();
        }

        _lastAnimatedMessageId = message.Id;
    }

    private void RefreshDetailHeader()
    {
        var conversation = _viewModel.SelectedConversation;
        DetailTitle.Text = conversation?.Title ?? string.Empty;
        DetailSubtitle.Text = conversation?.ScopeText ?? string.Empty;
        DetailAvatarText.Text = conversation?.AvatarText ?? string.Empty;
        var group = conversation?.IsGroup == true;
        DetailAvatarText.Visibility = group ? Visibility.Collapsed : Visibility.Visible;
        DetailAvatarGlyph.Visibility = group ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ConversationList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Selection also changes programmatically (refreshes, arrow keys); it only syncs the view model.
        if (ConversationList.SelectedItem is not ConversationThreadViewModel conversation)
        {
            return;
        }

        _viewModel.SelectedConversation = conversation;
        MessageList.SelectedItem = _viewModel.SelectedMessage;
    }

    private void ConversationList_ItemClick(object sender, ItemClickEventArgs e)
    {
        // Only an explicit click, tap or Enter opens the detail view in single-pane layouts.
        if (e.ClickedItem is not ConversationThreadViewModel conversation)
        {
            return;
        }

        _viewModel.SelectedConversation = conversation;
        if (!_twoPane)
        {
            _showingDetail = true;
            UpdateResponsiveLayout();
            // Move keyboard focus once the detail pane has been laid out; the clicked row just collapsed.
            DispatcherQueue.TryEnqueue(() => MessageList.Focus(FocusState.Programmatic));
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
            _searchTimer.Stop();
            _searchTimer.Start();
        }
    }

    private void SearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        _searchTimer.Stop();
        var value = args.ChosenSuggestion as string ?? args.QueryText;
        sender.Text = value;
        _viewModel.SearchText = value;
        RefreshFromViewModel();
    }

    private void ApplySearch()
    {
        SearchBox.ItemsSource = _viewModel.GetSearchSuggestions(SearchBox.Text);
        _viewModel.SearchText = SearchBox.Text;
        RefreshFromViewModel();
    }

    private void ClearSearchButton_Click(object sender, RoutedEventArgs e)
    {
        _searchTimer.Stop();
        SearchBox.Text = string.Empty;
        ApplySearch();
        SearchBox.Focus(FocusState.Programmatic);
    }

    private void QuickSnoozeCommand_CanExecuteRequested(XamlUICommand sender, CanExecuteRequestedEventArgs args)
    {
        var message = _viewModel.SelectedMessage;
        args.CanExecute = message is not null
                          && _viewModel.Section != InboxSection.Handled
                          && DateTimeOffset.Now.AddMinutes(_viewModel.QuickSnoozeMinutes) < message.ExpiresAt;
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
            await ShowSnoozeErrorAsync(error);
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
        var textFontFamily = (Microsoft.UI.Xaml.Media.FontFamily)Application.Current.Resources["DingTextFontFamily"];
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
        var hint = new TextBlock
        {
            Style = (Style)Application.Current.Resources["DingSecondaryTextStyle"],
            Text = $"需早于这条消息的清理时间：{message.ExpiresAt.LocalDateTime:M月d日 HH:mm}。"
        };
        var errorText = new TextBlock
        {
            Style = (Style)Application.Current.Resources["DingSecondaryTextStyle"],
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["DingDangerBrush"],
            Visibility = Visibility.Collapsed
        };
        AutomationProperties.SetLiveSetting(errorText, AutomationLiveSetting.Assertive);
        var content = new StackPanel { Spacing = 12, MinWidth = 320 };
        content.Children.Add(datePicker);
        content.Children.Add(timePicker);
        content.Children.Add(hint);
        content.Children.Add(errorText);
        DateTimeOffset? selected = null;
        var dialog = AppDialog.Create(XamlRoot, "选择稍后时间", content, "取消", "设为稍后", ContentDialogButton.Primary);
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
        if (await AppDialog.ShowAsync(dialog) == ContentDialogResult.Primary && selected is { } dueAt)
        {
            var error = await _viewModel.SnoozeAsync(message, dueAt);
            if (error is not null)
            {
                await ShowSnoozeErrorAsync(error);
            }
        }
    }

    private async void HandledButton_Click(object sender, RoutedEventArgs e) =>
        await _viewModel.MarkHandledAsync(_viewModel.SelectedMessage);

    private async void BulkActionButton_Click(object sender, RoutedEventArgs e)
    {
        var section = _viewModel.Section;
        var count = SectionCount(section);
        if (count == 0)
        {
            return;
        }

        if (section == InboxSection.Handled && !await AppDialog.ConfirmAsync(
                XamlRoot,
                $"删除全部 {count} 条已处理消息？",
                "删除后无法恢复，不影响钉钉中的原消息。",
                "全部删除"))
        {
            return;
        }

        BulkActionButton.IsEnabled = false;
        try
        {
            if (section is InboxSection.Inbox or InboxSection.Snoozed)
            {
                await _viewModel.MarkAllHandledAsync(section);
            }
            else
            {
                await _viewModel.DeleteAllHandledAsync();
            }
        }
        finally
        {
            RefreshBulkAction();
        }
    }

    private async void RestoreButton_Click(object sender, RoutedEventArgs e) =>
        await _viewModel.RestoreInboxAsync(_viewModel.SelectedMessage);

    private async void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        var message = _viewModel.SelectedMessage;
        if (message is null)
        {
            return;
        }

        if (await AppDialog.ConfirmAsync(XamlRoot, "删除这条消息？", "删除后无法恢复，不影响钉钉中的原消息。", "删除"))
        {
            await _viewModel.DeleteAsync(message);
        }
    }

    private void MessageInfoButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: MessageCardViewModel message } anchor)
        {
            ShowMessageInfo(message, anchor);
        }
    }

    private void MessageList_ContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (args.OriginalSource is not FrameworkElement { DataContext: MessageCardViewModel message } anchor)
        {
            return;
        }

        _menuMessage = message;
        _menuAnchor = anchor;
        _copyMenuItem.IsEnabled = message.CanCopy;
        if (args.TryGetPosition(anchor, out var point))
        {
            _messageMenu.ShowAt(anchor, new FlyoutShowOptions { Position = point });
        }
        else
        {
            _messageMenu.ShowAt(anchor);
        }

        args.Handled = true;
    }

    private void CopyMessage_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (FocusManager.GetFocusedElement(XamlRoot) is TextBox)
        {
            return;
        }

        args.Handled = CopyMessage(_viewModel.SelectedMessage);
    }

    private static bool CopyMessage(MessageCardViewModel? message)
    {
        if (message is not { CanCopy: true })
        {
            return false;
        }

        var package = new DataPackage();
        package.SetText(message.CopyText);
        Clipboard.SetContent(package);
        return true;
    }

    private static void ShowMessageInfo(MessageCardViewModel message, FrameworkElement anchor)
    {
        var content = new StackPanel { Spacing = 10, Width = 300 };
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
        var windowWidth = XamlRoot?.Size.Width ?? ActualWidth;
        var windowHeight = XamlRoot?.Size.Height ?? ActualHeight;
        var contentWidth = ActualWidth > 0 ? ActualWidth : windowWidth;
        _twoPane = AdaptiveLayout.IsTwoPane(windowWidth);
        var compact = AdaptiveLayout.IsCompact(windowWidth);
        BulkActionLabel.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        SectionSummaryText.Visibility = windowWidth < 860 ? Visibility.Collapsed : Visibility.Visible;
        // Grow with the DingLater font scale so the placeholder is never clipped at 大 / 特大.
        var fontScale = Math.Max(1d, (double)Application.Current.Resources["DingBodyFontSize"] / 16d);
        SearchBox.Width = compact ? 200 : Math.Round((windowWidth < 1180 ? 260 : 320) * fontScale);
        SearchBox.PlaceholderText = compact ? "搜索消息" : "搜索对象、发送者或正文";
        RootGrid.Padding = AdaptiveLayout.PagePadding(windowWidth, windowHeight);
        RootGrid.RowSpacing = compact ? 10 : 14;
        DetailHeader.Padding = compact ? new Thickness(12, 8, 12, 8) : new Thickness(20, 14, 20, 14);
        ActionBar.Padding = compact ? new Thickness(10) : new Thickness(16, 12, 16, 12);
        ConfigureSnoozeActionLayout(compact);
        foreach (var control in new Control[] { QuickSnoozeButton, TomorrowButton, ExactTimeButton, HandledButton })
        {
            control.HorizontalAlignment = compact ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
        }

        if (_viewModel.IsEmpty)
        {
            _showingDetail = false;
            ConversationPane.Visibility = Visibility.Collapsed;
            DetailPane.Visibility = Visibility.Collapsed;
            UnifiedEmptyState.Visibility = Visibility.Visible;
            return;
        }

        UnifiedEmptyState.Visibility = Visibility.Collapsed;
        if (_twoPane)
        {
            var conversationWidth = contentWidth >= 1500 ? 400 : contentWidth >= 1120 ? 360 : 340;
            PaneGrid.ColumnSpacing = 12;
            ConversationColumn.Width = new GridLength(conversationWidth);
            DetailColumn.Width = new GridLength(1, GridUnitType.Star);
            ConversationPane.Visibility = Visibility.Visible;
            DetailPane.Visibility = Visibility.Visible;
            BackButton.Visibility = Visibility.Collapsed;
            Grid.SetColumn(DetailPane, 1);
            Grid.SetColumnSpan(DetailPane, 1);
            return;
        }

        PaneGrid.ColumnSpacing = 0;
        ConversationColumn.Width = new GridLength(1, GridUnitType.Star);
        DetailColumn.Width = new GridLength(0);
        ConversationPane.Visibility = _showingDetail ? Visibility.Collapsed : Visibility.Visible;
        DetailPane.Visibility = _showingDetail ? Visibility.Visible : Visibility.Collapsed;
        BackButton.Visibility = _showingDetail ? Visibility.Visible : Visibility.Collapsed;
        Grid.SetColumn(DetailPane, 0);
        Grid.SetColumnSpan(DetailPane, 2);
    }

    private void ConfigureSnoozeActionLayout(bool compact)
    {
        if (_compactActionsLayout == compact && SnoozeActions.ColumnDefinitions.Count > 0)
        {
            return;
        }

        _compactActionsLayout = compact;
        SnoozeActions.ColumnDefinitions.Clear();
        SnoozeActions.RowDefinitions.Clear();
        if (compact)
        {
            SnoozeActions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            SnoozeActions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            SnoozeActions.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            SnoozeActions.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Place(QuickSnoozeButton, 0, 0);
            Place(TomorrowButton, 0, 1);
            Place(ExactTimeButton, 1, 0);
            Place(HandledButton, 1, 1);
            return;
        }

        // Snooze choices on the left, the primary "已处理" action pinned to the right edge.
        foreach (var width in new[] { GridLength.Auto, GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto })
        {
            SnoozeActions.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
        }

        SnoozeActions.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Place(QuickSnoozeButton, 0, 0);
        Place(TomorrowButton, 0, 1);
        Place(ExactTimeButton, 0, 2);
        Place(HandledButton, 0, 4);

        static void Place(FrameworkElement element, int row, int column)
        {
            Grid.SetRow(element, row);
            Grid.SetColumn(element, column);
        }
    }

    private void RefreshEmptyState()
    {
        var searching = !string.IsNullOrWhiteSpace(_viewModel.SearchText);
        ClearSearchButton.Visibility = searching ? Visibility.Visible : Visibility.Collapsed;
        (EmptyIcon.Glyph, EmptyTitle.Text, EmptyDescription.Text) = searching
            ? ("", "没有匹配的消息", "试试对象名、发送者或正文中的其他关键词。")
            : _viewModel.Section switch
            {
                InboxSection.Snoozed => ("", "没有稍后提醒", "设置过稍后提醒的消息会出现在这里，到时间后回到待处理。"),
                InboxSection.Handled => ("", "还没有已处理消息", "处理过的消息会保留到清理时间，可随时移回待处理。"),
                _ => ("", "待处理已清空", "新消息会按会话归拢到这里。")
            };
        UnifiedEmptyState.Visibility = _viewModel.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RefreshBulkAction()
    {
        var handled = _viewModel.Section == InboxSection.Handled;
        var count = SectionCount(_viewModel.Section);
        VisualStateManager.GoToState(this, handled ? "HandledSection" : "ActiveSection", false);
        BulkActionButton.IsEnabled = count > 0 && !_viewModel.IsBusy;
        BulkActionLabel.Text = handled ? "全部删除" : "全部标为已处理";
        var action = handled ? "删除" : "标为已处理";
        AutomationProperties.SetName(BulkActionButton, $"将全部 {count} 条消息{action}");
        ToolTipService.SetToolTip(
            BulkActionButton,
            count > 0 ? $"将本分类中的全部 {count} 条消息{action}" : $"没有可{action}的消息");
    }

    private int SectionCount(InboxSection section) => section switch
    {
        InboxSection.Inbox => _viewModel.InboxCount,
        InboxSection.Snoozed => _viewModel.SnoozedCount,
        InboxSection.Handled => _viewModel.HandledCount,
        _ => 0
    };

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
        AutomationProperties.SetHelpText(control, available ? string.Empty : unavailableReason);
    }

    private void RefreshCustomMinutesAvailability()
    {
        var message = _viewModel.SelectedMessage;
        var validNumber = !double.IsNaN(QuickMinutesBox.Value);
        var minutes = validNumber ? Math.Clamp((int)Math.Round(QuickMinutesBox.Value), 1, 1440) : 0;
        var available = message is not null
                        && validNumber
                        && DateTimeOffset.Now.AddMinutes(minutes) < message.ExpiresAt;
        SetAvailability(
            UseQuickMinutesButton,
            available,
            message is null ? "请先选择一条消息。" : "这个时长晚于消息清理时间。");
    }

    private static FrameworkElement InfoLine(string label, string value)
    {
        var panel = new StackPanel { Spacing = 2 };
        panel.Children.Add(new TextBlock
        {
            Text = label,
            Style = (Style)Application.Current.Resources["DingCaptionTextStyle"]
        });
        panel.Children.Add(new TextBlock
        {
            Text = value,
            Style = (Style)Application.Current.Resources["DingBodyTextStyle"],
            IsTextSelectionEnabled = true
        });
        return panel;
    }

    private Task ShowSnoozeErrorAsync(string message) =>
        AppDialog.AlertAsync(XamlRoot, "无法设置稍后时间", message);
}
