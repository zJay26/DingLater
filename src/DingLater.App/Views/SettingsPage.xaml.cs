using DingLater.App.ViewModels;
using DingLater.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace DingLater.App.Views;

public sealed partial class SettingsPage : Page
{
    private readonly MainViewModel _viewModel;
    private bool _updating;

    public SettingsPage(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
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
            UpdateResponsiveWidth();
            RefreshControls();
        };
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
        _updating = false;
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
        if (retentionDays < current)
        {
            var count = await _viewModel.CountRetentionImpactAsync(retentionDays);
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
                    _updating = false;
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

        _updating = false;
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
