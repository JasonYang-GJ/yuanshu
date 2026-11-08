using System.Text.Json;
using ScreenGuide.Core.Security;
using ScreenGuide.Skills.Abstractions;

namespace ScreenGuide.Core.Tests;

public sealed class ApplicationSearchPolicyTests
{
    private const string Binding = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private static readonly SkillDescriptor Descriptor = new("windows.safe-launch", "0.2.0", "Windows", true,
        new HashSet<string> { "desktop.application.search" });

    [Fact]
    public void LiteralSearchScopeCannotExpandIntoHotChartSelection()
    {
        var request = Request(new("music", "热门歌曲"));
        var input = System.Text.Json.Nodes.JsonNode.Parse(request.InputJson)!;
        input["ApplicationSearch"]!["MusicChart"] = "NetEaseHotChart";
        request = request with { InputJson = input.ToJsonString() };
        Assert.Equal("resource_scope_required", new CapabilityPolicyEngine().Evaluate(Descriptor, request).Code);
    }

    [Fact]
    public void ChartScopeIsBoundOnceAndDoesNotAlterOrdinaryCanonicalFormat()
    {
        var workflow = new ApplicationSearchWorkflow("music", "热门歌曲", ApplicationSearchFollowUp.PlayFirst,
            MusicChart: ApplicationSearchWorkflow.NetEaseHotChart);
        var policy = new CapabilityPolicyEngine();
        var request = Request(workflow);
        Assert.True(policy.AuthorizeOnce(Descriptor, request).IsAllowed);
        Assert.Equal("action_authorization_consumed", policy.AuthorizeOnce(Descriptor, request).Code);
        using var canonical = JsonDocument.Parse(workflow.ToCanonicalTarget(Binding));
        Assert.Equal("NetEaseHotChart", canonical.RootElement.GetProperty("musicChart").GetString());
        Assert.Equal(5, canonical.RootElement.EnumerateObject().Count());
        using var ordinary = JsonDocument.Parse((workflow with { MusicChart = null }).ToCanonicalTarget(Binding));
        Assert.Equal(4, ordinary.RootElement.EnumerateObject().Count());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("OtherChart")]
    [InlineData("neteasehotchart")]
    public void ChartScopeCannotBeRemovedOrChanged(string? replacement)
    {
        var workflow = new ApplicationSearchWorkflow("music", "热门歌曲", MusicChart: ApplicationSearchWorkflow.NetEaseHotChart);
        var request = Request(workflow);
        var changed = workflow with { MusicChart = replacement };
        request = request with { InputJson = Input(changed, Binding, changed.ApplicationId, changed.Query, "SearchApplication") };
        Assert.Equal("resource_scope_required", new CapabilityPolicyEngine().Evaluate(Descriptor, request).Code);
    }

    [Fact]
    public void ChartCannotBeCombinedWithReleaseSelectionOrOpenFollowUp()
    {
        var workflow = new ApplicationSearchWorkflow("music", "热门歌曲", MusicChart: ApplicationSearchWorkflow.NetEaseHotChart);
        Assert.Throws<ArgumentException>(() => (workflow with { LatestMusicArtist = "测试歌手" }).Validate());
        Assert.Throws<ArgumentException>(() => (workflow with { FollowUp = ApplicationSearchFollowUp.OpenFirst }).Validate());
    }

    [Fact]
    public void LiteralSearchAuthorizationCannotBeExpandedIntoLatestMusicLookup()
    {
        var workflow = new ApplicationSearchWorkflow("music", "周杰伦最新的歌");
        var request = Request(workflow);
        var input = System.Text.Json.Nodes.JsonNode.Parse(request.InputJson)!;
        input["ApplicationSearch"]!["LatestMusicArtist"] = "周杰伦";
        request = request with { InputJson = input.ToJsonString() };

        Assert.Equal("resource_scope_required", new CapabilityPolicyEngine().Evaluate(Descriptor, request).Code);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("林俊杰")]
    public void LatestMusicScopeCannotDropOrChangeTheBoundArtist(string? replacement)
    {
        var workflow = new ApplicationSearchWorkflow("music", "周杰伦最新的歌", LatestMusicArtist: "周杰伦");
        var request = Request(workflow);
        var changed = workflow with { LatestMusicArtist = replacement };
        request = request with { InputJson = Input(changed, Binding, changed.ApplicationId, changed.Query, "SearchApplication") };

        Assert.Equal("resource_scope_required", new CapabilityPolicyEngine().Evaluate(Descriptor, request).Code);
    }

    [Fact]
    public void LatestArtistScopeIsAuthorizedOnceAndOrdinaryScopeKeepsItsOriginalShape()
    {
        var workflow = new ApplicationSearchWorkflow("music", "周杰伦最新的歌", LatestMusicArtist: "周杰伦");
        var policy = new CapabilityPolicyEngine();
        var request = Request(workflow);
        Assert.True(policy.AuthorizeOnce(Descriptor, request).IsAllowed);
        Assert.Equal("action_authorization_consumed", policy.AuthorizeOnce(Descriptor, request).Code);
        using var latest = JsonDocument.Parse(workflow.ToCanonicalTarget(Binding));
        Assert.Equal("周杰伦", latest.RootElement.GetProperty("latestMusicArtist").GetString());
        using var ordinary = JsonDocument.Parse((workflow with { LatestMusicArtist = null }).ToCanonicalTarget(Binding));
        Assert.False(ordinary.RootElement.TryGetProperty("latestMusicArtist", out _));
        Assert.Equal(4, ordinary.RootElement.EnumerateObject().Count());
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(" 周杰伦")]
    [InlineData("周杰伦\n")]
    [InlineData("他们")]
    [InlineData("这位歌手")]
    public void LatestMusicRequiresAnExplicitBoundedArtist(string artist)
    {
        Assert.Throws<ArgumentException>(() =>
            new ApplicationSearchWorkflow("music", "最新的歌", LatestMusicArtist: artist).Validate());
    }

    [Fact]
    public void LatestArtistCannotExceedTheSearchResolutionBound()
    {
        Assert.Throws<ArgumentException>(() =>
            new ApplicationSearchWorkflow("music", "最新的歌", LatestMusicArtist: new string('名', 81)).Validate());
    }

    [Fact]
    public void ExplicitWorkflowScopeMustMatchEveryAuthorizedFieldAndCanOnlyBeUsedOnce()
    {
        var workflow = new ApplicationSearchWorkflow("music", "晴天", ApplicationSearchFollowUp.PlayFirst);
        var request = Request(workflow);
        var policy = new CapabilityPolicyEngine();
        Assert.True(policy.AuthorizeOnce(Descriptor, request).IsAllowed);
        Assert.Equal("action_authorization_consumed", policy.AuthorizeOnce(Descriptor, request).Code);
    }

    [Theory]
    [InlineData("app")]
    [InlineData("query")]
    [InlineData("followup")]
    [InlineData("binding")]
    [InlineData("target")]
    [InlineData("argument")]
    [InlineData("action")]
    public void ScopeDoesNotAuthorizeChangedInput(string field)
    {
        var workflow = new ApplicationSearchWorkflow("music", "晴天");
        var request = Request(workflow);
        var changed = workflow with
        {
            ApplicationId = field == "app" ? "other" : workflow.ApplicationId,
            Query = field == "query" ? "other" : workflow.Query,
            FollowUp = field == "followup" ? ApplicationSearchFollowUp.PlayFirst : workflow.FollowUp
        };
        request = request with { InputJson = Input(changed,
            field == "binding" ? new string('B', 64) : Binding,
            field == "target" ? "other" : changed.ApplicationId,
            field == "argument" ? "other" : changed.Query,
            field == "action" ? "OpenApplication" : "SearchApplication") };
        Assert.Equal("resource_scope_required", new CapabilityPolicyEngine().Evaluate(Descriptor, request).Code);
    }

    [Theory]
    [InlineData(SkillAuthorizationOrigin.Model)]
    [InlineData(SkillAuthorizationOrigin.Document)]
    [InlineData(SkillAuthorizationOrigin.ScreenContent)]
    [InlineData(SkillAuthorizationOrigin.Website)]
    public void UntrustedSuggestionsCannotAuthorizeWorkflow(SkillAuthorizationOrigin origin)
    {
        var request = Request(new("music", "晴天")) with { AuthorizationOrigin = origin };
        Assert.False(new CapabilityPolicyEngine().Evaluate(Descriptor, request).IsAllowed);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("invalid")]
    [InlineData("{\"ApplicationSearch\":{\"ApplicationId\":\"music\",\"Query\":\"晴天\",\"FollowUp\":99}}")]
    public void MalformedInputIsDenied(string json)
    {
        var request = Request(new("music", "晴天")) with { InputJson = json };
        Assert.False(new CapabilityPolicyEngine().Evaluate(Descriptor, request).IsAllowed);
    }

    private static SkillInvocationRequest Request(ApplicationSearchWorkflow workflow) => new(
        Guid.Empty, Guid.NewGuid(), Descriptor.Id, "desktop.application.search",
        Input(workflow, Binding, workflow.ApplicationId, workflow.Query, "SearchApplication"),
        [new("ApplicationSearch", null, workflow.ToCanonicalTarget(Binding), "Execute")],
        SkillAuthorizationOrigin.ExplicitUser, Guid.NewGuid());

    private static string Input(ApplicationSearchWorkflow workflow, string binding, string target, string argument, string kind) =>
        JsonSerializer.Serialize(new { ActionKind = kind, Target = target, Argument = argument,
            ApplicationTargetBinding = binding, ApplicationSearch = workflow });
}
