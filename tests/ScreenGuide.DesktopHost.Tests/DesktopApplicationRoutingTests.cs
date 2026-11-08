using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ScreenGuide.AI.Core;
using ScreenGuide.DesktopHost.Runtime;
using ScreenGuide.DesktopProtocol;
using ScreenGuide.Skills.Windows;

namespace ScreenGuide.DesktopHost.Tests;

public sealed class DesktopApplicationRoutingTests
{
    private const string ApplicationName = "星光播放器";
    private const string SpokenRequest = "帮我打开我桌面上的星光播放器";

    [Theory]
    [InlineData(SpokenRequest, "Text")]
    [InlineData(SpokenRequest, "Voice")]
    [InlineData("打开桌面上的星光播放器", "Text")]
    [InlineData("打开我的桌面上的星光播放器软件", "Text")]
    [InlineData("我说的是打开我桌面上的星光播放器", "Voice")]
    [InlineData("我的意思是打开桌面上的星光播放器", "Text")]
    public async Task DesktopQualifiedApplicationPlansAgainstTrustedCatalog(string text, string modality)
    {
        await using var fixture = await Fixture.CreateAsync([ApplicationName + ".lnk"]);
        var plan = await fixture.Client.PlanAssistantCommandAsync(new(text, InputModality: modality));

        Assert.Equal("OpenApplication", plan.IntentKind);
        Assert.Equal("Ready", plan.Readiness);
        Assert.Equal(fixture.Application.Id, plan.CanonicalTarget);
        Assert.True(plan.RequiresConfirmation);
        Assert.Null(plan.MissingContext);
        Assert.Empty(fixture.Launcher.Targets);
    }

    [Theory]
    [InlineData("蓝海浏览器")]
    [InlineData("新星搜索助手")]
    public async Task AnyRegisteredApplicationNameUsesTheSameDesktopRouting(string applicationName)
    {
        await using var fixture = await Fixture.CreateAsync([applicationName + ".lnk"], applicationName: applicationName);
        var plan = await fixture.Client.PlanAssistantCommandAsync(new($"帮我打开我桌面上的{applicationName}"));

        Assert.Equal("OpenApplication", plan.IntentKind);
        Assert.Equal(fixture.Application.Id, plan.CanonicalTarget);
        Assert.Empty(fixture.Launcher.Targets);
    }

    [Fact]
    public async Task FailedApplicationCanBeCorrectedWithoutSendingTheCorrectionToChat()
    {
        await using var fixture = await Fixture.CreateAsync(["Steam.lnk"], applicationName: "Steam");
        var session = await fixture.Client.StartNewSessionAsync("应用名纠正");
        var first = await fixture.Client.SubmitSessionInputAsync(new(
            "打开不存在的软件", "Voice", "missing-app", session.SessionId));
        await WaitForPhaseAsync(fixture.Client, first.TurnId, "Failed");
        var request = new SessionInputRequestDto("我说的是打开我桌面上的Steam", "Voice", "corrected-app", session.SessionId);
        var corrected = await fixture.Client.SubmitSessionInputAsync(request);
        var completed = await WaitForPhaseAsync(fixture.Client, corrected.TurnId, "Completed");
        var turn = Assert.Single(completed.Turns, item => item.Id == corrected.TurnId);
        Assert.Equal("OpenApplication", turn.IntentKind);
        Assert.Equal(request.Text, turn.InputText);
        Assert.Equal(fixture.Application.LaunchTarget, Assert.Single(fixture.Launcher.Targets));
        Assert.Equal(corrected.TurnId, (await fixture.Client.SubmitSessionInputAsync(request)).TurnId);
        Assert.Single(fixture.Launcher.Targets);
    }

    [Fact]
    public async Task MisspelledCorrectionReturnsLocalClarificationWithoutLaunchingOrChatting()
    {
        await using var fixture = await Fixture.CreateAsync(["Steam.lnk"], applicationName: "Steam");
        var session = await fixture.Client.StartNewSessionAsync("名称不确定");
        var input = await fixture.Client.SubmitSessionInputAsync(new(
            "我说的是打开我桌面上的STEM", "Voice", "uncertain-app", session.SessionId));
        var result = await WaitForPhaseAsync(fixture.Client, input.TurnId, "Failed");
        var turn = Assert.Single(result.Turns);
        Assert.Contains("Steam", turn.FailureMessage);
        Assert.Null(turn.ConversationTurnId);
        Assert.Empty(fixture.Launcher.Targets);
    }

    [Fact]
    public async Task SimilarApplicationSuggestionCannotReplaceAnExistingDesktopDocument()
    {
        await using var fixture = await Fixture.CreateAsync(["STEM.txt"], applicationName: "Steam");
        var plan = await fixture.Client.PlanAssistantCommandAsync(new("我说的是打开桌面上的STEM"));
        Assert.Equal("OpenFile", plan.IntentKind);
        Assert.Equal(fixture.PathFor("STEM.txt"), plan.CanonicalTarget);
        Assert.True(plan.RequiresConfirmation);
        Assert.Empty(fixture.Launcher.Targets);
    }

    [Theory]
    [InlineData("Text")]
    [InlineData("Voice")]
    public async Task OriginalTurnUsesTheExistingConfirmationAndOneShotApplicationPath(string modality)
    {
        await using var fixture = await Fixture.CreateAsync([ApplicationName + ".lnk"]);
        var session = await fixture.Client.StartNewSessionAsync("桌面应用分流");
        var request = new SessionInputRequestDto(SpokenRequest, modality, "desktop-app-once", session.SessionId);
        var input = await fixture.Client.SubmitSessionInputAsync(request);
        if (modality == "Text")
        {
            var waiting = await WaitForPhaseAsync(fixture.Client, input.TurnId, "WaitingForConfirmation");
            Assert.Equal(fixture.Application.Id, Assert.Single(waiting.Turns).PlanTarget);
            Assert.Empty(fixture.Launcher.Targets);
            await fixture.Client.ConfirmSessionTurnAsync(session.SessionId, input.TurnId, true);
        }
        var completed = await WaitForPhaseAsync(fixture.Client, input.TurnId, "Completed");
        var duplicate = await fixture.Client.SubmitSessionInputAsync(request);

        Assert.Equal(input.TurnId, duplicate.TurnId);
        Assert.Equal("OpenApplication", Assert.Single(completed.Turns).IntentKind);
        Assert.Equal(fixture.Application.LaunchTarget, Assert.Single(fixture.Launcher.Targets));
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("decline")]
    [InlineData("target-change")]
    [InlineData("id-change")]
    public async Task PendingDesktopApplicationCannotLaunchAfterStopOrTargetChange(string change)
    {
        await using var fixture = await Fixture.CreateAsync([ApplicationName + ".lnk"]);
        var session = await fixture.Client.StartNewSessionAsync("桌面应用确认边界");
        var input = await fixture.Client.SubmitSessionInputAsync(new(SpokenRequest, "Text", "bound-app", session.SessionId));
        await WaitForPhaseAsync(fixture.Client, input.TurnId, "WaitingForConfirmation");

        if (change == "cancel") await fixture.Client.CancelSessionTurnAsync(session.SessionId, input.TurnId);
        else
        {
            if (change == "target-change")
                fixture.Applications.Replacement = fixture.Application with { LaunchTarget = fixture.OtherExecutable };
            if (change == "id-change")
                fixture.Applications.Replacement = fixture.Application with { Id = "changed-app-id" };
            await fixture.Client.ConfirmSessionTurnAsync(session.SessionId, input.TurnId, change != "decline");
        }

        await WaitForPhaseAsync(fixture.Client, input.TurnId,
            change is "cancel" or "decline" ? "Cancelled" : "Failed");
        Assert.Empty(fixture.Launcher.Targets);
    }

    [Theory]
    [InlineData("打开我桌面上的文件星光播放器")]
    [InlineData("打开我桌面上的星光播放器.txt")]
    [InlineData("打开我桌面上的“星光播放器”")]
    [InlineData("查看我桌面上的星光播放器")]
    [InlineData("我说的是打开我桌面上的星光播放器.txt")]
    public async Task ExplicitDocumentReferenceNeverBecomesAnApplication(string text)
    {
        await using var fixture = await Fixture.CreateAsync([ApplicationName + ".txt"]);
        var plan = await fixture.Client.PlanAssistantCommandAsync(new(text));

        Assert.Equal("OpenFile", plan.IntentKind);
        Assert.Equal("Ready", plan.Readiness);
        Assert.Equal(fixture.PathFor(ApplicationName + ".txt"), plan.CanonicalTarget);
        Assert.Equal(0, fixture.Applications.PreferenceCalls);
        Assert.Empty(fixture.Launcher.Targets);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SelectedOrPersistedFileIntentCannotBeReplacedByAnApplication(bool persisted)
    {
        await using var fixture = await Fixture.CreateAsync([ApplicationName + ".txt"]);
        var plan = await fixture.Commands.PlanSessionAsync(
            new(SpokenRequest, SelectedFilePath: persisted ? null : fixture.PathFor(ApplicationName + ".txt")),
            Guid.NewGuid(), null, persisted ? UniversalIntentKind.OpenFile : null);

        Assert.Equal("OpenFile", plan.IntentKind);
        Assert.Equal("Ready", plan.Readiness);
        Assert.Equal(0, fixture.Applications.PreferenceCalls);
    }

    [Fact]
    public async Task PersistedFileIntentStillWaitsForAFileWhenOnlyTheApplicationExists()
    {
        await using var fixture = await Fixture.CreateAsync([ApplicationName + ".lnk"]);
        var plan = await fixture.Commands.PlanSessionAsync(new(SpokenRequest), Guid.NewGuid(), null,
            UniversalIntentKind.OpenFile);

        Assert.Equal("OpenFile", plan.IntentKind);
        Assert.Equal("NeedsContext", plan.Readiness);
        Assert.Equal(0, fixture.Applications.PreferenceCalls);
    }

    [Fact]
    public async Task DocumentAndApplicationCollisionWaitsForAnExplicitFileSelectionOnTheSameTurn()
    {
        await using var fixture = await Fixture.CreateAsync([ApplicationName + ".lnk", ApplicationName + ".txt"]);
        var session = await fixture.Client.StartNewSessionAsync("同名应用与文档");
        var input = await fixture.Client.SubmitSessionInputAsync(new(SpokenRequest, "Voice", "collision", session.SessionId));
        var waiting = await WaitForPhaseAsync(fixture.Client, input.TurnId, "WaitingForFile");
        Assert.Contains("应用", Assert.Single(waiting.Turns).ResultSummary);
        Assert.Contains("文档", waiting.Turns[0].ResultSummary);
        Assert.False(waiting.Turns[0].RequiresConfirmation);
        Assert.Empty(fixture.Launcher.Targets);

        var file = fixture.PathFor(ApplicationName + ".txt");
        await fixture.Client.ProvideSessionFileAsync(session.SessionId, input.TurnId, file);
        await WaitForPhaseAsync(fixture.Client, input.TurnId, "WaitingForConfirmation");
        await fixture.Client.ConfirmSessionTurnAsync(session.SessionId, input.TurnId, true);
        var completed = await WaitForPhaseAsync(fixture.Client, input.TurnId, "Completed");
        Assert.Equal(input.TurnId, Assert.Single(completed.Turns).Id);
        Assert.Equal("OpenFile", completed.Turns[0].IntentKind);
        Assert.Equal(file, Assert.Single(fixture.Launcher.Targets));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingApplicationRetainsDocumentResolutionWithoutExecuting(bool documentExists)
    {
        await using var fixture = await Fixture.CreateAsync(documentExists ? [ApplicationName + ".txt"] : []);
        fixture.Applications.FailureCode = InstalledApplicationErrorCodes.NotFound;
        var plan = await fixture.Client.PlanAssistantCommandAsync(new(SpokenRequest));

        Assert.Equal("OpenFile", plan.IntentKind);
        Assert.Equal(documentExists ? "Ready" : "NeedsContext", plan.Readiness);
        Assert.Empty(fixture.Launcher.Targets);
    }

    [Theory]
    [InlineData(InstalledApplicationErrorCodes.Ambiguous)]
    [InlineData(InstalledApplicationErrorCodes.TargetNotAllowed)]
    public async Task AmbiguousOrRejectedApplicationNeverFallsThroughToOpenADocument(string code)
    {
        await using var fixture = await Fixture.CreateAsync([ApplicationName + ".txt"]);
        fixture.Applications.FailureCode = code;
        var failure = await Assert.ThrowsAsync<DesktopApiException>(() =>
            fixture.Client.PlanAssistantCommandAsync(new(SpokenRequest)));

        Assert.Equal(code, failure.Error.Code);
        Assert.Empty(fixture.Launcher.Targets);
    }

    [Fact]
    public async Task UnsafeResolvedApplicationCannotProduceAReadyPlan()
    {
        await using var fixture = await Fixture.CreateAsync([ApplicationName + ".lnk"]);
        fixture.Applications.Replacement = fixture.Application with { LaunchTarget = fixture.PathFor("unsafe.cmd") };
        var failure = await Assert.ThrowsAsync<DesktopApiException>(() =>
            fixture.Client.PlanAssistantCommandAsync(new(SpokenRequest)));

        Assert.Equal(InstalledApplicationErrorCodes.TargetNotAllowed, failure.Error.Code);
        Assert.Empty(fixture.Launcher.Targets);
    }

    [Fact]
    public async Task IncompleteDesktopEnumerationCannotClaimThereIsNoConflictingDocument()
    {
        await using var fixture = await Fixture.CreateAsync([], incomplete: true);
        var plan = await fixture.Client.PlanAssistantCommandAsync(new(SpokenRequest));

        Assert.Equal("NeedsContext", plan.Readiness);
        Assert.False(plan.RequiresConfirmation);
        Assert.Empty(fixture.Launcher.Targets);
    }

    [Fact]
    public async Task CancellingDuringApplicationDiscoveryCreatesNoReadyPlan()
    {
        await using var fixture = await Fixture.CreateAsync([ApplicationName + ".lnk"]);
        using var cancellation = new CancellationTokenSource();
        fixture.Applications.AfterPreference = cancellation.Cancel;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Commands.PlanAsync(new(SpokenRequest), cancellation.Token));
        Assert.Empty(fixture.Launcher.Targets);
    }

    private static async Task<SessionSnapshotDto> WaitForPhaseAsync(IDesktopApiClient client, Guid turnId, string phase)
    {
        SessionSnapshotDto? last = null;
        for (var attempt = 0; attempt < 200; attempt++)
        {
            last = await client.GetCurrentSessionAsync();
            if (last?.Turns.SingleOrDefault(turn => turn.Id == turnId)?.Phase == phase) return last;
            await Task.Delay(25);
        }
        Assert.Fail($"Expected {phase}; got {last?.Turns.SingleOrDefault(turn => turn.Id == turnId)?.Phase}.");
        return last!;
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(DesktopHostTestEnvironment environment, string desktop,
            RecordingCatalog applications, KnownDesktopApplication application, string otherExecutable,
            IDesktopFileCatalog files)
        {
            Environment = environment;
            Desktop = desktop;
            Applications = applications;
            Application = application;
            OtherExecutable = otherExecutable;
            Host = environment.BuildHost(services =>
            {
                services.AddSingleton<IInstalledApplicationCatalog>(applications);
                services.AddSingleton<IDesktopFileCatalog>(files);
                services.AddSingleton<IDesktopProcessLauncher>(Launcher);
            });
            Client = new DesktopApiClient(environment.Options.PipeName);
        }

        private DesktopHostTestEnvironment Environment { get; }
        private string Desktop { get; }
        private IHost Host { get; }
        public IDesktopApiClient Client { get; }
        public AssistantCommandService Commands => Host.Services.GetRequiredService<AssistantCommandService>();
        public RecordingCatalog Applications { get; }
        public KnownDesktopApplication Application { get; }
        public string OtherExecutable { get; }
        public RecordingLauncher Launcher { get; } = new();
        public string PathFor(string name) => Path.Combine(Desktop, name);

        public static async Task<Fixture> CreateAsync(string[] names, bool incomplete = false,
            string applicationName = ApplicationName)
        {
            var environment = DesktopHostTestEnvironment.Create();
            var desktop = Path.Combine(environment.RootDirectory, "fixture-desktop");
            Directory.CreateDirectory(desktop);
            foreach (var name in names) await File.WriteAllTextAsync(Path.Combine(desktop, name), "fixture only");
            var executable = Path.Combine(environment.RootDirectory, "fixture-player.exe");
            var otherExecutable = Path.Combine(environment.RootDirectory, "fixture-other.exe");
            File.Copy(System.Environment.ProcessPath!, executable);
            File.Copy(System.Environment.ProcessPath!, otherExecutable);
            var catalog = new InstalledApplicationCatalog(() =>
                [new(applicationName, executable, ApplicationRegistrationSource.AppPath)]);
            var applications = new RecordingCatalog(catalog);
            IDesktopFileCatalog files = new DesktopFileCatalog([desktop]);
            var fixture = new Fixture(environment, desktop, applications,
                catalog.ResolveByDisplayName(applicationName), otherExecutable,
                incomplete ? new IncompleteCatalog(files) : files);
            await fixture.Host.StartAsync();
            return fixture;
        }

        public async ValueTask DisposeAsync()
        {
            await Host.StopAsync();
            Host.Dispose();
            await Environment.DisposeAsync();
        }
    }

    private sealed class RecordingCatalog(InstalledApplicationCatalog inner) : IInstalledApplicationCatalog
    {
        public int PreferenceCalls { get; private set; }
        public string? FailureCode { get; set; }
        public KnownDesktopApplication? Replacement { get; set; }
        public Action? AfterPreference { get; set; }
        public IReadOnlyList<KnownDesktopApplication> GetApplications() =>
            Replacement is null ? inner.GetApplications() : [Replacement];
        public KnownDesktopApplication? FindById(string id) => Replacement is null
            ? inner.FindById(id) : Replacement.Id == id ? Replacement : null;
        public KnownDesktopApplication? FindByDisplayName(string name) => ResolveByDisplayName(name);
        public KnownDesktopApplication ResolveByDisplayName(string name)
        {
            if (FailureCode is { } code) throw new InstalledApplicationResolutionException(code, "fixture resolution rejected");
            return Replacement ?? inner.ResolveByDisplayName(name);
        }
        public KnownDesktopApplication ResolveApplicationPreference(string name)
        {
            PreferenceCalls++;
            var result = ResolveByDisplayName(name);
            AfterPreference?.Invoke();
            return result;
        }
        public KnownDesktopApplication? FindBrowser(string name) => null;
    }

    private sealed class IncompleteCatalog(IDesktopFileCatalog inner) : IDesktopFileCatalog
    {
        public DesktopFileResolution Resolve(string name) => inner.Resolve(name);
        public DesktopFileSearchResult Find(string name) => new([], 0, false, "fixture incomplete enumeration");
        public void RequireUnchanged(DesktopFileBinding binding) => inner.RequireUnchanged(binding);
    }

    private sealed class RecordingLauncher : IDesktopProcessLauncher
    {
        public List<string> Targets { get; } = [];
        public int? Start(string target) { Targets.Add(target); return 123; }
        public VisibleDesktopLaunchResult OpenApplicationVisible(string target)
        {
            Targets.Add(target);
            return new(123, 456, ApplicationName);
        }
        public VisibleDesktopLaunchResult OpenWebsiteVisible(string? browser, Uri website) => throw new NotSupportedException();
    }
}
