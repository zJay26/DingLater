using System.ComponentModel;
using DingLater.App.ViewModels;
using DingLater.Core.Models;
using DingLater.Core.Updates;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using DispatcherQueuePriority = Microsoft.UI.Dispatching.DispatcherQueuePriority;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace DingLater.App.Views;

public sealed partial class SettingsPage : Page
{
    private static readonly IReadOnlyList<Option<UiFontScale>> FontScaleOptions =
    [
        new(UiFontScale.Small, "小"),
        new(UiFontScale.Standard, "标准"),
        new(UiFontScale.Large, "大"),
        new(UiFontScale.ExtraLarge, "特大")
    ];

    private static readonly IReadOnlyList<Option<GroupCaptureMode>> GroupModeOptions =
    [
        new(GroupCaptureMode.MentionsOnly, "仅 @我"),
        new(GroupCaptureMode.AllMessages, "全部群消息")
    ];

    private readonly MainViewModel _viewModel;
    private readonly UpdateViewModel _updates;
    private readonly DispatcherQueueTimer _copyFeedbackTimer;
    private bool _updating;

    public SettingsPage(MainViewModel viewModel, UpdateViewModel updates)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _updates = updates;
        DataContext = viewModel;
        FontScaleComboBox.ItemsSource = FontScaleOptions;
        GroupCaptureModeComboBox.ItemsSource = GroupModeOptions;
        _copyFeedbackTimer = DispatcherQueue.CreateTimer();
        _copyFeedbackTimer.Interval = TimeSpan.FromSeconds(2);
        _copyFeedbackTimer.IsRepeating = false;
        _copyFeedbackTimer.Tick += (_, _) => ResetCopyFeedback();
        Loaded += (_, _) =>
        {
            _updates.PropertyChanged += Updates_PropertyChanged;
            _viewModel.PropertyChanged += MainViewModel_PropertyChanged;
            UpdateResponsiveWidth();
            RefreshControls();
        };
        Unloaded += (_, _) =>
        {
            _updates.PropertyChanged -= Updates_PropertyChanged;
            _viewModel.PropertyChanged -= MainViewModel_PropertyChanged;
            _copyFeedbackTimer.Stop();
            ResetCopyFeedback();
        };
        SizeChanged += (_, _) => UpdateResponsiveWidth();
    }

    internal void RefreshControls()
    {
        _updating = true;
        try
        {
            var settings = _viewModel.Settings;
            FontScaleComboBox.SelectedItem = FontScaleOptions.First(option => option.Value == settings.UiFontScale);
            GroupCaptureModeComboBox.SelectedItem = GroupModeOptions.FirstOrDefault(option => option.Value == settings.GroupCaptureMode)
                                                    ?? GroupModeOptions[0];
            ReminderPreviewToggle.IsOn = settings.ShowReminderPreview;
            StartupToggle.IsEnabled = _viewModel.StartupAvailable;
            StartupToggle.IsOn = _viewModel.StartWithWindows;
            ToolTipService.SetToolTip(
                StartupToggle,
                _viewModel.StartupAvailable ? null : "当前环境无法创建 Windows 登录启动项。");
            RetentionNumberBox.Value = settings.RetentionDays;
            AutoUpdateToggle.IsOn = settings.AutomaticallyCheckUpdates;
            AlwaysOnTopToggle.IsOn = settings.AlwaysOnTopEnabled;
            RestoreTopmostToggle.IsOn = settings.RestoreTopmostOnExit;
            RefreshTopmostStatus();
            RefreshUpdates();
        }
        finally
        {
            _updating = false;
        }
    }

    internal void ShowUpdates()
    {
        // The update card sits further down and the page may have just been inserted: wait for layout first.
        if (IsLoaded)
        {
            BringUpdatesIntoView();
            return;
        }

        void OnLoaded(object sender, RoutedEventArgs args)
        {
            Loaded -= OnLoaded;
            BringUpdatesIntoView();
        }

        Loaded += OnLoaded;
    }

    private void BringUpdatesIntoView() =>
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => UpdateCard.StartBringIntoView(new BringIntoViewOptions
        {
            VerticalAlignmentRatio = 0.1,
            AnimationDesired = true
        }));

    private void Updates_PropertyChanged(object? sender, PropertyChangedEventArgs e) => RefreshUpdates();

    private void MainViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.AlwaysOnTopStatus) or nameof(MainViewModel.AlwaysOnTopHasError))
        {
            RefreshTopmostStatus();
        }
    }

    private void RefreshTopmostStatus()
    {
        var error = _viewModel.AlwaysOnTopHasError;
        TopmostStatusText.Text = _viewModel.AlwaysOnTopStatus;
        TopmostStatusText.Visibility = error ? Visibility.Collapsed : Visibility.Visible;
        TopmostInfoBar.Message = _viewModel.AlwaysOnTopStatus;
        TopmostInfoBar.Visibility = error ? Visibility.Visible : Visibility.Collapsed;
        TopmostInfoBar.IsOpen = error;
    }

    private async void AlwaysOnTopToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_updating) return;
        var enabled = AlwaysOnTopToggle.IsOn;
        if (!await _viewModel.SaveSettingAsync(settings => settings with { AlwaysOnTopEnabled = enabled })) RefreshControls();
    }

    private async void RestoreTopmostToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_updating) return;
        var restore = RestoreTopmostToggle.IsOn;
        if (!await _viewModel.SaveSettingAsync(settings => settings with { RestoreTopmostOnExit = restore })) RefreshControls();
    }

    private void RefreshUpdates()
    {
        CurrentVersionText.Text = $"DingLater {_updates.CurrentVersion}";
        LastUpdateCheckText.Text = _updates.LastCheckText;
        DownloadDirectoryText.Text = _updates.DownloadDirectory;
        UpdateStatusText.Text = _updates.Status;
        CheckUpdateButton.IsEnabled = _updates.CanCheck;
        CheckUpdateButton.Visibility = _updates.CanDownload || _updates.CanInstall || _updates.IsDownloading
            ? Visibility.Collapsed
            : Visibility.Visible;
        DownloadUpdateButton.Visibility = _updates.CanDownload ? Visibility.Visible : Visibility.Collapsed;
        InstallUpdateButton.Visibility = _updates.CanInstall ? Visibility.Visible : Visibility.Collapsed;
        SkipUpdateButton.Visibility = _updates.CanSkip ? Visibility.Visible : Visibility.Collapsed;
        CancelUpdateButton.Visibility = _updates.IsDownloading ? Visibility.Visible : Visibility.Collapsed;
        UpdateProgressBar.Visibility = _updates.IsDownloading ? Visibility.Visible : Visibility.Collapsed;
        UpdateProgressBar.Value = _updates.Progress;
        ReleaseNotesLink.Visibility = _updates.AvailableRelease is null ? Visibility.Collapsed : Visibility.Visible;
        ReleaseNotesLink.NavigateUri = _updates.AvailableRelease?.ReleaseUri;
        ChooseDownloadDirectoryButton.IsEnabled = ResetDownloadDirectoryButton.IsEnabled = !_updates.IsBusy;
    }

    private async void AutoUpdateToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_updating)
        {
            return;
        }

        var enabled = AutoUpdateToggle.IsOn;
        if (!await _viewModel.SaveSettingAsync(settings => settings with { AutomaticallyCheckUpdates = enabled }))
        {
            RefreshControls();
        }

        _updates.SettingsChanged();
        if (enabled)
        {
            await _updates.CheckIfDueAsync();
        }
    }

    private async void ChooseDownloadDirectoryButton_Click(object sender, RoutedEventArgs e)
    {
        ChooseDownloadDirectoryButton.IsEnabled = false;
        try
        {
            var picker = new Microsoft.Windows.Storage.Pickers.FolderPicker(XamlRoot.ContentIslandEnvironment.AppWindowId);
            var folder = await picker.PickSingleFolderAsync();
            if (folder is not null)
            {
                PortableUpdateInstaller.ValidateDirectories(folder.Path, AppContext.BaseDirectory);
                await _viewModel.SaveSettingAsync(settings => settings with { UpdateDownloadDirectory = folder.Path });
                _updates.SettingsChanged();
            }
        }
        catch (Exception exception)
        {
            UpdateStatusText.Text = $"无法使用该下载目录：{exception.Message}";
        }
        finally
        {
            ChooseDownloadDirectoryButton.IsEnabled = !_updates.IsBusy;
        }
    }

    private async void ResetDownloadDirectoryButton_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.SaveSettingAsync(settings => settings with { UpdateDownloadDirectory = string.Empty });
        _updates.SettingsChanged();
    }

    private async void CheckUpdateButton_Click(object sender, RoutedEventArgs e) => await _updates.CheckAsync();
    private async void DownloadUpdateButton_Click(object sender, RoutedEventArgs e) => await _updates.DownloadAsync();
    private async void SkipUpdateButton_Click(object sender, RoutedEventArgs e) => await _updates.SkipAsync();
    private void CancelUpdateButton_Click(object sender, RoutedEventArgs e) => _updates.Cancel();

    private async void InstallUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        var confirmed = await AppDialog.ConfirmAsync(
            XamlRoot,
            "现在重启并更新？",
            "DingLater 将暂时退出，备份旧程序并应用新版，然后重新打开。现有消息、设置和登录启动项会保留；你也可以稍后再更新。",
            "重启并更新",
            "稍后");
        if (confirmed)
        {
            await _updates.InstallAsync();
        }
    }

    private void UpdateResponsiveWidth()
    {
        var windowWidth = XamlRoot?.Size.Width ?? ActualWidth;
        if (windowWidth <= 0)
        {
            return;
        }

        SettingsContent.Width = Math.Min(960d, Math.Max(320d, windowWidth));
        SettingsContent.Padding = AdaptiveLayout.PagePadding(windowWidth, XamlRoot?.Size.Height ?? ActualHeight);
    }

    private async void FontScaleComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updating || FontScaleComboBox.SelectedItem is not Option<UiFontScale> option)
        {
            return;
        }

        if (!await _viewModel.SaveSettingAsync(settings => settings with { UiFontScale = option.Value }))
        {
            RefreshControls();
        }
    }

    private async void GroupCaptureModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updating || GroupCaptureModeComboBox.SelectedItem is not Option<GroupCaptureMode> option)
        {
            return;
        }

        if (!await _viewModel.SaveSettingAsync(settings => settings with { GroupCaptureMode = option.Value }))
        {
            RefreshControls();
        }
    }

    private async void ReminderPreviewToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_updating)
        {
            return;
        }

        var value = ReminderPreviewToggle.IsOn;
        if (!await _viewModel.SaveSettingAsync(settings => settings with { ShowReminderPreview = value }))
        {
            RefreshControls();
        }
    }

    private async void StartupToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_updating)
        {
            return;
        }

        if (!await _viewModel.SetStartWithWindowsAsync(StartupToggle.IsOn))
        {
            RefreshControls();
        }
    }

    private async void RetentionNumberBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_updating || double.IsNaN(args.NewValue))
        {
            return;
        }

        var retentionDays = Math.Clamp((int)Math.Round(args.NewValue), 1, 365);
        var current = _viewModel.Settings.RetentionDays;
        if (retentionDays == current)
        {
            return;
        }

        _updating = true;
        try
        {
            if (retentionDays < current)
            {
                var count = await _viewModel.TryCountRetentionImpactAsync(retentionDays);
                if (count is null)
                {
                    RetentionNumberBox.Value = current;
                    return;
                }

                if (count > 0 && !await AppDialog.ConfirmAsync(
                        XamlRoot,
                        "缩短消息留存期？",
                        $"将立即删除 {count} 条已到期消息，并取消对应提醒。",
                        "继续"))
                {
                    RetentionNumberBox.Value = current;
                    return;
                }
            }

            var saved = await _viewModel.SaveSettingAsync(
                settings => settings with { RetentionDays = retentionDays },
                applyRetention: true);
            if (!saved)
            {
                RetentionNumberBox.Value = current;
            }
        }
        finally
        {
            _updating = false;
        }
    }

    private async void DeleteAllButton_Click(object sender, RoutedEventArgs e)
    {
        if (await AppDialog.ConfirmAsync(
                XamlRoot,
                "删除全部本地消息？",
                "DingLater 将永久删除全部消息正文、待处理状态和稍后提醒。钉钉中的消息不会受影响；此操作无法撤销。",
                "永久删除"))
        {
            await _viewModel.DeleteAllAsync();
        }
    }

    private void CopyDiagnosticsButton_Click(object sender, RoutedEventArgs e)
    {
        var package = new DataPackage();
        package.SetText(_viewModel.BuildDiagnostics());
        Clipboard.SetContent(package);
        Clipboard.Flush();
        CopyDiagnosticsIcon.Glyph = "";
        CopyDiagnosticsLabel.Text = "已复制";
        _copyFeedbackTimer.Stop();
        _copyFeedbackTimer.Start();
    }

    private void ResetCopyFeedback()
    {
        CopyDiagnosticsIcon.Glyph = "";
        CopyDiagnosticsLabel.Text = "复制";
    }

    private sealed record Option<T>(T Value, string Label)
    {
        public override string ToString() => Label;
    }
}
