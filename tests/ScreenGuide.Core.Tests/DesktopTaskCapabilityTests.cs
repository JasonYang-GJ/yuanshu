using System.Text.Json;
using ScreenGuide.Core.Security;
using ScreenGuide.Skills.Abstractions;

namespace ScreenGuide.Core.Tests;

public sealed class DesktopTaskCapabilityTests
{
    private static readonly SkillDescriptor Descriptor = new("windows.desktop-task", "1", "测试任务", true,
        new HashSet<string> { "desktop.task.step" });

    [Fact]
    public void ExactStepIsConsumedOnceAndCannotBecomeAnotherCapability()
    {
        var policy = new CapabilityPolicyEngine();
        var request = Request();
        Assert.True(policy.AuthorizeOnce(Descriptor, request).IsAllowed);
        Assert.False(policy.AuthorizeOnce(Descriptor, request).IsAllowed);
        Assert.False(new CapabilityPolicyEngine().AuthorizeOnce(Descriptor, request with { Capability = "desktop.application.open" }).IsAllowed);
    }

    [Theory]
    [InlineData(SkillAuthorizationOrigin.Model)]
    [InlineData(SkillAuthorizationOrigin.ScreenContent)]
    [InlineData(SkillAuthorizationOrigin.Website)]
    [InlineData(SkillAuthorizationOrigin.Document)]
    public void ModelAndContentCannotAuthorizeTheStep(SkillAuthorizationOrigin origin) =>
        Assert.False(new CapabilityPolicyEngine().Evaluate(Descriptor, Request() with { AuthorizationOrigin = origin }).IsAllowed);

    [Theory]
    [InlineData("task")]
    [InlineData("snapshot")]
    [InlineData("window")]
    [InlineData("action")]
    [InlineData("broad")]
    [InlineData("empty")]
    public void AnyChangedScopeRefuses(string change)
    {
        var request = Request();
        var scope = JsonSerializer.Deserialize<DesktopTaskStepAuthorization>(request.InputJson)!;
        scope = change switch
        {
            "task" => scope with { TurnId = Guid.NewGuid() },
            "snapshot" => scope with { SnapshotId = Guid.NewGuid() },
            "window" => scope with { WindowBinding = new string('C', 64) },
            "action" => scope with { ActionBinding = new string('D', 64) },
            "empty" => scope with { TurnId = Guid.Empty },
            _ => scope
        };
        request = request with { InputJson = JsonSerializer.Serialize(scope) };
        if (change == "broad") request = request with { ResourceScopes = [new("DesktopTaskStep", null, "*", "Execute")] };
        Assert.False(new CapabilityPolicyEngine().Evaluate(Descriptor, request).IsAllowed);
    }

    private static SkillInvocationRequest Request()
    {
        var scope = new DesktopTaskStepAuthorization(Guid.NewGuid(), Guid.NewGuid(), new string('A', 64), new string('B', 64));
        return new(Guid.Empty, Guid.NewGuid(), Descriptor.Id, "desktop.task.step", JsonSerializer.Serialize(scope),
            [new("DesktopTaskStep", null, scope.ToScopeValue(), "Execute")], SkillAuthorizationOrigin.ExplicitUser, Guid.NewGuid());
    }
}
