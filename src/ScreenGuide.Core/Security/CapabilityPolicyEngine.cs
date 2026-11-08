using System.Collections.Concurrent;
using System.Text.Json;
using ScreenGuide.Skills.Abstractions;

namespace ScreenGuide.Core.Security;

public enum CapabilityPolicyOutcome
{
    Allowed,
    Denied
}

public sealed record CapabilityPolicyDecision(
    CapabilityPolicyOutcome Outcome,
    string Code,
    string UserMessage)
{
    public bool IsAllowed => Outcome == CapabilityPolicyOutcome.Allowed;
}

/// <summary>
/// V0.2 的确定性权限门禁。模型输出、屏幕文字、文档和网页内容都不能成为操作授权。
/// </summary>
public sealed class CapabilityPolicyEngine
{
    private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> TrustedCapabilities =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
        {
            ["windows.desktop-task"] = new HashSet<string>(StringComparer.Ordinal)
            {
                "desktop.task.step"
            },
            ["codex.project-task"] = new HashSet<string>(StringComparer.Ordinal)
            {
                "coding.execute"
            },
            ["windows.safe-launch"] = new HashSet<string>(StringComparer.Ordinal)
            {
                "desktop.application.open",
                "desktop.application.search",
                "desktop.website.open",
                "browser.open",
                "browser.open.visible-in-app",
                "file.open",
                "desktop.search.submit",
                "desktop.window.describe"
            }
        };

    private readonly ConcurrentDictionary<Guid, byte> _consumedAuthorizations = new();

    public CapabilityPolicyDecision Evaluate(
        SkillDescriptor descriptor,
        SkillInvocationRequest request)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(request);
        if (!descriptor.IsBuiltIn
            || !TrustedCapabilities.TryGetValue(descriptor.Id, out var capabilities))
        {
            return Deny("skill_not_trusted", "这个技能尚未被 ScreenGuide 信任。");
        }

        if (!string.Equals(descriptor.Id, request.SkillId, StringComparison.Ordinal)
            || !descriptor.Capabilities.Contains(request.Capability)
            || !capabilities.Contains(request.Capability))
        {
            return Deny("capability_not_allowed", "这个技能无权执行所请求的操作。");
        }

        if (request.AuthorizationOrigin != SkillAuthorizationOrigin.ExplicitUser)
        {
            return Deny("explicit_user_authorization_required", "需要用户本人明确下达本次操作指令。");
        }

        if (request.ActionAuthorizationId is null)
        {
            return Deny("action_authorization_missing", "本次操作缺少一次性用户授权。");
        }

        if (_consumedAuthorizations.ContainsKey(request.ActionAuthorizationId.Value))
        {
            return Deny("action_authorization_consumed", "本次用户授权已经使用，不能重复执行。");
        }

        if (!HasRequiredResourceScope(request))
        {
            return Deny(
                "resource_scope_required",
                "本次操作必须限制在一个明确显示并由用户确认的目标范围内。");
        }

        return new CapabilityPolicyDecision(
            CapabilityPolicyOutcome.Allowed,
            "allowed",
            "已允许本次明确确认的单次操作。");
    }

    public CapabilityPolicyDecision AuthorizeOnce(
        SkillDescriptor descriptor,
        SkillInvocationRequest request)
    {
        var decision = Evaluate(descriptor, request);
        if (!decision.IsAllowed)
        {
            return decision;
        }

        return _consumedAuthorizations.TryAdd(request.ActionAuthorizationId!.Value, 0)
            ? decision
            : Deny("action_authorization_consumed", "本次用户授权已经使用，不能重复执行。");
    }

    private static CapabilityPolicyDecision Deny(string code, string message) =>
        new(CapabilityPolicyOutcome.Denied, code, message);

    private static bool HasRequiredResourceScope(SkillInvocationRequest request)
    {
        if (request.ResourceScopes.Count != 1)
        {
            return false;
        }

        var scope = request.ResourceScopes[0];
        if (!string.Equals(scope.AccessMode, "Execute", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(scope.ScopeValue))
        {
            return false;
        }

        return request.Capability switch
        {
            "desktop.task.step" => scope.ScopeType == "DesktopTaskStep" && scope.ResourceId is null
                && MatchesDesktopTaskScope(request.InputJson, scope.ScopeValue),
            "coding.execute" =>
                string.Equals(scope.ScopeType, "Project", StringComparison.Ordinal)
                && scope.ResourceId is not null,
            "desktop.application.open" =>
                string.Equals(scope.ScopeType, "Application", StringComparison.Ordinal)
                && scope.ResourceId is null,
            "desktop.application.search" =>
                string.Equals(scope.ScopeType, "ApplicationSearch", StringComparison.Ordinal)
                && scope.ResourceId is null
                && MatchesApplicationSearchScope(request.InputJson, scope.ScopeValue),
            "desktop.website.open" =>
                string.Equals(scope.ScopeType, "Website", StringComparison.Ordinal)
                && scope.ResourceId is null,
            "browser.open" =>
                string.Equals(scope.ScopeType, "Website", StringComparison.Ordinal)
                && scope.ResourceId is null,
            "browser.open.visible-in-app" =>
                string.Equals(scope.ScopeType, "ApplicationWebsite", StringComparison.Ordinal)
                && scope.ResourceId is null,
            "file.open" =>
                string.Equals(scope.ScopeType, "File", StringComparison.Ordinal)
                && scope.ResourceId is null,
            "desktop.search.submit" =>
                string.Equals(scope.ScopeType, "Window", StringComparison.Ordinal)
                && scope.ResourceId is null,
            "desktop.window.describe" =>
                string.Equals(scope.ScopeType, "Window", StringComparison.Ordinal)
                && scope.ResourceId is null,
            _ => false
        };
    }

    private static bool MatchesDesktopTaskScope(string json, string scope)
    {
        try
        {
            var value = JsonSerializer.Deserialize<DesktopTaskStepAuthorization>(json);
            return value is not null && value.ToScopeValue() == scope;
        }
        catch (Exception e) when (e is JsonException or ArgumentException or NotSupportedException) { return false; }
    }

    private static bool MatchesApplicationSearchScope(string inputJson, string scopeValue)
    {
        try
        {
            var input = JsonSerializer.Deserialize<ApplicationSearchScopeInput>(inputJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (input?.ApplicationSearch is null || input.ActionKind != "SearchApplication"
                || input.ApplicationSearch.ApplicationId != input.Target
                || input.ApplicationSearch.Query != input.Argument
                || string.IsNullOrWhiteSpace(input.ApplicationTargetBinding)) return false;
            input.ApplicationSearch.Validate();
            return string.Equals(scopeValue,
                input.ApplicationSearch.ToCanonicalTarget(input.ApplicationTargetBinding), StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private sealed record ApplicationSearchScopeInput(string? ActionKind, string? Target, string? Argument,
        string? ApplicationTargetBinding, ApplicationSearchWorkflow? ApplicationSearch);
}
