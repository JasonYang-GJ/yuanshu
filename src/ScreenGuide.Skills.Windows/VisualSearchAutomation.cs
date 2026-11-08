namespace ScreenGuide.Skills.Windows;

/// <summary>One visible, cancellable consent inside the currently executing Session Turn.</summary>
public interface IVisualSearchConsent
{
    Task RequestAsync(ForegroundWindowSnapshot window, string query,
        CancellationToken cancellationToken = default);
    Task RequestResultReadAsync(ForegroundWindowSnapshot originWindow, ForegroundWindowSnapshot resultWindow,
        string query, CancellationToken cancellationToken = default) =>
        Task.FromException(new UnauthorizedAccessException("本次搜索结果窗口尚未获得单独查看许可。"));
}

public interface IVisualSearchResultWindowVerifier
{
    void Verify(ForegroundWindowSnapshot originWindow, ForegroundWindowSnapshot resultWindow);
}

/// <summary>Local single-window fallback. It may search, but cannot open results or send messages.</summary>
public interface IVisualSearchAutomation
{
    Task<ApplicationSearchWorkflowResult> SearchAsync(ForegroundWindowSnapshot window, string query,
        CancellationToken cancellationToken = default, Action? validateTarget = null);
}
