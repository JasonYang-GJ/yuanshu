using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using ScreenGuide.AI.Core;
using ScreenGuide.DesktopHost.Runtime;
using ScreenGuide.DesktopProtocol;
using ScreenGuide.Skills.Abstractions;
using ScreenGuide.Skills.Windows;

namespace ScreenGuide.DesktopHost.Tests;

public sealed class DesktopVisualTaskTests
{
    private const string Instruction = "在当前窗口看图点击绿色方案，再点击生成预览";

    [Theory]
    [InlineData("在当前窗口看图点击绿色方案", true)]
    [InlineData("使用测试软件，根据画面选择绿色方案", true)]
    [InlineData("在当前窗口点击绿色方案", false)]
    public void ExplicitVisualRoutingPreservesApplicationAndTask(string text, bool visual)
    {
        Assert.True(DesktopTaskRequest.TryParse(text, out var task));
        Assert.Equal(visual, task!.PrefersVisual);
        if (text.StartsWith("使用", StringComparison.Ordinal)) Assert.Equal("测试软件", task.ApplicationName);
        else Assert.True(task.UsesCurrentWindow);
    }

    [Theory]
    [InlineData("不要在当前窗口看图点击删除")]
    [InlineData("在当前窗口看图点击按钮会怎样？")]
    [InlineData("在当前窗口不要看图点击按钮")]
    [InlineData("使用测试软件，看图点击按钮，但不要执行")]
    public void VisualWordingDoesNotTurnNegationOrQuestionsIntoActions(string text) =>
        Assert.False(DesktopTaskRequest.TryParse(text, out _));

    [Theory]
    [InlineData("action", "0")]
    [InlineData("action", "click")]
    [InlineData("action", "RunScript")]
    [InlineData("text", "unrequested input")]
    [InlineData("evidenceText", "invented result")]
    public void StrictParserRejectsUnexpectedValues(string field, string value)
    {
        var snapshot = Rig.Snapshot(0);
        var proposal = JsonNode.Parse(Json(snapshot))!.AsObject();
        proposal[field] = value;
        Assert.Throws<DesktopTaskException>(() => StrictDesktopVisualProposalParser.Parse(proposal.ToJsonString(), snapshot));
    }

    [Fact]
    public void StrictParserBindsExactSnapshotAndInImageBounds()
    {
        var snapshot = Rig.Snapshot(0);
        Assert.Equal(DesktopVisualActionKind.Click, StrictDesktopVisualProposalParser.Parse(Json(snapshot), snapshot).Action);
        Assert.Throws<DesktopTaskException>(() => StrictDesktopVisualProposalParser.Parse(Json(snapshot), snapshot with { Id = Guid.NewGuid() }));
        foreach (var bad in new[] { new DesktopVisualBounds(-1, 0, 20, 20), new(999, 0, 20, 20), new(0, 999, 20, 20), new(0, 0, 0, 20), new(int.MaxValue, 0, int.MaxValue, 20) })
        {
            var json = JsonNode.Parse(Json(snapshot))!.AsObject();
            json["bounds"] = JsonSerializer.SerializeToNode(new { x = bad.X, y = bad.Y, width = bad.Width, height = bad.Height });
            Assert.Throws<DesktopTaskException>(() => StrictDesktopVisualProposalParser.Parse(json.ToJsonString(), snapshot));
        }
        Assert.Throws<DesktopTaskException>(() => StrictDesktopVisualProposalParser.Parse(Json(snapshot).Insert(1, "\"action\":\"Click\","), snapshot));
    }

    [Theory]
    [InlineData(980, 680, 362, 319, 236, 83)]
    [InlineData(1600, 900, 592, 423, 384, 108)]
    [InlineData(800, 1600, 296, 752, 192, 192)]
    public void RelativeCoordinatesConvertUsingTrustedImageDimensions(int width, int height, int x, int y, int w, int h)
    {
        var snapshot = Rig.Snapshot(0) with { ImageWidth = width, ImageHeight = height };
        var json = JsonNode.Parse(Json(snapshot))!.AsObject();
        json["bounds"] = JsonSerializer.SerializeToNode(new { x = 370, y = 470, width = 240, height = 120 });
        Assert.Equal(new DesktopVisualBounds(x, y, w, h), StrictDesktopVisualProposalParser.Parse(json.ToJsonString(), snapshot).Bounds);
        json["coordinateSpace"] = "pixels";
        Assert.Throws<DesktopTaskException>(() => StrictDesktopVisualProposalParser.Parse(json.ToJsonString(), snapshot));
        json.Remove("coordinateSpace");
        Assert.Throws<DesktopTaskException>(() => StrictDesktopVisualProposalParser.Parse(json.ToJsonString(), snapshot));
    }

    [Fact]
    public void CompletionRequiresNewSpecificTextAndAnActualAction()
    {
        var initial = Rig.Snapshot(0);
        var current = Rig.Snapshot(2);
        var done = new DesktopVisualProposal(current.Id, DesktopVisualActionKind.Finish, null, null, "", "看到了结果", "预览：绿色方案已选择");
        Assert.True(DesktopVisualTaskExecutionService.HasCompletionEvidence(initial, current, done, 2));
        Assert.False(DesktopVisualTaskExecutionService.HasCompletionEvidence(initial, current, done, 0));
        Assert.False(DesktopVisualTaskExecutionService.HasCompletionEvidence(current, current, done, 2));
        Assert.False(DesktopVisualTaskExecutionService.HasCompletionEvidence(initial, current, done with { EvidenceText = "绿色方案" }, 2));
        Assert.False(DesktopVisualTaskExecutionService.HasCompletionEvidence(initial, current, done with { EvidenceText = "预览：蓝色方案已选择" }, 2));
    }

    [Fact]
    public async Task WorkerOutputCannotAllocatePastItsBound()
    {
        Assert.Equal("safe", await DesktopVisualWorkerClient.ReadBoundedAsync(new StringReader("safe"), 4, CancellationToken.None));
        await Assert.ThrowsAsync<DesktopTaskException>(() => DesktopVisualWorkerClient.ReadBoundedAsync(new StringReader(new string('x', 5000)), 100, CancellationToken.None));
    }

    [Theory]
    [InlineData("Text")]
    [InlineData("Voice")]
    public async Task OneTurnUsesSeparateImageAndPerActionConsentThenFreshResult(string modality)
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var rig = new Rig();
        using var host = environment.BuildHost(s => rig.Register(s));
        await host.StartAsync();
        var client = new DesktopApiClient(environment.Options.PipeName);
        await client.SetChatRouteAsync(new("qwen", "qwen3.7-plus"));
        var session = await client.StartNewSessionAsync("视觉任务闭环");
        var submitted = await client.SubmitSessionInputAsync(new(Instruction, modality, "visual-complete", session.SessionId));
        var imageConsent = await Wait(client, submitted.TurnId, "TaskObservation");
        Assert.Contains("截图", imageConsent.ResultSummary);
        Assert.DoesNotContain("不会截图", imageConsent.ResultSummary);
        Assert.Equal(0, rig.Observations);
        Assert.Equal(0, rig.TextObservations);
        await Respond(client, session.SessionId, imageConsent, true);
        for (var i = 0; i < 2; i++)
        {
            var action = await Wait(client, submitted.TurnId, "TaskAction");
            Assert.Equal(i, rig.Inputs);
            Assert.NotEqual(imageConsent.WindowConsent!.ConsentId, action.WindowConsent!.ConsentId);
            await Assert.ThrowsAsync<DesktopApiException>(() => Respond(client, session.SessionId, imageConsent, true));
            await Respond(client, session.SessionId, action, true);
        }
        var resultReview = await Wait(client, submitted.TurnId, "TaskAction");
        Assert.Contains("请核对本次任务结果", resultReview.ResultSummary);
        Assert.Contains("不能单独证明整个任务完成", resultReview.ResultSummary);
        await Respond(client, session.SessionId, resultReview, true);
        var done = await Wait(client, submitted.TurnId, "Completed");
        Assert.Equal(2, rig.Inputs);
        Assert.Equal(5, rig.Observations); // initial, two results, two final verification reads
        Assert.Contains("预览：绿色方案已选择", done.ResultSummary);
        Assert.Single((await client.GetCurrentSessionAsync())!.Turns);
        Assert.All(rig.Turns, id => Assert.Equal(submitted.TurnId, id));
        await host.StopAsync();
    }

    [Theory]
    [InlineData("TaskObservation", false)]
    [InlineData("TaskObservation", true)]
    [InlineData("TaskAction", false)]
    [InlineData("TaskAction", true)]
    public async Task DeclineOrStopPreventsCaptureAndLaterInput(string boundary, bool stop)
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var rig = new Rig();
        using var host = environment.BuildHost(s => rig.Register(s));
        await host.StartAsync();
        var client = new DesktopApiClient(environment.Options.PipeName);
        await client.SetChatRouteAsync(new("qwen", "qwen3.7-plus"));
        var session = await client.StartNewSessionAsync("停止视觉任务");
        var submitted = await client.SubmitSessionInputAsync(new(Instruction, "Voice", "visual-stop", session.SessionId));
        var waiting = await Wait(client, submitted.TurnId, "TaskObservation");
        if (boundary == "TaskAction")
        {
            await Respond(client, session.SessionId, waiting, true);
            waiting = await Wait(client, submitted.TurnId, "TaskAction");
        }
        if (stop) await client.CancelSessionTurnAsync(session.SessionId, submitted.TurnId);
        else await Respond(client, session.SessionId, waiting, false);
        await Wait(client, submitted.TurnId, "Cancelled");
        await Assert.ThrowsAsync<DesktopApiException>(() => Respond(client, session.SessionId, waiting, true));
        Assert.Equal(0, rig.Inputs);
        Assert.Equal(boundary == "TaskAction" ? 1 : 0, rig.Observations);
        await WaitForPreviewClosed(rig);
        await host.StopAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnrenderedOrClosedPreviewCannotAuthorizeInput(bool closeAfterRender)
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var rig = new Rig { PreviewVisible = closeAfterRender };
        using var host = environment.BuildHost(s => rig.Register(s));
        await host.StartAsync();
        var client = new DesktopApiClient(environment.Options.PipeName);
        await client.SetChatRouteAsync(new("qwen", "qwen3.7-plus"));
        var session = await client.StartNewSessionAsync("必须显示精确视觉目标");
        var submitted = await client.SubmitSessionInputAsync(new(Instruction, "Text", "preview-visibility", session.SessionId));
        await Respond(client, session.SessionId, await Wait(client, submitted.TurnId, "TaskObservation"), true);
        if (closeAfterRender)
        {
            var action = await Wait(client, submitted.TurnId, "TaskAction");
            Assert.Equal(1, rig.OpenPreviews);
            rig.PreviewVisible = false;
            await Respond(client, session.SessionId, action, true);
        }
        await Wait(client, submitted.TurnId, "Failed");
        Assert.Equal(0, rig.Inputs);
        await WaitForPreviewClosed(rig);
        await host.StopAsync();
    }

    [Fact]
    public async Task RestartNeverRestoresImagesOrReplaysVisualInput()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var original = new Rig();
        using var firstHost = environment.BuildHost(s => original.Register(s));
        await firstHost.StartAsync();
        var client = new DesktopApiClient(environment.Options.PipeName);
        await client.SetChatRouteAsync(new("qwen", "qwen3.7-plus"));
        var session = await client.StartNewSessionAsync("视觉确认重启");
        var submitted = await client.SubmitSessionInputAsync(new(Instruction, "Voice", "visual-restart", session.SessionId));
        await Respond(client, session.SessionId, await Wait(client, submitted.TurnId, "TaskObservation"), true);
        var action = await Wait(client, submitted.TurnId, "TaskAction");
        var coordinator = firstHost.Services.GetRequiredService<SessionCoordinator>();
        firstHost.Dispose();
        var restoredRig = new Rig();
        using var restoredHost = environment.BuildHost(s => restoredRig.Register(s));
        await restoredHost.StartAsync();
        var restoredClient = new DesktopApiClient(environment.Options.PipeName);
        var restored = Assert.Single((await restoredClient.SetCurrentSessionAsync(session.SessionId)).Turns);
        Assert.Equal("Interrupted", restored.Phase);
        Assert.Null(restored.WindowConsent);
        await Assert.ThrowsAsync<DesktopApiException>(() => Respond(restoredClient, session.SessionId, action, true));
        Assert.Equal(0, original.Inputs);
        Assert.Equal(0, restoredRig.Observations);
        Assert.Equal(0, restoredRig.Inputs);
        await coordinator.StopAsync();
        await WaitForPreviewClosed(original);
        await restoredHost.StopAsync();
    }

    private static async Task WaitForPreviewClosed(Rig rig)
    {
        for (var i = 0; i < 100 && rig.OpenPreviews != 0; i++) await Task.Delay(10);
        Assert.Equal(0, rig.OpenPreviews);
    }

    [Fact]
    public async Task ChangedFrameAfterConfirmationDoesNotSendInput()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var rig = new Rig();
        using var host = environment.BuildHost(s => rig.Register(s));
        await host.StartAsync();
        var client = new DesktopApiClient(environment.Options.PipeName);
        await client.SetChatRouteAsync(new("qwen", "qwen3.7-plus"));
        var session = await client.StartNewSessionAsync("拒绝旧画面");
        var submitted = await client.SubmitSessionInputAsync(new(Instruction, "Text", "visual-changed", session.SessionId));
        await Respond(client, session.SessionId, await Wait(client, submitted.TurnId, "TaskObservation"), true);
        var action = await Wait(client, submitted.TurnId, "TaskAction");
        rig.Changed = true;
        await Respond(client, session.SessionId, action, true);
        await Wait(client, submitted.TurnId, "Failed");
        Assert.Equal(0, rig.Inputs);
        await host.StopAsync();
    }

    [Fact]
    public async Task WrongResultNeverCompletesAutomaticallyAndCanBeRejected()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var rig = new Rig { WrongResult = true };
        using var host = environment.BuildHost(s => rig.Register(s));
        await host.StartAsync();
        var client = new DesktopApiClient(environment.Options.PipeName);
        await client.SetChatRouteAsync(new("qwen", "qwen3.7-plus"));
        var session = await client.StartNewSessionAsync("必须由用户判断未知结果");
        var submitted = await client.SubmitSessionInputAsync(new(Instruction, "Text", "wrong-result", session.SessionId));
        await Respond(client, session.SessionId, await Wait(client, submitted.TurnId, "TaskObservation"), true);
        for (var i = 0; i < 2; i++) await Respond(client, session.SessionId, await Wait(client, submitted.TurnId, "TaskAction"), true);
        var review = await Wait(client, submitted.TurnId, "TaskAction");
        Assert.Contains("原任务：" + Instruction, review.ResultSummary);
        Assert.Contains("预览：蓝色方案已选择", review.ResultSummary);
        Assert.Equal("WaitingForWindowConsent", review.Phase);
        await Respond(client, session.SessionId, review, false);
        await Wait(client, submitted.TurnId, "Cancelled");
        Assert.Equal(2, rig.Inputs);
        await host.StopAsync();
    }

    [Fact]
    public async Task UnsupportedTextControlsAskForNewImageConsentWithinTheOriginalTurn()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var rig = new Rig();
        using var host = environment.BuildHost(s => rig.Register(s));
        await host.StartAsync();
        var client = new DesktopApiClient(environment.Options.PipeName);
        await client.SetChatRouteAsync(new("qwen", "qwen3.7-plus"));
        var session = await client.StartNewSessionAsync("无法读控件时申请图片许可");
        var submitted = await client.SubmitSessionInputAsync(new("在当前窗口点击绿色方案", "Text", "visual-fallback", session.SessionId));
        var textConsent = await Wait(client, submitted.TurnId, "TaskObservation");
        Assert.Contains("不会截图", textConsent.ResultSummary);
        await Respond(client, session.SessionId, textConsent, true);
        UnifiedSessionTurnDto imageConsent;
        do { await Task.Delay(20); imageConsent = await Wait(client, submitted.TurnId, "TaskObservation"); }
        while (imageConsent.WindowConsent!.ConsentId == textConsent.WindowConsent!.ConsentId);
        Assert.Contains("窗口图片", imageConsent.ResultSummary);
        Assert.Equal(0, rig.Observations);
        Assert.Equal(1, rig.TextObservations);
        await Assert.ThrowsAsync<DesktopApiException>(() => Respond(client, session.SessionId, textConsent, true));
        await Respond(client, session.SessionId, imageConsent, false);
        await Wait(client, submitted.TurnId, "Cancelled");
        Assert.Equal(0, rig.Inputs);
        Assert.Single((await client.GetCurrentSessionAsync())!.Turns);
        await host.StopAsync();
    }

    [Fact]
    public async Task ReplacementInvalidatesOriginalVisualConsent()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var rig = new Rig();
        using var host = environment.BuildHost(s => rig.Register(s));
        await host.StartAsync();
        var client = new DesktopApiClient(environment.Options.PipeName);
        await client.SetChatRouteAsync(new("qwen", "qwen3.7-plus"));
        var session = await client.StartNewSessionAsync("替换视觉任务");
        var first = await client.SubmitSessionInputAsync(new(Instruction, "Voice", "visual-old", session.SessionId));
        await Respond(client, session.SessionId, await Wait(client, first.TurnId, "TaskObservation"), true);
        var oldAction = await Wait(client, first.TurnId, "TaskAction");
        var next = await client.SubmitSessionInputAsync(new(Instruction, "Voice", "visual-new", session.SessionId));
        await Wait(client, first.TurnId, "Cancelled");
        var nextConsent = await Wait(client, next.TurnId, "TaskObservation");
        Assert.NotEqual(oldAction.WindowConsent!.ConsentId, nextConsent.WindowConsent!.ConsentId);
        await Assert.ThrowsAsync<DesktopApiException>(() => Respond(client, session.SessionId, oldAction, true));
        Assert.Equal(0, rig.Inputs);
        await Respond(client, session.SessionId, nextConsent, false);
        await Wait(client, next.TurnId, "Cancelled");
        await host.StopAsync();
    }

    private static string Json(DesktopVisualSnapshot s) => JsonSerializer.Serialize(new {
        snapshotId = s.Id, coordinateSpace = "normalized1000", action = "Click", bounds = new { x = 10, y = 10, width = 50, height = 30 },
        text = (string?)null, target = "绿色方案", reason = "选择目标", evidenceText = (string?)null });
    private static Task<SessionSnapshotDto> Respond(DesktopApiClient client, Guid session, UnifiedSessionTurnDto turn, bool granted) =>
        client.RespondSessionWindowConsentAsync(session, turn.Id, granted, consentId: turn.WindowConsent!.ConsentId);
    private static async Task<UnifiedSessionTurnDto> Wait(DesktopApiClient client, Guid id, string expected)
    {
        var until = DateTime.UtcNow.AddSeconds(12);
        while (DateTime.UtcNow < until)
        {
            var turn = (await client.GetCurrentSessionAsync())?.Turns.SingleOrDefault(t => t.Id == id);
            if (turn is not null && (turn.Phase == expected || turn.Phase == "WaitingForWindowConsent" && turn.WindowConsent?.Purpose == expected)) return turn;
            if (turn?.Phase is "Failed" or "Completed" or "Cancelled" or "Interrupted") throw new InvalidOperationException($"Expected {expected}, got {turn.Phase}: {turn.FailureMessage}");
            await Task.Delay(20);
        }
        throw new TimeoutException(expected);
    }

    private sealed class Rig : IDesktopVisualAutomation, IDesktopTaskAutomation, IDesktopTaskWindowActivator,
        IForegroundWindowContextProvider, IDesktopVisualStepPlanner, IDesktopTaskStepPlanner, IDesktopVisualTargetPreview
    {
        internal static readonly DesktopTaskWindow Window = new(6412, 123, "fixture", new(2026, 8, 17, 8, 0, 0, TimeSpan.Zero),
            @"C:\synthetic\fixture.exe", "VisualFixture", "合成看图窗口");
        private int observations, inputs, textObservations;
        internal int Observations => Volatile.Read(ref observations);
        internal int TextObservations => Volatile.Read(ref textObservations);
        internal int Inputs => Volatile.Read(ref inputs);
        internal bool Changed { get; set; }
        internal bool WrongResult { get; set; }
        internal bool PreviewVisible { get; set; } = true;
        internal int OpenPreviews { get; private set; }
        internal List<Guid> Turns { get; } = [];
        internal void Register(IServiceCollection services)
        {
            services.AddSingleton<IDesktopVisualAutomation>(this);
            services.AddSingleton<IDesktopTaskAutomation>(this);
            services.AddSingleton<IDesktopTaskWindowActivator>(this);
            services.AddSingleton<IForegroundWindowContextProvider>(this);
            services.AddSingleton<IDesktopVisualStepPlanner>(this);
            services.AddSingleton<IDesktopTaskStepPlanner>(this);
            services.AddSingleton<IDesktopVisualTargetPreview>(this);
        }
        internal static DesktopVisualSnapshot Snapshot(int state) => new(Guid.NewGuid(), Window, new(100, 100, 400, 300),
            400, 300, 400, 300, 96, "frame-" + state, new string('A', 64),
            state == 2 ? "绿色方案 蓝色方案 生成预览 预览：绿色方案已选择" : "绿色方案 蓝色方案 生成预览", DateTimeOffset.UtcNow);
        public Task<DesktopVisualObservation> ObserveAsync(DesktopTaskWindow window, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Interlocked.Increment(ref observations);
            var snapshot = Snapshot(Inputs);
            if (WrongResult && Inputs >= 2) snapshot = snapshot with { OcrText = "绿色方案 蓝色方案 生成预览 预览：蓝色方案已选择" };
            return Task.FromResult(new DesktopVisualObservation(snapshot, [1, 2, 3]));
        }
        Task<DesktopTaskSnapshot> IDesktopTaskAutomation.ObserveAsync(DesktopTaskWindow window, CancellationToken token)
        { Interlocked.Increment(ref textObservations); return Task.FromResult(new DesktopTaskSnapshot(Guid.NewGuid(), Window, [], false)); }
        public Task<DesktopVisualReceipt> ExecuteAsync(DesktopVisualSnapshot snapshot, DesktopVisualProposal action, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (Changed) throw new DesktopTaskException("desktop_visual_frame_changed", "画面已变");
            Interlocked.Increment(ref inputs);
            return Task.FromResult(new DesktopVisualReceipt(true, "已输入"));
        }
        public Task<DesktopTaskActionReceipt> ExecuteAsync(DesktopTaskWindow window, DesktopTaskControl control, DesktopTaskProposal action,
            CancellationToken token, string? reviewedContextFingerprint = null) => throw new InvalidOperationException("No UIA input expected.");
        public Task<DesktopTaskWindow> BindAsync(long handle, CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.FromResult(Window); }
        public Task ResumeAsync(DesktopTaskWindow window, CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.CompletedTask; }
        public ForegroundWindowSnapshot? GetLastExternalWindow() => new(Window.Handle, Window.Title, Window.ProcessName, Window.ProcessId, Window.ProcessStartedAtUtc, DateTimeOffset.UtcNow);
        public ForegroundWindowSnapshot? ResolveWindow(long handle) => handle == Window.Handle ? GetLastExternalWindow() : null;
        public Task<DesktopVisualProposal> PlanAsync(Guid turn, FrozenChatModelRoute route, string instruction,
            DesktopVisualObservation observation, IReadOnlyList<string> history, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); Turns.Add(turn);
            var s = observation.Snapshot;
            return Task.FromResult(Inputs >= 2
                ? new DesktopVisualProposal(s.Id, DesktopVisualActionKind.Finish, null, null, "", "已经看到结果", WrongResult ? "预览：蓝色方案已选择" : "预览：绿色方案已选择")
                : new(s.Id, DesktopVisualActionKind.Click, new(10 + Inputs * 60, 10, 50, 30), null, Inputs == 0 ? "绿色方案" : "生成预览", "执行用户目标"));
        }
        public Task<DesktopTaskProposal> PlanAsync(Guid turn, FrozenChatModelRoute route, string instruction,
            DesktopTaskSnapshot snapshot, IReadOnlyList<string> history, CancellationToken cancellationToken = default) =>
            Task.FromResult(new DesktopTaskProposal(snapshot.Id, DesktopTaskActionKind.Ask, null, null, "未找到控件"));
        public Task<IDesktopVisualPreviewLease> ShowAsync(DesktopVisualObservation observation, DesktopVisualProposal proposal, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Assert.Equal(observation.Snapshot.Id, proposal.SnapshotId);
            OpenPreviews++;
            return Task.FromResult<IDesktopVisualPreviewLease>(new PreviewLease(this));
        }
        private sealed class PreviewLease(Rig owner) : IDesktopVisualPreviewLease
        {
            private bool closed;
            public bool IsVisible => !closed && owner.PreviewVisible;
            public void Dispose() { if (!closed) { closed = true; owner.OpenPreviews--; } }
        }
    }
}
