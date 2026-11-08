using ScreenGuide.Skills.Windows;

namespace ScreenGuide.DesktopHost.Runtime;

internal enum VisualSearchConsentPurpose { Search, ResultRead }

internal sealed record VisualSearchConsentRequest(VisualSearchConsentPurpose Purpose,
    ForegroundWindowSnapshot OriginWindow, ForegroundWindowSnapshot Window, string Query);

/// <summary>
/// An execution-local request for visible permission. It never keeps a current task
/// or grants permission outside the coordinator operation that installed the scope.
/// </summary>
internal sealed class VisualSearchConsentChannel : IVisualSearchConsent
{
    private readonly AsyncLocal<Subscription?> _current = new();

    public IDisposable Observe(Func<VisualSearchConsentRequest, CancellationToken, Task> request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var parent = _current.Value;
        var subscription = new Subscription(request);
        _current.Value = subscription;
        return new Scope(this, subscription, parent);
    }

    public Task RequestAsync(ForegroundWindowSnapshot window, string query,
        CancellationToken cancellationToken = default) =>
        RequestCoreAsync(new(VisualSearchConsentPurpose.Search, window, window, query), cancellationToken);

    public Task RequestResultReadAsync(ForegroundWindowSnapshot originWindow,
        ForegroundWindowSnapshot resultWindow, string query, CancellationToken cancellationToken = default) =>
        RequestCoreAsync(new(VisualSearchConsentPurpose.ResultRead, originWindow, resultWindow, query), cancellationToken);

    private async Task RequestCoreAsync(VisualSearchConsentRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request.OriginWindow);
        ArgumentNullException.ThrowIfNull(request.Window);
        cancellationToken.ThrowIfCancellationRequested();
        var subscription = _current.Value;
        var isSearch = request.Purpose == VisualSearchConsentPurpose.Search;
        if (subscription is null || subscription.Closed
            || !isSearch && (subscription.Original is not { } original
                || !WindowIdentityContract.Matches(original.Window, request.OriginWindow)
                || original.Query != request.Query || request.Window.WindowHandle == request.OriginWindow.WindowHandle)
            || Interlocked.CompareExchange(ref subscription.Stage, isSearch ? 1 : 3, isSearch ? 0 : 2)
                != (isSearch ? 0 : 2))
            throw new UnauthorizedAccessException("本次视觉搜索缺少有效的会话授权，或该授权已经使用。");

        if (isSearch) subscription.Original = request;
        try
        {
            await subscription.Request(request, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (subscription.Closed)
                throw new UnauthorizedAccessException("本次视觉搜索已经结束，不能继续读取或操作窗口。");
            Interlocked.Exchange(ref subscription.Stage, isSearch ? 2 : 4);
        }
        catch
        {
            Interlocked.Exchange(ref subscription.Stage, -1);
            throw;
        }
    }

    private sealed class Subscription(Func<VisualSearchConsentRequest, CancellationToken, Task> request)
    {
        public Func<VisualSearchConsentRequest, CancellationToken, Task> Request { get; } = request;
        public volatile bool Closed;
        public int Stage;
        public VisualSearchConsentRequest? Original;
    }

    private sealed class Scope(VisualSearchConsentChannel owner, Subscription subscription, Subscription? parent)
        : IDisposable
    {
        public void Dispose()
        {
            subscription.Closed = true;
            if (ReferenceEquals(owner._current.Value, subscription)) owner._current.Value = parent;
        }
    }
}
