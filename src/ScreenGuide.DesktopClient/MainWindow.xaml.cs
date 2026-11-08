using System.Diagnostics;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using ScreenGuide.DesktopClient.Services;
using ScreenGuide.DesktopProtocol;
using ScreenGuide.Voice.Windows;
using WpfComboBox = System.Windows.Controls.ComboBox;
using WpfComboBoxItem = System.Windows.Controls.ComboBoxItem;

namespace ScreenGuide.DesktopClient;

public partial class MainWindow : Window
{
    private readonly IDesktopApiClient _api;
    private readonly DesktopHostProcessManager _hostProcessManager;
    private readonly DesktopClientSettingsStore _settingsStore;
    private readonly WindowsStartupService _startupService;
    private readonly TrayIconService _tray;
    private readonly DesktopNotificationCoordinator _notifications;
    private readonly IContinuousVoiceListener _voice;
    private readonly VoiceSessionController _voiceSession;
    private readonly Func<string, CancellationToken, Task> _speakAsync;
    private readonly VoiceReplyGate _voiceReplies = new();
    private Guid _voiceSubmissionNoticeOwner;
    private Guid? _automaticSpeechRequest;
    private readonly DispatcherTimer _refreshTimer;
    private readonly CancellationTokenSource _lifetime = new();
    private IReadOnlyList<ProjectDto> _projects = [];
    private IReadOnlyList<DesktopApplicationDto> _desktopApplications = [];
    private IReadOnlyList<TaskSummaryDto> _tasks = [];
    private IReadOnlyList<MemoryDto> _memories = [];
    private bool _isRenderingMemoryConsent;
    private bool _isInvalidatingMemoryConsent;
    private bool _isInvalidatingPointerConsent;
    private SystemStatusDto? _systemStatus;
    private Guid? _selectedTaskId;
    private Guid? _selectedConversationId;
    private SessionSnapshotDto? _currentSession;
    private readonly SessionProjectionCache _sessionProjectionCache = new();
    private Task? _sessionUpdateLoop;
    private bool _isRefreshing;
    private bool _isLoadingAiSettings;
    private bool _isLoadingMemories;
    private bool _isRenderingAiSettings;
    private bool _isHostOnline;
    private bool _forceClose;
    private bool _voiceCommandBusy;
    private VoiceUtterance? _queuedVoiceUtterance;
    private long? _queuedVoiceGeneration;
    private CancellationTokenSource? _activeVoiceCommandCancellation;
    private IInputRegistrationBoundary? _latestInputRegistration;
    private (string? InstanceId, DateTimeOffset? StartedAtUtc) _registrationCoordinator;
    private long _replacementInputVersion;
    private CancellationTokenSource _replacementInputCancellation = new();
    private const string PendingVoiceCancellationMessage = "正在取消上一条请求；收到回执后才能确认停止。";
    private CancellationTokenSource? _speechCancellation;
    private string? _activeSpeechText;
    private string? _lastAssistantSpeechText;
    private DateTimeOffset _lastAssistantSpeechEndedAt;
    private string? _lastVoiceUtterance;
    private DateTimeOffset _lastVoiceUtteranceAt;
    private DesktopClientSettings _settings;
    private AiSettingsDto? _aiSettings;
    private Task? _initializationTask;

    public MainWindow(
        IDesktopApiClient api,
        DesktopHostProcessManager? hostProcessManager = null,
        DesktopClientSettingsStore? settingsStore = null,
        WindowsStartupService? startupService = null,
        TrayIconService? tray = null,
        DesktopClientSettings? settings = null,
        IContinuousVoiceListener? voice = null,
        Func<string, CancellationToken, Task>? speechOutput = null)
    {
        InitializeComponent();
        _api = api;
        _hostProcessManager = hostProcessManager ?? new DesktopHostProcessManager(api);
        _settingsStore = settingsStore ?? new DesktopClientSettingsStore();
        _startupService = startupService ?? new WindowsStartupService();
        _tray = tray ?? new TrayIconService();
        _settings = settings ?? new DesktopClientSettings();
        _voice = voice ?? new OfflineContinuousVoiceListener();
        _speakAsync = speechOutput ?? new WindowsSpeechOutput().SpeakAsync;
        _voiceSession = new VoiceSessionController(_voice);
        _voice.PartialTextChanged += Voice_PartialTextChanged;
        _voice.UtteranceRecognized += Voice_UtteranceRecognized;
        _voiceSession.StateChanged += VoiceSession_StateChanged;
        _notifications = new DesktopNotificationCoordinator(_tray, _settingsStore);
        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(5)
        };
        _refreshTimer.Tick += RefreshTimer_Tick;
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
        Closed += MainWindow_Closed;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        => await InitializeClientAsync();

    public Task InitializeClientAsync() => _initializationTask ??= InitializeClientCoreAsync();

    private async Task InitializeClientCoreAsync()
    {
        ProductVersionText.Text = $"版本 {Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.2.0"}";
        RenderVoiceStatus();
        await RefreshAllAsync(showErrors: true);
        if (_forceClose || _lifetime.IsCancellationRequested) return;
        _sessionUpdateLoop = RunSessionUpdateLoopAsync();
        _refreshTimer.Start();
    }

    private async void MainWindow_Closed(object? sender, EventArgs e)
    {
        _assistantWindow?.CloseForExit();
        CancelPendingVoiceInput();
        CancelPendingPointerGesture();
        _pointerGesture.Dispose();
        _refreshTimer.Stop();
        _activeVoiceCommandCancellation?.Cancel();
        _activeVoiceCommandCancellation?.Dispose();
        _lifetime.Cancel();
        _voice.PartialTextChanged -= Voice_PartialTextChanged;
        _voice.UtteranceRecognized -= Voice_UtteranceRecognized;
        _voiceSession.StateChanged -= VoiceSession_StateChanged;
        await _voiceSession.DisposeAsync();
        await CloseIslandSurfaceAsync();
        await Task.Run(async () => await _voice.DisposeAsync());
        if (_sessionUpdateLoop is not null)
        {
            try
            {
                await _sessionUpdateLoop;
            }
            catch (OperationCanceledException)
            {
            }
        }
        _lifetime.Dispose();
    }

    private async void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if ((_assistantWindow is not null || _islandBridge is { IsVisible: true }) && !_forceClose)
        {
            // Closing the management surface returns to the still-visible assistant.
            e.Cancel = true;
            ShowAssistantSurface();
            return;
        }
        CancelPendingVoiceInput();
        _ = _voiceSession.StopAsync();
        CancelPendingPointerGesture();
        if (!_pointerCancellationCompletion.IsCompletedSuccessfully)
        {
            e.Cancel = true;
            try { await _pointerCancellationCompletion; }
            catch { return; } // Keep the visible window available for a retry if Host did not acknowledge.
            Close();
            return;
        }
        if (_forceClose)
        {
            _ = _voiceSession.StopAsync();
            return;
        }

        if (_settings.RunInBackground && _settings.CloseToTray)
        {
            e.Cancel = true;
            _ = _voiceSession.StopAsync();
            Hide();
            StatusBarText.Text = "元枢正在后台运行。";
            return;
        }

        _forceClose = true;
        _ = _voiceSession.StopAsync();
        _ = Dispatcher.BeginInvoke(() => System.Windows.Application.Current.Shutdown());
    }

    private async void RefreshTimer_Tick(object? sender, EventArgs e) =>
        await RefreshAllAsync(showErrors: false);

    private async Task RefreshAllAsync(bool showErrors)
    {
        if (_isRefreshing)
        {
            return;
        }

        _isRefreshing = true;
        try
        {
            var dashboardTask = _api.GetDashboardAsync(_lifetime.Token);
            var projectsTask = _api.ListProjectsAsync(_lifetime.Token);
            var tasksTask = _api.ListTasksAsync(cancellationToken: _lifetime.Token);
            var desktopApplicationsTask = _api.ListDesktopApplicationsAsync(_lifetime.Token);
            var sessionTask = _api.GetCurrentSessionAsync(_lifetime.Token);
            await Task.WhenAll(
                dashboardTask,
                projectsTask,
                tasksTask,
                desktopApplicationsTask,
                sessionTask);
            var dashboard = await dashboardTask;
            _projects = await projectsTask;
            _tasks = await tasksTask;
            _desktopApplications = await desktopApplicationsTask;
            _systemStatus = dashboard.System;
            SetHostOnline(true);
            RenderDashboard(dashboard);
            RenderProjects();
            RenderTaskSelectors();
            RenderDesktopActions();
            RenderHistory();
            RenderSession(await sessionTask);
            RenderSettings();
            _settings = await _notifications.ProcessAsync(_tasks, _settings, _lifetime.Token);
            if (_selectedTaskId is { } taskId)
            {
                await LoadTaskDetailsAsync(taskId, navigate: false);
            }
            else
            {
                var current = dashboard.WaitingTasks.FirstOrDefault()
                    ?? dashboard.ActiveTasks.FirstOrDefault();
                if (current is not null)
                {
                    _selectedTaskId = current.Id;
                    await LoadTaskDetailsAsync(current.Id, navigate: false);
                }
            }

            StatusBarText.Text = "已连接本机会话中枢。";
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            SetHostOnline(false);
            _tray.UpdateState(TrayVisualState.Error);
            StatusBarText.Text = "本机中枢暂时离线，正在尝试恢复连接。";
            if (showErrors)
            {
                ShowError("本机任务服务暂时无法连接。", exception);
            }

            try
            {
                await _hostProcessManager.EnsureRunningAsync(_lifetime.Token);
            }
            catch
            {
            }
        }
        finally
        {
            _isRefreshing = false;
        }
    }

    private void RenderDashboard(DashboardDto dashboard)
    {
        ActiveCountText.Text = dashboard.ActiveTaskCount.ToString();
        WaitingCountText.Text = dashboard.WaitingTaskCount.ToString();
        ProjectCountText.Text = dashboard.AuthorizedProjectCount.ToString();
        TodayCompletedCountText.Text = dashboard.CompletedTodayCount.ToString();
        FailedCountText.Text = dashboard.FailedTaskCount.ToString();
        RenderVoiceStatus();
        DashboardActiveList.ItemsSource = dashboard.WaitingTasks
            .Concat(dashboard.ActiveTasks)
            .Select(TaskRow.From)
            .ToArray();
        DashboardRecentList.ItemsSource = dashboard.RecentTasks.Select(TaskRow.From).ToArray();
        HostStatusText.Text = dashboard.System.HostOnline ? "本机中枢在线" : "本机中枢离线";
        CodexStatusText.Text = dashboard.System.Codex.IsCompatible
            ? $"Codex {dashboard.System.Codex.Version} 可用"
            : dashboard.System.Codex.IsInstalled ? "Codex 版本不兼容" : "未找到 Codex";
        CodexStatusPill.Background = BrushFrom(
            dashboard.System.Codex.IsCompatible ? "#EAF6F0" : "#FFF3E5");
        CodexStatusText.Foreground = BrushFrom(
            dashboard.System.Codex.IsCompatible ? "#1F8A6A" : "#A56210");
    }

    private void RenderProjects()
    {
        var rows = _projects.Select(ProjectRow.From).ToArray();
        var selectedId = (ProjectsListView.SelectedItem as ProjectRow)?.Id;
        ProjectsListView.ItemsSource = rows;
        ProjectsListView.SelectedItem = rows.FirstOrDefault(row => row.Id == selectedId);
    }

    private void RenderTaskSelectors()
    {
        var authorized = _projects
            .Where(project => project.AuthorizationState == "Authorized")
            .ToArray();
        PreserveProjectSelection(NewTaskProjectComboBox, authorized);
        PreserveProjectSelection(MemoryProjectComboBox, authorized);
        PreserveMemoryPreviewProjectSelection(authorized);
        NewTaskSubmitButton.IsEnabled = authorized.Length > 0 && _isHostOnline;
    }

    private void PreserveMemoryPreviewProjectSelection(ProjectDto[] projects)
    {
        var selectedId = (MemoryPreviewProjectComboBox.SelectedItem as MemoryPreviewProjectOption)?.ProjectId;
        var options = new[] { new MemoryPreviewProjectOption(null, "仅全局记忆") }
            .Concat(projects.Select(project => new MemoryPreviewProjectOption(project.Id, project.Name)))
            .ToArray();
        MemoryPreviewProjectComboBox.ItemsSource = options;
        MemoryPreviewProjectComboBox.SelectedItem = options.FirstOrDefault(option => option.ProjectId == selectedId)
            ?? options[0];
    }

    private static void PreserveProjectSelection(WpfComboBox comboBox, ProjectDto[] projects)
    {
        var selectedId = (comboBox.SelectedItem as ProjectDto)?.Id;
        comboBox.ItemsSource = projects;
        comboBox.SelectedItem = projects.FirstOrDefault(project => project.Id == selectedId)
            ?? projects.FirstOrDefault();
    }

    private void RenderDesktopActions()
    {
        var selectedId = (DesktopApplicationComboBox.SelectedItem as DesktopApplicationDto)?.Id;
        DesktopApplicationComboBox.ItemsSource = _desktopApplications;
        DesktopApplicationComboBox.SelectedItem = _desktopApplications.FirstOrDefault(item =>
            item.Id == selectedId) ?? _desktopApplications.FirstOrDefault();
        OpenDesktopApplicationButton.IsEnabled = _isHostOnline && _desktopApplications.Count > 0;
        OpenDesktopWebsiteButton.IsEnabled = _isHostOnline;
    }

    private void RenderHistory()
    {
        var filter = (HistoryFilterComboBox.SelectedItem as WpfComboBoxItem)?.Tag?.ToString();
        var filtered = string.IsNullOrWhiteSpace(filter)
            ? _tasks
            : _tasks.Where(task =>
                string.Equals(task.Status, filter, StringComparison.OrdinalIgnoreCase)
                || string.Equals(task.VerificationStatus, filter, StringComparison.OrdinalIgnoreCase))
                .ToArray();
        HistoryListView.ItemsSource = filtered.Select(HistoryRow.From).ToArray();
    }

    private void RenderSession(SessionSnapshotDto? snapshot)
    {
        if (snapshot is not null
            && !ReferenceEquals(snapshot, _sessionProjectionCache.Snapshot)
            && !_sessionProjectionCache.Reset(snapshot))
        {
            return;
        }

        if (snapshot is null && _currentSession is not null)
        {
            return;
        }

        snapshot = _sessionProjectionCache.Snapshot ?? snapshot;

        if (_currentSession is { } previous && snapshot is not null
            && (previous.SessionId != snapshot.SessionId
                || previous.CoordinatorInstanceId != snapshot.CoordinatorInstanceId
                || previous.CoordinatorStartedAtUtc != snapshot.CoordinatorStartedAtUtc))
        {
            var hostChanged = previous.CoordinatorInstanceId != snapshot.CoordinatorInstanceId
                || previous.CoordinatorStartedAtUtc != snapshot.CoordinatorStartedAtUtc;
            CancelPendingVoiceInput(invalidateOperation: hostChanged, preserveQueuedInput: !hostChanged);
        }
        _currentSession = snapshot;
        ReconcileInputCancellation(snapshot);
        if (_automaticSpeechRequest is { } speakingRequest
            && !_voiceReplies.IsCurrent(speakingRequest, snapshot, _voiceSession.ListeningGeneration))
        {
            _speechCancellation?.Cancel();
            _automaticSpeechRequest = null;
        }
        if (_pointerGesture.IsArmed && _pointerGestureStillValid?.Invoke() != true) CancelPendingPointerGesture();
        _selectedConversationId = snapshot?.ConversationId;
        RenderConversationMemoryChoices();
        var presentation = SessionUiPresenter.Present(snapshot);
        SessionTitleText.Text = presentation.Title;
        SessionStatusText.Text = presentation.StatusText;
        SessionProjectText.Text = presentation.ProjectText;
        SessionStatusText.Foreground = BrushFrom(presentation.StatusTone switch
        {
            "Busy" => "#2E6DD8",
            "Waiting" => "#A56210",
            "Success" => "#1F8A6A",
            "Error" => "#C94C4C",
            _ => "#52656D"
        });
        StopSessionButton.Visibility = presentation.ShowStop
            ? Visibility.Visible
            : Visibility.Collapsed;
        AssistantPlanBorder.Visibility = presentation.ShowContextCard
            ? Visibility.Visible
            : Visibility.Collapsed;
        AssistantPlanSummaryText.Text = presentation.OriginalRequest;
        AssistantPlanDetailText.Text = presentation.DetailText;
        SessionProjectPickerPanel.Visibility = presentation.ShowProjectPicker
            ? Visibility.Visible
            : Visibility.Collapsed;
        SessionFilePickerPanel.Visibility = presentation.ShowFilePicker
            ? Visibility.Visible
            : Visibility.Collapsed;
        PreserveProjectSelection(
            SessionProjectComboBox,
            _projects.Where(project => project.AuthorizationState == "Authorized").ToArray());
        ConfirmAssistantPlanButton.Content = presentation.PrimaryActionText;
        ConfirmAssistantPlanButton.Visibility = presentation.ShowContextCard
            ? Visibility.Visible
            : Visibility.Collapsed;
        CancelSessionContextButton.Content = presentation.CancelActionText;
        var observing = snapshot?.ForegroundTurn?.Phase == "ObservingWindow";
        WindowObservationBorder.Visibility = observing ? Visibility.Visible : Visibility.Collapsed;
        if (observing)
        {
            WindowObservationTargetText.Text = string.IsNullOrWhiteSpace(snapshot?.ForegroundTurn?.WindowTitle)
                ? "只读取本次确认的单个窗口；图像不保存、不上传。"
                : $"目标：{snapshot.ForegroundTurn.WindowTitle}。图像不保存、不上传。";
        }

        var lastTurn = snapshot?.Turns.OrderBy(item => item.SequenceNumber).LastOrDefault();
        RefreshVoiceCancellationStatus();
        AssistantResultBorder.Visibility = presentation.ShowResult && lastTurn is not null
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (lastTurn is not null && presentation.ShowResult)
        {
            AssistantResultStatusText.Text = presentation.StatusText;
            AssistantResultText.Text = SessionUiPresenter.ResultText(lastTurn)
                                       ?? presentation.StatusText;
            AssistantResultBorder.Background = BrushFrom(lastTurn.Phase == "Failed" ? "#FCECED" : "#F7FAFB");
        }

        var messages = snapshot?.Messages.Select(ConversationMessageRow.From).ToArray()
                       ?? Array.Empty<ConversationMessageRow>();
        SessionMessagesList.ItemsSource = messages;
        SessionMessagesBorder.Visibility = messages.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        ConversationMessagesList.ItemsSource = messages;
        ConversationMessagesList.Visibility = messages.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        ConversationMessagesPanel.Visibility = messages.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        LoadEarlierMessagesButton.Visibility = messages.Length > 0 && _sessionProjectionCache.HasEarlierMessages
            ? Visibility.Visible
            : Visibility.Collapsed;
        ConversationEmptyPanel.Visibility = messages.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ConversationTitleText.Text = presentation.Title;
        ConversationStateText.Text = presentation.StatusText;
        ConversationInputTextBox.IsEnabled = _isHostOnline && snapshot is not null;
        SendConversationButton.IsEnabled = ConversationInputTextBox.IsEnabled;
        AskPointerQuestionButton.IsEnabled = ConversationInputTextBox.IsEnabled;
        StopConversationButton.Visibility = presentation.ShowStop ? Visibility.Visible : Visibility.Collapsed;
        RenderMemoryOutboundConsent(snapshot);
        RenderPointerAnswerConsent(snapshot);
        ConversationListBox.ItemsSource = snapshot is null
            ? Array.Empty<ConversationRow>()
            : new[]
            {
                new ConversationRow(
                    snapshot.ConversationId,
                    snapshot.Title,
                    presentation.StatusText)
            };
        if (messages.Length > 0)
        {
            SessionMessagesList.ScrollIntoView(messages[^1]);
            ConversationMessagesList.ScrollIntoView(messages[^1]);
        }
        var reply = _voiceReplies.TryTakeReply(snapshot, _voiceSession.ListeningGeneration);
        if (reply is not null) _ = SpeakAssistantAsync(reply.Text, reply.RequestId);
        RenderAssistantSurface();
    }

    private async Task RunSessionUpdateLoopAsync()
    {
        while (!_lifetime.IsCancellationRequested)
        {
            try
            {
                var current = _currentSession;
                if (current is null)
                {
                    await RefreshSessionAsync();
                    await Task.Delay(TimeSpan.FromMilliseconds(100), _lifetime.Token);
                    continue;
                }

                var update = await _api.WaitForSessionProjectionAsync(
                    new SessionProjectionCursorDto(
                        current.CoordinatorInstanceId,
                        current.CoordinatorStartedAtUtc,
                        current.SessionId,
                        current.ChangeVersion,
                        _sessionProjectionCache.LatestMessageSequenceNumber,
                        20_000),
                    _lifetime.Token);
                SetHostOnline(true);
                if (string.Equals(update.Kind, "ResetRequired", StringComparison.Ordinal))
                {
                    await RefreshSessionAsync();
                }
                else if (_sessionProjectionCache.Apply(update))
                {
                    RenderSession(_sessionProjectionCache.Snapshot);
                }
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
                return;
            }
            catch
            {
                SetHostOnline(false);
                await Task.Delay(TimeSpan.FromSeconds(2), _lifetime.Token);
            }
        }
    }

    private async Task RefreshSessionAsync()
    {
        var snapshot = await _api.GetCurrentSessionAsync(_lifetime.Token);
        RenderSession(snapshot);
    }

    private void RenderSettings()
    {
        if (_systemStatus is null)
        {
            return;
        }

        SettingsHostStatusText.Text = _systemStatus.HostOnline ? "在线" : "离线";
        SettingsCodexStatusText.Text = _systemStatus.Codex.Message;
        SettingsCodexVersionText.Text = _systemStatus.Codex.Version ?? "未检测到";
        SettingsVoiceStatusText.Text = _voiceSession.Status.Message;
        SettingsDatabaseStatusText.Text = _systemStatus.DatabaseStatus == "Ready" ? "正常" : _systemStatus.DatabaseStatus;
        SettingsDeviceIdText.Text = _systemStatus.DeviceId;
        SettingsDataPathText.Text = _systemStatus.DataDirectory;
        StartWithWindowsCheckBox.IsChecked = _settings.StartWithWindows;
        RunInBackgroundCheckBox.IsChecked = _settings.RunInBackground;
        CloseToTrayCheckBox.IsChecked = _settings.CloseToTray;
        NotificationsEnabledCheckBox.IsChecked = _settings.NotificationsEnabled;
    }

    private async Task LoadAiSettingsForPageAsync()
    {
        if (_isLoadingAiSettings || _lifetime.IsCancellationRequested)
        {
            return;
        }

        _isLoadingAiSettings = true;
        try
        {
            AiProviderHealthText.Text = "正在读取普通聊天大脑设置…";
            var settings = await _api.GetAiSettingsAsync(_lifetime.Token);
            RenderAiSettings(settings);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            AiProviderHealthText.Text = "AI 设置暂时无法读取。";
            ShowError("AI 设置暂时无法读取，请确认本机中枢正在运行。", exception);
        }
        finally
        {
            _isLoadingAiSettings = false;
        }
    }

    private async void LoadEarlierMessagesButton_Click(object sender, RoutedEventArgs e)
    {
        var current = _currentSession;
        var before = _sessionProjectionCache.NextBeforeMessageSequenceNumber;
        if (current is null || before is null || !_sessionProjectionCache.HasEarlierMessages)
        {
            return;
        }

        LoadEarlierMessagesButton.IsEnabled = false;
        try
        {
            var page = await _api.GetSessionMessagesPageAsync(
                current.SessionId,
                before,
                pageSize: 50,
                _lifetime.Token);
            if (_sessionProjectionCache.ApplyEarlierMessages(page))
            {
                RenderSession(_sessionProjectionCache.Snapshot);
            }
        }
        catch (DesktopApiException exception)
        {
            ConversationStateText.Text = exception.Error.UserMessage;
        }
        finally
        {
            LoadEarlierMessagesButton.IsEnabled = true;
        }
    }

    private void RenderMemoryOutboundConsent(SessionSnapshotDto? snapshot)
    {
        var consent = snapshot?.MemoryOutboundConsents?
            .SingleOrDefault(item => item.TurnId == snapshot.ForegroundTurn?.Id);
        _isRenderingMemoryConsent = true;
        try
        {
            MemoryOutboundConsentBorder.Visibility = consent is null
                ? Visibility.Collapsed
                : Visibility.Visible;
            if (consent is null)
            {
                MemoryOutboundItemsControl.ItemsSource = null;
                return;
            }

            MemoryOutboundRouteText.Text =
                $"AI 服务：{consent.ProviderId} · 模型：{consent.ModelId} · HTTPS 去向：{consent.DestinationOrigin}";
            MemoryOutboundProjectText.Text = consent.ProjectId is null
                ? "项目绑定：无（仅全局记忆）"
                : $"项目绑定：{consent.ProjectName ?? "已授权项目"}（{consent.ProjectId:D}）";
            MemoryOutboundItemsControl.ItemsSource = consent.Items.Select((item, index) =>
                new MemoryOutboundConsentItemRow(
                    $"{index + 1}. {item.Title}",
                    item.Body,
                    $"类别：{MemoryCategoryLabel(item.Category)} · 范围：{MemoryScopeLabel(item.Scope)} · version {item.Version} · {item.CharacterCount} 字符"))
                .ToArray();
            MemoryOutboundBudgetText.Text =
                $"合计：{consent.ItemCount} 条，标题与正文共 {consent.TotalCharacters} 个 UTF-16 字符。";
        }
        finally
        {
            _isRenderingMemoryConsent = false;
        }
    }

    private void RenderPointerAnswerConsent(SessionSnapshotDto? snapshot)
    {
        var consent = snapshot?.PointerAnswerConsents?
            .SingleOrDefault(item => item.TurnId == snapshot.ForegroundTurn?.Id);
        PointerAnswerConsentBorder.Visibility = consent is null
            ? Visibility.Collapsed
            : Visibility.Visible;
        if (consent is null)
        {
            PointerAnswerQuestionText.Clear();
            PointerAnswerOcrText.Clear();
            return;
        }

        PointerAnswerRouteText.Text =
            $"AI 服务：{consent.ProviderId} · 模型：{consent.ModelId} · HTTPS 去向：{consent.DestinationOrigin}";
        PointerAnswerPromptText.Text =
            $"Prompt：{consent.PromptId}@{consent.PromptVersion} · SHA-256：{consent.PromptContentHash}";
        PointerAnswerQuestionText.Text = consent.Question;
        PointerAnswerOcrText.Text = consent.OcrText;
        PointerAnswerShapeText.Text =
            $"区域：{consent.RegionWidth}×{consent.RegionHeight} · 来源：{consent.RegionSource} · OCR {consent.OcrLineCount} 行 / {consent.OcrCharacterCount} 字符 · 图片发送：否";
    }

    private async Task LoadMemoriesForPageAsync(Guid? preferredMemoryId = null)
    {
        if (_isLoadingMemories || _lifetime.IsCancellationRequested)
        {
            return;
        }

        _isLoadingMemories = true;
        try
        {
            _memories = await _api.ListMemoriesAsync(_lifetime.Token);
            var rows = _memories.Select(MemoryRow.From).ToArray();
            var selectedId = preferredMemoryId ?? (MemoryListBox.SelectedItem as MemoryRow)?.Item.Id;
            MemoryListBox.ItemsSource = rows;
            MemoryListBox.SelectedItem = rows.FirstOrDefault(row => row.Item.Id == selectedId);
            if (MemoryListBox.SelectedItem is null && preferredMemoryId is null)
            {
                ClearMemoryEditor();
            }


            RenderConversationMemoryChoices();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ShowError("长期记忆暂时无法读取，请确认本机中枢正在运行。", exception);
        }
        finally
        {
            _isLoadingMemories = false;
        }
    }

    private void RenderConversationMemoryChoices()
    {
        if (!IsLoaded)
        {
            return;
        }

        var selectedIds = ConversationMemorySelectionList.SelectedItems
            .OfType<MemoryRow>()
            .Select(row => row.Item.Id)
            .ToHashSet();
        var selectedProjectId = _currentSession?.SelectedProjectId;
        var rows = _memories
            .Where(item => item.Status == "Active"
                           && (item.Scope == "Global" || item.ProjectId == selectedProjectId))
            .Select(MemoryRow.From)
            .ToArray();
        _isRenderingMemoryConsent = true;
        try
        {
            ConversationMemorySelectionList.ItemsSource = rows;
            foreach (var row in rows.Where(row => selectedIds.Contains(row.Item.Id)))
            {
                ConversationMemorySelectionList.SelectedItems.Add(row);
            }
        }
        finally
        {
            _isRenderingMemoryConsent = false;
        }
    }

    private void RenderAiSettings(
        AiSettingsDto settings,
        string? preferredProviderId = null,
        string? preferredModelId = null)
    {
        _aiSettings = settings;
        var selectedProviderId = preferredProviderId
                                 ?? (AiChatProviderComboBox.SelectedItem as AiProviderSettingsDto)?.ProviderId
                                 ?? settings.CurrentChatRoute.ProviderId;
        _isRenderingAiSettings = true;
        try
        {
            var providers = settings.Providers.ToArray();
            AiChatProviderComboBox.ItemsSource = providers;
            AiChatProviderComboBox.SelectedItem = providers.FirstOrDefault(item =>
                    string.Equals(item.ProviderId, selectedProviderId, StringComparison.OrdinalIgnoreCase))
                ?? providers.FirstOrDefault(item =>
                    string.Equals(
                        item.ProviderId,
                        settings.CurrentChatRoute.ProviderId,
                        StringComparison.OrdinalIgnoreCase))
                ?? providers.FirstOrDefault();
        }
        finally
        {
            _isRenderingAiSettings = false;
        }

        RenderSelectedAiProvider(preferredModelId);
    }

    private void RenderSelectedAiProvider(string? preferredModelId = null)
    {
        var provider = AiChatProviderComboBox.SelectedItem as AiProviderSettingsDto;
        if (provider is null)
        {
            AiChatModelComboBox.ItemsSource = Array.Empty<AiModelSettingsDto>();
            AiDataDestinationText.Text = "当前没有可选择的普通聊天 Provider。";
            AiCredentialStatusText.Text = "状态：未配置";
            AiProviderHealthText.Text = "连接状态：不可用";
            UpdateAiSettingsButtons();
            return;
        }

        var selectedModelId = AiSettingsUiPolicy.SelectModelId(
            provider,
            preferredModelId
            ?? (AiChatModelComboBox.SelectedItem as AiModelSettingsDto)?.ModelId,
            _aiSettings?.CurrentChatRoute);

        _isRenderingAiSettings = true;
        try
        {
            var models = provider.Models.ToArray();
            AiChatModelComboBox.ItemsSource = models;
            AiChatModelComboBox.SelectedItem = models.FirstOrDefault(item =>
                    string.Equals(item.ModelId, selectedModelId, StringComparison.OrdinalIgnoreCase))
                ?? models.FirstOrDefault();
        }
        finally
        {
            _isRenderingAiSettings = false;
        }

        var destination = SensitiveDataSanitizer.Redact(provider.DataDestination);
        AiDataDestinationText.Text = provider.SendsDataOffDevice
            ? $"发送位置：{destination}。普通聊天内容会离开本机并发送到该服务。"
            : $"处理位置：{destination}。普通聊天内容不会离开本机。";
        var healthMessage = SensitiveDataSanitizer.Redact(provider.Health.SafeMessage);
        AiProviderHealthText.Text = string.IsNullOrWhiteSpace(healthMessage)
            ? $"连接状态：{HealthStateLabel(provider.Health.State)}"
            : $"连接状态：{HealthStateLabel(provider.Health.State)}。{healthMessage}";
        UpdateAiSettingsButtons();
    }

    private void UpdateAiSettingsButtons()
    {
        var provider = AiChatProviderComboBox.SelectedItem as AiProviderSettingsDto;
        var model = AiChatModelComboBox.SelectedItem as AiModelSettingsDto;
        SaveAiChatRouteButton.IsEnabled = _isHostOnline && provider is not null && model is not null;
        var credential = provider is null
            ? new AiCredentialPresentation(false, false, false, "状态：未配置")
            : AiSettingsUiPolicy.PresentCredential(
                provider,
                !string.IsNullOrWhiteSpace(AiCredentialPasswordBox.Password),
                _isHostOnline);
        AiCredentialRequiredPanel.Visibility = credential.ShowCredentialInputs
            ? Visibility.Visible
            : Visibility.Collapsed;
        AiCredentialNotRequiredText.Visibility = provider is not null && !credential.ShowCredentialInputs
            ? Visibility.Visible
            : Visibility.Collapsed;
        AiCredentialNotRequiredText.Text = credential.StatusText;
        AiCredentialStatusText.Text = credential.ShowCredentialInputs
            ? credential.StatusText
            : "状态：无需设置";
        SaveAiCredentialButton.IsEnabled = credential.CanSave;
        DeleteAiCredentialButton.IsEnabled = credential.CanDelete;
        CheckAiProviderHealthButton.IsEnabled = _isHostOnline && provider is not null;
    }

    private void UpdateProvider(AiProviderSettingsDto updatedProvider, string? preferredModelId = null)
    {
        if (_aiSettings is null)
        {
            return;
        }

        _aiSettings = _aiSettings with
        {
            Providers = _aiSettings.Providers
                .Select(item => string.Equals(
                    item.ProviderId,
                    updatedProvider.ProviderId,
                    StringComparison.OrdinalIgnoreCase)
                    ? updatedProvider
                    : item)
                .ToArray()
        };
        RenderAiSettings(_aiSettings, updatedProvider.ProviderId, preferredModelId);
    }

    private static string HealthStateLabel(string state) => state switch
    {
        "Healthy" => "正常",
        "Degraded" => "可用但受限",
        "Unavailable" => "不可用",
        "NotConfigured" => "未配置",
        _ => "尚未检查"
    };

    private async Task LoadTaskDetailsAsync(Guid taskId, bool navigate, Func<bool>? isCurrent = null)
    {
        var details = await _api.GetTaskAsync(taskId, _lifetime.Token);
        if (details is null || isCurrent?.Invoke() == false)
        {
            return;
        }

        _selectedTaskId = taskId;
        CurrentTaskEmptyPanel.Visibility = Visibility.Collapsed;
        CurrentTaskContent.Visibility = Visibility.Visible;
        CurrentTaskTitleText.Text = details.Summary.Title;
        CurrentTaskStatusText.Text = StatusLabel(details.Summary.Status);
        CurrentTaskProjectText.Text = $"项目：{details.Summary.ProjectName}";
        CurrentTaskDurationText.Text = DurationText(details.Summary);
        CurrentInstructionText.Text = details.Instruction;
        CurrentTaskCancelButton.IsEnabled = details.Summary.Status is "Pending" or "Running" or "WaitingForUser";
        CurrentEventsList.ItemsSource = details.Events.Select(EventRow.From).ToArray();
        DecisionCard.Visibility = details.Summary.Status == "WaitingForUser"
            ? Visibility.Visible
            : Visibility.Collapsed;
        DecisionQuestionText.Text = details.PendingDecision?.Question ?? "需要补充信息后才能继续。";
        RenderEvidence(details);
        if (navigate)
        {
            ShowPage(AppPage.CurrentTask);
        }
    }

    private void RenderEvidence(TaskDetailsDto details)
    {
        var evidence = details.Evidence;
        if (evidence is null)
        {
            CurrentVerificationStatusText.Text = "等待证据";
            CurrentUserSummaryText.Text = details.Summary.Status == "Running"
                ? "任务正在执行，完成后会生成可核实的结果。"
                : "当前还没有生成完整的验证证据。";
            SetVerificationColors("Pending");
            EvidenceConflictBanner.Visibility = Visibility.Collapsed;
            EvidenceStatsText.Text = "尚无最终文件和测试证据。";
            ChangedFilesList.ItemsSource = null;
            TestCommandsList.ItemsSource = null;
            AgentFinalExplanationText.Text = "—";
        }
        else
        {
            CurrentVerificationStatusText.Text = VerificationLabel(evidence.VerificationStatus);
            CurrentUserSummaryText.Text = evidence.UserSummary;
            SetVerificationColors(evidence.VerificationStatus);
            EvidenceConflictBanner.Visibility = evidence.AgentClaimContradictedByEvidence
                ? Visibility.Visible
                : Visibility.Collapsed;
            EvidenceStatsText.Text =
                $"文件：新增 {evidence.AddedFileCount}，修改 {evidence.ModifiedFileCount}，删除 {evidence.DeletedFileCount}\n" +
                $"Diff：+{evidence.AddedLineCount?.ToString() ?? "?"} / -{evidence.DeletedLineCount?.ToString() ?? "?"}\n" +
                $"测试：{TestStatusLabel(evidence.TestStatus)}，通过 {evidence.PassedTests?.ToString() ?? "?"}，失败 {evidence.FailedTests?.ToString() ?? "?"}";
            ChangedFilesList.ItemsSource = evidence.ChangedFiles.Select(FileRow.From).ToArray();
            TestCommandsList.ItemsSource = evidence.TestCommands.Select(TestRow.From).ToArray();
            AgentFinalExplanationText.Text = string.IsNullOrWhiteSpace(evidence.AgentFinalExplanation)
                ? "编程助手没有提供最终说明。"
                : evidence.AgentFinalExplanation;
        }

        TaskTechnicalInfoText.Text =
            $"运行标识: {details.ThreadId ?? "—"}\n" +
            $"Attempt: {details.CurrentAttempt}\n" +
            $"Connector: {details.ConnectorVersion ?? "—"}\n" +
            $"Working directory: {details.WorkingDirectoryRelativePath}";
    }

    private void SetVerificationColors(string status)
    {
        var (background, border) = status switch
        {
            "Verified" => ("#EAF6F0", "#79B79C"),
            "Unverified" => ("#FFF7E6", "#E4C47C"),
            "VerificationFailed" or "Failed" => ("#FCECED", "#E3A1A5"),
            "Cancelled" => ("#F0F2F2", "#B7C0C0"),
            "Interrupted" => ("#FFF3E5", "#D9A263"),
            _ => ("#EEF3F5", "#B8CBD3")
        };
        CurrentVerificationBanner.Background = BrushFrom(background);
        CurrentVerificationBanner.BorderBrush = BrushFrom(border);
    }

    private async Task CreateTaskAsync(ProjectDto? project, string instruction, string? title)
    {
        CancelPendingVoiceInput();
        var operation = CaptureInputOperation();
        CancelPendingPointerGesture();
        if (project is null)
        {
            ShowError("请先选择一个已授权项目。", null);
            return;
        }

        if (string.IsNullOrWhiteSpace(instruction))
        {
            ShowError("请描述希望电脑完成什么。", null);
            return;
        }

        await RunCommandAsync(async () =>
        {
            if (!await AwaitPendingVoiceSubmissionAsync(operation, allowRegistrationHandoff: true)) return;
            var session = await StartSessionForOperationAsync(operation,
                string.IsNullOrWhiteSpace(title) ? "编程任务" : title.Trim(),
                _lifetime.Token);
            if (!TryAdoptCreatedSession(ref operation, session)) return;
            if (!await AwaitPendingVoiceSubmissionAsync(operation)) return;
            var submitted = await SubmitInputForOperationAsync(operation,
                new SessionInputRequestDto(
                    instruction.Trim(),
                    "ProgrammingTask",
                    $"desktop-programming-{Guid.NewGuid():N}",
                    session.SessionId),
                _lifetime.Token);
            try
            {
                if (!IsInputOperationCurrent(operation)) return;
                _ = await WaitForSessionTurnPhaseAsync(operation, submitted.TurnId, "WaitingForProject");
                if (!await AwaitPendingVoiceSubmissionAsync(operation)) return;
                var selected = await _api.ProvideSessionProjectAsync(
                    session.SessionId,
                    submitted.TurnId,
                    project.Id,
                    _lifetime.Token);
                if (!IsInputOperationCurrent(operation)) return;
                var selectedTurn = selected.Turns.Single(turn => turn.Id == submitted.TurnId);
                if (selectedTurn.Phase != "WaitingForConfirmation")
                {
                    throw new InvalidOperationException("编程任务没有进入确认状态，请稍后重试。 ");
                }

                if (!await AwaitPendingVoiceSubmissionAsync(operation)) return;
                var started = await _api.ConfirmSessionTurnAsync(
                    session.SessionId,
                    submitted.TurnId,
                    confirmed: true,
                    _lifetime.Token);
                if (!IsInputOperationCurrent(operation)) return;
                RenderSession(started);
                var taskId = started.Turns.Single(turn => turn.Id == submitted.TurnId).TaskId
                             ?? throw new InvalidOperationException("编程任务没有成功建立。 ");
                _selectedTaskId = taskId;
                await LoadTaskDetailsAsync(taskId, navigate: true, isCurrent: () => IsInputOperationCurrent(operation));
                if (!IsInputOperationCurrent(operation)) return;
                await RefreshAllAsync(showErrors: false);
            }
            finally { await CancelSupersededTurnAsync(operation, submitted); }
        }, isCurrent: () => IsInputOperationCurrent(operation));
    }

    private async Task<SessionSnapshotDto> WaitForSessionTurnPhaseAsync(
        InputOperation operation,
        Guid turnId,
        params string[] expectedPhases)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        SessionSnapshotDto? snapshot = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (!IsInputOperationCurrent(operation)) throw new OperationCanceledException();
            snapshot ??= await _api.GetCurrentSessionAsync(_lifetime.Token).WaitAsync(operation.Cancellation);
            if (!IsInputOperationCurrent(operation)) throw new OperationCanceledException();
            if (snapshot is not null && (snapshot.SessionId != operation.Session?.SessionId
                || snapshot.CoordinatorInstanceId != operation.Session?.CoordinatorInstanceId
                || snapshot.CoordinatorStartedAtUtc != operation.Session?.CoordinatorStartedAtUtc))
                throw new InvalidOperationException("话题已经变化，这次操作没有继续。 ");
            var turn = snapshot?.Turns.SingleOrDefault(item => item.Id == turnId);
            if (turn is not null && expectedPhases.Contains(turn.Phase, StringComparer.Ordinal))
            {
                return snapshot!;
            }

            if (turn?.Phase is "Failed" or "Cancelled" or "Interrupted")
            {
                throw new InvalidOperationException(
                    turn.FailureMessage ?? "编程任务没有进入可继续状态。 ");
            }

            snapshot = await _api.WaitForSessionUpdateAsync(
                snapshot?.ChangeVersion ?? -1,
                2_000,
                _lifetime.Token).WaitAsync(operation.Cancellation);
        }

        throw new TimeoutException("等待统一会话建立编程任务超时。 ");
    }

    private async Task RunCommandAsync(
        Func<Task> operation,
        CancellationToken operationToken = default,
        Func<bool>? isCurrent = null)
    {
        try
        {
            Mouse.OverrideCursor = Cursors.Wait;
            ErrorBanner.Visibility = Visibility.Collapsed;
            await operation();
        }
        catch (OperationCanceledException) when (
            _lifetime.IsCancellationRequested || operationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (isCurrent?.Invoke() == false) return;
            var error = UserFacingErrorMapper.Map(exception);
            ShowError($"{error.Message} {error.SuggestedAction}", exception);
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }
    }

    private async Task SubmitVoiceCommandAsync(
        string text,
        long? listeningGeneration,
        CancellationToken cancellationToken,
        InputOperation operation)
    {
        CancelPendingPointerGesture();
        var replyRequest = _voiceReplies.BeginInput(_currentSession, listeningGeneration,
            operation.CoordinatorInstanceId, operation.CoordinatorStartedAtUtc);
        var noticeOwner = _voiceSubmissionNoticeOwner = Guid.NewGuid();
        var submissionContext = _currentSession;
        await RunCommandAsync(async () =>
        {
            using var submission = new RevocableVoiceSubmission(
                receiptToken => _api.SubmitSessionInputAsync(new SessionInputRequestDto(
                    text,
                    "Voice",
                    $"desktop-voice-{Guid.NewGuid():N}",
                    submissionContext?.SessionId), receiptToken),
                (receipt, stopToken) => _api.CancelSessionTurnAsync(receipt.SessionId, receipt.TurnId, stopToken),
                cancellationToken,
                _lifetime.Token,
                (receipt, exception) => Dispatcher.BeginInvoke(() =>
                {
                    var current = _currentSession;
                    if (_forceClose || _lifetime.IsCancellationRequested || noticeOwner != _voiceSubmissionNoticeOwner
                        || current is null || submissionContext is null
                        || current.SessionId != submissionContext.SessionId
                        || current.CoordinatorInstanceId != submissionContext.CoordinatorInstanceId
                        || current.CoordinatorStartedAtUtc != submissionContext.CoordinatorStartedAtUtc
                        || (_voiceSession.ListeningGeneration is { } activeGeneration && activeGeneration != listeningGeneration)) return;
                    // Never attach an old submission's error to a newer Turn or topic.
                    var expectedTurn = receipt?.TurnId ?? submissionContext.Turns.LastOrDefault()?.Id;
                    if (current.Turns.LastOrDefault()?.Id != expectedTurn) return;
                    ShowError("已停止接收这条语音，但尚未确认上一条请求已停止。请查看当前任务并重试停止。", exception);
                }),
                confirmed: _ => Dispatcher.BeginInvoke(new Action(RefreshVoiceCancellationStatus)));
            SetInputRegistration(submission, operation);
            var submitted = await submission.WaitAsync();
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsInputOperationCurrent(operation, requireSameSession: submissionContext is not null)
                || _voiceSession.ListeningGeneration != listeningGeneration) return;
            _voiceReplies.AttachTurn(replyRequest, submitted);
            StatusBarText.Text = submitted.WasDuplicate
                ? "这条请求已经收到，正在继续处理。"
                : "已交给当前话题处理。";
            await RefreshSessionAsync();
        }, cancellationToken);
    }

    private async void ConfirmAssistantPlanButton_Click(object sender, RoutedEventArgs e)
    {
        CancelPendingVoiceInput();
        var operation = CaptureInputOperation();
        var session = _currentSession;
        var turn = session?.ForegroundTurn;
        if (session is null || turn is null)
        {
            return;
        }

        await RunCommandAsync(async () =>
        {
            if (!await AwaitPendingVoiceSubmissionAsync(operation)) return;
            if (_currentSession?.SessionId != session.SessionId || _currentSession.ForegroundTurn?.Id != turn.Id) return;
            SessionSnapshotDto snapshot = turn.Phase switch
            {
                "WaitingForProject" when SessionProjectComboBox.SelectedItem is ProjectDto project =>
                    await _api.ProvideSessionProjectAsync(session.SessionId, turn.Id, project.Id, _lifetime.Token),
                "WaitingForFile" => await ChooseAndProvideSessionFileAsync(session, turn, operation),
                "WaitingForWindow" => await _api.RetrySessionTurnAsync(
                    session.SessionId,
                    turn.Id,
                    _lifetime.Token),
                "WaitingForWindowConsent" => await _api.RespondSessionWindowConsentAsync(
                    session.SessionId,
                    turn.Id,
                    granted: true,
                    _lifetime.Token,
                    turn.WindowConsent?.ConsentId),
                "WaitingForConfirmation" => await _api.ConfirmSessionTurnAsync(
                    session.SessionId,
                    turn.Id,
                    confirmed: true,
                    _lifetime.Token),
                _ => throw new InvalidOperationException("这条请求当前不需要补充或确认。 ")
            };
            if (!IsInputOperationCurrent(operation))
            {
                await CancelSupersededTurnAsync(operation, new(session.SessionId, turn.Id, false));
                return;
            }
            RenderSession(snapshot);
        }, isCurrent: () => IsInputOperationCurrent(operation));
    }

    private async Task<SessionSnapshotDto> ChooseAndProvideSessionFileAsync(
        SessionSnapshotDto session,
        UnifiedSessionTurnDto turn,
        InputOperation operation)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择这一次要处理的文件",
            Multiselect = false,
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true)
        {
            return session;
        }

        if (!await AwaitPendingVoiceSubmissionAsync(operation)
            || _currentSession?.SessionId != session.SessionId || _currentSession.ForegroundTurn?.Id != turn.Id)
            return _currentSession ?? session;

        return await _api.ProvideSessionFileAsync(
            session.SessionId,
            turn.Id,
            dialog.FileName,
            _lifetime.Token);
    }

    private async void StopWindowObservationButton_Click(object sender, RoutedEventArgs e)
    {
        await CancelCurrentSessionTurnAsync();
    }

    private async void CancelAssistantPlanButton_Click(object sender, RoutedEventArgs e)
    {
        CancelPendingVoiceInput();
        var operation = CaptureInputOperation();
        var session = _currentSession;
        var turn = session?.ForegroundTurn;
        if (session is null || turn is null)
        {
            return;
        }

        await RunCommandAsync(async () =>
        {
            var snapshot = turn.Phase == "WaitingForWindowConsent"
                ? await _api.RespondSessionWindowConsentAsync(
                    session.SessionId,
                    turn.Id,
                    granted: false,
                    _lifetime.Token,
                    turn.WindowConsent?.ConsentId)
                : await _api.CancelSessionTurnAsync(
                    session.SessionId,
                    turn.Id,
                    _lifetime.Token);
            if (!IsInputOperationCurrent(operation)) return;
            RenderSession(snapshot);
            StatusBarText.Text = "已取消，不会继续执行。";
        }, isCurrent: () => IsInputOperationCurrent(operation));
    }

    private async void StopSessionButton_Click(object sender, RoutedEventArgs e) =>
        await CancelCurrentSessionTurnAsync();

    private async Task CancelCurrentSessionTurnAsync()
    {
        CancelPendingVoiceInput();
        var operation = CaptureInputOperation();
        CancelPendingPointerGesture();
        var session = _currentSession;
        var turn = session?.ForegroundTurn;
        if (session is null || turn is null)
        {
            return;
        }

        await RunCommandAsync(async () =>
        {
            StatusBarText.Text = "正在停止当前请求…";
            var snapshot = await _api.CancelSessionTurnAsync(
                session.SessionId,
                turn.Id,
                _lifetime.Token);
            if (!IsInputOperationCurrent(operation)) return;
            RenderSession(snapshot);
        }, isCurrent: () => IsInputOperationCurrent(operation));
    }

    private async void NewTopicButton_Click(object sender, RoutedEventArgs e)
    {
        CancelPendingVoiceInput();
        var operation = CaptureInputOperation();
        CancelPendingPointerGesture();
        await RunCommandAsync(async () =>
        {
            if (!await AwaitPendingVoiceSubmissionAsync(operation, allowRegistrationHandoff: true)) return;
            var snapshot = await StartSessionForOperationAsync(operation,
                "新话题",
                _lifetime.Token);
            if (!TryAdoptCreatedSession(ref operation, snapshot)) return;
            VoiceTranscriptText.Text = "等待你说话…";
            StatusBarText.Text = "已经开始一个新话题。";
        }, isCurrent: () => IsInputOperationCurrent(operation));
    }

    private async void StartVoiceButton_Click(object sender, RoutedEventArgs e)
    {
        VoiceTranscriptText.Text = "等待你说话…";
        await _voiceSession.StartAsync(_lifetime.Token);
    }

    private async void StopVoiceButton_Click(object sender, RoutedEventArgs e)
    {
        CancelPendingVoiceInput();
        await _voiceSession.StopAsync();
    }

    private void Voice_PartialTextChanged(object? sender, VoicePartialText partial)
    {
        var generation = _voiceSession.ListeningGeneration;
        if (generation is null) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (_voiceSession.ListeningGeneration == generation
                && _voiceReception.AcceptsPartial(generation, partial.RecognitionGeneration)
                && !string.IsNullOrWhiteSpace(partial.Text))
            {
                var echo = IsAssistantSpeechEcho(partial.Text);
                if (!echo) _localVoiceReply.Clear();
                if (!echo && VoiceReceptionGate.IsStopPhrase(partial.Text))
                {
                    // A complete Stop cannot be appended to an unfinished search or
                    // deferred as a possible permission. Its final uses the existing
                    // Host cancellation path, without creating a chat request.
                    CancelPendingVoiceInput();
                    VoiceTranscriptText.Text = partial.Text;
                    VoiceStateText.Text = "正在停止上一条";
                    PresentIslandPartial(partial.Text);
                    return;
                }
                if (!echo && VisualSearchVoiceConsentGate.ShouldDeferPartial(partial.Text, _currentSession))
                {
                    // ASR may revise a permission prefix. Wait for the final text to
                    // distinguish clarification from a replacement; explicit Stop wins.
                    VoiceTranscriptText.Text = partial.Text;
                    _islandVoiceText = partial.Text;
                    _islandPhaseOverride = null;
                    RenderAssistantSurface();
                    return;
                }
                if (!echo && VoiceReceptionGate.IsCallPrefix(partial.Text))
                {
                    // Wait for the final: this may be only a call, or may grow into
                    // an ordinary question/action which keeps the normal routing.
                    VoiceTranscriptText.Text = partial.Text;
                    PresentIslandPartial(partial.Text);
                    return;
                }
                var preview = echo ? new VoiceSegmentPreview(partial.Text, false)
                    : _voiceSegments.ObservePartial(partial.Text, generation.Value, partial.RecognitionGeneration);
                var text = preview.Text;
                VoiceTranscriptText.Text = text;
                if ((SpeechEchoMatcher.CanInterrupt(partial.Text)
                    || VisualSearchVoiceConsentBinding.From(_currentSession) is not null
                        && VisualSearchVoiceConsentGate.IsImmediateInterruption(partial.Text)) && !echo && !preview.ContinuesPhrase)
                {
                    // A partial can still grow into a connected search clause. Keep only
                    // its short recognition prefix until the final disambiguates it.
                    CancelPendingVoiceInput(preserveVoiceSegments: true);
                    VoiceStateText.Text = "听到你插话，正在停止上一条";
                }
                PresentIslandPartial(text);
            }
        });
    }

    private void Voice_UtteranceRecognized(object? sender, VoiceUtterance utterance)
    {
        var generation = _voiceSession.ListeningGeneration;
        if (generation is null) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (_voiceSession.ListeningGeneration != generation || IsAssistantSpeechEcho(utterance.Text)) return;
            if (TryHandleVisualConsentUtterance(utterance, generation.Value)) return;
            switch (_voiceReception.Classify(utterance.Text, generation, utterance.RecognitionGeneration))
            {
                case VoiceReceptionDecision.Submit:
                    QueueVoiceUtterance(utterance with { Text = _voiceSegments.AcceptFinal(
                        utterance.Text, generation.Value, utterance.RecognitionGeneration) });
                    break;
                case VoiceReceptionDecision.Rest: _ = ChangeLocalReceptionAsync(standby: true); break;
                case VoiceReceptionDecision.Stop: _ = CancelCurrentSessionTurnAsync(); break;
                case VoiceReceptionDecision.Call:
                    if (!IsAssistantSpeechEcho(VoiceReceptionGate.CanonicalWakePhrase(utterance.Text)!))
                        _ = AcknowledgeCallAsync(utterance.Text);
                    break;
                case VoiceReceptionDecision.Wake:
                    // ASR may spell the same sound as 元书/袁叔. Check the canonical
                    // wake phrase too so that a spoken explanation cannot wake itself.
                    if (!IsAssistantSpeechEcho(VoiceReceptionGate.CanonicalWakePhrase(utterance.Text)!))
                        _ = ChangeLocalReceptionAsync(standby: false);
                    break;
            }
        });
    }

    private void QueueVoiceUtterance(VoiceUtterance utterance)
    {
        if (!_voiceSession.IsListening) return;
        var text = utterance.Text.Trim();
        if (text.Length == 0 || IsAssistantSpeechEcho(text))
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (string.Equals(text, _lastVoiceUtterance, StringComparison.Ordinal)
            && now - _lastVoiceUtteranceAt < TimeSpan.FromSeconds(3))
        {
            return;
        }

        _lastVoiceUtterance = text;
        _lastVoiceUtteranceAt = now;
        RevokeVisualVoiceConsent();
        InvalidateInputOperation();
        VoiceTranscriptText.Text = text;
        AssistantPlanBorder.Visibility = Visibility.Collapsed;
        AssistantResultBorder.Visibility = Visibility.Collapsed;
        ErrorBanner.Visibility = Visibility.Collapsed;
        _queuedVoiceUtterance = utterance with { Text = text };
        _queuedVoiceGeneration = _voiceSession.ListeningGeneration;
        CancelVoiceFeedback();
        _activeVoiceCommandCancellation?.Cancel();
        PresentIslandFinal(text);
        if (_voiceCommandBusy)
        {
            VoiceStateText.Text = "已收到新指令，正在停止上一条";
            StatusBarText.Text = $"新指令优先：{text}";
            return;
        }

        _ = ProcessVoiceQueueAsync();
    }

    private async Task ProcessVoiceQueueAsync()
    {
        _voiceCommandBusy = true;
        try
        {
            while (_queuedVoiceUtterance is not null)
            {
                var operation = CaptureInputOperation();
                if (!await AwaitPendingVoiceSubmissionAsync(operation, allowRegistrationHandoff: true))
                {
                    if (operation.Version == _replacementInputVersion)
                    {
                        _queuedVoiceUtterance = null;
                        _queuedVoiceGeneration = null;
                    }
                    continue;
                }
                if (_queuedVoiceUtterance is not { } utterance) continue;
                var generation = _queuedVoiceGeneration;
                _queuedVoiceUtterance = null;
                if (generation is null || generation != _voiceSession.ListeningGeneration) continue;
                _activeVoiceCommandCancellation?.Dispose();
                _activeVoiceCommandCancellation =
                    CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                var cancellationToken = _activeVoiceCommandCancellation.Token;
                try
                {
                    await AwaitIslandFinalPresentationAsync(operation, cancellationToken);
                    VoiceStateText.Text = "已听懂，正在处理";
                    StatusBarText.Text = $"已听到：{utterance.Text}";
                    _islandPhaseOverride = null;
                    RenderIslandSurface();
                    await SubmitVoiceCommandAsync(utterance.Text, generation, cancellationToken, operation);
                }
                catch (OperationCanceledException) when (
                    (cancellationToken.IsCancellationRequested || operation.Cancellation.IsCancellationRequested
                     || !IsInputOperationCurrent(operation))
                    && !_lifetime.IsCancellationRequested)
                {
                    StatusBarText.Text = _queuedVoiceUtterance is null
                        ? "上一条指令已停止。"
                        : "上一条指令已停止，正在处理你的新指令。";
                }
            }
        }
        finally
        {
            _activeVoiceCommandCancellation?.Dispose();
            _activeVoiceCommandCancellation = null;
            _voiceCommandBusy = false;
            RenderVoiceStatus();
        }
    }

    private bool IsAssistantSpeechEcho(string text)
    {
        var reference = _activeSpeechText;
        if (string.IsNullOrWhiteSpace(reference)
            && DateTimeOffset.UtcNow - _lastAssistantSpeechEndedAt < TimeSpan.FromSeconds(3))
        {
            reference = _lastAssistantSpeechText;
        }

        return !string.IsNullOrWhiteSpace(reference)
               && SpeechEchoMatcher.IsLikelyAssistantEcho(text, reference);
    }

    private void VoiceSession_StateChanged(object? sender, VoiceSessionStatus status) =>
        Dispatcher.BeginInvoke(() =>
        {
            SynchronizeVoiceReception();
            if (_voiceSession.ListeningGeneration is null) CancelVoiceFeedback();
            RenderVoiceStatus();
        });

    private void RenderVoiceStatus()
    {
        var status = _voiceSession.Status;
        VoiceStateText.Text = status.Message;
        if (_voiceSession.IsListening)
            VoiceStateText.Text = _voiceReception.Mode switch
            {
                VoiceReceptionMode.Standby => "本地待命 · 说“元枢”唤醒",
                VoiceReceptionMode.Transitioning => _receptionTransitionText,
                _ => status.Message
            };
        StartVoiceButton.IsEnabled = status.CanStart;
        StartVoiceButton.Content = status.State == VoiceSessionState.Faulted ? "重试语音" : "开始语音";
        StopVoiceButton.IsEnabled = status.CanStop;
        SettingsVoiceStatusText.Text = VoiceStateText.Text;
        DashboardCodexVersionText.Text = status.State switch
        {
            VoiceSessionState.Listening => "正在聆听",
            VoiceSessionState.HearingSpeech => "正在识别",
            VoiceSessionState.Starting => "正在启动",
            VoiceSessionState.Stopping => "正在停止",
            VoiceSessionState.Faulted or VoiceSessionState.CleanupFailed => "需要检查",
            _ => "未启动"
        };
        var color = status.State switch
        {
            VoiceSessionState.Listening => "#1F8A6A",
            VoiceSessionState.HearingSpeech => "#2E6DD8",
            VoiceSessionState.Faulted or VoiceSessionState.CleanupFailed => "#C94C4C",
            _ => "#A56210"
        };
        VoiceStateDot.Background = BrushFrom(color);
        RenderAssistantSurface();
        DashboardCodexVersionText.Foreground = BrushFrom(color);
    }

    private async void ReadAssistantResultButton_Click(object sender, RoutedEventArgs e)
    {
        var text = AssistantResultText.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        CancelVoiceFeedback();
        await SpeakAssistantAsync(text);
    }

    private void CancelVoiceFeedback()
    {
        _voiceReplies.Invalidate();
        _speechCancellation?.Cancel();
    }

    private void CancelPendingVoiceInput(bool invalidateOperation = true, bool preserveQueuedInput = false,
        bool preserveVoiceSegments = false)
    {
        RevokeVisualVoiceConsent();
        if (!preserveVoiceSegments) _voiceSegments.Reset();
        if (invalidateOperation)
        {
            _islandPhaseOverride = null;
            CancelReceptionTransition();
            InvalidateInputOperation();
        }
        if (!preserveQueuedInput)
        {
            _queuedVoiceUtterance = null;
            _queuedVoiceGeneration = null;
        }
        _activeVoiceCommandCancellation?.Cancel();
        if (_latestInputRegistration?.IsRegistrationBoundaryPending == true)
            StatusBarText.Text = PendingVoiceCancellationMessage;
        CancelVoiceFeedback();
    }

    private sealed class InputOperation(long version, SessionSnapshotDto? session, CancellationToken cancellation)
    {
        public long Version { get; } = version;
        public CancellationToken Cancellation { get; } = cancellation;
        public SessionSnapshotDto? Session { get; set; } = session;
        // A clean profile has no Session yet. Capture the coordinator independently
        // before its first write, so its own first Session is not a Host restart.
        public string? CoordinatorInstanceId { get; set; } = session?.CoordinatorInstanceId;
        public DateTimeOffset? CoordinatorStartedAtUtc { get; set; } = session?.CoordinatorStartedAtUtc;
    }

    private async Task<SessionSnapshotDto> StartSessionForOperationAsync(
        InputOperation operation, string title, CancellationToken cancellationToken)
    {
        using var input = CancellationTokenSource.CreateLinkedTokenSource(operation.Cancellation, cancellationToken);
        IInputRegistrationBoundary? registration = null;
        using var submission = new RevocableInputSubmission<SessionSnapshotDto>(
            receiptToken => _api.StartNewSessionAsync(title, receiptToken),
            // Creating a topic does not itself execute work. Its acknowledgement is
            // still required before any newer write may register at the Host.
            (_, _) => Task.CompletedTask,
            input.Token, _lifetime.Token,
            confirmed: _ => Dispatcher.BeginInvoke(new Action(() =>
            {
                if (ReferenceEquals(_latestInputRegistration, registration)
                    && StatusBarText.Text == PendingVoiceCancellationMessage)
                    StatusBarText.Text = "已停止后续操作，话题登记已完成。";
            })));
        registration = submission;
        SetInputRegistration(submission, operation);
        _voiceSubmissionNoticeOwner = Guid.NewGuid();
        return await submission.WaitAsync();
    }

    private async Task<SessionTurnCommandResultDto> SubmitInputForOperationAsync(
        InputOperation operation, SessionInputRequestDto request, CancellationToken cancellationToken)
    {
        using var input = CancellationTokenSource.CreateLinkedTokenSource(operation.Cancellation, cancellationToken);
        IInputRegistrationBoundary? registration = null;
        using var submission = new RevocableInputSubmission<SessionTurnCommandResultDto>(
            receiptToken => _api.SubmitSessionInputAsync(request, receiptToken),
            (receipt, stopToken) => _api.CancelSessionTurnAsync(receipt.SessionId, receipt.TurnId, stopToken),
            input.Token, _lifetime.Token,
            confirmed: receipt => Dispatcher.BeginInvoke(new Action(() =>
                RefreshInputCancellationStatus(registration, receipt))));
        registration = submission;
        SetInputRegistration(submission, operation);
        _voiceSubmissionNoticeOwner = Guid.NewGuid();
        return await submission.WaitAsync();
    }

    private void InvalidateInputOperation()
    {
        _localVoiceReply.Clear();
        _replacementInputVersion++;
        var previous = _replacementInputCancellation;
        _replacementInputCancellation = new CancellationTokenSource();
        previous.Cancel();
        previous.Dispose();
    }

    private void SetInputRegistration(IInputRegistrationBoundary registration, InputOperation operation)
    {
        _latestInputRegistration = registration;
        _registrationCoordinator = (operation.CoordinatorInstanceId, operation.CoordinatorStartedAtUtc);
    }

    private void ReconcileInputCancellation(SessionSnapshotDto? snapshot)
    {
        // A retry of Stop (or a later trusted projection) can confirm a cancellation
        // whose original RPC reply was lost. Reconcile only the exact Host and receipt.
        if (snapshot is null || _latestInputRegistration is not { } registration
            || _registrationCoordinator.InstanceId != snapshot.CoordinatorInstanceId
            || _registrationCoordinator.StartedAtUtc != snapshot.CoordinatorStartedAtUtc
            || registration.RegistrationReceipt is not SessionTurnCommandResultDto receipt
            || receipt.SessionId != snapshot.SessionId
            || snapshot.Turns.SingleOrDefault(turn => turn.Id == receipt.TurnId)?.Phase != "Cancelled") return;
        registration.AcknowledgeCancellation(receipt);
    }

    private InputOperation CaptureInputOperation() =>
        new(_replacementInputVersion, _currentSession, _replacementInputCancellation.Token);

    private bool IsInputOperationCurrent(InputOperation operation, bool requireSameSession = true) =>
        operation.Version == _replacementInputVersion && !_forceClose && !_lifetime.IsCancellationRequested
        && (_currentSession is null || (operation.CoordinatorInstanceId == _currentSession.CoordinatorInstanceId
            && operation.CoordinatorStartedAtUtc == _currentSession.CoordinatorStartedAtUtc))
        && (!requireSameSession || operation.Session?.SessionId == _currentSession?.SessionId);

    private bool TryAdoptCreatedSession(ref InputOperation operation, SessionSnapshotDto created)
    {
        // A poll may have already projected our own newly created Session. Only this
        // exact reply can advance the captured operation; never adopt a newer input.
        if (!IsInputOperationCurrent(operation, requireSameSession: false)
            || created.CoordinatorInstanceId != operation.CoordinatorInstanceId
            || created.CoordinatorStartedAtUtc != operation.CoordinatorStartedAtUtc
            || (_currentSession?.SessionId != operation.Session?.SessionId && _currentSession?.SessionId != created.SessionId))
            return false;
        operation.Session = created;
        RenderSession(created);
        return IsInputOperationCurrent(operation);
    }

    private async Task CancelSupersededTurnAsync(InputOperation operation, SessionTurnCommandResultDto receipt, bool force = false)
    {
        if (!force && IsInputOperationCurrent(operation)) return;
        // Cancellation is scoped to this receipt, never the current/newer Turn.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await _api.CancelSessionTurnAsync(receipt.SessionId, receipt.TurnId, deadline.Token).WaitAsync(deadline.Token); }
        catch (Exception exception)
        {
            // The Host remains the source of truth: never report cancellation as
            // successful and never attach this stale error to a newer input.
            Trace.TraceWarning("Superseded input cancellation was not confirmed ({0}).", exception.GetType().Name);
        }
    }

    private async Task<bool> AwaitPendingVoiceSubmissionAsync(InputOperation operation, bool allowRegistrationHandoff = false)
    {
        if (!IsInputOperationCurrent(operation, requireSameSession: !allowRegistrationHandoff)) return false;
        if (operation.CoordinatorInstanceId is null)
        {
            var identity = await _api.WaitForSessionProjectionAsync(new SessionProjectionCursorDto(
                "", DateTimeOffset.MinValue, Guid.Empty, -1, 0, 0), _lifetime.Token);
            if (operation.Version != _replacementInputVersion || _forceClose || _lifetime.IsCancellationRequested) return false;
            operation.CoordinatorInstanceId = identity.CoordinatorInstanceId;
            operation.CoordinatorStartedAtUtc = identity.CoordinatorStartedAtUtc;
            if (!IsInputOperationCurrent(operation, requireSameSession: !allowRegistrationHandoff)) return false;
        }
        var pending = _latestInputRegistration;
        if (pending is not null)
        {
            try
            {
                // Shared by voice, text, New Topic and selected actions. Waiting only in
                // the voice queue lets an older RPC register after a newer UI request.
                await pending.WaitForRegistrationBoundaryAsync();
            }
            catch (Exception exception)
            {
                if (pending.WasDispatched && !pending.HasConfirmedCancellation)
                {
                    if (IsInputOperationCurrent(operation))
                        ShowError("上一条请求尚未确认停止，这次新操作没有提交。请查看当前任务并重试停止。", exception);
                    return false;
                }
            }
        }
        if (allowRegistrationHandoff && pending?.RegistrationReceipt is { } receipt
            && IsInputOperationCurrent(operation, requireSameSession: false))
        {
            // A superseded creation may register its Session while the user's newer
            // New Topic waits. Accept only that acknowledged Session as an intermediate
            // binding; the newer operation still retains its original input generation.
            var priorSessionId = receipt switch
            {
                SessionSnapshotDto created => created.SessionId,
                SessionTurnCommandResultDto submitted => submitted.SessionId,
                _ => (Guid?)null
            };
            if (priorSessionId != operation.Session?.SessionId)
            {
                var registered = await _api.GetCurrentSessionAsync(_lifetime.Token);
                if (registered is null) return false;
                if (registered.SessionId == priorSessionId)
                {
                    if (!TryAdoptCreatedSession(ref operation, registered)) return false;
                }
                else if (registered.SessionId != operation.Session?.SessionId) return false;
            }
        }
        // Stop, a newer input, a new topic or a Host change wins while this request waits.
        return IsInputOperationCurrent(operation);
    }

    private void RefreshVoiceCancellationStatus()
    {
        if (_latestInputRegistration is { HasConfirmedCancellation: true, RegistrationReceipt: SessionTurnCommandResultDto receipt })
            RefreshInputCancellationStatus(_latestInputRegistration, receipt);
    }

    private void RefreshInputCancellationStatus(IInputRegistrationBoundary? registration, SessionTurnCommandResultDto receipt)
    {
        if (_forceClose || _lifetime.IsCancellationRequested || StatusBarText.Text != PendingVoiceCancellationMessage
            || !ReferenceEquals(_latestInputRegistration, registration)
            || _currentSession is not { } session || session.SessionId != receipt.SessionId) return;
        var latest = session.Turns.OrderBy(item => item.SequenceNumber).LastOrDefault();
        if (latest?.Id == receipt.TurnId && latest.Phase == "Cancelled")
            StatusBarText.Text = "上一条请求已取消。";
    }

    private Task SpeakAssistantAsync(string text, Guid? automaticRequest = null, bool localReceptionReply = false) =>
        SpeakAssistantCoreAsync(text, automaticRequest, localReceptionReply, localCallReply: false);

    private async Task SpeakAssistantCoreAsync(string text, Guid? automaticRequest, bool localReceptionReply,
        bool localCallReply)
    {
        if (automaticRequest is { } request
            && !_voiceReplies.IsCurrent(request, _currentSession, _voiceSession.ListeningGeneration)) return;
        _speechCancellation?.Cancel();
        var speechCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _speechCancellation = speechCancellation;
        _localCallSpeech = localCallReply;
        _automaticSpeechRequest = automaticRequest;
        ReadAssistantResultButton.IsEnabled = false;
        _activeSpeechText = text;
        var receptionMode = _voiceReception.Mode == VoiceReceptionMode.Standby
            ? VoiceReceptionMode.Standby : VoiceReceptionMode.Direct;
        RenderAssistantSurface();
        try
        {
            if (!localReceptionReply) await ResetSpeechReceptionAsync(speechCancellation, receptionMode);
            speechCancellation.Token.ThrowIfCancellationRequested();
            await _speakAsync(text, speechCancellation.Token);
        }
        catch (OperationCanceledException) when (speechCancellation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (ReferenceEquals(_speechCancellation, speechCancellation)
                && !speechCancellation.IsCancellationRequested)
                ShowError("文字回复已完成，但语音播放失败，请检查电脑声音输出后点击“朗读结果”。", exception);
        }
        finally
        {
            if (!localReceptionReply && ReferenceEquals(_speechCancellation, speechCancellation)
                && !speechCancellation.IsCancellationRequested)
            {
                try { await ResetSpeechReceptionAsync(speechCancellation, receptionMode); }
                catch (OperationCanceledException) { }
                catch (Exception exception)
                {
                    // ResetSpeechReceptionAsync already arbitrates stopping by its
                    // transition ownership. A late fault must not stop a new microphone
                    // session or overwrite the new playback's status after this await.
                    if (ReferenceEquals(_speechCancellation, speechCancellation)
                        && !speechCancellation.IsCancellationRequested
                        && _voiceSession.ListeningGeneration is null)
                        ShowError("语音接收未能安全恢复，麦克风已停止，请重新开始语音。", exception);
                }
            }
            // A cancelled older playback may finish after a new one. It owns only its own
            // cancellation source and must never clear or dispose the current playback.
            if (ReferenceEquals(_speechCancellation, speechCancellation))
            {
                _lastAssistantSpeechText = text;
                _lastAssistantSpeechEndedAt = DateTimeOffset.UtcNow;
                _activeSpeechText = null;
                _localCallSpeech = false;
                _speechCancellation = null;
                _automaticSpeechRequest = null;
                ReadAssistantResultButton.IsEnabled = true;
            }
            speechCancellation.Dispose();
            RenderVoiceStatus();
        }
    }

    private async void NewTaskSubmitButton_Click(object sender, RoutedEventArgs e)
    {
        await CreateTaskAsync(
            NewTaskProjectComboBox.SelectedItem as ProjectDto,
            NewTaskInstructionTextBox.Text,
            NewTaskTitleTextBox.Text);
        NewTaskInstructionTextBox.Clear();
        NewTaskTitleTextBox.Clear();
    }

    private void NewTaskProjectComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        TaskDirectoryNoticeText.Text = NewTaskProjectComboBox.SelectedItem is ProjectDto project
            ? $"编程助手将在这个项目目录内执行任务：\n{project.RootPath}"
            : "编程助手将在所选项目目录内执行任务。";
    }

    private async void AddProjectButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择要授权给编程助手的 Git 项目",
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        await RunCommandAsync(async () =>
        {
            await _api.AddProjectAsync(new AddProjectRequestDto(dialog.FolderName), _lifetime.Token);
            await RefreshAllAsync(showErrors: false);
        });
    }

    private void ProjectsListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProjectsListView.SelectedItem is not ProjectRow project)
        {
            RevokeProjectButton.IsEnabled = false;
            SelectedProjectDetailText.Text = "选择项目查看详情。";
            return;
        }

        SelectedProjectDetailText.Text =
            $"{project.Name}  ·  {project.GitStatus}  ·  {project.RecentTaskCount} 个历史任务";
        RevokeProjectButton.IsEnabled = project.AuthorizationState == "Authorized" && !project.HasActiveTask;
    }

    private async void RevokeProjectButton_Click(object sender, RoutedEventArgs e)
    {
        if (ProjectsListView.SelectedItem is not ProjectRow project)
        {
            return;
        }

        if (MessageBox.Show(
                $"删除“{project.Name}”的授权？\n\n不会删除项目文件或历史任务。",
                "删除项目授权",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        await RunCommandAsync(async () =>
        {
            await _api.RevokeProjectAsync(project.Id, _lifetime.Token);
            await RefreshAllAsync(showErrors: false);
        });
    }

    private async void CurrentTaskCancelButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedTaskId is not { } taskId)
        {
            return;
        }

        if (MessageBox.Show(
                "取消当前任务？编程助手及其子进程会被停止。",
                "取消任务",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        await RunCommandAsync(async () =>
        {
            await _api.CancelTaskAsync(taskId, cancellationToken: _lifetime.Token);
            await LoadTaskDetailsAsync(taskId, navigate: false);
        });
    }

    private async void ContinueTaskButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedTaskId is not { } taskId || string.IsNullOrWhiteSpace(DecisionAnswerTextBox.Text))
        {
            ShowError("请输入回答后再继续任务。", null);
            return;
        }

        var answer = DecisionAnswerTextBox.Text;
        CancelPendingVoiceInput();
        var operation = CaptureInputOperation();
        await RunCommandAsync(async () =>
        {
            if (!await AwaitPendingVoiceSubmissionAsync(operation)) return;
            await _api.ContinueTaskAsync(taskId, answer, cancellationToken: _lifetime.Token);
            if (!IsInputOperationCurrent(operation)) return;
            DecisionAnswerTextBox.Clear();
            await LoadTaskDetailsAsync(taskId, navigate: false, isCurrent: () => IsInputOperationCurrent(operation));
        }, isCurrent: () => IsInputOperationCurrent(operation));
    }

    private async void ClearHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(
                "清理所有已经结束的任务历史？\n\n项目文件和正在运行的任务不会被删除。",
                "清理历史记录",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        await RunCommandAsync(async () =>
        {
            var count = await _api.ClearHistoryAsync(_lifetime.Token);
            _selectedTaskId = null;
            MessageBox.Show($"已清理 {count} 条任务历史。", "清理完成");
            await RefreshAllAsync(showErrors: false);
        });
    }

    private async void OpenDesktopApplicationButton_Click(object sender, RoutedEventArgs e)
    {
        if (DesktopApplicationComboBox.SelectedItem is not DesktopApplicationDto application)
        {
            ShowError("请选择一个允许打开的应用。", null);
            return;
        }

        if (MessageBox.Show(
                $"只授权本次操作：打开“{application.DisplayName}”。\n\n不会输入内容、点击控件或执行其他操作。",
                "确认打开应用",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        await ExecuteDesktopActionAsync(
            $"启动“{application.DisplayName}”",
            expectedIntentKind: "OpenApplication",
            expectedTarget: application.Id);
    }

    private async void NewConversationButton_Click(object sender, RoutedEventArgs e)
    {
        CancelPendingVoiceInput();
        var operation = CaptureInputOperation();
        CancelPendingPointerGesture();
        await RunCommandAsync(async () =>
        {
            if (!await AwaitPendingVoiceSubmissionAsync(operation, allowRegistrationHandoff: true)) return;
            var session = await StartSessionForOperationAsync(operation, "新话题", _lifetime.Token);
            if (!TryAdoptCreatedSession(ref operation, session)) return;
            ConversationInputTextBox.Clear();
            ConversationInputTextBox.Focus();
        }, isCurrent: () => IsInputOperationCurrent(operation));
    }

    private void ConversationListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
    }

    private async void SendConversationButton_Click(object sender, RoutedEventArgs e)
    {
        CancelPendingVoiceInput();
        var operation = CaptureInputOperation();
        CancelPendingPointerGesture();
        var session = _currentSession;
        if (session is null)
        {
            ShowError("请先新建一个对话。", null);
            return;
        }

        var message = ConversationInputTextBox.Text;
        if (string.IsNullOrWhiteSpace(message))
        {
            ShowError("请输入想聊的内容。", null);
            return;
        }

        await RunCommandAsync(async () =>
        {
            if (!await AwaitPendingVoiceSubmissionAsync(operation, allowRegistrationHandoff: true)) return;
            var memoryItems = ConversationMemorySelectionList.SelectedItems
                .OfType<MemoryRow>()
                .Select(row => new MemoryOutboundItemReferenceDto(row.Item.Id, row.Item.Version))
                .ToArray();
            if (memoryItems.Length > 8)
            {
                throw new InvalidOperationException("每个 Turn 最多只能选择 8 条长期记忆。");
            }

            var submitted = await SubmitInputForOperationAsync(operation,
                new SessionInputRequestDto(
                    message,
                    "Text",
                    $"desktop-chat-{Guid.NewGuid():N}",
                    operation.Session!.SessionId,
                    MemoryItems: memoryItems),
                _lifetime.Token);
            if (!IsInputOperationCurrent(operation))
            {
                await CancelSupersededTurnAsync(operation, submitted);
                return;
            }
            _isRenderingMemoryConsent = true;
            try
            {
                ConversationInputTextBox.Clear();
                ConversationMemorySelectionList.UnselectAll();
            }
            finally
            {
                _isRenderingMemoryConsent = false;
            }

            await RefreshSessionAsync();
        }, isCurrent: () => IsInputOperationCurrent(operation));
    }

    private async void ConfirmMemoryOutboundButton_Click(object sender, RoutedEventArgs e) =>
        await RespondMemoryOutboundConsentAsync(confirmed: true);

    private async void DeclineMemoryOutboundButton_Click(object sender, RoutedEventArgs e) =>
        await RespondMemoryOutboundConsentAsync(confirmed: false);

    private async void AskPointerQuestionButton_Click(object sender, RoutedEventArgs e)
    {
        CancelPendingVoiceInput();
        var operation = CaptureInputOperation();
        CancelPendingPointerGesture();
        var session = _currentSession;
        var question = ConversationInputTextBox.Text.Trim();
        if (session is null || question.Length == 0)
        {
            ShowError("请先输入要询问当前指针位置的问题。", null);
            return;
        }
        if (ConversationMemorySelectionList.SelectedItems.Count > 0)
        {
            ShowError("指针区域提问不能同时携带长期记忆，请先清除记忆选择。", null);
            return;
        }
        if (MessageBox.Show(
                "只授权这一次本机读取：确认后，请在 30 秒内切到目标应用，把指针放到要问的位置，按 Ctrl+Alt+F8。\n\n只读取该前台窗口的一小块区域并在本机识别文字。此时不会发送给 AI；之后会显示完整文字预览，由你再次确认。",
                "确认本机区域读取", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        await RunCommandAsync(async () =>
        {
            if (!await AwaitPendingVoiceSubmissionAsync(operation)) return;
            ArmPointerGesture(DateTimeOffset.UtcNow.AddSeconds(30),
                "请在 30 秒内切到目标应用，把指针放到要问的位置，按 Ctrl+Alt+F8。现在尚未读取屏幕。",
                () => _currentSession?.SessionId == session.SessionId
                    && IsInputOperationCurrent(operation)
                    && _currentSession?.ForegroundTurn?.Id == session.ForegroundTurn?.Id
                    && ConversationInputTextBox.Text.Trim() == question
                    && ConversationMemorySelectionList.SelectedItems.Count == 0,
                async token =>
                {
                    if (!await AwaitPendingVoiceSubmissionAsync(operation)) return;
                    token.ThrowIfCancellationRequested();
                    var anchor = await _api.PreparePointerRegionAsync(new PreparePointerRegionRequestDto(true), token);
                    await _pointerRequestCancellation.BindAsync(
                        cancellation => _api.CancelPointerRegionAsync(anchor.AnchorId, cancellation), token);
                    var submitted = false;
                    try
                    {
                        token.ThrowIfCancellationRequested();
                        if (!await AwaitPendingVoiceSubmissionAsync(operation)) return;
                        token.ThrowIfCancellationRequested();
                        var result = await SubmitInputForOperationAsync(operation, new SessionInputRequestDto(
                            question, "Text", $"desktop-pointer-answer-{Guid.NewGuid():N}",
                            session.SessionId, PointerAnchorId: anchor.AnchorId), token);
                        submitted = true;
                        await _pointerRequestCancellation.BindAsync(
                            cancellation => _api.CancelSessionTurnAsync(session.SessionId, result.TurnId, cancellation), token);
                        token.ThrowIfCancellationRequested();
                        if (!IsInputOperationCurrent(operation))
                        {
                            await CancelSupersededTurnAsync(operation, result);
                            return;
                        }
                        _isRenderingMemoryConsent = true;
                        try { ConversationInputTextBox.Clear(); }
                        finally { _isRenderingMemoryConsent = false; }
                        await RefreshSessionAsync();
                    }
                    finally
                    {
                        if (!submitted)
                        {
                            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                            await _api.CancelPointerRegionAsync(anchor.AnchorId, cleanup.Token).WaitAsync(cleanup.Token);
                        }
                    }
                });
        }, isCurrent: () => IsInputOperationCurrent(operation));
    }

    private async void ConfirmPointerAnswerButton_Click(object sender, RoutedEventArgs e) =>
        await RespondPointerAnswerConsentAsync(confirmed: true);

    private async void DeclinePointerAnswerButton_Click(object sender, RoutedEventArgs e) =>
        await RespondPointerAnswerConsentAsync(confirmed: false);

    private async Task RespondPointerAnswerConsentAsync(bool confirmed)
    {
        CancelPendingVoiceInput();
        var operation = CaptureInputOperation();
        CancelPendingPointerGesture(cancelRequest: false);
        var session = _currentSession;
        var consent = session?.PointerAnswerConsents?
            .SingleOrDefault(item => item.TurnId == session.ForegroundTurn?.Id);
        if (session is null || consent is null) return;

        await RunCommandAsync(async () =>
        {
            if (!confirmed)
            {
                var cancelled = await _api.CancelSessionTurnAsync(session.SessionId, consent.TurnId, _lifetime.Token);
                if (IsInputOperationCurrent(operation)) RenderSession(cancelled);
                return;
            }
            if (!await AwaitPendingVoiceSubmissionAsync(operation)) return;
            ArmPointerGesture(consent.ExpiresAtUtc,
                "已确认这份文字预览。请在预览过期前切回原目标窗口，按 Ctrl+Alt+F8 发送这一次；不会重新读取图片。",
                () => _currentSession?.SessionId == session.SessionId
                    && IsInputOperationCurrent(operation)
                    && _currentSession?.ForegroundTurn?.Id == consent.TurnId
                    && _currentSession?.PointerAnswerConsents?.Any(item => item.ConsentId == consent.ConsentId
                        && item.PreviewHash == consent.PreviewHash) == true,
                async token =>
                {
                    if (!await AwaitPendingVoiceSubmissionAsync(operation)) return;
                    token.ThrowIfCancellationRequested();
                    await _pointerRequestCancellation.BindAsync(
                        cancellation => _api.CancelSessionTurnAsync(session.SessionId, consent.TurnId, cancellation), token);
                    token.ThrowIfCancellationRequested();
                    if (!await AwaitPendingVoiceSubmissionAsync(operation)) return;
                    token.ThrowIfCancellationRequested();
                    var snapshot = await _api.ConfirmPointerAnswerAsync(session.SessionId,
                        consent.TurnId, consent.ConsentId, consent.PreviewHash, true, token);
                    token.ThrowIfCancellationRequested();
                    if (!IsInputOperationCurrent(operation))
                    {
                        await CancelSupersededTurnAsync(operation, new(session.SessionId, consent.TurnId, false));
                        return;
                    }
                    RenderSession(snapshot);
                });
        }, isCurrent: () => IsInputOperationCurrent(operation));
    }
    private async Task RespondMemoryOutboundConsentAsync(bool confirmed)
    {
        CancelPendingVoiceInput();
        var operation = CaptureInputOperation();
        var session = _currentSession;
        var consent = session?.MemoryOutboundConsents?
            .SingleOrDefault(item => item.TurnId == session.ForegroundTurn?.Id);
        if (session is null || consent is null)
        {
            return;
        }

        await RunCommandAsync(async () =>
        {
            if (confirmed && !await AwaitPendingVoiceSubmissionAsync(operation)) return;
            var snapshot = await _api.ConfirmMemoryOutboundAsync(
                session.SessionId,
                consent.TurnId,
                consent.ConsentId,
                confirmed,
                _lifetime.Token);
            if (!IsInputOperationCurrent(operation))
            {
                if (confirmed) await CancelSupersededTurnAsync(operation, new(session.SessionId, consent.TurnId, false));
                return;
            }
            RenderSession(snapshot);
        }, isCurrent: () => IsInputOperationCurrent(operation));
    }

    private async void ConversationMemorySelectionList_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        await InvalidateDisplayedPointerConsentAsync();
        await InvalidateDisplayedMemoryConsentAsync();
    }

    private async void ConversationInputTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(ConversationInputTextBox.Text)) CancelPendingVoiceInput();
        await InvalidateDisplayedMemoryConsentAsync();
        await InvalidateDisplayedPointerConsentAsync();
    }

    private async Task InvalidateDisplayedMemoryConsentAsync()
    {
        if (_isRenderingMemoryConsent || _isInvalidatingMemoryConsent)
        {
            return;
        }

        var session = _currentSession;
        var consent = session?.MemoryOutboundConsents?
            .SingleOrDefault(item => item.TurnId == session.ForegroundTurn?.Id);
        if (session is null || consent is null)
        {
            return;
        }

        _isInvalidatingMemoryConsent = true;
        try
        {
            var snapshot = await _api.ConfirmMemoryOutboundAsync(
                session.SessionId,
                consent.TurnId,
                consent.ConsentId,
                confirmed: false,
                _lifetime.Token);
            RenderSession(snapshot);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ShowError("长期记忆出站确认已失效，请重新提交。", exception);
        }
        finally
        {
            _isInvalidatingMemoryConsent = false;
        }
    }

    private async Task InvalidateDisplayedPointerConsentAsync()
    {
        if (_isRenderingMemoryConsent || _isInvalidatingPointerConsent)
        {
            return;
        }

        CancelPendingPointerGesture();

        var session = _currentSession;
        var consent = session?.PointerAnswerConsents?
            .SingleOrDefault(item => item.TurnId == session.ForegroundTurn?.Id);
        if (session is null || consent is null)
        {
            return;
        }

        _isInvalidatingPointerConsent = true;
        try
        {
            var snapshot = await _api.CancelSessionTurnAsync(
                session.SessionId,
                consent.TurnId,
                _lifetime.Token);
            RenderSession(snapshot);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ShowError("指针区域出站确认已失效，请重新指向并提交。", exception);
        }
        finally
        {
            _isInvalidatingPointerConsent = false;
        }
    }

    private async void StopConversationButton_Click(object sender, RoutedEventArgs e)
    {
        await CancelCurrentSessionTurnAsync();
    }

    private void ConversationOpenTaskButton_Click(object sender, RoutedEventArgs e) =>
        ShowPage(AppPage.NewTask);

    private void ConversationOpenActionsButton_Click(object sender, RoutedEventArgs e) =>
        ShowPage(AppPage.DesktopActions);

    private async void OpenDesktopWebsiteButton_Click(object sender, RoutedEventArgs e)
    {
        var website = DesktopWebsiteTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(website))
        {
            ShowError("请输入要打开的 https 网站地址。", null);
            return;
        }

        if (!Uri.TryCreate(website, UriKind.Absolute, out var websiteUri)
            || !string.Equals(websiteUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrWhiteSpace(websiteUri.UserInfo))
        {
            ShowError("请输入完整、有效的 https 网站地址。", null);
            return;
        }

        if (MessageBox.Show(
                $"只授权本次操作：用默认浏览器打开下面的网站。\n\n{website}\n\n请确认地址正确。",
                "确认打开网站",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        await ExecuteDesktopActionAsync(
            $"打开{website}",
            expectedIntentKind: "OpenWebsite",
            expectedTarget: websiteUri.AbsoluteUri);
    }

    private async Task ExecuteDesktopActionAsync(
        string commandText,
        string expectedIntentKind,
        string expectedTarget)
    {
        CancelPendingVoiceInput();
        var operation = CaptureInputOperation();
        CancelPendingPointerGesture();
        await RunCommandAsync(async () =>
        {
            if (!await AwaitPendingVoiceSubmissionAsync(operation, allowRegistrationHandoff: true)) return;
            var submitted = await SubmitInputForOperationAsync(operation,
                new SessionInputRequestDto(
                    commandText,
                    "Text",
                    $"desktop-ui-action-{Guid.NewGuid():N}",
                    operation.Session?.SessionId,
                    expectedIntentKind,
                    expectedTarget),
                _lifetime.Token);
            try
            {
                if (operation.Session is null)
                {
                    var firstSession = await _api.GetCurrentSessionAsync(_lifetime.Token);
                    if (firstSession is null || firstSession.SessionId != submitted.SessionId
                        || !TryAdoptCreatedSession(ref operation, firstSession))
                    {
                        await CancelSupersededTurnAsync(operation, submitted, force: true);
                        return;
                    }
                }
                if (!IsInputOperationCurrent(operation)) return;
                var waiting = await WaitForSessionTurnPhaseAsync(operation, submitted.TurnId, "WaitingForConfirmation");
                var plannedTurn = waiting.Turns.Single(item => item.Id == submitted.TurnId);
                if (!SessionUiPresenter.MatchesConfirmedTarget(
                        plannedTurn,
                        expectedIntentKind,
                        expectedTarget))
                {
                    await _api.CancelSessionTurnAsync(
                        waiting.SessionId,
                        submitted.TurnId,
                        _lifetime.Token);
                    throw new InvalidOperationException(
                        "安全检查发现实际计划与刚才确认的目标不一致，因此没有执行这次操作。 ");
                }

                if (!await AwaitPendingVoiceSubmissionAsync(operation)) return;
                var completed = await _api.ConfirmSessionTurnAsync(
                    waiting.SessionId,
                    submitted.TurnId,
                    confirmed: true,
                    _lifetime.Token);
                if (!IsInputOperationCurrent(operation)) return;
                RenderSession(completed);
                var turn = completed.Turns.Single(item => item.Id == submitted.TurnId);
                var succeeded = turn.Phase == "Completed";
                var message = SessionUiPresenter.ResultText(turn)
                              ?? (succeeded ? "操作已完成。" : "这次操作没有成功。");
                DesktopActionResultText.Text = message;
                DesktopActionResultBorder.Background = BrushFrom(succeeded ? "#EAF6F0" : "#FCECED");
                DesktopActionResultBorder.BorderBrush = BrushFrom(succeeded ? "#79B79C" : "#E3A1A5");
                DesktopActionResultBorder.Visibility = Visibility.Visible;
                StatusBarText.Text = message;
            }
            finally { await CancelSupersededTurnAsync(operation, submitted); }
        }, isCurrent: () => IsInputOperationCurrent(operation));
    }

    private async void SaveSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var runInBackground = RunInBackgroundCheckBox.IsChecked == true;
        _settings = _settings with
        {
            StartWithWindows = StartWithWindowsCheckBox.IsChecked == true,
            RunInBackground = runInBackground,
            CloseToTray = runInBackground && CloseToTrayCheckBox.IsChecked == true,
            NotificationsEnabled = NotificationsEnabledCheckBox.IsChecked == true
        };
        await RunCommandAsync(async () =>
        {
            _startupService.SetEnabled(_settings.StartWithWindows);
            await _settingsStore.SaveAsync(_settings, _lifetime.Token);
            StatusBarText.Text = "设置已保存。";
        });
    }

    private void AiChatProviderComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isRenderingAiSettings)
        {
            CancelPendingPointerGesture();
            _ = InvalidateDisplayedPointerConsentAsync();
            AiCredentialPasswordBox.Clear();
            RenderSelectedAiProvider();
        }
    }

    private void AiChatModelComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isRenderingAiSettings)
        {
            CancelPendingPointerGesture();
            _ = InvalidateDisplayedPointerConsentAsync();
            UpdateAiSettingsButtons();
        }
    }

    private void AiCredentialPasswordBox_PasswordChanged(object sender, RoutedEventArgs e) =>
        UpdateAiSettingsButtons();

    private async void SaveAiChatRouteButton_Click(object sender, RoutedEventArgs e)
    {
        if (AiChatProviderComboBox.SelectedItem is not AiProviderSettingsDto provider
            || AiChatModelComboBox.SelectedItem is not AiModelSettingsDto model)
        {
            ShowError("请先选择普通聊天的 Provider 和 Model。", null);
            return;
        }

        await RunCommandAsync(async () =>
        {
            var settings = await _api.SetChatRouteAsync(
                new SetChatRouteRequestDto(provider.ProviderId, model.ModelId),
                _lifetime.Token);
            RenderAiSettings(settings, provider.ProviderId, model.ModelId);
            StatusBarText.Text = "普通聊天大脑已保存，将从下一轮普通聊天开始使用；当前正在运行的回答不会切换。";
        });
    }

    private async void SaveAiCredentialButton_Click(object sender, RoutedEventArgs e)
    {
        if (AiChatProviderComboBox.SelectedItem is not AiProviderSettingsDto provider)
        {
            ShowError("请先选择要配置的 Provider。", null);
            return;
        }

        var credentialPresentation = AiSettingsUiPolicy.PresentCredential(
            provider,
            hasSecretInput: true,
            _isHostOnline);
        if (!credentialPresentation.ShowCredentialInputs)
        {
            ShowError("这个 Provider 使用登录账号，不需要在元枢保存 Key。", null);
            return;
        }

        var secret = AiCredentialPasswordBox.Password;
        if (string.IsNullOrWhiteSpace(secret))
        {
            ShowError("请输入要保存的 Provider 密钥。", null);
            return;
        }

        var selectedModelId = (AiChatModelComboBox.SelectedItem as AiModelSettingsDto)?.ModelId;
        await AiCredentialSubmission.RunAsync(
            secret,
            value => RunCommandAsync(async () =>
            {
                var status = await _api.SetProviderCredentialAsync(
                    new SetProviderCredentialRequestDto(provider.ProviderId, value),
                    _lifetime.Token);
                UpdateProvider(
                    provider with { ConfigurationState = status.ConfigurationState },
                    selectedModelId);
                StatusBarText.Text = "密钥已安全保存；出于安全原因，元枢不会显示或读回密钥。";
            }),
            AiCredentialPasswordBox.Clear);
    }

    private async void DeleteAiCredentialButton_Click(object sender, RoutedEventArgs e)
    {
        if (AiChatProviderComboBox.SelectedItem is not AiProviderSettingsDto provider)
        {
            ShowError("请先选择要清除密钥的 Provider。", null);
            return;
        }

        var credentialPresentation = AiSettingsUiPolicy.PresentCredential(
            provider,
            hasSecretInput: false,
            _isHostOnline);
        if (!credentialPresentation.ShowCredentialInputs)
        {
            ShowError("这个 Provider 使用登录账号，没有可删除的 Key。", null);
            return;
        }

        var confirmed = MessageBox.Show(
            AiSettingsUiPolicy.CredentialDeleteConfirmation(provider),
            "删除 Provider 密钥",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No) == MessageBoxResult.Yes;
        if (!confirmed)
        {
            return;
        }

        var selectedModelId = (AiChatModelComboBox.SelectedItem as AiModelSettingsDto)?.ModelId;
        await RunCommandAsync(async () =>
        {
            var status = await _api.DeleteProviderCredentialAsync(
                new ProviderIdRequestDto(provider.ProviderId),
                _lifetime.Token);
            AiCredentialPasswordBox.Clear();
            UpdateProvider(
                provider with { ConfigurationState = status.ConfigurationState },
                selectedModelId);
            StatusBarText.Text = "Provider 密钥已删除。";
        });
    }

    private async void CheckAiProviderHealthButton_Click(object sender, RoutedEventArgs e)
    {
        if (AiChatProviderComboBox.SelectedItem is not AiProviderSettingsDto provider)
        {
            ShowError("请先选择要检查的 Provider。", null);
            return;
        }

        var selectedModelId = (AiChatModelComboBox.SelectedItem as AiModelSettingsDto)?.ModelId;
        await RunCommandAsync(async () =>
        {
            var health = await _api.CheckAiProviderHealthAsync(
                new ProviderIdRequestDto(provider.ProviderId),
                _lifetime.Token);
            UpdateProvider(
                AiSettingsUiPolicy.ApplyHealth(provider, health),
                selectedModelId);
            StatusBarText.Text = "Provider 连接检查已完成。";
        });
    }

    private void MemoryListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MemoryListBox.SelectedItem is not MemoryRow row)
        {
            ToggleMemoryButton.IsEnabled = false;
            DeleteMemoryButton.IsEnabled = false;
            return;
        }

        var item = row.Item;
        SelectTaggedItem(MemoryCategoryComboBox, item.Category);
        SelectTaggedItem(MemoryScopeComboBox, item.Scope);
        MemoryProjectComboBox.SelectedItem = item.ProjectId is { } projectId
            ? _projects.FirstOrDefault(project => project.Id == projectId)
            : null;
        MemoryTitleTextBox.Text = item.Title ?? string.Empty;
        MemoryBodyTextBox.Text = item.Body ?? string.Empty;
        MemoryExpiryDatePicker.SelectedDate = item.ExpiresAtUtc?.ToLocalTime().Date;
        MemoryMetadataText.Text =
            $"来源：{item.Source} · 状态：{MemoryStatusLabel(item.Status)} · 更新时间：{item.UpdatedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm}"
            + (item.ExpiresAtUtc is { } expires
                ? $" · 到期：{expires.ToLocalTime():yyyy-MM-dd}"
                : string.Empty);
        ToggleMemoryButton.Content = item.Status == "Disabled" ? "启用" : "停用";
        ToggleMemoryButton.IsEnabled = true;
        DeleteMemoryButton.IsEnabled = true;
        UpdateMemoryScopeEditor();
    }

    private void MemoryScopeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UpdateMemoryScopeEditor();

    private async void MemoryPreviewQueryTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ClearMemoryPreviewResults();
        await InvalidateDisplayedMemoryConsentAsync();
    }

    private async void MemoryPreviewProjectComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ClearMemoryPreviewResults();
        await InvalidateDisplayedMemoryConsentAsync();
    }

    private async void RunMemoryPreviewButton_Click(object sender, RoutedEventArgs e)
    {
        ClearMemoryPreviewResults();
        var query = MemoryPreviewQueryTextBox.Text;
        var projectId = (MemoryPreviewProjectComboBox.SelectedItem as MemoryPreviewProjectOption)?.ProjectId;
        await RunCommandAsync(async () =>
        {
            var preview = await _api.PreviewMemoriesAsync(
                new MemoryPreviewRequestDto(query, projectId),
                _lifetime.Token);
            if (!string.Equals(MemoryPreviewQueryTextBox.Text, query, StringComparison.Ordinal)
                || (MemoryPreviewProjectComboBox.SelectedItem as MemoryPreviewProjectOption)?.ProjectId != projectId)
            {
                return;
            }

            MemoryPreviewResultsListBox.ItemsSource = preview.Items
                .Select(MemoryPreviewRow.From)
                .ToArray();
            MemoryPreviewSummaryText.Text =
                $"本机候选 {preview.CandidateCount} 条，匹配 {preview.SelectedCount} 条，共 {preview.TotalCharacters} 个字符。";
            StatusBarText.Text = "本地相关记忆预览已更新。";
        });
    }

    private void ClearMemoryPreviewResults()
    {
        MemoryPreviewResultsListBox.ItemsSource = null;
        MemoryPreviewSummaryText.Text = string.Empty;
    }

    private void NewMemoryButton_Click(object sender, RoutedEventArgs e)
    {
        MemoryListBox.SelectedItem = null;
        ClearMemoryEditor();
        MemoryTitleTextBox.Focus();
    }

    private async void SaveMemoryButton_Click(object sender, RoutedEventArgs e)
    {
        var category = SelectedTag(MemoryCategoryComboBox);
        var scope = SelectedTag(MemoryScopeComboBox);
        var projectId = scope == "Project"
            ? (MemoryProjectComboBox.SelectedItem as ProjectDto)?.Id
            : null;
        if (string.IsNullOrWhiteSpace(category)
            || string.IsNullOrWhiteSpace(scope)
            || (scope == "Project" && projectId is null))
        {
            ShowError("请选择记忆类别和有效的已授权项目。", null);
            return;
        }

        DateTimeOffset? expiresAtUtc = MemoryExpiryDatePicker.SelectedDate is { } selectedDate
            ? new DateTimeOffset(
                    selectedDate.Date.AddDays(1),
                    TimeZoneInfo.Local.GetUtcOffset(selectedDate.Date.AddDays(1)))
                .ToUniversalTime()
            : null;
        var selected = (MemoryListBox.SelectedItem as MemoryRow)?.Item;
        await RunCommandAsync(async () =>
        {
            var saved = selected is null
                ? await _api.CreateMemoryAsync(new CreateMemoryRequestDto(
                    category,
                    scope,
                    projectId,
                    MemoryTitleTextBox.Text,
                    MemoryBodyTextBox.Text,
                    expiresAtUtc), _lifetime.Token)
                : await _api.UpdateMemoryAsync(new UpdateMemoryRequestDto(
                    selected.Id,
                    selected.Version,
                    category,
                    scope,
                    projectId,
                    MemoryTitleTextBox.Text,
                    MemoryBodyTextBox.Text,
                    expiresAtUtc), _lifetime.Token);
            if (selected is not null)
            {
                await InvalidateDisplayedMemoryConsentAsync();
            }

            await LoadMemoriesForPageAsync(saved.Id);
            StatusBarText.Text = selected is null ? "长期记忆已保存在本机。" : "长期记忆已修正。";
        });
    }

    private async void ToggleMemoryButton_Click(object sender, RoutedEventArgs e)
    {
        if (MemoryListBox.SelectedItem is not MemoryRow row)
        {
            return;
        }

        var enable = row.Item.Status == "Disabled";
        await RunCommandAsync(async () =>
        {
            var updated = await _api.SetMemoryEnabledAsync(new SetMemoryEnabledRequestDto(
                row.Item.Id,
                row.Item.Version,
                enable), _lifetime.Token);
            await InvalidateDisplayedMemoryConsentAsync();
            await LoadMemoriesForPageAsync(updated.Id);
            StatusBarText.Text = enable ? "长期记忆已启用。" : "长期记忆已停用。";
        });
    }

    private async void DeleteMemoryButton_Click(object sender, RoutedEventArgs e)
    {
        if (MemoryListBox.SelectedItem is not MemoryRow row)
        {
            return;
        }

        var confirmed = MessageBox.Show(
            $"确定删除“{row.Item.Title}”吗？删除后正文会从本机数据库中移除。",
            "删除长期记忆",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No) == MessageBoxResult.Yes;
        if (!confirmed)
        {
            return;
        }

        await RunCommandAsync(async () =>
        {
            _ = await _api.DeleteMemoryAsync(new DeleteMemoryRequestDto(
                row.Item.Id,
                row.Item.Version,
                Confirmed: true), _lifetime.Token);
            await InvalidateDisplayedMemoryConsentAsync();
            await LoadMemoriesForPageAsync();
            StatusBarText.Text = "长期记忆已删除。";
        });
    }

    private void ClearMemoryEditor()
    {
        SelectTaggedItem(MemoryCategoryComboBox, "UserFact");
        SelectTaggedItem(MemoryScopeComboBox, "Global");
        MemoryProjectComboBox.SelectedItem = null;
        MemoryTitleTextBox.Clear();
        MemoryBodyTextBox.Clear();
        MemoryExpiryDatePicker.SelectedDate = null;
        MemoryMetadataText.Text = "来源：用户明确保存";
        ToggleMemoryButton.IsEnabled = false;
        DeleteMemoryButton.IsEnabled = false;
        UpdateMemoryScopeEditor();
    }

    private void UpdateMemoryScopeEditor()
    {
        if (MemoryProjectComboBox is null)
        {
            return;
        }

        var projectScoped = SelectedTag(MemoryScopeComboBox) == "Project";
        MemoryProjectComboBox.Visibility = projectScoped ? Visibility.Visible : Visibility.Collapsed;
        if (projectScoped)
        {
            var authorized = _projects
                .Where(project => project.AuthorizationState == "Authorized")
                .ToArray();
            PreserveProjectSelection(MemoryProjectComboBox, authorized);
        }
    }

    private static string? SelectedTag(WpfComboBox comboBox) =>
        (comboBox.SelectedItem as WpfComboBoxItem)?.Tag as string;

    private static void SelectTaggedItem(WpfComboBox comboBox, string tag)
    {
        comboBox.SelectedItem = comboBox.Items
            .OfType<WpfComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag as string, tag, StringComparison.Ordinal));
    }

    public void OpenDashboardFromTray() => ShowWindowAndPage(AppPage.Dashboard);

    public void OpenNewTaskFromTray() => ShowWindowAndPage(AppPage.Dashboard);

    public void OpenCurrentTaskFromTray() => ShowWindowAndPage(AppPage.CurrentTask);

    public void OpenSettingsFromTray() => ShowWindowAndPage(AppPage.Settings);

    public async Task OpenTaskFromNotificationAsync(Guid taskId)
    {
        ShowWindowAndPage(AppPage.CurrentTask);
        await RunCommandAsync(() => LoadTaskDetailsAsync(taskId, navigate: false));
    }

    public void OpenConversationCenterFromOnboarding()
        => ShowWindowAndPage(AppPage.Conversations);

    public async Task CancelCurrentTaskFromTrayAsync()
    {
        if (_selectedTaskId is not { } taskId)
        {
            ShowWindowAndPage(AppPage.CurrentTask);
            return;
        }

        await RunCommandAsync(async () =>
        {
            await _api.CancelTaskAsync(taskId, cancellationToken: _lifetime.Token);
            await LoadTaskDetailsAsync(taskId, navigate: false);
        });
    }

    public async Task ExitApplicationAsync()
    {
        if (_forceClose) return;
        _forceClose = true;
        CancelPendingVoiceInput();
        CancelPendingPointerGesture();
        _assistantWindow?.Render("正在退出", "正在关闭麦克风和本机服务…", false, false, false, false);
        await BoundedShutdown.TryCompleteAsync(_ => _voiceSession.StopAsync(), TimeSpan.FromSeconds(5));
        await CloseIslandSurfaceAsync();
        await BoundedShutdown.TryCompleteAsync(token => _api.ShutdownHostAsync(token), TimeSpan.FromSeconds(5));

        _assistantWindow?.CloseForExit();
        System.Windows.Application.Current.Shutdown();
    }

    private void ShowWindowAndPage(AppPage page)
    {
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
        ShowPage(page);
    }

    private void OpenDataDirectoryButton_Click(object sender, RoutedEventArgs e) =>
        OpenDirectory(_systemStatus?.DataDirectory);

    private void OpenLogsDirectoryButton_Click(object sender, RoutedEventArgs e) =>
        OpenDirectory(_systemStatus?.LogsDirectory);

    private static void OpenDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo("explorer.exe", path) { UseShellExecute = true });
    }

    private void HistoryFilterComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded)
        {
            RenderHistory();
        }
    }

    private async void TaskList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var taskId = sender switch
        {
            ListBox listBox when listBox.SelectedItem is TaskRow row => row.Id,
            ListView listView when listView.SelectedItem is HistoryRow row => row.Id,
            _ => Guid.Empty
        };
        if (taskId != Guid.Empty)
        {
            await RunCommandAsync(() => LoadTaskDetailsAsync(taskId, navigate: true));
        }
    }

    private void NavigationButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { CommandParameter: string page }
            && Enum.TryParse<AppPage>(page, out var parsed))
        {
            ShowPage(parsed);
        }
    }

    private void OpenNewTaskButton_Click(object sender, RoutedEventArgs e) => ShowPage(AppPage.NewTask);

    private void ShowPage(AppPage page)
    {
        DashboardPage.Visibility = page == AppPage.Dashboard ? Visibility.Visible : Visibility.Collapsed;
        ConversationsPage.Visibility = page == AppPage.Conversations ? Visibility.Visible : Visibility.Collapsed;
        ProjectsPage.Visibility = page == AppPage.Projects ? Visibility.Visible : Visibility.Collapsed;
        DesktopActionsPage.Visibility = page == AppPage.DesktopActions ? Visibility.Visible : Visibility.Collapsed;
        NewTaskPage.Visibility = page == AppPage.NewTask ? Visibility.Visible : Visibility.Collapsed;
        CurrentTaskPage.Visibility = page == AppPage.CurrentTask ? Visibility.Visible : Visibility.Collapsed;
        HistoryPage.Visibility = page == AppPage.History ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = page == AppPage.Settings ? Visibility.Visible : Visibility.Collapsed;
        if (page == AppPage.Settings)
        {
            _ = LoadAiSettingsForPageAsync();
            _ = LoadMemoriesForPageAsync();
        }
        else if (page == AppPage.Conversations)
        {
            _ = LoadMemoriesForPageAsync();
        }
        foreach (var button in new[]
                 {
                     DashboardNavButton, ConversationsNavButton, ProjectsNavButton, DesktopActionsNavButton, NewTaskNavButton,
                     CurrentTaskNavButton, HistoryNavButton, SettingsNavButton
                 })
        {
            button.Tag = null;
        }

        var selected = page switch
        {
            AppPage.Dashboard => DashboardNavButton,
            AppPage.Conversations => ConversationsNavButton,
            AppPage.Projects => ProjectsNavButton,
            AppPage.DesktopActions => DesktopActionsNavButton,
            AppPage.NewTask => NewTaskNavButton,
            AppPage.CurrentTask => CurrentTaskNavButton,
            AppPage.History => HistoryNavButton,
            _ => SettingsNavButton
        };
        selected.Tag = "Selected";
        PageTitleText.Text = page switch
        {
            AppPage.Dashboard => "首页",
            AppPage.Conversations => "对话",
            AppPage.Projects => "项目",
            AppPage.DesktopActions => "电脑操作",
            AppPage.NewTask => "编程任务",
            AppPage.CurrentTask => "当前任务",
            AppPage.History => "任务历史",
            _ => "设置"
        };
    }

    private void SetHostOnline(bool online)
    {
        if (online && !_isHostOnline)
        {
            CancelPendingVoiceInput();
            _currentSession = null;
        }

        _isHostOnline = online;
        HostStatusText.Text = online ? "本机中枢在线" : "本机中枢离线";
        HostStatusPill.Background = BrushFrom(online ? "#EAF6F0" : "#FCECED");
        HostStatusText.Foreground = BrushFrom(online ? "#1F8A6A" : "#C94C4C");
        UpdateAiSettingsButtons();
    }

    private void ShowError(string message, Exception? exception)
    {
        ErrorMessageText.Text = message;
        ErrorTechnicalText.Text = exception is null
            ? "没有更多技术信息。"
            : UserFacingErrorMapper.Map(exception).TechnicalDetail;
        ErrorBanner.Visibility = Visibility.Visible;
        RenderAssistantSurface();
    }

    private void CloseErrorButton_Click(object sender, RoutedEventArgs e) =>
        ErrorBanner.Visibility = Visibility.Collapsed;

    private static string StatusLabel(string status) => status switch
    {
        "Pending" => "准备中",
        "Running" => "正在执行",
        "WaitingForUser" => "等待你决定",
        "CancellationRequested" => "正在取消",
        "Succeeded" => "执行完成",
        "Failed" => "执行失败",
        "Cancelled" => "已取消",
        "Interrupted" => "意外中断",
        _ => status
    };

    private static string VerificationLabel(string status) => status switch
    {
        "Verified" => "系统已验证",
        "Unverified" => "尚未验证",
        "VerificationFailed" => "验证未通过",
        "Cancelled" => "任务已取消",
        "Failed" => "任务失败",
        "Interrupted" => "任务已中断",
        _ => "等待证据"
    };

    private static string TestStatusLabel(string status) => status switch
    {
        "Passed" => "测试通过",
        "Failed" => "测试失败",
        "NotRun" => "没有测试证据",
        "Incomplete" => "测试证据不完整",
        _ => status
    };

    private static string ConversationStatusLabel(string status, string? failureMessage) => status switch
    {
        "Ready" => "可以继续聊天",
        "Responding" => "元枢正在回答…",
        "Failed" => string.IsNullOrWhiteSpace(failureMessage) ? "上次回答失败" : failureMessage,
        "Interrupted" => "上次回答意外中断，可以继续发送消息",
        _ => status
    };

    private static string DurationText(TaskSummaryDto task)
    {
        if (task.StartedAtUtc is null)
        {
            return "尚未开始";
        }

        var end = task.CompletedAtUtc ?? DateTimeOffset.UtcNow;
        var duration = end - task.StartedAtUtc.Value;
        return duration.TotalHours >= 1
            ? $"已用 {duration.TotalHours:0.#} 小时"
            : $"已用 {Math.Max(0, duration.TotalMinutes):0} 分钟";
    }

    private static SolidColorBrush BrushFrom(string color) =>
        new((Color)ColorConverter.ConvertFromString(color));

    private enum AppPage
    {
        Dashboard,
        Conversations,
        Projects,
        DesktopActions,
        NewTask,
        CurrentTask,
        History,
        Settings
    }

    private sealed record TaskRow(Guid Id, string Text)
    {
        public static TaskRow From(TaskSummaryDto task) =>
            new(task.Id, $"{task.Title}  ·  {StatusLabel(task.Status)}");

        public override string ToString() => Text;
    }

    private sealed record ConversationRow(Guid Id, string Title, string StatusText)
    {
        public static ConversationRow From(ConversationSummaryDto conversation) => new(
            conversation.Id,
            conversation.Title,
            ConversationStatusLabel(conversation.Status, conversation.FailureMessage));
    }

    private sealed record ConversationMessageRow(
        string RoleText,
        string Content,
        string TimeText,
        Brush Background,
        Brush BorderBrush,
        HorizontalAlignment Alignment,
        Thickness Margin)
    {
        public static ConversationMessageRow From(ConversationMessageDto message)
        {
            var isUser = message.Role == "User";
            return new ConversationMessageRow(
                isUser ? "你" : "元枢",
                message.Content,
                message.CreatedAtUtc.ToLocalTime().ToString("HH:mm"),
                BrushFrom(isUser ? "#EAF0FF" : "#F7FAFB"),
                BrushFrom(isUser ? "#BCD0F6" : "#DDE5E8"),
                isUser ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                isUser ? new Thickness(90, 5, 0, 5) : new Thickness(0, 5, 90, 5));
        }
    }

    private sealed record ProjectRow(
        Guid Id,
        string Name,
        string RootPath,
        string GitStatus,
        string AuthorizationState,
        int RecentTaskCount,
        bool HasActiveTask,
        string ActiveText)
    {
        public static ProjectRow From(ProjectDto project) => new(
            project.Id,
            project.Name,
            project.RootPath,
            project.GitStatus,
            project.AuthorizationState,
            project.RecentTaskCount,
            project.HasActiveTask,
            project.HasActiveTask ? "是" : "否");
    }

    private sealed record MemoryRow(MemoryDto Item, string Title, string Summary)
    {
        public static MemoryRow From(MemoryDto item) => new(
            item,
            item.Title ?? "已删除的记忆",
            $"{MemoryCategoryLabel(item.Category)} · {MemoryScopeLabel(item.Scope)} · {MemoryStatusLabel(item.Status)} · {item.UpdatedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm}");
    }

    private sealed record MemoryPreviewProjectOption(Guid? ProjectId, string Name);

    private sealed record MemoryPreviewRow(string Title, string Summary)
    {
        public static MemoryPreviewRow From(MemoryPreviewMatchDto match) => new(
            match.Item.Title ?? "无标题",
            $"{MemoryCategoryLabel(match.Item.Category)} · {MemoryScopeLabel(match.Item.Scope)} · {MemoryStatusLabel(match.Item.Status)}"
            + $" · {match.Item.UpdatedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm} · 分数 {match.Score}"
            + $" · 匹配依据：{string.Join("、", match.Explanations)}");
    }

    private sealed record MemoryOutboundConsentItemRow(string Header, string Body, string Metadata);

    private static string MemoryCategoryLabel(string category) => category switch
    {
        "UserFact" => "用户事实",
        "UserPreference" => "用户偏好",
        "ProjectNote" => "项目备注",
        "Decision" => "决定",
        _ => category
    };

    private static string MemoryScopeLabel(string scope) =>
        scope == "Project" ? "项目" : "全局";

    private static string MemoryStatusLabel(string status) => status switch
    {
        "Active" => "已启用",
        "Disabled" => "已停用",
        "Deleted" => "已删除",
        _ => status
    };

    private sealed record HistoryRow(
        Guid Id,
        string Title,
        string ProjectName,
        string Status,
        string VerificationStatus,
        string TimeText,
        string UserSummary)
    {
        public static HistoryRow From(TaskSummaryDto task) => new(
            task.Id,
            task.Title,
            task.ProjectName,
            StatusLabel(task.Status),
            string.IsNullOrWhiteSpace(task.VerificationStatus)
                ? "等待证据"
                : VerificationLabel(task.VerificationStatus),
            task.CreatedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
            task.UserSummary ?? "尚无结果摘要");
    }

    private sealed record EventRow(string Text)
    {
        public static EventRow From(TaskEventDto item) => new(
            $"{item.OccurredAtUtc.ToLocalTime():HH:mm:ss}  {item.Message}");

        public override string ToString() => Text;
    }

    private sealed record FileRow(string Text)
    {
        public static FileRow From(EvidenceFileDto file) => new(
            $"{file.ChangeType}  {file.RelativePath}  (+{file.AddedLines?.ToString() ?? "?"}/-{file.DeletedLines?.ToString() ?? "?"})");

        public override string ToString() => Text;
    }

    private sealed record TestRow(string Text)
    {
        public static TestRow From(TestCommandDto command) => new(
            $"exit {command.ExitCode?.ToString() ?? "?"}  {command.Command}");

        public override string ToString() => Text;
    }
}
