using System.Collections.Concurrent;
using ScreenGuide.AI.Core;
using ScreenGuide.Core.Tasking;
using ScreenGuide.DesktopProtocol;
using ScreenGuide.Skills.Abstractions;
using ScreenGuide.Skills.Windows;

namespace ScreenGuide.DesktopHost.Runtime;

/// <summary>
/// The provider-neutral entry for V0.2. Planning never executes an action. A ready action plan
/// is held in memory for two minutes and can be consumed exactly once after visible confirmation.
/// </summary>
public sealed class AssistantCommandService(
    IIntentPlanner planner,
    ISemanticIntentSuggester semanticIntent,
    IForegroundWindowContextProvider foregroundWindows,
    IInstalledApplicationCatalog applications,
    LocalTaskEntryService tasks,
    ConversationService conversations,
    DesktopActionEntryService desktopActions,
    WindowUnderstandingService windowUnderstanding,
    TimeProvider timeProvider,
    IDesktopFileCatalog? desktopFiles = null,
    IDesktopTaskExecutor? desktopTasks = null)
{
    private readonly IDesktopFileCatalog _desktopFiles = desktopFiles ?? new DesktopFileCatalog();
    private readonly ConcurrentDictionary<Guid, PendingPlan> _plans = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _windowObservations =
        new(StringComparer.Ordinal);

    internal void ValidateFilePlanForContinuation(Guid? planId)
    {
        if (planId is null || !_plans.TryGetValue(planId.Value, out var pending)
            || pending.Plan.Kind != UniversalIntentKind.OpenFile
            || pending.Plan.ExpiresAtUtc <= timeProvider.GetUtcNow())
            throw new InvalidOperationException("文件确认已失效，请重新选择并确认。");
        if (pending.DesktopFile is not null) _desktopFiles.RequireUnchanged(pending.DesktopFile);
    }

    internal void ValidateApplicationPlanForContinuation(Guid? previousPlanId, Guid currentPlanId)
    {
        var now = timeProvider.GetUtcNow();
        if (previousPlanId is null || !_plans.TryGetValue(previousPlanId.Value, out var previous)
            || !_plans.TryGetValue(currentPlanId, out var current)
            || previous.Plan.Kind != UniversalIntentKind.OpenApplication
            || current.Plan.Kind != UniversalIntentKind.OpenApplication
            || previous.Plan.ExpiresAtUtc <= now || current.Plan.ExpiresAtUtc <= now
            || string.IsNullOrWhiteSpace(previous.ApplicationTargetBinding)
            || !string.Equals(previous.ApplicationId, current.ApplicationId, StringComparison.Ordinal)
            || !string.Equals(previous.ApplicationTargetBinding, current.ApplicationTargetBinding, StringComparison.Ordinal))
            throw new InstalledApplicationResolutionException(
                InstalledApplicationErrorCodes.TargetChanged,
                "刚才确认的应用目标已经变化或失效，本次没有打开程序；请重新确认。");
    }

    public async Task<AssistantIntentPlanDto> PlanAsync(
        PlanAssistantCommandRequestDto request,
        CancellationToken cancellationToken = default) =>
        await PlanCoreAsync(
                request,
                sessionTurnId: null,
                frozenRoute: null,
                persistedIntent: null,
                confirmedPlanId: null,
                cancellationToken)
            .ConfigureAwait(false);

    public async Task<AssistantIntentPlanDto> PlanSessionAsync(
        PlanAssistantCommandRequestDto request,
        Guid sessionTurnId,
        FrozenChatModelRoute? frozenRoute,
        UniversalIntentKind? persistedIntent,
        CancellationToken cancellationToken = default,
        Guid? confirmedPlanId = null)
    {
        if (sessionTurnId == Guid.Empty)
        {
            throw new ArgumentException("Session Turn 标识不能为空。", nameof(sessionTurnId));
        }

        return await PlanCoreAsync(
                request,
                sessionTurnId,
                frozenRoute,
                persistedIntent,
                confirmedPlanId,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<AssistantIntentPlanDto> PlanCoreAsync(
        PlanAssistantCommandRequestDto request,
        Guid? sessionTurnId,
        FrozenChatModelRoute? frozenRoute,
        UniversalIntentKind? persistedIntent,
        Guid? confirmedPlanId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var inputModality = NormalizeInputModality(request.InputModality);
        DesktopFileRequest.TryParse(request.Text, out var desktopRequest);
        if (inputModality == "ProgrammingTask"
            || persistedIntent is not (null or UniversalIntentKind.OpenFile or UniversalIntentKind.FindDesktopFile))
            desktopRequest = null;
        var selectedFile = desktopRequest?.Mode == DesktopFileRequestMode.Find
            ? null : NormalizeSelectedFile(request.SelectedFilePath);
        DesktopFileResolution? desktopResolution = null;
        DesktopFileSearchResult? discoveredDesktopFiles = null;
        if (desktopRequest is { Mode: DesktopFileRequestMode.Open }
            && !request.Text.Any(char.IsControl) && DesktopFileRequest.IsValidName(desktopRequest.Name))
        {
            if (confirmedPlanId is not null)
            {
                // Re-planning may refresh context, but a confirmation must never replace
                // the file's original fingerprint with metadata sampled after confirmation.
                if (!_plans.TryGetValue(confirmedPlanId.Value, out var previous)
                    || previous.SessionTurnId != sessionTurnId
                    || previous.Plan.Kind != UniversalIntentKind.OpenFile
                    || previous.Plan.ExpiresAtUtc <= timeProvider.GetUtcNow()
                    || previous.DesktopFile is not { } boundFile
                    || !string.Equals(boundFile.FullPath, selectedFile, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("刚才确认的桌面文件已失效或发生变化，请重新选择并确认，尚未打开。");
                _desktopFiles.RequireUnchanged(boundFile);
                desktopResolution = new(boundFile, "已保留刚才确认的桌面文件。");
            }
            else if (selectedFile is not null)
            {
                desktopResolution = _desktopFiles.BindSelected(desktopRequest.Name, selectedFile);
            }
            else
            {
                var found = _desktopFiles.Find(desktopRequest.Name);
                discoveredDesktopFiles = found;
                desktopResolution = found.Complete && found.TotalMatches == 1 && found.Candidates.Count == 1
                    ? new(found.Candidates[0], "已找到唯一匹配的桌面文件。")
                    : new(null, DescribeDesktopFiles(found, forOpening: true));
            }
            selectedFile = desktopResolution.File?.FullPath;
        }
        cancellationToken.ThrowIfCancellationRequested();
        var project = await FindAuthorizedProjectAsync(request.SelectedProjectId, cancellationToken)
            .ConfigureAwait(false);
        var foreground = foregroundWindows.GetLastExternalWindow();
        var context = new IntentPlanningContext(
            selectedFile,
            project?.Id,
            project?.Name,
            foreground is null
                ? null
                : new ForegroundApplicationContext(
                    foreground.WindowHandle,
                    foreground.WindowTitle,
                    foreground.ProcessName),
            request.ForegroundObservationConsent,
            inputModality == "ProgrammingTask"
                ? UniversalIntentKind.CodingTask
                : persistedIntent,
            PreferInstalledApplication: persistedIntent == UniversalIntentKind.OpenApplication);
        var plan = planner.Plan(request.Text, context);
        KnownDesktopApplication? plannedApplication = null;
        // "Open the app on my desktop" also matches the desktop-file grammar.
        // Only a fresh, implicit target can be reconsidered. Explicit filenames,
        // quoted file references, chosen files and persisted intents keep their identity.
        // A complete document lookup is required before ruling out a name collision.
        if (persistedIntent is null
            && discoveredDesktopFiles is { Complete: true }
            && desktopRequest is { Mode: DesktopFileRequestMode.Open }
            && !Path.HasExtension(desktopRequest.Name)
            && !desktopRequest.Name.Contains("文档", StringComparison.Ordinal)
            && request.Text.IndexOfAny(['“', '”', '"', '「', '」']) < 0)
        {
            var applicationPlan = planner.Plan(request.Text, context with { PreferInstalledApplication = true });
            if (applicationPlan.Kind == UniversalIntentKind.OpenApplication
                && applicationPlan.Readiness == IntentPlanReadiness.Ready)
            {
                try
                {
                    var application = applications.ResolveApplicationPreference(applicationPlan.Target!);
                    cancellationToken.ThrowIfCancellationRequested();
                    RequireAllowedApplication(application);
                    if (discoveredDesktopFiles.TotalMatches == 0)
                    {
                        plannedApplication = application;
                        plan = applicationPlan;
                        desktopResolution = null;
                    }
                    else
                    {
                        // Do not silently choose the app or an automatically found document.
                        // File selection continues the original Turn with an explicit file intent.
                        desktopResolution = new(null,
                            $"“{desktopRequest.Name}”同时匹配已安装应用和桌面文档。"
                            + "请明确说出要打开的软件，或点击“选择文件”选择文档；本次没有打开任何内容。");
                        plan = planner.Plan(request.Text, context with { SelectedFilePath = null });
                    }
                }
                catch (InstalledApplicationResolutionException exception)
                    when (exception.Code == InstalledApplicationErrorCodes.NotFound)
                {
                    // With no matching document, keep an uncertain application
                    // name in local clarification instead of asking for a file.
                    // Similar names never become a bound/executable target.
                    if (discoveredDesktopFiles.TotalMatches == 0 && exception.SuggestedNames.Count > 0)
                        throw;
                    // Only absence may retain the document route. Ambiguity and unsafe
                    // application targets must not fall through to a different action.
                }
            }
        }
        if (plan.Kind is UniversalIntentKind.OpenWebsite or UniversalIntentKind.ApplicationSearch or UniversalIntentKind.Unsupported or UniversalIntentKind.DesktopTask
            && context.ExplicitUserIntent is null
            && !request.Text.Any(char.IsControl)
            && DeterministicIntentPlanner.TryGetApplicationPreference(request.Text, out var applicationName))
        {
            try
            {
                plannedApplication = applications.ResolveApplicationPreference(applicationName);
                plan = planner.Plan(request.Text, context with { PreferInstalledApplication = true });
            }
            catch (InstalledApplicationResolutionException exception)
                when (exception.Code == InstalledApplicationErrorCodes.NotFound)
            {
                // A literal registered application name takes precedence over a
                // website or an implicit search split. Only absence may fall back;
                // ambiguity and rejected launch targets must never authorize a search.
            }
        }
        if (plan.Kind == UniversalIntentKind.SearchForeground && plan.Readiness == IntentPlanReadiness.NeedsContext
            && foreground is not null && context.ForegroundApplication is not null)
        {
            plan = planner.Plan(request.Text, context with
            {
                ForegroundApplication = context.ForegroundApplication with { ApplicationNames = applications.GetNamesForWindow(foreground) }
            });
        }
        if (desktopResolution is not null && plan.Kind == UniversalIntentKind.OpenFile)
        {
            if (desktopResolution.File is not null
                && !string.Equals(plan.Target, desktopResolution.File.FullPath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("桌面文件计划与选定文件不一致，本次没有打开文件。");
            plan = plan with
            {
                UserSummary = desktopResolution.File is null ? desktopResolution.Message : plan.UserSummary,
                ConfirmationText = desktopResolution.File is null ? null
                    : $"确认用默认应用打开桌面文件“{Path.GetFileName(selectedFile)}”？\n完整路径：{selectedFile}"
            };
        }
        if (plan.Kind == UniversalIntentKind.Conversation
            && context.ExplicitUserIntent is null
            && sessionTurnId is not null
            && frozenRoute is not null
            && SemanticIntentCandidateDetector.ShouldEvaluate(request.Text))
        {
            var suggestion = await semanticIntent.SuggestAsync(
                    sessionTurnId.Value,
                    frozenRoute,
                    request.Text,
                    cancellationToken)
                .ConfigureAwait(false);
            var explicitIntent = SemanticIntentSuggestionPolicy.SelectExplicitIntent(suggestion);
            if (explicitIntent is not null)
            {
                // The model contributes only a kind. Target, missing context,
                // permissions and confirmation are recomputed from trusted local state.
                plan = planner.Plan(
                    request.Text,
                    context with { ExplicitUserIntent = explicitIntent });
            }
        }

        ApplicationSearchWorkflow? applicationSearch = null;
        if (plan.Readiness == IntentPlanReadiness.Ready
            && plan.Kind is UniversalIntentKind.OpenApplication or UniversalIntentKind.ApplicationSearch)
        {
            plannedApplication ??= applications.ResolveByDisplayName(
                plan.Kind == UniversalIntentKind.ApplicationSearch
                    ? plan.ApplicationSearch?.ApplicationName ?? string.Empty
                    : plan.Target ?? string.Empty);
            RequireAllowedApplication(plannedApplication);

            if (plan.Kind == UniversalIntentKind.ApplicationSearch)
            {
                var search = plan.ApplicationSearch
                    ?? throw new InvalidDataException("应用搜索计划缺少明确的搜索内容。");
                if (!Enum.TryParse<ApplicationSearchFollowUp>(search.FollowUp, false, out var followUp)
                    || !Enum.IsDefined(followUp))
                    throw new InvalidDataException("应用搜索计划包含尚未开放的后续操作。");
                applicationSearch = new ApplicationSearchWorkflow(
                    plannedApplication.Id,
                    DesktopSearchQuery.NormalizeAndValidate(search.Query),
                    followUp,
                    search.LatestMusicArtist,
                    search.MusicChart);
                applicationSearch.Validate();
            }
        }

        DesktopTaskPlan? desktopTask = null;
        if (plan.Kind == UniversalIntentKind.DesktopTask && plan.Readiness == IntentPlanReadiness.Ready)
        {
            if (sessionTurnId is null || frozenRoute is null || desktopTasks is null)
                throw new DesktopTaskException("desktop_task_route_missing", "通用任务需要可用的当前模型和本次会话，请检查模型设置后重试。");
            if (!DesktopTaskRequest.TryParse(request.Text, out var taskRequest))
                throw new DesktopTaskException("desktop_task_instruction_invalid", "请明确指定软件和要完成的操作。");
            var taskApplication = taskRequest!.UsesCurrentWindow ? null
                : applications.ResolveByDisplayName(taskRequest.ApplicationName!);
            if (taskApplication is not null) RequireAllowedApplication(taskApplication);
            desktopTask = new(sessionTurnId.Value, request.Text, frozenRoute, taskApplication,
                taskApplication is null ? null : InstalledApplicationCatalog.CreateTargetBinding(taskApplication),
                taskApplication is null ? foreground : null);
        }
        RemoveExpired();
        if (plan.Readiness == IntentPlanReadiness.Ready)
        {
            _plans[plan.Id] = new PendingPlan(
                plan,
                foreground,
                request.ForegroundObservationConsent,
                inputModality,
                plannedApplication?.Id,
                plannedApplication is null
                    ? null
                    : InstalledApplicationCatalog.CreateTargetBinding(plannedApplication),
                desktopResolution?.File,
                applicationSearch,
                sessionTurnId,
                desktopTask);
        }

        return Map(plan,
            plan.Kind is UniversalIntentKind.ApplicationSearch or UniversalIntentKind.FindDesktopFile ? null : foreground,
            plannedApplication?.Id,
            applicationSearch?.ToCanonicalTarget(InstalledApplicationCatalog.CreateTargetBinding(plannedApplication!)),
            desktopTask?.CanonicalTarget);
    }

    public async Task<AssistantCommandResultDto> ExecuteAsync(
        ExecuteAssistantCommandRequestDto request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (!_plans.TryRemove(request.PlanId, out var pending))
        {
            throw new InvalidOperationException("这条操作计划已经失效或处理过，请重新说一次。 ");
        }

        var plan = pending.Plan;
        if (plan.ExpiresAtUtc is not null && plan.ExpiresAtUtc <= timeProvider.GetUtcNow())
        {
            throw new InvalidOperationException("这条操作确认已超时，请重新说一次。 ");
        }

        if (plan.Readiness != IntentPlanReadiness.Ready)
        {
            throw new InvalidOperationException("当前还缺少必要信息，不能执行。 ");
        }

        var explicitlyAuthorizedByVoice =
            string.Equals(pending.InputModality, "Voice", StringComparison.Ordinal)
            && string.Equals(request.AuthorizationSource, "ExplicitVoice", StringComparison.Ordinal)
            && IsDirectVoiceAction(plan.Kind);
        if (plan.RequiresConfirmation && !request.Confirmed && !explicitlyAuthorizedByVoice)
        {
            throw new UnauthorizedAccessException("需要你在界面中确认这一次电脑操作。 ");
        }

        var effectiveRequest = explicitlyAuthorizedByVoice
            ? request with { Confirmed = true }
            : request;

        if (plan.Kind is UniversalIntentKind.SearchForeground or UniversalIntentKind.DescribeForeground)
        {
            var expected = pending.Foreground
                ?? throw new WindowIdentityException(
                    WindowIdentityErrorCodes.Missing,
                    "这条窗口授权缺少可信身份，请重新选择窗口并确认。 ");
            WindowIdentityContract.RequireMatch(
                expected,
                foregroundWindows.ResolveWindow(expected.WindowHandle));
        }

        if (pending.DesktopFile is not null)
            _desktopFiles.RequireUnchanged(pending.DesktopFile);

        return plan.Kind switch
        {
            UniversalIntentKind.DesktopTask => await (desktopTasks
                ?? throw new DesktopTaskException("desktop_task_unavailable", "通用任务服务未配置。"))
                .ExecuteAsync(pending.DesktopTask ?? throw new UnauthorizedAccessException("本次任务范围缺失。"),
                    effectiveRequest.IdempotencyKey ?? throw new UnauthorizedAccessException("任务执行标识缺失。"), cancellationToken),
            UniversalIntentKind.Conversation => await ExecuteConversationAsync(plan, effectiveRequest, cancellationToken)
                .ConfigureAwait(false),
            UniversalIntentKind.CodingTask => await ExecuteCodingTaskAsync(plan, effectiveRequest, cancellationToken)
                .ConfigureAwait(false),
            UniversalIntentKind.OpenApplication => await ExecuteApplicationAsync(
                    plan,
                    pending.ApplicationId,
                    pending.ApplicationTargetBinding,
                    effectiveRequest,
                    cancellationToken)
                .ConfigureAwait(false),
            UniversalIntentKind.OpenWebsite => await ExecuteWebsiteAsync(
                plan, effectiveRequest, cancellationToken).ConfigureAwait(false),
            UniversalIntentKind.ApplicationSearch => await ExecuteApplicationSearchAsync(
                plan, pending, effectiveRequest, cancellationToken).ConfigureAwait(false),
            UniversalIntentKind.OpenFile => await ExecuteDesktopAsync(
                plan, effectiveRequest, "OpenFile", plan.Target!, null, cancellationToken).ConfigureAwait(false),
            UniversalIntentKind.FindDesktopFile => ExecuteFindDesktopFiles(plan, cancellationToken),
            UniversalIntentKind.SearchForeground => await ExecuteDesktopAsync(
                plan, effectiveRequest, "SearchForeground", plan.Target!, pending.Foreground, cancellationToken)
                .ConfigureAwait(false),
            UniversalIntentKind.DescribeForeground => await ExecuteWindowUnderstandingAsync(
                plan, effectiveRequest, pending.Foreground, pending.ObservationConsent, cancellationToken)
                .ConfigureAwait(false),
            _ => throw new NotSupportedException("当前版本还不能执行这类请求。")
        };
    }

    private AssistantCommandResultDto ExecuteFindDesktopFiles(IntentPlan plan, CancellationToken cancellationToken)
    {
        if (!DesktopFileRequest.TryParse(plan.OriginalText, out var request)
            || request!.Mode != DesktopFileRequestMode.Find
            || !DesktopFileRequest.IsValidName(request.Name)
            || !string.Equals(request.Name, plan.Target, StringComparison.Ordinal))
            throw new InvalidOperationException("这次查找的桌面文件名已失效，请重新说明。");
        cancellationToken.ThrowIfCancellationRequested();
        var found = _desktopFiles.Find(request.Name);
        cancellationToken.ThrowIfCancellationRequested();
        return new(plan.Kind.ToString(), found.Complete ? "Completed" : "Failed",
            DescribeDesktopFiles(found, forOpening: false), "NotApplicable");
    }

    private static string DescribeDesktopFiles(DesktopFileSearchResult found, bool forOpening)
    {
        if (!found.Complete)
            return $"{found.Message}\n尚未完成完整查找，没有打开任何文件。";
        if (found.TotalMatches == 0)
            return $"{found.Message}\n没有打开任何文件。";
        var visible = found.Candidates.Take(8).ToArray();
        var names = string.Join("\n", visible.Select((file, index) => $"{index + 1}. {Path.GetFileName(file.FullPath)}"));
        var remainder = found.TotalMatches > visible.Length ? $"\n这里只列出前 {visible.Length} 项。" : string.Empty;
        var next = forOpening
            ? "请点击“选择文件”指定这次要打开的桌面文件，确认后才会打开。"
            : "如需打开，请再明确说出要打开的文件名。";
        return $"找到 {found.TotalMatches} 个匹配的桌面文档：\n{names}{remainder}\n没有打开任何文件。{next}";
    }

    public bool CancelWindowObservation(string operationId)
    {
        if (string.IsNullOrWhiteSpace(operationId))
        {
            return false;
        }

        if (!_windowObservations.TryGetValue(operationId, out var cancellation))
        {
            return false;
        }

        try
        {
            cancellation.Cancel();
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    private async Task<AssistantCommandResultDto> ExecuteConversationAsync(
        IntentPlan plan,
        ExecuteAssistantCommandRequestDto request,
        CancellationToken cancellationToken)
    {
        var title = plan.OriginalText.Length <= 36
            ? plan.OriginalText
            : plan.OriginalText[..36] + "…";
        var conversation = await conversations.CreateAsync(title, cancellationToken)
            .ConfigureAwait(false);
        var result = await conversations.SendAsync(
                conversation.Id,
                plan.OriginalText,
                request.IdempotencyKey,
                cancellationToken)
            .ConfigureAwait(false);
        return new AssistantCommandResultDto(
            plan.Kind.ToString(),
            "Processing",
            "已开始回答，不会操作电脑。",
            "NotApplicable",
            ConversationId: result.ConversationId,
            WasDuplicate: result.WasDuplicate);
    }

    private async Task<AssistantCommandResultDto> ExecuteCodingTaskAsync(
        IntentPlan plan,
        ExecuteAssistantCommandRequestDto request,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(plan.Target, out var projectId))
        {
            throw new InvalidDataException("编程任务缺少有效的授权项目。 ");
        }

        var result = await tasks.CreateTaskAsync(
                new CreateLocalTaskRequest(
                    projectId,
                    plan.OriginalText,
                    IdempotencyKey: request.IdempotencyKey),
                cancellationToken)
            .ConfigureAwait(false);
        return new AssistantCommandResultDto(
            plan.Kind.ToString(),
            "Processing",
            "编程任务已交给授权项目中的编程助手，执行过程和结果证据会持续记录。",
            "Pending",
            TaskId: result.TaskId,
            WasDuplicate: result.WasDuplicate);
    }

    private Task<AssistantCommandResultDto> ExecuteApplicationAsync(
        IntentPlan plan,
        string? applicationId,
        string? applicationTargetBinding,
        ExecuteAssistantCommandRequestDto request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(applicationId)
            || string.IsNullOrWhiteSpace(applicationTargetBinding))
        {
            throw new InstalledApplicationResolutionException(
                InstalledApplicationErrorCodes.TargetChanged,
                "已确认的应用目标已经失效，请重新说出应用名称并确认。");
        }

        return ExecuteDesktopAsync(
            plan,
            request,
            "OpenApplication",
            applicationId,
            null,
            cancellationToken,
            applicationTargetBinding: applicationTargetBinding);
    }

    private async Task<AssistantCommandResultDto> ExecuteApplicationSearchAsync(
        IntentPlan plan,
        PendingPlan pending,
        ExecuteAssistantCommandRequestDto request,
        CancellationToken cancellationToken)
    {
        var workflow = pending.ApplicationSearch
            ?? throw new InvalidDataException("应用搜索计划缺少本次操作范围。");
        var binding = pending.ApplicationTargetBinding
            ?? throw new InvalidDataException("应用搜索计划缺少可信应用身份。");
        cancellationToken.ThrowIfCancellationRequested();
        var outcome = await desktopActions.ExecuteApplicationSearchWorkflowAsync(
            new ExecuteDesktopActionRequestDto(
                "SearchApplication", workflow.ApplicationId, request.Confirmed,
                request.IdempotencyKey, AuthorizationSource: request.AuthorizationSource),
            workflow, binding, cancellationToken).ConfigureAwait(false);
        var result = outcome.Result;
        var verified = outcome.Evidence;
        var facts = new List<string>();
        if (verified?.ApplicationVerified == true) facts.Add("已核对并打开本次指定的应用窗口");
        if (verified?.QuerySubmitted == true) facts.Add(verified.SubmissionMode == "LiveFilter"
            ? "已输入搜索词，应用实时筛选" : "搜索内容已准确写入明确识别的搜索框并提交一次");
        if (verified?.ResultsVerified == true)
            facts.Add(verified.NoResults ? "界面已明确显示没有匹配结果" : "已在目标应用中检查到本次搜索结果");
        if (verified?.FollowUpVerified == true)
            facts.Add(workflow.FollowUp == ApplicationSearchFollowUp.PlayFirst
                ? "已核对第一条结果的播放状态" : "已核对第一条结果打开后的界面");
        var status = result.Succeeded ? "WorkflowVerified" : "NotVerified";
        return new AssistantCommandResultDto(
            plan.Kind.ToString(), result.Succeeded ? "Completed" : "Failed", result.Message,
            status, CommandId: result.CommandId, WasDuplicate: result.WasDuplicate,
            Evidence: new AssistantActionEvidenceDto(
                Guid.NewGuid(), status, result.Message, facts,
                result.Succeeded ? Array.Empty<string>() : ["本次完整操作尚未验证成功"],
                timeProvider.GetUtcNow()));
    }

    private Task<AssistantCommandResultDto> ExecuteWebsiteAsync(
        IntentPlan plan,
        ExecuteAssistantCommandRequestDto request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(plan.PreferredApplicationName))
        {
            return ExecuteDesktopAsync(
                plan, request, "OpenWebsite", plan.Target!, null, cancellationToken);
        }

        var browser = applications.FindBrowser(plan.PreferredApplicationName)
            ?? throw new InvalidOperationException(
                $"没有找到已安装的“{plan.PreferredApplicationName}”，因此没有打开网站。 ");
        return ExecuteDesktopAsync(
            plan,
            request,
            "OpenWebsiteInApplication",
            plan.Target!,
            null,
            cancellationToken,
            browser.Id);
    }

    private async Task<AssistantCommandResultDto> ExecuteDesktopAsync(
        IntentPlan plan,
        ExecuteAssistantCommandRequestDto request,
        string actionKind,
        string target,
        ForegroundWindowSnapshot? foreground,
        CancellationToken cancellationToken,
        string? applicationId = null,
        string? applicationTargetBinding = null)
    {
        if (actionKind is "SearchForeground" or "DescribeForeground" && foreground is null)
        {
            throw new InvalidOperationException("目标窗口已经不可用，请重新切回目标软件。 ");
        }

        var desktopRequest = new ExecuteDesktopActionRequestDto(
            actionKind,
            target,
            request.Confirmed,
            request.IdempotencyKey,
            foreground?.WindowHandle,
            foreground?.WindowTitle,
            applicationId,
            request.AuthorizationSource);
        var result = foreground is null
            ? actionKind == "OpenApplication" && applicationTargetBinding is not null
                ? await desktopActions.ExecuteBoundApplicationAsync(
                    desktopRequest,
                    applicationTargetBinding,
                    cancellationToken).ConfigureAwait(false)
                : await desktopActions.ExecuteAsync(desktopRequest, cancellationToken).ConfigureAwait(false)
            : await desktopActions.ExecuteTrustedWindowAsync(
                desktopRequest,
                foreground,
                cancellationToken).ConfigureAwait(false);
        var verification = actionKind is "OpenApplication" or "SearchForeground" or "DescribeForeground"
            or "OpenWebsite" or "OpenWebsiteInApplication"
            ? "ExecutionVerified"
            : "LaunchRequested";
        var verifiedFacts = actionKind switch
        {
            "SearchForeground" => new[] { "已在本次确认的窗口中，向可靠识别的搜索框输入搜索词并执行搜索" },
            "DescribeForeground" => new[] { "只读取了目标单个窗口公开给 Windows 的控件名称" },
            "OpenApplication" => new[] { "应用窗口已经显示在用户眼前" },
            "OpenWebsite" => new[] { "浏览器窗口已经显示在前台" },
            "OpenWebsiteInApplication" => new[] { "指定浏览器窗口已经显示在前台" },
            _ => new[] { "Windows 已接受本次启动请求" }
        };
        var unverifiedFacts = actionKind switch
        {
            "SearchForeground" => new[] { "搜索结果页面是否加载完成尚未验证" },
            "DescribeForeground" => Array.Empty<string>(),
            "OpenWebsite" or "OpenWebsiteInApplication" => new[] { "网页内容是否完全加载尚未验证" },
            "OpenApplication" => new[] { "应用内部内容是否完全加载尚未验证" },
            "OpenFile" => new[] { "文件是否完成加载尚未验证" },
            _ => new[] { "最终界面状态尚未验证" }
        };
        var evidence = new AssistantActionEvidenceDto(
            Guid.NewGuid(),
            verification,
            result.Message,
            verifiedFacts,
            unverifiedFacts,
            timeProvider.GetUtcNow());
        return new AssistantCommandResultDto(
            plan.Kind.ToString(),
            result.Succeeded ? "Completed" : "Failed",
            result.Message,
            verification,
            CommandId: result.CommandId,
            WasDuplicate: result.WasDuplicate,
            Evidence: evidence);
    }

    private async Task<ProjectRecord?> FindAuthorizedProjectAsync(
        Guid? projectId,
        CancellationToken cancellationToken)
    {
        if (projectId is null)
        {
            return null;
        }

        return (await tasks.GetAuthorizedProjectsAsync(cancellationToken).ConfigureAwait(false))
            .SingleOrDefault(project => project.Id == projectId.Value);
    }

    private async Task<AssistantCommandResultDto> ExecuteWindowUnderstandingAsync(
        IntentPlan plan,
        ExecuteAssistantCommandRequestDto request,
        ForegroundWindowSnapshot? foreground,
        bool observationConsent,
        CancellationToken cancellationToken)
    {
        if (foreground is null)
        {
            throw new InvalidOperationException("目标窗口已经不可用，请重新切回目标软件。 ");
        }

        if (!request.Confirmed || !observationConsent)
        {
            throw new UnauthorizedAccessException("需要你在界面中明确同意并确认本次窗口查看。 ");
        }

        var operationId = request.IdempotencyKey?.Trim();
        if (string.IsNullOrWhiteSpace(operationId))
        {
            throw new InvalidDataException("窗口查看缺少本次操作编号。 ");
        }

        using var observationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (!_windowObservations.TryAdd(operationId, observationCancellation))
        {
            throw new InvalidOperationException("同一个窗口查看操作已经在进行中。 ");
        }

        WindowUnderstandingResult result;
        try
        {
            result = await windowUnderstanding.DescribeAsync(
                    foreground,
                    explicitConsent: true,
                    observationCancellation.Token)
                .ConfigureAwait(false);
        }
        finally
        {
            _windowObservations.TryRemove(operationId, out _);
        }
        var evidence = new AssistantActionEvidenceDto(
            Guid.NewGuid(),
            "ObservationCompleted",
            result.UserSummary,
            [
                "只读取了确认卡中显示的单个目标窗口",
                "画面只在内存中用于本机分析并已清理",
                "未截取整个桌面，也未把图像发送到云端"
            ],
            result.Limitations,
            timeProvider.GetUtcNow());
        return new AssistantCommandResultDto(
            plan.Kind.ToString(),
            "Completed",
            result.UserSummary,
            "ObservationCompleted",
            Evidence: evidence);
    }

    private static void RequireAllowedApplication(KnownDesktopApplication application)
    {
        if (!InstalledApplicationCatalog.IsAllowedLaunchTarget(application))
            throw new InstalledApplicationResolutionException(
                InstalledApplicationErrorCodes.TargetNotAllowed,
                "这个应用目标不符合安全启动要求，本次没有打开任何程序。");
    }

    private static string? NormalizeSelectedFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var fullPath = Path.GetFullPath(path.Trim());
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("刚才选择的文件已经不存在。", fullPath);
        }

        return fullPath;
    }

    private static string NormalizeInputModality(string? value) =>
        string.Equals(value, "Voice", StringComparison.OrdinalIgnoreCase)
            ? "Voice"
            : string.Equals(value, "ProgrammingTask", StringComparison.OrdinalIgnoreCase)
                ? "ProgrammingTask"
                : "Text";

    private static bool IsDirectVoiceAction(UniversalIntentKind kind) =>
        kind is UniversalIntentKind.OpenApplication
            or UniversalIntentKind.OpenWebsite
            or UniversalIntentKind.SearchForeground
            or UniversalIntentKind.ApplicationSearch;

    private AssistantIntentPlanDto Map(
        IntentPlan plan,
        ForegroundWindowSnapshot? foreground,
        string? plannedApplicationId = null,
        string? applicationSearchTarget = null,
        string? desktopTaskTarget = null)
    {
        var canonicalTarget = plan.Kind switch
        {
            UniversalIntentKind.DesktopTask => desktopTaskTarget,
            UniversalIntentKind.OpenApplication => plannedApplicationId,
            UniversalIntentKind.ApplicationSearch => applicationSearchTarget,
            UniversalIntentKind.OpenWebsite when Uri.TryCreate(
                plan.Target,
                UriKind.Absolute,
                out var website) => website.AbsoluteUri,
            _ => plan.Target
        };
        return new AssistantIntentPlanDto(
            plan.Id,
            plan.Kind.ToString(),
            plan.Readiness.ToString(),
            plan.UserSummary,
            plan.RequiresConfirmation,
            plan.ConfirmationText,
            plan.MissingContext,
            plan.ExpiresAtUtc,
            foreground is null
                ? null
                : new ForegroundApplicationDto(
                    foreground.WindowHandle,
                    foreground.WindowTitle,
                    foreground.ProcessName,
                    foreground.ObservedAtUtc),
            canonicalTarget);
    }

    private void RemoveExpired()
    {
        var now = timeProvider.GetUtcNow();
        foreach (var pair in _plans)
        {
            if (pair.Value.Plan.ExpiresAtUtc is not null
                && pair.Value.Plan.ExpiresAtUtc <= now)
            {
                _plans.TryRemove(pair.Key, out _);
            }
        }
    }

    private sealed record PendingPlan(
        IntentPlan Plan,
        ForegroundWindowSnapshot? Foreground,
        bool ObservationConsent,
        string InputModality,
        string? ApplicationId,
        string? ApplicationTargetBinding,
        DesktopFileBinding? DesktopFile = null,
        ApplicationSearchWorkflow? ApplicationSearch = null,
        Guid? SessionTurnId = null,
        DesktopTaskPlan? DesktopTask = null);
}
