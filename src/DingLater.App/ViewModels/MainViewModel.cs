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
    private readonly object _refreshSync = new();
    private readonly SemaphoreSlim _settingsGate = new(1, 1);
    private Task? _refreshTask;
    private bool _refreshRequested;
    private volatile bool _disposed;
    private List<StoredMessage> _allMessages = [];
    private Dictionary<InboxState, List<ConversationThreadViewModel>> _threads = [];
    private IReadOnlyList<string> _searchSuggestions = [];
    private InboxSection _section;
    private string _searchText = string.Empty;
    private bool _isBusy;
    private bool _startupAvailable;
    private bool _startWithWindows;
    private string _statusText = "正在准备捕获…";
    private ConversationThreadViewModel? _selectedConversation;
    private MessageCardViewModel? _selectedMessage;
    private bool _isSettingsPage;
    private string _alwaysOnTopStatus = "正在准备窗口置顶…";
    private bool _alwaysOnTopHasError;

    public MainViewModel(InboxService inbox, StartupService startup, Action<Action>? dispatch = null)
    {
        _inbox = inbox;
        _startup = startup;
        var dispatcher = dispatch ?? (action => action());
        _dispatch = action => dispatcher(() =>
        {
            if (!_disposed)
            {
                action();
            }
        });
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

    public string AlwaysOnTopStatus => _alwaysOnTopStatus;
    public bool AlwaysOnTopHasError => _alwaysOnTopHasError;

    internal void SetAlwaysOnTopStatus(string message, bool isError) => _dispatch(() =>
    {
        SetProperty(ref _alwaysOnTopStatus, message, nameof(AlwaysOnTopStatus));
        SetProperty(ref _alwaysOnTopHasError, isError, nameof(AlwaysOnTopHasError));
    });

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
        InboxSection.Snoozed => "稍后提醒",
        InboxSection.Handled => "已处理",
        _ => "待处理"
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
    public int InboxCount { get; private set; }
    public StoredMessage? LatestInboxMessage { get; private set; }
    public int SnoozedCount { get; private set; }
    public int HandledCount { get; private set; }
    public bool CapturePaused => Settings.CapturePaused;
    public string CaptureActionText => CapturePaused ? "开始捕获" : "暂停捕获";
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

    public Task RefreshAsync()
    {
        lock (_refreshSync)
        {
            if (_disposed)
            {
                return Task.CompletedTask;
            }

            _refreshRequested = true;
            return _refreshTask ??= Task.Run(RefreshLoopAsync);
        }
    }

    private async Task RefreshLoopAsync()
    {
        try
        {
            _dispatch(() => IsBusy = true);
            while (true)
            {
                lock (_refreshSync)
                {
                    if (_disposed || !_refreshRequested)
                    {
                        _refreshTask = null;
                        _dispatch(() => IsBusy = false);
                        return;
                    }

                    _refreshRequested = false;
                }

                var messages = (await _inbox.ListAsync().ConfigureAwait(false)).ToList();
                _dispatch(() =>
                {
                    _allMessages = messages;
                    RebuildThreads();
                    ApplyFilter();
                });
            }
        }
        catch (Exception exception)
        {
            lock (_refreshSync)
            {
                _refreshTask = null;
                _dispatch(() =>
                {
                    IsBusy = false;
                    ErrorOccurred?.Invoke(this, $"刷新失败：{exception.Message}");
                });
            }
        }
    }

    public IReadOnlyList<string> GetSearchSuggestions(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        return _searchSuggestions
            .Where(value => value.Contains(query.Trim(), StringComparison.CurrentCultureIgnoreCase))
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

    public async Task<int> MarkAllHandledAsync(InboxSection? section = null)
    {
        try
        {
            var currentState = (section ?? Section) switch
            {
                InboxSection.Inbox => InboxState.Inbox,
                InboxSection.Snoozed => InboxState.Snoozed,
                _ => throw new InvalidOperationException("已处理分类不支持再次批量标记。")
            };
            var updated = await _inbox.MarkAllHandledAsync(currentState).ConfigureAwait(false);
            await RefreshAsync().ConfigureAwait(false);
            return updated;
        }
        catch (Exception exception)
        {
            _dispatch(() => ErrorOccurred?.Invoke(this, $"批量标记失败：{exception.Message}"));
            return 0;
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

    public async Task<bool> DeleteAsync(MessageCardViewModel? item)
    {
        if (item is null)
        {
            return false;
        }

        try
        {
            await _inbox.DeleteAsync(item.Id).ConfigureAwait(false);
            await RefreshAsync().ConfigureAwait(false);
            return true;
        }
        catch (Exception exception)
        {
            _dispatch(() => ErrorOccurred?.Invoke(this, $"删除失败：{exception.Message}"));
            return false;
        }
    }

    public async Task<int> DeleteAllHandledAsync()
    {
        try
        {
            var deleted = await _inbox.DeleteHandledAsync().ConfigureAwait(false);
            await RefreshAsync().ConfigureAwait(false);
            return deleted;
        }
        catch (Exception exception)
        {
            _dispatch(() => ErrorOccurred?.Invoke(this, $"批量删除失败：{exception.Message}"));
            return 0;
        }
    }

    public async Task<bool> ToggleCaptureAsync()
    {
        await _settingsGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await _inbox.SetCapturePausedAsync(!CapturePaused).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception)
        {
            _dispatch(() => ErrorOccurred?.Invoke(this, $"更改捕获状态失败：{exception.Message}"));
            return false;
        }
        finally
        {
            _settingsGate.Release();
            _dispatch(() =>
            {
                OnPropertyChanged(nameof(CapturePaused));
                OnPropertyChanged(nameof(CaptureActionText));
                UpdateHealth(_inbox.Health);
            });
        }
    }

    public async Task<int?> TryCountRetentionImpactAsync(int retentionDays)
    {
        try
        {
            return await _inbox.CountRetentionImpactAsync(Math.Clamp(retentionDays, 1, 365)).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _dispatch(() => ErrorOccurred?.Invoke(this, $"无法计算留存期影响：{exception.Message}"));
            return null;
        }
    }

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

        SearchText = string.Empty;
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
        _disposed = true;
        _inbox.InboxChanged -= OnInboxChanged;
        _inbox.HealthChanged -= OnHealthChanged;
        // Queued refresh/settings continuations may still be finishing during shutdown.
    }

    private void RebuildThreads()
    {
        var previous = _threads.SelectMany(section => section.Value.Select(thread =>
                new KeyValuePair<(InboxState, string), ConversationThreadViewModel>((section.Key, thread.Key), thread)))
            .ToDictionary();
        _threads = _allMessages.GroupBy(message => message.State).ToDictionary(
            section => section.Key,
            section => section.GroupBy(ConversationPresentation.GetKey, StringComparer.Ordinal)
                .Select(group => previous.TryGetValue((section.Key, group.Key), out var thread) && thread.HasSameMessages(group)
                    ? thread
                    : new ConversationThreadViewModel(group))
                .OrderBy(thread => section.Key == InboxState.Snoozed ? thread.NextReminderAt : DateTimeOffset.MinValue)
                .ThenByDescending(thread => thread.LatestAt)
                .ThenBy(thread => thread.Key, StringComparer.Ordinal)
                .ToList());
        _searchSuggestions = _threads.Values.SelectMany(threads => threads).Select(thread => thread.Title)
            .Concat(_allMessages.SelectMany(message => new[] { message.Captured.Conversation, message.Captured.Sender }))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.CurrentCultureIgnoreCase).ToList();
        InboxCount = _allMessages.Count(message => message.State == InboxState.Inbox);
        SnoozedCount = _allMessages.Count(message => message.State == InboxState.Snoozed);
        HandledCount = _allMessages.Count(message => message.State == InboxState.Handled);
        LatestInboxMessage = _allMessages.Where(message => message.State == InboxState.Inbox)
            .MaxBy(ConversationPresentation.GetMessageTime);
        OnPropertyChanged(nameof(InboxCount));
        OnPropertyChanged(nameof(LatestInboxMessage));
        OnPropertyChanged(nameof(SnoozedCount));
        OnPropertyChanged(nameof(HandledCount));
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

        var threads = _threads.GetValueOrDefault(targetState, [])
            .Where(thread => thread.Matches(SearchText.Trim()))
            .ToList();

        for (var index = 0; index < threads.Count; index++)
        {
            var thread = threads[index];
            if (index < Conversations.Count && ReferenceEquals(Conversations[index], thread))
            {
                continue;
            }

            var existingIndex = Conversations.IndexOf(thread);
            if (existingIndex >= 0)
            {
                Conversations.Move(existingIndex, index);
            }
            else
            {
                Conversations.Insert(index, thread);
            }
        }

        while (Conversations.Count > threads.Count)
        {
            Conversations.RemoveAt(Conversations.Count - 1);
        }

        var selected = previousKey is null
            ? Conversations.FirstOrDefault()
            : Conversations.FirstOrDefault(thread => thread.Key == previousKey)
              ?? Conversations.ElementAtOrDefault(Math.Clamp(previousIndex, 0, Math.Max(0, Conversations.Count - 1)));
        var selectedMessage = previousMessageId is null
            ? selected?.Messages.FirstOrDefault()
            : selected?.Messages.FirstOrDefault(message => message.Id == previousMessageId)
              ?? selected?.Messages.FirstOrDefault();
        SetProperty(ref _selectedConversation, selected, nameof(SelectedConversation));
        SetProperty(ref _selectedMessage, selectedMessage, nameof(SelectedMessage));

        OnPropertyChanged(nameof(IsEmpty));
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
