using ScreenGuide.Skills.Abstractions;

namespace ScreenGuide.DesktopHost.Runtime;

internal sealed record DesktopTaskInteraction(DesktopTaskWindow Window, string Purpose,
    string Description, string BindingHash);

// Execution-local callbacks, not another current Session/Turn or authority store.
internal sealed class DesktopTaskInteractionChannel
{
    private readonly AsyncLocal<Subscription?> current = new();
    public IDisposable Observe(Func<DesktopTaskInteraction, CancellationToken, Task> consent,
        Func<string, CancellationToken, Task> progress)
    {
        var previous = current.Value;
        var owned = new Subscription(consent, progress);
        current.Value = owned;
        return new Scope(this, owned, previous);
    }
    public Task RequestAsync(DesktopTaskInteraction request, CancellationToken cancellationToken) =>
        RequireCurrent().Consent(request, cancellationToken);
    public Task ReportAsync(string description, CancellationToken cancellationToken) =>
        RequireCurrent().Progress(description, cancellationToken);
    private Subscription RequireCurrent() => current.Value is { Closed: false } value ? value
        : throw new UnauthorizedAccessException("通用操作必须由当前会话协调并显示授权。");
    private sealed record Subscription(Func<DesktopTaskInteraction, CancellationToken, Task> Consent,
        Func<string, CancellationToken, Task> Progress) { public bool Closed { get; set; } }
    private sealed class Scope(DesktopTaskInteractionChannel owner, Subscription owned, Subscription? previous) : IDisposable
    {
        public void Dispose() { owned.Closed = true; if (ReferenceEquals(owner.current.Value, owned)) owner.current.Value = previous; }
    }
}
