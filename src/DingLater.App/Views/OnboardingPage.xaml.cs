using DingLater.App.Services;
using DingLater.Core.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DingLater.App.Views;

public sealed partial class OnboardingPage : Page
{
    private readonly StartupService _startup;
    private readonly InboxService _inbox;
    private readonly TaskCompletionSource<bool> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public OnboardingPage(StartupService startup, InboxService inbox)
    {
        InitializeComponent();
        _startup = startup;
        _inbox = inbox;
    }

    internal Task<bool> Completion => _completion.Task;

    private async void ContinueButton_Click(object sender, RoutedEventArgs e)
    {
        if (ConsentCheckBox.IsChecked != true)
        {
            return;
        }

        ContinueButton.IsEnabled = false;
        StatusInfoBar.IsOpen = true;
        StatusInfoBar.Severity = InfoBarSeverity.Informational;
        StatusInfoBar.Message = "正在建立新消息起点…";
        var startupEnabled = false;
        try
        {
            if (StartupToggle.IsOn)
            {
                startupEnabled = await _startup.SetEnabledAsync(true);
            }

            await _inbox.SaveSettingsAsync(_inbox.Settings with
            {
                OnboardingCompleted = true,
                CaptureConsentVersion = 1,
                StartupChoiceMade = true,
                StartWithWindows = startupEnabled
            }, applyRetention: false);
            _completion.TrySetResult(true);
        }
        catch (Exception exception)
        {
            if (startupEnabled)
            {
                try
                {
                    await _startup.SetEnabledAsync(false);
                }
                catch
                {
                    // Keep the original save failure visible; Settings can repair startup state later.
                }
            }

            StatusInfoBar.Severity = InfoBarSeverity.Error;
            StatusInfoBar.Message = $"无法保存设置：{exception.Message}";
            ContinueButton.IsEnabled = true;
        }
    }

    private void ConsentCheckBox_Changed(object sender, RoutedEventArgs e) =>
        ContinueButton.IsEnabled = ConsentCheckBox.IsChecked == true;

    private void ExitButton_Click(object sender, RoutedEventArgs e) =>
        _completion.TrySetResult(false);
}
