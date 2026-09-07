using System.Security.Cryptography;
using System.ComponentModel;
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
    private MessageCrypto? _messageCrypto;
    private string? _pendingActivation;
    private bool _exiting;
    private bool _packageSmokeTest;
    private string? _smokeDataRoot;

    public App()
    {
        InitializeComponent();
        UnhandledException += App_UnhandledException;
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        var commandLine = Environment.GetCommandLineArgs().Skip(1).ToArray();
        _mainWindow = new MainWindow();
        _mainWindow.HideRequested += MainWindow_HideRequested;
        _mainWindow.Activate();

        if (commandLine.Any(arg => string.Equals(arg, "--package-smoke-test", StringComparison.OrdinalIgnoreCase)))
        {
            _packageSmokeTest = true;
            await StartPackageSmokeTestAsync();
            return;
        }

        var startupLaunch = commandLine.Any(arg => string.Equals(arg, "--startup", StringComparison.OrdinalIgnoreCase));
        var activation = commandLine.FirstOrDefault(arg => arg.StartsWith("dinglater://", StringComparison.OrdinalIgnoreCase)) ?? string.Empty;
        var demoMode = IsDemoBuild();
        _singleInstance = new SingleInstanceCoordinator(demoMode ? "Demo" : null);
        if (!_singleInstance.IsPrimary)
        {
            if (!await _singleInstance.ForwardAsync(string.IsNullOrWhiteSpace(activation) ? "show" : activation))
            {
                await _mainWindow.ShowErrorAsync(
                    "无法打开 DingLater",
                    "已有实例正在运行，但本次激活未能转交。请从系统托盘打开 DingLater，或退出后重试。");
            }

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
            MessageCrypto messageCrypto;
            try
            {
                messageCrypto = new MessageCrypto(masterKey);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(masterKey);
            }

            _messageCrypto = messageCrypto;
            var store = new SqliteMessageStore(Path.Combine(dataRoot, "dinglater.db"), messageCrypto);
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
                var onboarding = new OnboardingPage(startupService, _inbox);
                _mainWindow.SetContent(onboarding);
                if (!await onboarding.Completion)
                {
                    await ExitApplicationAsync();
                    return;
                }

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
            _tray.ExitRequested += Tray_ExitRequested;
            _tray.MessageOpenRequested += (_, id) => _mainWindow.Dispatch(() => OpenMessage(id));
            _tray.SetPaused(_inbox.Settings.CapturePaused);
            _viewModel.PropertyChanged += ViewModel_PropertyChanged;
            UpdateTrayStatus();
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
            try
            {
                await _mainWindow.ShowErrorAsync(
                    "DingLater 启动失败",
                    $"{exception.GetType().Name}：{exception.Message}");
            }
            finally
            {
                await ExitApplicationAsync();
            }
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

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(MainViewModel.InboxCount)
            or nameof(MainViewModel.LatestInboxMessage)
            or nameof(MainViewModel.Settings))
        {
            _mainWindow?.Dispatch(UpdateTrayStatus);
        }
    }

    private void UpdateTrayStatus()
    {
        if (_tray is null || _viewModel is null)
        {
            return;
        }

        _tray.SetPendingMessages(
            _viewModel.InboxCount,
            _viewModel.LatestInboxMessage,
            _viewModel.Settings.ShowReminderPreview);
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

    private void MainWindow_HideRequested(object? sender, EventArgs e)
        => _mainWindow?.HideToTray();

    private void Tray_ExitRequested(object? sender, EventArgs e)
        => _ = ExitApplicationAsync();

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
        try
        {
            _maintenanceTimer?.Stop();
            _packageSmokeTimer?.Stop();
            if (_reminderScheduler is not null)
            {
                _reminderScheduler.ReminderDue -= ReminderScheduler_ReminderDue;
            }

            try
            {
                var tray = _tray;
                _tray = null;
                tray?.Dispose();
            }
            catch
            {
                // Exit must continue even if the native tray window is already gone.
            }

            if (_viewModel is not null)
            {
                _viewModel.FontScaleChanged -= ViewModel_FontScaleChanged;
                _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
                _viewModel.Dispose();
            }

            if (_inbox is not null)
            {
                try
                {
                    await _inbox.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch
                {
                    // A stuck source must not leave a background-only process behind.
                }
            }

            _reminderScheduler?.Dispose();
            _messageCrypto?.Dispose();
            _messageCrypto = null;
            _singleInstance?.Dispose();
        }
        finally
        {
            _mainWindow?.ForceClose();
            Exit();
        }
    }

    private async Task StartPackageSmokeTestAsync()
    {
        if (_mainWindow is null)
        {
            Exit();
            return;
        }

        // Exercise the shipping shell and its templates using only isolated synthetic data.
        _smokeDataRoot = Path.Combine(Path.GetTempPath(), "DingLater-PackageSmoke", Guid.NewGuid().ToString("N"));
        var key = RandomNumberGenerator.GetBytes(32);
        try
        {
            _messageCrypto = new MessageCrypto(key);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }

        var store = new SqliteMessageStore(Path.Combine(_smokeDataRoot, "smoke.db"), _messageCrypto);
        _reminderScheduler = new WindowsReminderScheduler();
        _inbox = new InboxService(store, _reminderScheduler, []);
        await _inbox.InitializeAsync();
        var now = DateTimeOffset.Now;
        foreach (var state in new[] { InboxState.Inbox, InboxState.Snoozed, InboxState.Handled })
        {
            var result = await store.AddAsync(new CapturedMessage(
                CaptureSourceKind.Synthetic, now, "示例项目群", "示例联系人",
                "这是一条合成测试消息，用于验证便携版界面、搜索和提醒显示。", MessageKind.Normal, 1, "package-smoke",
                SourceIdentity: state.ToString(), ConversationScope: ConversationScope.Group), 7);
            await store.UpdateStateAsync(result.Message.Id, state, state == InboxState.Snoozed ? now.AddMinutes(30) : null, now);
        }

        _viewModel = new MainViewModel(_inbox, new StartupService(), action => _mainWindow.Dispatch(action));
        await _viewModel.RefreshAsync();
        RebuildShell();
        _tray = new TrayIconService();
        _tray.SetPendingMessages(_viewModel.InboxCount, latestMessage: null, includePreview: false);
        _mainWindow.ShowFromBackground();
        _packageSmokeTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _packageSmokeTimer.Interval = Environment.GetCommandLineArgs().Contains("--interactive-smoke-test", StringComparer.OrdinalIgnoreCase)
            ? TimeSpan.FromMinutes(10) : TimeSpan.FromSeconds(2);
        _packageSmokeTimer.IsRepeating = false;
        _packageSmokeTimer.Tick += async (_, _) =>
        {
            _packageSmokeTimer?.Stop();
            await ExitApplicationAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (_smokeDataRoot is not null && Directory.Exists(_smokeDataRoot))
            {
                Directory.Delete(_smokeDataRoot, recursive: true);
            }
        };
        _packageSmokeTimer.Start();
    }

    private async void App_UnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        if (_packageSmokeTest)
        {
            e.Handled = true;
            Environment.ExitCode = 1;
            await ExitApplicationAsync();
            return;
        }

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
