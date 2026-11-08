using ScreenGuide.DesktopHost.Runtime;
using ScreenGuide.Skills.Windows;

namespace ScreenGuide.DesktopHost.Tests;

public sealed class VisualSearchConsentChannelTests
{
    private static readonly ForegroundWindowSnapshot Window = new(42, "测试窗口", "fixture", 123,
        DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

    [Fact]
    public async Task NoCoordinatorScopeCannotGrantPermission()
    {
        var channel = new VisualSearchConsentChannel();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => channel.RequestAsync(Window, "测试"));
    }

    [Fact]
    public async Task OneScopeAllowsOnlyOneConsentRequest()
    {
        var channel = new VisualSearchConsentChannel();
        var calls = 0;
        using (channel.Observe((_, _) => { calls++; return Task.CompletedTask; }))
        {
            await channel.RequestAsync(Window, "测试");
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => channel.RequestAsync(Window, "测试"));
        }
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => channel.RequestAsync(Window, "测试"));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task CapturedExecutionContextCannotReuseAClosedScope()
    {
        var channel = new VisualSearchConsentChannel();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task late;
        using (channel.Observe((_, _) => Task.CompletedTask))
            late = Task.Run(async () => { await release.Task; await channel.RequestAsync(Window, "测试"); });
        release.SetResult();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => late);
    }

    [Fact]
    public async Task OneReadOnlyResultWindowMayFollowTheOriginalSearchButCannotAuthorizeMoreInput()
    {
        var channel = new VisualSearchConsentChannel();
        var purposes = new List<VisualSearchConsentPurpose>();
        var popup = Window with { WindowHandle = 43, WindowTitle = "" };
        using var scope = channel.Observe((request, _) => { purposes.Add(request.Purpose); return Task.CompletedTask; });
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => channel.RequestResultReadAsync(Window, popup, "测试"));
        await channel.RequestAsync(Window, "测试");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => channel.RequestResultReadAsync(Window, popup, "其他词"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => channel.RequestResultReadAsync(Window with { ProcessId = 99 }, popup, "测试"));
        await channel.RequestResultReadAsync(Window, popup, "测试");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => channel.RequestResultReadAsync(Window, popup, "测试"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => channel.RequestAsync(Window, "测试"));
        Assert.Equal(new[] { VisualSearchConsentPurpose.Search, VisualSearchConsentPurpose.ResultRead }, purposes);
    }
}
