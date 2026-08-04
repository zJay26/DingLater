using System.Security.Cryptography;
#if DEBUG
using DingLater.App.Capture;
#endif
using DingLater.App.Services;
using DingLater.App.ViewModels;
using DingLater.App.Views;
using DingLater.Core.Capture.DingTalkDatabase;
using DingLater.Core.Models;
using DingLater.Core.Security;
using DingLater.Core.Services;
using DingLater.Core.Storage;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace DingLater.App;

public partial class App : Application
{
    private SingleInstanceCoordinator? _singleInstance;
    private InboxService? _inbox;
    private MainViewModel? _viewModel;
    private MainWindow? _mainWindow;
    private ShellPage? _shellPage;
    private TrayIconService? _tray;
    private DispatcherQueueTimer? _maintenanceTimer;
    private DispatcherQueueTimer? _packageSmokeTimer;
    private WindowsReminderScheduler? _reminderScheduler;
    private string? _pendingActivation;
    private bool _exiting;
    private bool _onboarding;

    public App()
    {
        InitializeComponent();
        UnhandledException += App_UnhandledException;
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        var commandLine = Environment.GetCommandLineArgs().Skip(1).ToArray();
        _mainWindow = new MainWindow();
        _mainWindow.CloseRequested += MainWindow_CloseRequested;
        _mainWindow.Activate();

        if (commandLine.Any(arg => string.Equals(arg, "--package-smoke-test", StringComparison.OrdinalIgnoreCase)))
        {
            StartPackageSmokeTest();
            return;
        }

        var startupLaunch = commandLine.Any(arg => string.Equals(arg, "--startup", StringComparison.OrdinalIgnoreCase));
        var activation = commandLine.FirstOrDefault(arg => arg.StartsWith("dinglater://", StringComparison.OrdinalIgnoreCase)) ?? string.Empty;
        var demoMode = IsDemoBuild();
        _singleInstance = new SingleInstanceCoordinator(demoMode ? "Demo" : null);
        if (!_singleInstance.IsPrimary)
        {
            await _singleInstance.ForwardAsync(string.IsNullOrWhiteSpace(activation) ? "show" : activation);
            _mainWindow.ForceClose();
            Exit();
            return;
        }

        _singleInstance.ActivationReceived += SingleInstance_ActivationReceived;
        _singleInstance.StartListening();
        _pendingActivation = activation;

        try
        {
            var dataRoot = demoMode
                ? Path.Combine(Path.GetTempPath(), "DingLater-Debug")
                : AppDataLocator.Resolve();
            if (!demoMode)
            {
                await LegacyMsixDataMigrator.MigrateIfNeededAsync(dataRoot);
            }

            var keyProvider = new FileMasterKeyProvider(
                Path.Combine(dataRoot, "keys", "master.key"),
                new DpapiSecretProtector());
            var masterKey = keyProvider.GetOrCreate();
            var crypto = new MessageCrypto(masterKey);
            CryptographicOperations.ZeroMemory(masterKey);
            var store = new SqliteMessageStore(Path.Combine(dataRoot, "dinglater.db"), crypto);
            _reminderScheduler = new WindowsReminderScheduler();
#if DEBUG
            var syntheticSource = new SyntheticCaptureSource();
            var databaseSource = new DingTalkDatabaseSource(store);
            _inbox = new InboxService(store, _reminderScheduler, demoMode ? [syntheticSource] : [databaseSource]);
#else
            var databaseSource = new DingTalkDatabaseSource(store);
            _inbox = new InboxService(store, _reminderScheduler, [databaseSource]);
#endif
            await _inbox.InitializeAsync();
            ApplyTypography(_inbox.Settings.UiFontScale);

            var startupService = new StartupService();
            if (demoMode)
            {
                await _inbox.DeleteAllAsync();
                await _inbox.SaveSettingsAsync(_inbox.Settings with
                {
                    OnboardingCompleted = true,
                    CaptureConsentVersion = 1
                }, applyRetention: false);
            }
            else if (!_inbox.Settings.OnboardingCompleted || _inbox.Settings.CaptureConsentVersion < 1)
            {
                _onboarding = true;
                var onboarding = new OnboardingPage(startupService, _inbox);
                _mainWindow.SetContent(onboarding);
                if (!await onboarding.Completion)
                {
                    await ExitApplicationAsync();
                    return;
                }

                _onboarding = false;
            }

            _viewModel = new MainViewModel(_inbox, startupService, _mainWindow.Dispatch);
            _viewModel.FontScaleChanged += ViewModel_FontScaleChanged;
            await _viewModel.InitializeAsync();
            RebuildShell();

            _tray = new TrayIconService();
            _tray.OpenRequested += (_, _) => _mainWindow.Dispatch(_mainWindow.ShowFromBackground);
            _tray.PauseToggleRequested += async (_, _) =>
            {
                await _viewModel.ToggleCaptureAsync();
                _mainWindow.Dispatch(() => _tray.SetPaused(_inbox.Settings.CapturePaused));
            };
            _tray.ExitRequested += async (_, _) => await ExitApplicationAsync();
            _tray.MessageOpenRequested += (_, id) => _mainWindow.Dispatch(() => OpenMessage(id));
            _tray.SetPaused(_inbox.Settings.CapturePaused);
            _reminderScheduler.ReminderDue += ReminderScheduler_ReminderDue;
            await _inbox.RestoreReminderScheduleAsync();

            await _inbox.StartCaptureAsync();
#if DEBUG
            if (demoMode)
            {
                await syntheticSource.SeedAsync();
                await Task.Delay(120);
                await _viewModel.RefreshAsync();
            }
#endif
            _maintenanceTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
            _maintenanceTimer.Interval = TimeSpan.FromMinutes(1);
            _maintenanceTimer.Tick += async (_, _) => await _inbox.RunMaintenanceAsync();
            _maintenanceTimer.Start();

            if (startupLaunch)
            {
                _mainWindow.HideToTray();
            }

            ProcessActivation(_pendingActivation);
        }
        catch (Exception exception)
        {
            await _mainWindow.ShowErrorAsync(
                "DingLater 启动失败",
                $"{exception.GetType().Name}：{exception.Message}");
        }
    }

    private void RebuildShell()
    {
        if (_viewModel is null || _mainWindow is null)
        {
            return;
        }

        _shellPage = new ShellPage(_viewModel);
        _mainWindow.SetContent(_shellPage);
    }

    private void ViewModel_FontScaleChanged(object? sender, UiFontScale scale)
    {
        ApplyTypography(scale);
        RebuildShell();
    }

    private static void ApplyTypography(UiFontScale scale)
    {
        scale = Enum.IsDefined(scale) ? scale : UiFontScale.Standard;
        var dictionaries = Current.Resources.MergedDictionaries;
        var existing = dictionaries.FirstOrDefault(dictionary =>
            dictionary.Source?.OriginalString.Contains("/Styles/Typography.", StringComparison.OrdinalIgnoreCase) == true);
        var replacement = new ResourceDictionary
        {
            Source = new Uri($"ms-appx:///Styles/Typography.{scale}.xaml")
        };
        if (existing is null)
        {
            dictionaries.Add(replacement);
            return;
        }

        var index = dictionaries.IndexOf(existing);
        dictionaries[index] = replacement;
    }

    private void MainWindow_CloseRequested(object? sender, EventArgs e)
    {
        if (_tray is not null && !_onboarding)
        {
            _mainWindow?.HideToTray();
            return;
        }

        _ = ExitApplicationAsync();
    }

    private void SingleInstance_ActivationReceived(object? sender, string activation) =>
        _mainWindow?.Dispatch(() => ProcessActivation(activation));

    private void ReminderScheduler_ReminderDue(object? sender, ReminderDueEventArgs args) =>
        _mainWindow?.Dispatch(() => _tray?.ShowReminder(args.Message, args.IncludePreview));

    private void ProcessActivation(string? activation)
    {
        if (_mainWindow is null || _shellPage is null)
        {
            _pendingActivation = activation;
            return;
        }

        if (DingLaterActivation.TryParseMessageId(activation, out var id))
        {
            OpenMessage(id);
        }
        else if (!string.IsNullOrWhiteSpace(activation))
        {
            _mainWindow.ShowFromBackground();
        }
    }

    private void OpenMessage(Guid id)
    {
        _mainWindow?.ShowFromBackground();
        _shellPage?.OpenMessage(id);
    }

    private async Task ExitApplicationAsync()
    {
        if (_exiting)
        {
            return;
        }

        _exiting = true;
        _maintenanceTimer?.Stop();
        if (_reminderScheduler is not null)
        {
            _reminderScheduler.ReminderDue -= ReminderScheduler_ReminderDue;
        }

        _tray?.Dispose();
        if (_viewModel is not null)
        {
            _viewModel.FontScaleChanged -= ViewModel_FontScaleChanged;
            _viewModel.Dispose();
        }

        if (_inbox is not null)
        {
            await _inbox.DisposeAsync();
        }

        _reminderScheduler?.Dispose();
        _singleInstance?.Dispose();
        _mainWindow?.ForceClose();
        Exit();
    }

    private void StartPackageSmokeTest()
    {
        if (_mainWindow is null)
        {
            Exit();
            return;
        }

        _mainWindow.SetContent(new Microsoft.UI.Xaml.Controls.TextBlock
        {
            Text = "正在验证 DingLater 便携包",
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        });
        _mainWindow.ShowFromBackground();
        _packageSmokeTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _packageSmokeTimer.Interval = TimeSpan.FromSeconds(2);
        _packageSmokeTimer.IsRepeating = false;
        _packageSmokeTimer.Tick += (_, _) =>
        {
            _packageSmokeTimer?.Stop();
            _mainWindow.ForceClose();
            Exit();
        };
        _packageSmokeTimer.Start();
    }

    private async void App_UnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        if (_mainWindow is null)
        {
            e.Handled = false;
            return;
        }

        e.Handled = true;
        await _mainWindow.ShowErrorAsync("DingLater 遇到错误", e.Exception.Message);
    }

    private static bool IsDemoBuild()
    {
#if DEBUG
        return Environment.GetCommandLineArgs().Any(arg =>
                   string.Equals(arg, "--demo", StringComparison.OrdinalIgnoreCase))
               || string.Equals(
                   Environment.GetEnvironmentVariable("DINGLATER_DEMO"),
                   "1",
                   StringComparison.Ordinal);
#else
        return false;
#endif
    }
}
