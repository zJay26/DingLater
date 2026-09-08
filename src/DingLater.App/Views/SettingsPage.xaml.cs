using System.ComponentModel;
using DingLater.App.ViewModels;
using DingLater.Core.Models;
using DingLater.Core.Updates;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace DingLater.App.Views;

public sealed partial class SettingsPage : Page
{
    private readonly MainViewModel _viewModel;
    private readonly UpdateViewModel _updates;
    private bool _updating;

    public SettingsPage(MainViewModel viewModel, UpdateViewModel updates)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _updates = updates;
        DataContext = viewModel;
        FontScaleComboBox.ItemsSource = new[]
        {
            new FontScaleOption(UiFontScale.Small, "小"),
            new FontScaleOption(UiFontScale.Standard, "标准"),
            new FontScaleOption(UiFontScale.Large, "大"),
            new FontScaleOption(UiFontScale.ExtraLarge, "特大")
        };
        Loaded += (_, _) =>
        {
            _updates.PropertyChanged += Updates_PropertyChanged;
            UpdateResponsiveWidth();
            RefreshControls();
        };
        Unloaded += (_, _) => _updates.PropertyChanged -= Updates_PropertyChanged;
        SizeChanged += (_, _) => UpdateResponsiveWidth();
    }

    internal void RefreshControls()
    {
        _updating = true;
        var settings = _viewModel.Settings;
        FontScaleComboBox.SelectedItem = ((IEnumerable<FontScaleOption>)FontScaleComboBox.ItemsSource)
            .First(option => option.Value == settings.UiFontScale);
        GroupMessagesToggle.IsOn = settings.GroupCaptureMode == GroupCaptureMode.AllMessages;
        ReminderPreviewToggle.IsOn = settings.ShowReminderPreview;
        StartupToggle.IsEnabled = _viewModel.StartupAvailable;
        StartupToggle.IsOn = _viewModel.StartWithWindows;
        ToolTipService.SetToolTip(
            StartupToggle,
            _viewModel.StartupAvailable ? null : "当前环境无法创建 Windows 登录启动项。");
        RetentionNumberBox.Value = settings.RetentionDays;
        AutoUpdateToggle.IsOn = settings.AutomaticallyCheckUpdates;
        RefreshUpdates();
        _updating = false;
    }

    internal void ShowUpdates()
    {
        UpdatesExpander.IsExpanded = true;
        SettingsScroller.ChangeView(null, 0, null);
    }

    private void Updates_PropertyChanged(object? sender, PropertyChangedEventArgs e) => RefreshUpdates();

    private void RefreshUpdates()
    {
        CurrentVersionText.Text = _updates.CurrentVersionText;
        LastUpdateCheckText.Text = _updates.LastCheckText;
        DownloadDirectoryText.Text = _updates.DownloadDirectory;
        UpdateStatusText.Text = _updates.Status;
        CheckUpdateButton.IsEnabled = _updates.CanCheck;
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
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "现在重启并更新？",
            Content = "DingLater 将暂时退出，备份旧程序并应用新版，然后重新打开。现有消息、设置和登录启动项会保留；你也可以稍后再更新。",
            PrimaryButtonText = "重启并更新",
            CloseButtonText = "稍后",
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
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

        var navigationReserve = windowWidth >= 1008 ? 190d : 48d;
        SettingsContent.Width = Math.Min(960d, Math.Max(320d, windowWidth - navigationReserve));
    }

    private async void FontScaleComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updating || FontScaleComboBox.SelectedItem is not FontScaleOption option)
        {
            return;
        }

        if (!await _viewModel.SaveSettingAsync(settings => settings with { UiFontScale = option.Value }))
        {
            RefreshControls();
        }
    }

    private async void GroupMessagesToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_updating)
        {
            return;
        }

        var mode = GroupMessagesToggle.IsOn ? GroupCaptureMode.AllMessages : GroupCaptureMode.MentionsOnly;
        if (!await _viewModel.SaveSettingAsync(settings => settings with { GroupCaptureMode = mode }))
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

                if (count > 0)
                {
                    var dialog = new ContentDialog
                    {
                        XamlRoot = XamlRoot,
                        Title = "缩短消息留存期？",
                        Content = $"将立即删除 {count} 条已到期消息，并取消对应提醒。",
                        PrimaryButtonText = "继续",
                        CloseButtonText = "取消",
                        DefaultButton = ContentDialogButton.Close
                    };
                    if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                    {
                        RetentionNumberBox.Value = current;
                        return;
                    }
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
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "删除全部本地消息？",
            Content = "DingLater 将永久删除全部消息正文、待处理状态和稍后提醒。钉钉中的消息不会受影响；此操作无法撤销。",
            PrimaryButtonText = "永久删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
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
        CopyInfoBar.IsOpen = true;
    }

    private sealed record FontScaleOption(UiFontScale Value, string Label)
    {
        public override string ToString() => Label;
    }
}
