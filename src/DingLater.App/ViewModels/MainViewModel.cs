using System.Collections.ObjectModel;
using DingLater.App.Services;
using DingLater.Core.Models;
using DingLater.Core.Services;

namespace DingLater.App.ViewModels;

public enum InboxSection
{
    Inbox,
    Snoozed,
    Handled
}

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly InboxService _inbox;
    private readonly StartupService _startup;
    private readonly Action<Action> _dispatch;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly SemaphoreSlim _settingsGate = new(1, 1);
    private List<StoredMessage> _allMessages = [];
    private InboxSection _section;
    private string _searchText = string.Empty;
    private bool _isBusy;
    private bool _startupAvailable;
    private bool _startWithWindows;
    private string _statusText = "正在准备捕获…";
    private ConversationThreadViewModel? _selectedConversation;
    private MessageCardViewModel? _selectedMessage;
    private bool _isSettingsPage;

    public MainViewModel(InboxService inbox, StartupService startup, Action<Action>? dispatch = null)
    {
        _inbox = inbox;
        _startup = startup;
        _dispatch = dispatch ?? (action => action());
        Conversations = [];
        HealthSources = [];
        _inbox.InboxChanged += OnInboxChanged;
        _inbox.HealthChanged += OnHealthChanged;
    }

    public ObservableCollection<ConversationThreadViewModel> Conversations { get; }
    public ObservableCollection<CaptureHealthViewModel> HealthSources { get; }

    public event EventHandler<string>? ErrorOccurred;
    public event EventHandler<UiFontScale>? FontScaleChanged;

    public AppSettings Settings => _inbox.Settings;

    public InboxSection Section
    {
        get => _section;
        set
        {
            if (SetProperty(ref _section, value))
            {
                OnPropertyChanged(nameof(SectionTitle));
                ApplyFilter();
            }
        }
    }

    public string SectionTitle => Section switch
    {
        InboxSection.Snoozed => "稍后",
        InboxSection.Handled => "已处理",
        _ => "收件箱"
    };

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value ?? string.Empty))
            {
                ApplyFilter();
            }
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    public bool IsEmpty => Conversations.Count == 0;
    public int InboxCount => _allMessages.Count(item => item.State == InboxState.Inbox);
    public int SnoozedCount => _allMessages.Count(item => item.State == InboxState.Snoozed);
    public int HandledCount => _allMessages.Count(item => item.State == InboxState.Handled);
    public bool CapturePaused => Settings.CapturePaused;
    public string CaptureActionText => CapturePaused ? "继续捕获" : "暂停捕获";
    public int QuickSnoozeMinutes => Settings.QuickSnoozeMinutes;
    public string QuickSnoozeText => SnoozeTimeFormatter.FormatRelativeMinutes(QuickSnoozeMinutes);

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public bool StartupAvailable
    {
        get => _startupAvailable;
        private set => SetProperty(ref _startupAvailable, value);
    }

    public bool StartWithWindows
    {
        get => _startWithWindows;
        private set => SetProperty(ref _startWithWindows, value);
    }

    public bool IsSettingsPage
    {
        get => _isSettingsPage;
        set => SetProperty(ref _isSettingsPage, value);
    }

    public ConversationThreadViewModel? SelectedConversation
    {
        get => _selectedConversation;
        set
        {
            if (SetProperty(ref _selectedConversation, value))
            {
                SelectedMessage = value?.Messages.FirstOrDefault();
            }
        }
    }

    public MessageCardViewModel? SelectedMessage
    {
        get => _selectedMessage;
        set => SetProperty(ref _selectedMessage, value);
    }

    public async Task InitializeAsync()
    {
        var startupState = await _startup.GetStateAsync().ConfigureAwait(false);
        _dispatch(() =>
        {
            StartupAvailable = startupState.Available;
            StartWithWindows = startupState.Enabled;
        });
        await RefreshAsync().ConfigureAwait(false);
        _dispatch(() => UpdateHealth(_inbox.Health));
    }

    public async Task RefreshAsync()
    {
        await _refreshGate.WaitAsync().ConfigureAwait(false);
        try
        {
            _dispatch(() => IsBusy = true);
            var messages = (await _inbox.ListAsync().ConfigureAwait(false)).ToList();
            _dispatch(() =>
            {
                _allMessages = messages;
                ApplyFilter();
            });
        }
        catch (Exception exception)
        {
            _dispatch(() => ErrorOccurred?.Invoke(this, $"刷新失败：{exception.Message}"));
        }
        finally
        {
            _dispatch(() => IsBusy = false);
            _refreshGate.Release();
        }
    }

    public IReadOnlyList<string> GetSearchSuggestions(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        return _allMessages
            .SelectMany(message => new[]
            {
                ConversationPresentation.GetTitle([message]),
                message.Captured.Conversation,
                message.Captured.Sender
            })
            .Where(value => !string.IsNullOrWhiteSpace(value)
                            && value.Contains(query, StringComparison.CurrentCultureIgnoreCase))
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .Take(8)
            .ToList();
    }

    public async Task<string?> SnoozeAsync(MessageCardViewModel? item, DateTimeOffset dueAt)
    {
        if (item is null)
        {
            return "请先选择一条消息。";
        }

        if (!SnoozeTimeFormatter.TryValidate(dueAt, item.ExpiresAt, DateTimeOffset.Now, out var error))
        {
            return error;
        }

        try
        {
            await _inbox.SnoozeAsync(item.Id, dueAt).ConfigureAwait(false);
            await RefreshAsync().ConfigureAwait(false);
            return null;
        }
        catch (ArgumentOutOfRangeException exception)
        {
            return exception.Message;
        }
        catch (Exception exception)
        {
            return $"设置稍后失败：{exception.Message}";
        }
    }

    public async Task<bool> SnoozeRememberedMinutesAsync(MessageCardViewModel? item, int minutes)
    {
        minutes = Math.Clamp(minutes, 1, 1440);
        var error = await SnoozeAsync(item, DateTimeOffset.Now.AddMinutes(minutes)).ConfigureAwait(false);
        if (error is not null)
        {
            _dispatch(() => ErrorOccurred?.Invoke(this, error));
            return false;
        }

        var saved = await SaveSettingAsync(settings => settings with { QuickSnoozeMinutes = minutes }).ConfigureAwait(false);
        if (saved)
        {
            _dispatch(() =>
            {
                OnPropertyChanged(nameof(QuickSnoozeMinutes));
                OnPropertyChanged(nameof(QuickSnoozeText));
            });
        }

        return saved;
    }

    public async Task MarkHandledAsync(MessageCardViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        try
        {
            await _inbox.MarkHandledAsync(item.Id).ConfigureAwait(false);
            await RefreshAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _dispatch(() => ErrorOccurred?.Invoke(this, $"标记失败：{exception.Message}"));
        }
    }

    public async Task RestoreInboxAsync(MessageCardViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        try
        {
            await _inbox.RestoreInboxAsync(item.Id).ConfigureAwait(false);
            await RefreshAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _dispatch(() => ErrorOccurred?.Invoke(this, $"恢复失败：{exception.Message}"));
        }
    }

    public async Task<bool> ToggleCaptureAsync()
    {
        try
        {
            await _inbox.SetCapturePausedAsync(!CapturePaused).ConfigureAwait(false);
            _dispatch(() =>
            {
                OnPropertyChanged(nameof(CapturePaused));
                OnPropertyChanged(nameof(CaptureActionText));
                UpdateHealth(_inbox.Health);
            });
            return true;
        }
        catch (Exception exception)
        {
            _dispatch(() => ErrorOccurred?.Invoke(this, $"更改捕获状态失败：{exception.Message}"));
            return false;
        }
    }

    public async Task<int> CountRetentionImpactAsync(int retentionDays) =>
        await _inbox.CountRetentionImpactAsync(Math.Clamp(retentionDays, 1, 365)).ConfigureAwait(false);

    public async Task<bool> SaveSettingAsync(
        Func<AppSettings, AppSettings> update,
        bool applyRetention = false)
    {
        await _settingsGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var previous = _inbox.Settings;
            var next = update(previous).Normalize();
            await _inbox.SaveSettingsAsync(next, applyRetention).ConfigureAwait(false);
            _dispatch(() =>
            {
                if (previous.UiFontScale != next.UiFontScale)
                {
                    FontScaleChanged?.Invoke(this, next.UiFontScale);
                }

                OnPropertyChanged(nameof(Settings));
                OnPropertyChanged(nameof(QuickSnoozeMinutes));
                OnPropertyChanged(nameof(QuickSnoozeText));
            });
            return true;
        }
        catch (Exception exception)
        {
            _dispatch(() => ErrorOccurred?.Invoke(this, $"保存设置失败：{exception.Message}"));
            return false;
        }
        finally
        {
            _settingsGate.Release();
        }
    }

    public async Task<bool> SetStartWithWindowsAsync(bool enabled)
    {
        if (!StartupAvailable)
        {
            return false;
        }

        var previous = StartWithWindows;
        var actual = await _startup.SetEnabledAsync(enabled).ConfigureAwait(false);
        if (actual != enabled)
        {
            _dispatch(() => ErrorOccurred?.Invoke(this, "Windows 未能更改登录启动项。"));
            return false;
        }

        var saved = await SaveSettingAsync(settings => settings with
        {
            StartWithWindows = actual,
            StartupChoiceMade = true
        }).ConfigureAwait(false);
        if (!saved)
        {
            await _startup.SetEnabledAsync(previous).ConfigureAwait(false);
        }

        _dispatch(() => StartWithWindows = saved ? actual : previous);
        return saved;
    }

    public async Task DeleteAllAsync()
    {
        try
        {
            await _inbox.DeleteAllAsync().ConfigureAwait(false);
            await RefreshAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _dispatch(() => ErrorOccurred?.Invoke(this, $"删除失败：{exception.Message}"));
        }
    }

    public string BuildDiagnostics() => DiagnosticsService.Build(_inbox.Health);

    public void SelectFromActivation(Guid id)
    {
        var item = _allMessages.FirstOrDefault(message => message.Id == id);
        if (item is null)
        {
            return;
        }

        Section = item.State switch
        {
            InboxState.Snoozed => InboxSection.Snoozed,
            InboxState.Handled => InboxSection.Handled,
            _ => InboxSection.Inbox
        };

        var conversation = Conversations.FirstOrDefault(thread => thread.Messages.Any(message => message.Id == id));
        if (conversation is null)
        {
            return;
        }

        SelectedConversation = conversation;
        SelectedMessage = conversation.Messages.First(message => message.Id == id);
    }

    public void Dispose()
    {
        _inbox.InboxChanged -= OnInboxChanged;
        _inbox.HealthChanged -= OnHealthChanged;
        _refreshGate.Dispose();
        _settingsGate.Dispose();
    }

    private void ApplyFilter()
    {
        var previousKey = SelectedConversation?.Key;
        var previousMessageId = SelectedMessage?.Id;
        var previousIndex = SelectedConversation is null ? 0 : Conversations.IndexOf(SelectedConversation);
        var targetState = Section switch
        {
            InboxSection.Snoozed => InboxState.Snoozed,
            InboxSection.Handled => InboxState.Handled,
            _ => InboxState.Inbox
        };

        var threads = _allMessages
            .Where(message => message.State == targetState)
            .GroupBy(ConversationPresentation.GetKey, StringComparer.Ordinal)
            .Select(group => new ConversationThreadViewModel(group))
            .Where(thread => thread.Matches(SearchText))
            .OrderByDescending(thread => thread.LatestAt)
            .ToList();

        Conversations.Clear();
        foreach (var thread in threads)
        {
            Conversations.Add(thread);
        }

        var selected = previousKey is null
            ? Conversations.FirstOrDefault()
            : Conversations.FirstOrDefault(thread => thread.Key == previousKey)
              ?? Conversations.ElementAtOrDefault(Math.Clamp(previousIndex, 0, Math.Max(0, Conversations.Count - 1)));
        _selectedConversation = selected;
        OnPropertyChanged(nameof(SelectedConversation));
        _selectedMessage = previousMessageId is null
            ? selected?.Messages.FirstOrDefault()
            : selected?.Messages.FirstOrDefault(message => message.Id == previousMessageId)
              ?? selected?.Messages.FirstOrDefault();
        OnPropertyChanged(nameof(SelectedMessage));

        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(InboxCount));
        OnPropertyChanged(nameof(SnoozedCount));
        OnPropertyChanged(nameof(HandledCount));
    }

    private void UpdateHealth(IEnumerable<CaptureHealth> health)
    {
        HealthSources.Clear();
        var list = health.ToList();
        foreach (var item in list)
        {
            HealthSources.Add(new CaptureHealthViewModel(item));
        }

        StatusText = CapturePaused
            ? "捕获已暂停"
            : list.Any(item => item.State == CaptureHealthState.Healthy)
                ? "捕获中"
                : "等待钉钉";
    }

    private void OnInboxChanged(object? sender, EventArgs args) =>
        _dispatch(() => _ = RefreshAsync());

    private void OnHealthChanged(object? sender, CaptureHealth health) =>
        _dispatch(() => UpdateHealth(_inbox.Health));
}
