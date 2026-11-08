using System.Text.Json;
using ScreenGuide.Skills.Abstractions;

namespace ScreenGuide.Skills.Windows.Tests;

public sealed class VisualSearchFallbackTests
{
    private static readonly KnownDesktopApplication App = new("registered-app", "测试搜索工具", Environment.ProcessPath!);
    private static readonly ForegroundWindowSnapshot Window = new(42, "测试搜索工具", "fixture", 123,
        DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddMinutes(1));
    private static string Binding => InstalledApplicationCatalog.CreateTargetBinding(App);
    private const string Query = "AI 查询";

    [Fact]
    public async Task NamedSearchPassesSameBoundWindowAndNormalizedQueryToVisualOnce()
    {
        var driver = new Driver();
        var visual = new Visual();
        var result = await Executor(driver, visual).ExecuteAsync(App, Binding, new(App.Id, "ＡＩ 查询"));

        Assert.Equal(Success, result);
        Assert.Equal(1, visual.Calls);
        Assert.Equal(Window, visual.Window);
        Assert.Equal(Query, visual.Query);
        Assert.True(visual.HadTargetValidator);
        Assert.Equal(1, visual.Writes);
        Assert.Equal(1, driver.Searches);
        Assert.Equal(0, driver.Reads);
        Assert.Equal(0, driver.Invocations);
    }

    [Fact]
    public async Task NativeSuccessDoesNotStartVisualSearch()
    {
        var driver = new Driver { ErrorCode = null };
        var visual = new Visual();
        var result = await Executor(driver, visual).ExecuteAsync(App, Binding, Request());

        Assert.True(result.ResultsVerified);
        Assert.Equal(0, visual.Calls);
        Assert.Equal(1, driver.Searches);
        Assert.Equal(1, driver.Reads);
    }

    [Theory]
    [InlineData(DesktopSearchErrorCodes.CandidateAmbiguous)]
    [InlineData(DesktopSearchErrorCodes.WriteVerificationFailed)]
    [InlineData(DesktopSearchErrorCodes.TargetChanged)]
    [InlineData(DesktopSearchErrorCodes.FocusChanged)]
    [InlineData(DesktopSearchErrorCodes.InvalidQuery)]
    public async Task NativeErrorsOtherThanMissingFieldNeverStartVisualSearch(string code)
    {
        var visual = new Visual();
        var driver = new Driver { ErrorCode = code };
        var error = await Assert.ThrowsAsync<DesktopSearchException>(() =>
            Executor(driver, visual).ExecuteAsync(App, Binding, Request()));

        Assert.Equal(code, error.Code);
        Assert.Equal(0, visual.Calls);
        Assert.Equal(1, driver.Searches);
    }

    [Theory]
    [InlineData(ApplicationSearchFollowUp.OpenFirst)]
    [InlineData(ApplicationSearchFollowUp.PlayFirst)]
    public async Task MissingNativeFieldDoesNotAuthorizeVisualFollowUp(ApplicationSearchFollowUp followUp)
    {
        var visual = new Visual();
        var driver = new Driver();
        var error = await Assert.ThrowsAsync<DesktopSearchException>(() =>
            Executor(driver, visual).ExecuteAsync(App, Binding, Request() with { FollowUp = followUp }));

        Assert.Equal(DesktopSearchErrorCodes.CandidateNotFound, error.Code);
        Assert.Equal(0, visual.Calls);
        Assert.Equal(0, driver.Invocations);
    }

    [Fact]
    public async Task LatestMusicDoesNotFallBackToUnboundVisualResults()
    {
        var driver = new Driver { Current = Window with { ProcessName = "cloudmusic" } };
        var visual = new Visual();
        var error = await Assert.ThrowsAsync<DesktopSearchException>(() =>
            Executor(driver, visual).ExecuteAsync(App, Binding, Request() with { LatestMusicArtist = "示例歌手" }));

        Assert.Equal(DesktopSearchErrorCodes.CandidateNotFound, error.Code);
        Assert.Equal(0, visual.Calls);
    }

    [Fact]
    public async Task NoVisualServicePreservesMissingFieldFailure()
    {
        var error = await Assert.ThrowsAsync<DesktopSearchException>(() =>
            Executor(new Driver(), null).ExecuteAsync(App, Binding, Request()));
        Assert.Equal(DesktopSearchErrorCodes.CandidateNotFound, error.Code);
    }

    public static TheoryData<string> InvalidEvidence => new()
    {
        "application", "query", "results", "follow-up-invoked", "follow-up-verified", "enter", "unknown-mode"
    };

    [Theory]
    [MemberData(nameof(InvalidEvidence))]
    public async Task NamedSearchRejectsIncompleteOrUnauthorizedVisualEvidence(string invalid)
    {
        var visual = new Visual { Result = InvalidResult(invalid) };
        var error = await Assert.ThrowsAsync<ApplicationSearchWorkflowException>(() =>
            Executor(new Driver(), visual).ExecuteAsync(App, Binding, Request()));
        Assert.Equal("application_search_incomplete", error.Code);
        Assert.Equal(1, visual.Calls);
    }

    [Theory]
    [InlineData("registration")]
    [InlineData("window")]
    public async Task VisualTargetValidatorStopsAChangedApplicationBeforeWrite(string change)
    {
        var catalog = new Catalog();
        var driver = new Driver();
        var visual = new Visual
        {
            BeforeValidate = () =>
            {
                if (change == "registration") catalog.Application = App with { LaunchTarget = App.LaunchTarget + ".changed" };
                else driver.Current = Window with { ProcessId = Window.ProcessId + 1 };
            }
        };

        if (change == "registration")
            Assert.Equal("application_search_target_changed", (await Assert.ThrowsAsync<ApplicationSearchWorkflowException>(() =>
                Executor(driver, visual, catalog).ExecuteAsync(App, Binding, Request()))).Code);
        else
            Assert.Equal(WindowIdentityErrorCodes.Changed, (await Assert.ThrowsAsync<WindowIdentityException>(() =>
                Executor(driver, visual, catalog).ExecuteAsync(App, Binding, Request()))).Code);
        Assert.True(visual.HadTargetValidator);
        Assert.Equal(0, visual.Writes);
        Assert.Equal(0, driver.Invocations);
    }

    [Fact]
    public async Task VisualTargetValidatorObservesCancellationBeforeWrite()
    {
        using var cancellation = new CancellationTokenSource();
        var visual = new Visual { BeforeValidate = cancellation.Cancel };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Executor(new Driver(), visual).ExecuteAsync(App, Binding, Request(), cancellation.Token));
        Assert.Equal(0, visual.Writes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationReachesPendingVisualSearchAndRejectsLateSuccess(bool foreground)
    {
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var visual = new Visual
        {
            Run = async token =>
            {
                entered.SetResult();
                await Task.Delay(Timeout.Infinite, token);
                return Success;
            }
        };
        Task operation = foreground
            ? Adapter(new Native(), visual).StartAsync(ForegroundRequest(), cancellation.Token)
            : Executor(new Driver(), visual).ExecuteAsync(App, Binding, Request(), cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        Assert.Equal(1, visual.Calls);

        using var lateCancellation = new CancellationTokenSource();
        var late = new Visual { Run = _ => { lateCancellation.Cancel(); return Task.FromResult(Success); } };
        Task lateOperation = foreground
            ? Adapter(new Native(), late).StartAsync(ForegroundRequest(), lateCancellation.Token)
            : Executor(new Driver(), late).ExecuteAsync(App, Binding, Request(), lateCancellation.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => lateOperation);
    }

    [Fact]
    public async Task ForegroundFallbackUsesOriginalWindowAndSameNormalizedQueryAsNativeAttempt()
    {
        var native = new Native();
        var visual = new Visual();
        var adapter = Adapter(native, visual);
        var start = await adapter.StartAsync(ForegroundRequest(" ＡＩ 查询 "));
        var result = await adapter.GetFinalResultAsync(start.Run);

        Assert.NotNull(result);
        Assert.True(result.Succeeded);
        Assert.Equal(Success, JsonSerializer.Deserialize<ApplicationSearchWorkflowResult>(result.DataJson!));
        Assert.Equal(Window, visual.Window);
        Assert.Equal(native.Query, visual.Query);
        Assert.Equal(Query, visual.Query);
        Assert.Equal(1, visual.Calls);
        Assert.Equal(1, native.Searches);
    }

    [Theory]
    [MemberData(nameof(InvalidEvidence))]
    public async Task ForegroundFallbackRejectsIncompleteOrUnauthorizedVisualEvidence(string invalid)
    {
        var visual = new Visual { Result = InvalidResult(invalid) };
        var error = await Assert.ThrowsAsync<DesktopSearchException>(() =>
            Adapter(new Native(), visual).StartAsync(ForegroundRequest()));
        Assert.Equal(DesktopSearchErrorCodes.WriteVerificationFailed, error.Code);
    }

    [Theory]
    [InlineData(DesktopSearchErrorCodes.CandidateAmbiguous)]
    [InlineData(DesktopSearchErrorCodes.WriteVerificationFailed)]
    [InlineData(DesktopSearchErrorCodes.TargetChanged)]
    [InlineData(DesktopSearchErrorCodes.FocusChanged)]
    public async Task ForegroundFallbackIsNotARetryAfterAmbiguityOrPossibleWrite(string code)
    {
        var native = new Native { ErrorCode = code };
        var visual = new Visual();
        var error = await Assert.ThrowsAsync<DesktopSearchException>(() => Adapter(native, visual).StartAsync(ForegroundRequest()));
        Assert.Equal(code, error.Code);
        Assert.Equal(0, visual.Calls);
        Assert.Equal(1, native.Searches);
    }

    [Fact]
    public async Task ForegroundNativeSuccessDoesNotInvokeVisualSearch()
    {
        var visual = new Visual();
        await Adapter(new Native { ErrorCode = null }, visual).StartAsync(ForegroundRequest());
        Assert.Equal(0, visual.Calls);
    }

    [Fact]
    public async Task OtherCapabilityCannotTriggerVisualSearch()
    {
        var visual = new Visual();
        var error = await Assert.ThrowsAsync<DesktopSearchException>(() => Adapter(new Native(), visual).StartAsync(
            Invocation(WindowsDesktopCapabilities.DescribeForeground,
                new("DescribeForeground", "", Window.WindowHandle, Window.WindowTitle, WindowIdentity: Window))));
        Assert.Equal(DesktopSearchErrorCodes.CandidateNotFound, error.Code);
        Assert.Equal(0, visual.Calls);
    }

    [Fact]
    public async Task MissingIdentityAndInvalidQueryCannotReachVisualOrNativeInput()
    {
        var visual = new Visual();
        var native = new Native();
        var adapter = Adapter(native, visual);
        await Assert.ThrowsAsync<WindowIdentityException>(() => adapter.StartAsync(Invocation(
            WindowsDesktopCapabilities.SearchForeground, new("SearchForeground", Query))));
        await Assert.ThrowsAsync<DesktopSearchException>(() => adapter.StartAsync(ForegroundRequest("query\nother")));
        Assert.Equal(0, visual.Calls);
        Assert.Equal(0, native.Searches);
    }

    private static ApplicationSearchWorkflowResult Success => new(true, true, true, false, false, false, "已核对结果。")
        { SubmissionMode = "LiveFilter" };
    private static ApplicationSearchWorkflowResult InvalidResult(string invalid) => invalid switch
    {
        "application" => Success with { ApplicationVerified = false },
        "query" => Success with { QuerySubmitted = false },
        "results" => Success with { ResultsVerified = false },
        "follow-up-invoked" => Success with { FollowUpInvoked = true },
        "follow-up-verified" => Success with { FollowUpVerified = true },
        "enter" => Success with { SubmissionMode = "Enter" },
        _ => Success with { SubmissionMode = "unknown" }
    };
    private static ApplicationSearchWorkflow Request() => new(App.Id, Query);
    private static WindowsApplicationSearchWorkflowAutomation Executor(Driver driver, Visual? visual, Catalog? catalog = null) =>
        new(catalog ?? new Catalog(), driver, TimeSpan.Zero, latestMusic: new Music(), visualSearch: visual);
    private static WindowsDesktopSkillAdapter Adapter(Native native, Visual visual) =>
        new(new UnusedLauncher(), new Catalog(), native, visualSearch: visual);
    private static SkillInvocationRequest ForegroundRequest(string query = Query) => Invocation(
        WindowsDesktopCapabilities.SearchForeground,
        new("SearchForeground", query, Window.WindowHandle, Window.WindowTitle, WindowIdentity: Window));
    private static SkillInvocationRequest Invocation(string capability, WindowsDesktopActionInput input) =>
        new(Guid.NewGuid(), Guid.NewGuid(), "windows.safe-launch", capability, JsonSerializer.Serialize(input),
            [new SkillResourceScope("Test", null, "explicit-target", "Execute")], SkillAuthorizationOrigin.ExplicitUser, Guid.NewGuid());

    private sealed class Visual : IVisualSearchAutomation
    {
        public int Calls { get; private set; }
        public int Writes { get; private set; }
        public ForegroundWindowSnapshot? Window { get; private set; }
        public string? Query { get; private set; }
        public bool HadTargetValidator { get; private set; }
        public Action? BeforeValidate { get; init; }
        public Func<CancellationToken, Task<ApplicationSearchWorkflowResult>>? Run { get; init; }
        public ApplicationSearchWorkflowResult Result { get; init; } = Success;
        public Task<ApplicationSearchWorkflowResult> SearchAsync(ForegroundWindowSnapshot window, string query,
            CancellationToken cancellationToken = default, Action? validateTarget = null)
        {
            Calls++;
            (Window, Query, HadTargetValidator) = (window, query, validateTarget is not null);
            BeforeValidate?.Invoke();
            validateTarget?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            Writes++;
            return Run?.Invoke(cancellationToken) ?? Task.FromResult(Result);
        }
    }

    private sealed class Catalog : IInstalledApplicationCatalog
    {
        public KnownDesktopApplication Application { get; set; } = App;
        public IReadOnlyList<KnownDesktopApplication> GetApplications() => [Application];
        public KnownDesktopApplication? FindById(string id) => id == Application.Id ? Application : null;
        public KnownDesktopApplication? FindByDisplayName(string name) => name == Application.DisplayName ? Application : null;
        public KnownDesktopApplication? FindBrowser(string name) => null;
    }

    private sealed class Driver : IApplicationSearchWorkflowDriver
    {
        public ForegroundWindowSnapshot Current { get; set; } = Window;
        public string? ErrorCode { get; init; } = DesktopSearchErrorCodes.CandidateNotFound;
        public int Searches { get; private set; }
        public int Reads { get; private set; }
        public int Invocations { get; private set; }
        public ForegroundWindowSnapshot Open(KnownDesktopApplication application, CancellationToken token) => Current;
        public ForegroundWindowSnapshot RequireBound(KnownDesktopApplication application, ForegroundWindowSnapshot expected, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            WindowIdentityContract.RequireMatch(expected, Current);
            return Current;
        }
        public void Search(ForegroundWindowSnapshot window, string query, CancellationToken token)
        {
            Searches++;
            if (ErrorCode is not null) throw new DesktopSearchException(ErrorCode, "fixture");
        }
        public SearchResultPage ReadResults(ForegroundWindowSnapshot window, string query, CancellationToken token)
        {
            Reads++;
            return new(SearchResultPageState.Empty, query, "empty", []);
        }
        public void Invoke(ForegroundWindowSnapshot window, SearchResultPage page, ApplicationSearchFollowUp followUp,
            Action validateAtInvoke, CancellationToken token) => Invocations++;
        public bool VerifyFollowUp(ForegroundWindowSnapshot window, SearchResultItem item, ApplicationSearchFollowUp followUp,
            CancellationToken token) => throw new InvalidOperationException("Unexpected follow-up.");
    }

    private sealed class Native : IReliableDesktopAutomation
    {
        public string? ErrorCode { get; init; } = DesktopSearchErrorCodes.CandidateNotFound;
        public int Searches { get; private set; }
        public string? Query { get; private set; }
        public DesktopAutomationResult Search(ForegroundWindowSnapshot window, string query)
        {
            Searches++;
            Query = query;
            if (ErrorCode is not null) throw new DesktopSearchException(ErrorCode, "fixture");
            return new(true, "native success");
        }
        public DesktopAutomationResult Describe(ForegroundWindowSnapshot window) =>
            throw new DesktopSearchException(DesktopSearchErrorCodes.CandidateNotFound, "fixture");
    }

    private sealed class Music : ILatestMusicResolver
    {
        public Task<LatestMusicResolution> ResolveAsync(string artist, CancellationToken token = default) =>
            Task.FromResult(new LatestMusicResolution(artist, "示例专辑", new DateOnly(2026, 1, 1), ["示例歌曲"], Query, true, []));
    }

    private sealed class UnusedLauncher : IDesktopProcessLauncher
    {
        public int? Start(string target) => throw new InvalidOperationException("Unexpected launch.");
        public VisibleDesktopLaunchResult OpenApplicationVisible(string target) => throw new InvalidOperationException("Unexpected launch.");
        public VisibleDesktopLaunchResult OpenWebsiteVisible(string? target, Uri website) => throw new InvalidOperationException("Unexpected launch.");
    }
}
