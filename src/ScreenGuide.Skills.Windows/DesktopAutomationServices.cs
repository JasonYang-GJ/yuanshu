using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using System.Windows.Forms;

namespace ScreenGuide.Skills.Windows;

public sealed record ForegroundWindowSnapshot(
    long WindowHandle,
    string WindowTitle,
    string ProcessName,
    int ProcessId,
    DateTimeOffset ProcessStartTimeUtc,
    DateTimeOffset ObservedAtUtc);

public static class WindowIdentityErrorCodes
{
    public const string Missing = "window_identity_missing";
    public const string Changed = "window_identity_changed";
}

public sealed class WindowIdentityException(string code, string message)
    : InvalidOperationException(message)
{
    public string Code { get; } = code;
}

public static class WindowIdentityContract
{
    public static bool Matches(
        ForegroundWindowSnapshot expected,
        ForegroundWindowSnapshot? actual) =>
        actual is not null
        && expected.WindowHandle == actual.WindowHandle
        && expected.ProcessId > 0
        && expected.ProcessId == actual.ProcessId
        && expected.ProcessStartTimeUtc == actual.ProcessStartTimeUtc
        && string.Equals(expected.ProcessName, actual.ProcessName, StringComparison.OrdinalIgnoreCase)
        && string.Equals(expected.WindowTitle, actual.WindowTitle, StringComparison.Ordinal);

    public static void RequireMatch(
        ForegroundWindowSnapshot expected,
        ForegroundWindowSnapshot? actual)
    {
        if (expected.ProcessId <= 0 || expected.ProcessStartTimeUtc == default)
        {
            throw new WindowIdentityException(
                WindowIdentityErrorCodes.Missing,
                "这条窗口授权缺少可信身份，请重新选择窗口并确认。 ");
        }

        if (!Matches(expected, actual))
        {
            throw new WindowIdentityException(
                WindowIdentityErrorCodes.Changed,
                "目标窗口身份已经变化，请重新选择窗口并确认。 ");
        }
    }
}

public sealed record DesktopAutomationResult(bool Verified, string Summary, string? TechnicalDetail = null)
{
    // Call-local readback of the input actually submitted. Never serialized or used for
    // a later write. A failed receipt must not be replaced by discovering another input.
    internal Func<ForegroundWindowSnapshot, string, CancellationToken, bool>? VerifySubmittedInput { get; init; }
}

public static class DesktopSearchErrorCodes
{
    public const string InvalidQuery = "desktop_search_query_invalid";
    public const string CandidateNotFound = "desktop_search_candidate_not_found";
    public const string CandidateAmbiguous = "desktop_search_candidate_ambiguous";
    public const string TargetChanged = "desktop_search_target_changed";
    public const string WriteVerificationFailed = "desktop_search_write_verification_failed";
    public const string FocusChanged = "desktop_search_focus_changed";
}

public sealed class DesktopSearchException(string code, string message)
    : InvalidOperationException(message)
{
    public string Code { get; } = code;
}

public static class DesktopSearchQuery
{
    public static string NormalizeAndValidate(string? query)
    {
        var raw = query ?? string.Empty;
        if (raw.Any(char.IsControl))
        {
            throw new DesktopSearchException(
                DesktopSearchErrorCodes.InvalidQuery,
                "搜索内容不能包含换行、制表符或控制字符。 ");
        }

        var normalized = raw.Trim().Normalize(NormalizationForm.FormKC);
        if (normalized.Length is < 1 or > 200)
        {
            throw new DesktopSearchException(
                DesktopSearchErrorCodes.InvalidQuery,
                "搜索内容应为 1 到 200 个字符。 ");
        }

        return normalized;
    }
}

internal readonly record struct DesktopSearchBounds(double X, double Y, double Width, double Height)
{
    public bool IsUsable =>
        double.IsFinite(X)
        && double.IsFinite(Y)
        && double.IsFinite(Width)
        && double.IsFinite(Height)
        && Width > 0
        && Height > 0;
}

internal sealed record DesktopSearchControlSnapshot(
    string RuntimeId,
    long OwnerWindowHandle,
    string Name,
    string AutomationId,
    string HelpText,
    string LabeledBy,
    bool IsEnabled,
    bool IsOffscreen,
    bool IsPassword,
    bool IsReadOnly,
    bool SupportsValuePattern,
    bool HasKeyboardFocus,
    DesktopSearchBounds Bounds,
    string Value,
    string NativeSearchIdentity = "",
    bool HasNativeSearchSemantics = false,
    bool NativeSearchUsesContainer = false,
    bool NativeSearchContainerPending = false);

internal sealed record DesktopSearchControl(object NativeReference, DesktopSearchControlSnapshot Snapshot);

internal interface IPreparedNativeSearchAutomation
{
    DesktopAutomationResult SearchWithPreparationLocator(ForegroundWindowSnapshot window, string query,
        DesktopSearchControl? locator, CancellationToken cancellationToken);
}

internal interface IBoundApplicationSearchAutomation
{
    DesktopAutomationResult SearchBoundApplication(ForegroundWindowSnapshot window, string query,
        DesktopSearchControl? locator, Func<CancellationToken, ForegroundWindowSnapshot> validateWindow,
        CancellationToken cancellationToken);
}

internal interface IDesktopSearchAutomationDriver
{
    bool IsWindow(long windowHandle);

    bool TryActivate(long windowHandle);

    long GetForegroundWindowHandle();

    IReadOnlyList<DesktopSearchControl> Discover(long windowHandle);

    DesktopSearchControlSnapshot Read(DesktopSearchControl control);

    void Focus(DesktopSearchControl control);

    void Focus(DesktopSearchControl control, Action validateAtFocusBoundary)
    {
        validateAtFocusBoundary();
        Focus(control);
    }

    void SetValue(DesktopSearchControl control, string value);

    void SubmitEnter(DesktopSearchControl control, Action validateAtSendBoundary);
}

internal sealed class SafeForegroundSearchExecutor(
    IForegroundWindowContextProvider foregroundWindows,
    IDesktopSearchAutomationDriver driver)
{
    private const int MinimumConfidence = 100;

    internal static bool IsReliableCandidate(DesktopSearchControlSnapshot snapshot, long expectedWindowHandle) =>
        IsSafe(snapshot, expectedWindowHandle) && HasStrongSearchEvidence(snapshot);

    public DesktopAutomationResult Search(ForegroundWindowSnapshot expectedWindow, string query, CancellationToken cancellationToken = default)
        => SearchCore(expectedWindow, query, null, cancellationToken);

    internal DesktopAutomationResult SearchBoundApplication(ForegroundWindowSnapshot expectedWindow, string query,
        Func<CancellationToken, ForegroundWindowSnapshot> validateWindow, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(validateWindow);
        return SearchCore(expectedWindow, query, validateWindow, cancellationToken);
    }

    private DesktopAutomationResult SearchCore(ForegroundWindowSnapshot expectedWindow, string query,
        Func<CancellationToken, ForegroundWindowSnapshot>? validateWindow, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(expectedWindow);
        var normalizedQuery = DesktopSearchQuery.NormalizeAndValidate(query);

        RequireSearchWindow(expectedWindow, cancellationToken);
        if (!driver.IsWindow(expectedWindow.WindowHandle))
        {
            throw Changed();
        }

        if (!driver.TryActivate(expectedWindow.WindowHandle))
        {
            throw Changed();
        }

        RequireSearchWindowAndForeground(expectedWindow, cancellationToken);
        IReadOnlyList<DesktopSearchControl> discovered;
        try
        {
            discovered = driver.Discover(expectedWindow.WindowHandle);
        }
        catch (Exception exception) when (IsVolatileAutomationFailure(exception))
        {
            throw Changed();
        }

        RequireSearchWindowAndForeground(expectedWindow, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        // Multiple UIA proxies may describe one IA2 node. Merge only independently bound native identities.
        var uniqueControls = discovered.GroupBy(control => string.IsNullOrEmpty(control.Snapshot.NativeSearchIdentity)
                ? "uia:" + control.Snapshot.RuntimeId : control.Snapshot.NativeSearchIdentity)
            .Select(group =>
            {
                var first = group.First();
                if (group.Any(item => (item.Snapshot with { RuntimeId = first.Snapshot.RuntimeId }) != first.Snapshot))
                    throw Changed();
                return first;
            });
        var safeCandidates = uniqueControls
            .Where(control => IsReliableCandidate(control.Snapshot, expectedWindow.WindowHandle))
            .Select(control => new ScoredControl(control, Score(control.Snapshot)))
            .ToArray();
        if (safeCandidates.Length == 0)
        {
            throw new DesktopSearchException(
                DesktopSearchErrorCodes.CandidateNotFound,
                "没有找到可可靠写入的搜索或地址栏，因此没有输入任何内容。 ");
        }

        if (safeCandidates.Length != 1)
        {
            throw new DesktopSearchException(
                DesktopSearchErrorCodes.CandidateAmbiguous,
                "无法唯一确认安全的搜索或地址栏，因此没有输入任何内容。 ");
        }

        var selected = safeCandidates[0].Control;
        var confidence = safeCandidates[0].Score;
        var bound = selected.Snapshot;
        var beforeFocus = ReadAndRequireBoundTarget(selected, bound, expectedWindow.WindowHandle);
        if (!IsSafe(beforeFocus, expectedWindow.WindowHandle))
        {
            throw Changed();
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            driver.Focus(selected, () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                RequireSearchWindowAndForeground(expectedWindow, cancellationToken);
            });
            RequireSearchWindowAndForeground(expectedWindow, cancellationToken);
            var beforeWrite = ReadAndRequireBoundTarget(selected, bound, expectedWindow.WindowHandle);
            RequireSearchWindowAndForeground(expectedWindow, cancellationToken);
            if (!beforeWrite.HasKeyboardFocus)
            {
                throw FocusChanged();
            }

            cancellationToken.ThrowIfCancellationRequested();
            driver.SetValue(selected, normalizedQuery);
            DesktopSearchControlSnapshot afterWrite;
            var readbackStarted = Stopwatch.GetTimestamp();
            do
            {
                cancellationToken.ThrowIfCancellationRequested();
                RequireSearchWindowAndForeground(expectedWindow, cancellationToken);
                afterWrite = ReadAndRequireBoundTarget(selected, bound, expectedWindow.WindowHandle, allowPendingSearchContainer: true);
                RequireSearchWindowAndForeground(expectedWindow, cancellationToken);
                if (!afterWrite.HasKeyboardFocus) throw FocusChanged();
                if (!(bound.HasNativeSearchSemantics && afterWrite.NativeSearchContainerPending)
                    && string.Equals(afterWrite.Value, normalizedQuery, StringComparison.Ordinal)) break;
                // CEF acknowledges ValuePattern.SetValue before its renderer exposes the new value.
                // Wait only for this independently bound search input; never type or submit twice.
                if (!bound.HasNativeSearchSemantics || string.IsNullOrEmpty(bound.NativeSearchIdentity)
                    || Stopwatch.GetElapsedTime(readbackStarted) >= TimeSpan.FromSeconds(1)) break;
                if (cancellationToken.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(50)))
                    cancellationToken.ThrowIfCancellationRequested();
            } while (true);

            if (bound.HasNativeSearchSemantics && afterWrite.NativeSearchContainerPending
                || !string.Equals(afterWrite.Value, normalizedQuery, StringComparison.Ordinal))
            {
                throw new DesktopSearchException(
                    DesktopSearchErrorCodes.WriteVerificationFailed,
                    "搜索内容没有被精确写入，因此没有提交。 ");
            }

            RequireSearchWindowAndForeground(expectedWindow, cancellationToken);
            var beforeSubmit = ReadAndRequireBoundTarget(selected, bound, expectedWindow.WindowHandle);
            if (!beforeSubmit.HasKeyboardFocus)
            {
                throw FocusChanged();
            }

            if (!string.Equals(beforeSubmit.Value, normalizedQuery, StringComparison.Ordinal))
            {
                throw new DesktopSearchException(
                    DesktopSearchErrorCodes.WriteVerificationFailed,
                    "搜索内容在提交前已经变化，因此没有提交。 ");
            }

            driver.SubmitEnter(selected, () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                RequireSearchWindowAndForeground(expectedWindow, cancellationToken);
                var atSendBoundary = ReadAndRequireBoundTarget(
                    selected,
                    bound,
                    expectedWindow.WindowHandle);
                RequireSearchWindowAndForeground(expectedWindow, cancellationToken);
                if (!atSendBoundary.HasKeyboardFocus)
                {
                    throw FocusChanged();
                }

                if (!string.Equals(atSendBoundary.Value, normalizedQuery, StringComparison.Ordinal))
                {
                    throw new DesktopSearchException(
                        DesktopSearchErrorCodes.WriteVerificationFailed,
                        "搜索内容在提交边界已经变化，因此没有提交。 ");
                }
            });
        }
        catch (DesktopSearchException)
        {
            throw;
        }
        catch (WindowIdentityException)
        {
            throw;
        }
        catch (ApplicationSearchWorkflowException)
        {
            throw;
        }
        catch (Exception exception) when (IsVolatileAutomationFailure(exception))
        {
            throw Changed();
        }

        return new DesktopAutomationResult(
            true,
            "已确认内容写入唯一识别的搜索框并提交；搜索结果页面尚未验证。",
            $"UniqueCandidate=true; Confidence={confidence}; ExactValueVerified=true; SubmitCount=1")
        {
            VerifySubmittedInput = bound.HasNativeSearchSemantics && bound.NativeSearchIdentity.Length > 0
                ? VerifySubmittedInput : null
        };

        bool VerifySubmittedInput(ForegroundWindowSnapshot currentWindow, string expectedQuery, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            // Navigation may change the title; no window/process identity may change.
            if (expectedQuery != normalizedQuery || !WindowIdentityContract.Matches(expectedWindow,
                    currentWindow with { WindowTitle = expectedWindow.WindowTitle })) return false;
            try
            {
                RequireSearchWindowAndForeground(currentWindow, token);
                var current = ReadAndRequireBoundTarget(selected, bound, expectedWindow.WindowHandle);
                token.ThrowIfCancellationRequested();
                RequireSearchWindowAndForeground(currentWindow, token);
                return IsReliableCandidate(current, expectedWindow.WindowHandle)
                    && current.HasNativeSearchSemantics && !current.NativeSearchContainerPending
                    && current.Value == normalizedQuery;
            }
            catch (Exception exception) when (IsVolatileAutomationFailure(exception))
            {
                token.ThrowIfCancellationRequested();
                return false;
            }
        }

        void RequireSearchWindow(ForegroundWindowSnapshot reference, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            cancellationToken.ThrowIfCancellationRequested();
            if (validateWindow is null)
            {
                RequireWindow(reference);
                return;
            }
            // Only the application workflow can provide this live validator. It rechecks
            // its original registered target, process lifetime, native path and foreground.
            // A changing title carries no new authority; every other identity stays exact.
            var current = validateWindow(token);
            WindowIdentityContract.RequireMatch(reference, current with { WindowTitle = reference.WindowTitle });
            token.ThrowIfCancellationRequested();
            cancellationToken.ThrowIfCancellationRequested();
        }

        void RequireSearchWindowAndForeground(ForegroundWindowSnapshot reference, CancellationToken token)
        {
            RequireSearchWindow(reference, token);
            if (driver.GetForegroundWindowHandle() != reference.WindowHandle) throw Changed();
        }
    }

    private void RequireWindow(ForegroundWindowSnapshot expectedWindow) =>
        WindowIdentityContract.RequireMatch(
            expectedWindow,
            foregroundWindows.ResolveWindow(expectedWindow.WindowHandle));

    private DesktopSearchControlSnapshot ReadAndRequireBoundTarget(
        DesktopSearchControl control,
        DesktopSearchControlSnapshot bound,
        long expectedWindowHandle,
        bool allowPendingSearchContainer = false)
    {
        DesktopSearchControlSnapshot current;
        try
        {
            current = driver.Read(control);
        }
        catch (Exception exception) when (IsVolatileAutomationFailure(exception))
        {
            throw Changed();
        }

        var sameNativeInput = bound.HasNativeSearchSemantics
            && !string.IsNullOrEmpty(bound.NativeSearchIdentity)
            && string.Equals(bound.NativeSearchIdentity, current.NativeSearchIdentity, StringComparison.Ordinal);
        var pendingContainer = allowPendingSearchContainer && sameNativeInput && bound.NativeSearchUsesContainer
            && current.NativeSearchUsesContainer && current.NativeSearchContainerPending && !current.HasNativeSearchSemantics;
        var independentlyBoundSearch = sameNativeInput && (current.HasNativeSearchSemantics || pendingContainer);
        if (current.OwnerWindowHandle != expectedWindowHandle
            || !independentlyBoundSearch && !string.Equals(current.RuntimeId, bound.RuntimeId, StringComparison.Ordinal)
            || !independentlyBoundSearch && current.Bounds != bound.Bounds
            || (!independentlyBoundSearch || bound.NativeSearchUsesContainer) && !string.Equals(current.Name, bound.Name, StringComparison.Ordinal)
            || !string.Equals(current.AutomationId, bound.AutomationId, StringComparison.Ordinal)
            || !string.Equals(current.HelpText, bound.HelpText, StringComparison.Ordinal)
            || !string.Equals(current.LabeledBy, bound.LabeledBy, StringComparison.Ordinal)
            || !string.Equals(current.NativeSearchIdentity, bound.NativeSearchIdentity, StringComparison.Ordinal)
            || !pendingContainer && current.HasNativeSearchSemantics != bound.HasNativeSearchSemantics
            || current.NativeSearchUsesContainer != bound.NativeSearchUsesContainer
            || bound.HasNativeSearchSemantics && current.NativeSearchContainerPending && !pendingContainer
            || !IsSafe(current, expectedWindowHandle))
        {
            throw Changed();
        }

        return current;
    }

    private static bool IsSafe(DesktopSearchControlSnapshot snapshot, long expectedWindowHandle) =>
        snapshot.OwnerWindowHandle == expectedWindowHandle
        && !string.IsNullOrWhiteSpace(snapshot.RuntimeId)
        && snapshot.IsEnabled
        && !snapshot.IsOffscreen
        && !snapshot.IsPassword
        && !snapshot.IsReadOnly
        && snapshot.SupportsValuePattern
        && snapshot.Bounds.IsUsable;

    private static int Score(DesktopSearchControlSnapshot snapshot) =>
        (snapshot.HasNativeSearchSemantics && !string.IsNullOrEmpty(snapshot.NativeSearchIdentity) ? 140 : 0)
        + ScoreEvidence(snapshot.Name, 20)
        + ScoreEvidence(snapshot.LabeledBy, 20)
        + ScoreEvidence(snapshot.AutomationId, 10)
        + ScoreEvidence(snapshot.HelpText, 0);

    private static bool HasStrongSearchEvidence(DesktopSearchControlSnapshot snapshot) =>
        // Internal IDs and help text can describe an unrelated embedded webpage
        // field. They may corroborate a search input, never establish one alone.
        snapshot.HasNativeSearchSemantics && !string.IsNullOrEmpty(snapshot.NativeSearchIdentity)
        || ScoreEvidence(snapshot.Name, 0) >= MinimumConfidence
        || ScoreEvidence(snapshot.LabeledBy, 0) >= MinimumConfidence;

    private static int ScoreEvidence(string value, int sourceBonus)
    {
        var normalized = (value ?? string.Empty).Trim().Normalize(NormalizationForm.FormKC);
        if (normalized.Length == 0)
        {
            return 0;
        }

        if (normalized.Equals("search", StringComparison.OrdinalIgnoreCase)) return 100 + sourceBonus;

        var strongPhrases = new[]
        {
            "地址和搜索栏", "地址栏", "搜索框", "搜索栏", "搜索", "查找",
            "address and search bar", "address bar", "search box", "search field", "omnibox"
        };
        if (strongPhrases.Any(phrase => normalized.Contains(phrase, StringComparison.OrdinalIgnoreCase)))
        {
            return 100 + sourceBonus;
        }

        var weakTerms = new[] { "address", "search", "find", "地址", "网址" };
        return weakTerms.Any(term => normalized.Contains(term, StringComparison.OrdinalIgnoreCase))
            ? 50 + sourceBonus
            : 0;
    }

    private static bool IsVolatileAutomationFailure(Exception exception) =>
        exception is ElementNotAvailableException
            or InvalidOperationException
            or ArgumentException
            or Win32Exception
            or COMException;

    private static DesktopSearchException Changed() =>
        new(
            DesktopSearchErrorCodes.TargetChanged,
            "目标窗口或搜索控件已经变化，因此没有提交。 ");

    private static DesktopSearchException FocusChanged() =>
        new(
            DesktopSearchErrorCodes.FocusChanged,
            "搜索控件的输入焦点已经变化，因此没有提交。 ");

    private sealed record ScoredControl(DesktopSearchControl Control, int Score);
}

public interface IForegroundWindowContextProvider
{
    ForegroundWindowSnapshot? GetLastExternalWindow();

    ForegroundWindowSnapshot? ResolveWindow(long windowHandle)
    {
        var current = GetLastExternalWindow();
        return current?.WindowHandle == windowHandle ? current : null;
    }
}

public interface IReliableDesktopAutomation
{
    DesktopAutomationResult Search(ForegroundWindowSnapshot expectedWindow, string query);

    DesktopAutomationResult Search(ForegroundWindowSnapshot expectedWindow, string query, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Search(expectedWindow, query);
    }

    DesktopAutomationResult Describe(ForegroundWindowSnapshot expectedWindow);
}

/// <summary>
/// Remembers the most recently focused non-ScreenGuide top-level window so clicking the
/// assistant does not lose the user's intended target. No screenshot or control value is stored.
/// </summary>
public sealed class ForegroundWindowTracker : IForegroundWindowContextProvider, IDisposable
{
    private readonly System.Threading.Timer _timer;
    private ForegroundWindowSnapshot? _lastExternal;
    private int _polling;

    public ForegroundWindowTracker()
    {
        _timer = new System.Threading.Timer(
            _ => Poll(), null, TimeSpan.Zero, TimeSpan.FromMilliseconds(400));
    }

    public ForegroundWindowSnapshot? GetLastExternalWindow() => Volatile.Read(ref _lastExternal);

    public ForegroundWindowSnapshot? ResolveWindow(long windowHandle) =>
        TryReadWindow(new IntPtr(windowHandle));

    public void Dispose() => _timer.Dispose();

    private void Poll()
    {
        if (Interlocked.Exchange(ref _polling, 1) != 0)
        {
            return;
        }

        try
        {
            var handle = GetForegroundWindow();
            if (handle == IntPtr.Zero || !IsWindow(handle))
            {
                return;
            }

            var snapshot = TryReadWindow(handle);
            if (snapshot is null
                || snapshot.ProcessId == Environment.ProcessId
                || snapshot.ProcessName.StartsWith("ScreenGuide.", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            Volatile.Write(ref _lastExternal, snapshot);
        }
        catch (ArgumentException)
        {
            // The foreground process can exit between Win32 calls.
        }
        catch (InvalidOperationException)
        {
            // The foreground process can exit between Win32 calls.
        }
        catch (Win32Exception)
        {
            // Windows can deny process metadata while the foreground target changes.
        }
        catch (NotSupportedException)
        {
            // Some protected processes do not expose a start time.
        }
        finally
        {
            Volatile.Write(ref _polling, 0);
        }
    }

    private static ForegroundWindowSnapshot? TryReadWindow(IntPtr handle)
    {
        if (handle == IntPtr.Zero || !IsWindow(handle))
        {
            return null;
        }

        try
        {
            _ = GetWindowThreadProcessId(handle, out var processId);
            if (processId == 0 || processId > int.MaxValue)
            {
                return null;
            }

            using var process = Process.GetProcessById((int)processId);
            var processName = process.ProcessName;
            var processStartTimeUtc = new DateTimeOffset(process.StartTime.ToUniversalTime());
            var title = GetTitle(handle).Trim();
            if (string.IsNullOrWhiteSpace(title))
            {
                return null;
            }

            return new ForegroundWindowSnapshot(
                handle.ToInt64(),
                title,
                processName,
                process.Id,
                processStartTimeUtc,
                DateTimeOffset.UtcNow);
        }
        catch (Exception exception) when (exception is
            ArgumentException or
            InvalidOperationException or
            Win32Exception or
            NotSupportedException)
        {
            return null;
        }
    }

    private static string GetTitle(IntPtr handle)
    {
        var length = GetWindowTextLength(handle);
        if (length <= 0)
        {
            return string.Empty;
        }

        var buffer = new char[Math.Min(length + 1, 512)];
        var written = GetWindowText(handle, buffer, buffer.Length);
        return written > 0 ? new string(buffer, 0, written) : string.Empty;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, char[] text, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hWnd);
}

public sealed class WindowsUiAutomationService : IReliableDesktopAutomation, IPreparedNativeSearchAutomation,
    IBoundApplicationSearchAutomation
{
    private readonly IForegroundWindowContextProvider _foregroundWindows;
    private readonly SafeForegroundSearchExecutor _searchExecutor;
    private readonly bool _usesDefaultNativeDriver;

    public WindowsUiAutomationService(IForegroundWindowContextProvider foregroundWindows)
        : this(foregroundWindows, new WindowsDesktopSearchAutomationDriver(), usesDefaultNativeDriver: true)
    {
    }

    internal WindowsUiAutomationService(
        IForegroundWindowContextProvider foregroundWindows,
        IDesktopSearchAutomationDriver searchDriver)
        : this(foregroundWindows, searchDriver, usesDefaultNativeDriver: false)
    {
    }

    private WindowsUiAutomationService(IForegroundWindowContextProvider foregroundWindows,
        IDesktopSearchAutomationDriver searchDriver, bool usesDefaultNativeDriver)
    {
        _foregroundWindows = foregroundWindows;
        _searchExecutor = new SafeForegroundSearchExecutor(foregroundWindows, searchDriver);
        _usesDefaultNativeDriver = usesDefaultNativeDriver;
    }

    public DesktopAutomationResult Search(ForegroundWindowSnapshot expectedWindow, string query)
        => _searchExecutor.Search(expectedWindow, query);

    public DesktopAutomationResult Search(ForegroundWindowSnapshot expectedWindow, string query, CancellationToken cancellationToken)
        => _searchExecutor.Search(expectedWindow, query, cancellationToken);

    DesktopAutomationResult IPreparedNativeSearchAutomation.SearchWithPreparationLocator(
        ForegroundWindowSnapshot window, string query, DesktopSearchControl? locator, CancellationToken cancellationToken)
        => SearchWithPreparationLocator(window, query, locator, cancellationToken);

    internal DesktopAutomationResult SearchWithPreparationLocator(ForegroundWindowSnapshot window, string query,
        DesktopSearchControl? locator, CancellationToken cancellationToken)
        // A per-call native driver may use the reference only as a locator. Never replace
        // an injected driver/decorator or retain a mutable locator in this singleton service.
        => _usesDefaultNativeDriver && locator is not null
            ? new SafeForegroundSearchExecutor(_foregroundWindows, new WindowsDesktopSearchAutomationDriver(locator))
                .Search(window, query, cancellationToken)
            : _searchExecutor.Search(window, query, cancellationToken);

    DesktopAutomationResult IBoundApplicationSearchAutomation.SearchBoundApplication(ForegroundWindowSnapshot window,
        string query, DesktopSearchControl? locator, Func<CancellationToken, ForegroundWindowSnapshot> validateWindow,
        CancellationToken cancellationToken) => SearchBoundApplication(window, query, locator, validateWindow, cancellationToken);

    internal DesktopAutomationResult SearchBoundApplication(ForegroundWindowSnapshot window, string query,
        DesktopSearchControl? locator, Func<CancellationToken, ForegroundWindowSnapshot> validateWindow,
        CancellationToken cancellationToken)
        => _usesDefaultNativeDriver && locator is not null
            ? new SafeForegroundSearchExecutor(_foregroundWindows, new WindowsDesktopSearchAutomationDriver(locator))
                .SearchBoundApplication(window, query, validateWindow, cancellationToken)
            : _searchExecutor.SearchBoundApplication(window, query, validateWindow, cancellationToken);

    public DesktopAutomationResult Describe(ForegroundWindowSnapshot expectedWindow)
    {
        ArgumentNullException.ThrowIfNull(expectedWindow);
        RequireCurrentIdentity(expectedWindow);
        var handle = new IntPtr(expectedWindow.WindowHandle);
        if (handle == IntPtr.Zero || !IsWindow(handle))
        {
            throw new InvalidOperationException("目标窗口已经关闭，请重新切回目标软件后再试。 ");
        }

        RequireCurrentIdentity(expectedWindow);
        var root = AutomationElement.FromHandle(handle)
            ?? throw new InvalidOperationException("Windows 无法读取这个窗口的控件结构。 ");
        var title = SafeCurrent(() => root.Current.Name);
        var controls = root.FindAll(TreeScope.Descendants, Condition.TrueCondition)
            .Cast<AutomationElement>()
            .Select(element => new
            {
                Name = SafeCurrent(() => element.Current.Name),
                Type = SafeCurrent(() => element.Current.ControlType?.LocalizedControlType ?? string.Empty)
            })
            .Where(item => !string.IsNullOrWhiteSpace(item.Name))
            .Take(30)
            .Select(item => $"{item.Type}“{item.Name.Trim()}”")
            .ToArray();
        var summary = controls.Length == 0
            ? $"当前窗口“{title}”没有向 Windows 公开可说明的控件名称。"
            : $"当前窗口“{title}”包含：{string.Join("、", controls)}。";
        return new DesktopAutomationResult(true, summary);
    }

    private void RequireCurrentIdentity(ForegroundWindowSnapshot expectedWindow) =>
        WindowIdentityContract.RequireMatch(
            expectedWindow,
            _foregroundWindows.ResolveWindow(expectedWindow.WindowHandle));

    private static string SafeCurrent(Func<string> getter)
    {
        try
        {
            return getter() ?? string.Empty;
        }
        catch (ElementNotAvailableException)
        {
            return string.Empty;
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hWnd);

}

internal class WindowsDesktopSearchAutomationDriver : IDesktopSearchAutomationDriver
{
    private DesktopSearchControl? _preparationLocator;

    internal WindowsDesktopSearchAutomationDriver(DesktopSearchControl? preparationLocator = null)
        => _preparationLocator = preparationLocator;

    public bool IsWindow(long windowHandle) =>
        windowHandle != 0 && NativeIsWindow(new IntPtr(windowHandle));

    public bool TryActivate(long windowHandle) =>
        VisibleWindowActivation.TryActivate(new IntPtr(windowHandle), TimeSpan.FromSeconds(2));

    public virtual long GetForegroundWindowHandle() => GetForegroundWindow().ToInt64();

    public IReadOnlyList<DesktopSearchControl> Discover(long windowHandle) => Discover(windowHandle, nativePreparation: false);

    // Preparation is read-only and retried by the bounded readiness loop. During CEF startup,
    // a missing/mismatched native-to-UIA binding must not trigger an expensive whole-page
    // legacy-proxy traversal. Read the actual UIA/native binding, but do not enumerate the
    // entire page just to wait for it. These samples are readiness hints, not uniqueness
    // proofs or input authority. Actual Search independently uses full Discover.
    internal IReadOnlyList<DesktopSearchControl> DiscoverNativePreparation(long windowHandle) =>
        Discover(windowHandle, nativePreparation: true);

    internal Func<long, IReadOnlyList<DesktopSearchControl>> CreateNativePreparationProbe() =>
        CreateReadOnlyPreparationProbe(DiscoverNativePreparation, Read);

    internal static Func<long, IReadOnlyList<DesktopSearchControl>> CreateReadOnlyPreparationProbe(
        Func<long, IReadOnlyList<DesktopSearchControl>> discover,
        Func<DesktopSearchControl, DesktopSearchControlSnapshot> read)
    {
        // Only the caller's one readiness wait owns these references. Every sample reads
        // current properties and parent/native binding again. A final stable reference may
        // locate the input later; Search must independently rebuild its token and proof.
        long sampledWindow = 0;
        IReadOnlyList<DesktopSearchControl> sampled = [];
        return window =>
        {
            if (sampledWindow != window) { sampledWindow = window; sampled = []; }
            if (sampled.Count > 0)
            {
                try
                {
                    var current = sampled.Select(control => control with { Snapshot = read(control) }).ToArray();
                    if (current.Any(control => control.Snapshot.OwnerWindowHandle != window))
                    { sampled = []; return []; }
                    sampled = current;
                    return current;
                }
                catch (Exception exception) when (exception is ElementNotAvailableException or COMException
                    or InvalidOperationException or ArgumentException or Win32Exception)
                { sampled = []; }
            }
            sampled = discover(window);
            return sampled;
        };
    }

    private IReadOnlyList<DesktopSearchControl> Discover(long windowHandle, bool nativePreparation)
    {
        var locator = nativePreparation ? null : Interlocked.Exchange(ref _preparationLocator, null);
        var legacyRoots = LegacyBrowserSearchSemantics.Open(windowHandle);
        var root = AutomationElement.FromHandle(new IntPtr(windowHandle))
            ?? throw new ElementNotAvailableException();
        var rootRuntimeId = RuntimeId(root);
        var condition = new PropertyCondition(
            AutomationElement.ControlTypeProperty,
            ControlType.Edit);
        var result = new List<DesktopSearchControl>();
        // Query native renderer roots directly. The top-level legacy proxy can expose each DOM node twice
        // and traverse the entire page twice; native parent/child ownership is rechecked on every read.
        var discoveryRoots = legacyRoots.Select(item => AutomationElement.FromHandle(new IntPtr(item.Handle))).ToArray();
        foreach (var discoveryRoot in discoveryRoots.Length == 0 ? [root] : discoveryRoots)
        {
            var nativeRoot = legacyRoots.SingleOrDefault(item => item.Handle == discoveryRoot.Current.NativeWindowHandle);
            DesktopSearchControl? ReadInput(AutomationElement inputRoot)
            {
                var element = inputRoot.FindFirst(TreeScope.Descendants, condition);
                if (element is null) return null;
                var token = new AutomationToken(inputRoot, RuntimeId(inputRoot), element, windowHandle, legacyRoots);
                return new DesktopSearchControl(token, ReadToken(token));
            }
            DesktopSearchControl? ReadFirstInput() => ReadFirstSearchInput(
                nativePreparation && discoveryRoots.Length > 0, () => ReadInput(discoveryRoot),
                () => { _ = root.FindFirst(TreeScope.Descendants, condition); });
            DesktopSearchControl? ReadLocator(string nativeIdentity) => TryReadPreparationLocator(nativeIdentity, windowHandle,
                () => locator?.NativeReference is AutomationToken token && token.Element.Current.ControlType == ControlType.Edit,
                () =>
                {
                    var previous = Token(locator!);
                    if (previous.WindowHandle != windowHandle) return null;
                    var previousRootHandle = previous.Root.Current.NativeWindowHandle;
                    // Top-level preparation proxies remain bound to a fresh top root;
                    // renderer proxies remain bound to their own freshly opened renderer.
                    var currentRoot = previousRootHandle == windowHandle ? root : discoveryRoot;
                    var currentRootId = RuntimeId(currentRoot);
                    if (previousRootHandle != currentRoot.Current.NativeWindowHandle
                        || previous.RootRuntimeId != currentRootId || RuntimeId(previous.Root) != previous.RootRuntimeId
                        || RuntimeId(previous.Element) != locator!.Snapshot.RuntimeId) return null;
                    var token = new AutomationToken(currentRoot, currentRootId, previous.Element, windowHandle, legacyRoots);
                    return new DesktopSearchControl(token, ReadToken(token));
                });
            if (nativePreparation)
            {
                if (nativeRoot is not null && ReadFirstInput() is { } hint) result.Add(hint);
                continue;
            }
            if (nativeRoot is not null && TryReadSoleNativeEditWithRetry(
                () => LegacyBrowserSearchSemantics.ReadNativeEditProof(nativeRoot, windowHandle),
                ReadFirstInput, windowHandle, locator is null ? null : ReadLocator) is { } single)
            {
                result.Add(single);
                continue;
            }
            foreach (AutomationElement element in discoveryRoot.FindAll(TreeScope.Descendants, condition))
            {
                try
                {
                    var token = new AutomationToken(discoveryRoot, RuntimeId(discoveryRoot), element, windowHandle, legacyRoots);
                    result.Add(new DesktopSearchControl(token, ReadToken(token)));
                }
                catch (ElementNotAvailableException)
                {
                    // A disappearing control is omitted; later phases fail closed on the bound control.
                }
            }
        }

        if (!nativePreparation && result.Count == 0 && discoveryRoots.Length > 0)
            foreach (AutomationElement element in root.FindAll(TreeScope.Descendants, condition))
            {
                try {
                    var token = new AutomationToken(root, rootRuntimeId, element, windowHandle, legacyRoots);
                    result.Add(new DesktopSearchControl(token, ReadToken(token)));
                }
                catch (ElementNotAvailableException) { }
            }

        return result;
    }

    internal static DesktopSearchControl? TryReadSoleNativeEditWithRetry(
        Func<LegacyBrowserSearchSemantics.NativeEditProof> readCompleteNativeTree,
        Func<DesktopSearchControl?> readFirstUiaEdit, long windowHandle,
        Func<string, DesktopSearchControl?>? readPreparationLocator = null)
    {
        string? identity = null;
        return TryReadSoleNativeEdit(() =>
        {
            var proof = readCompleteNativeTree();
            // CEF may replace a node during startup. Only an incomplete read gets one
            // fresh, complete proof; known absence/ambiguity never retries into authority.
            if (proof.State == LegacyBrowserSearchSemantics.NativeEditProofState.Incomplete)
                proof = readCompleteNativeTree();
            return identity = proof.State == LegacyBrowserSearchSemantics.NativeEditProofState.Unique ? proof.Identity : null;
        }, () =>
        {
            DesktopSearchControl? candidate = null;
            try { candidate = readPreparationLocator?.Invoke(identity!); }
            catch (Exception exception) when (IsUnavailableLocator(exception)) { }
            return candidate ?? readFirstUiaEdit();
        }, windowHandle);
    }

    internal static DesktopSearchControl? ReadFirstSearchInput(bool allowTopPriming,
        Func<DesktopSearchControl?> readRenderer, Action primeTopProvider)
    {
        var candidate = readRenderer();
        // A cold renderer may need its containing UIA provider initialized. Discard the
        // top query result; the next read-only sample must reacquire the renderer input.
        if (candidate is null && allowTopPriming) primeTopProvider();
        return candidate;
    }

    internal static DesktopSearchControl? TryReadPreparationLocator(string nativeIdentity, long windowHandle,
        Func<bool> isCurrentEdit, Func<DesktopSearchControl?> readFreshToken)
    {
        try
        {
            // FindFirst used to enforce ControlType.Edit. A retained reference must enforce
            // that live condition too, before rebuilding its token with the current roots.
            if (!isCurrentEdit()) return null;
            var current = readFreshToken();
            return current is not null && current.Snapshot.OwnerWindowHandle == windowHandle
                && current.Snapshot.NativeSearchIdentity == nativeIdentity && nativeIdentity.Length > 0
                && current.Snapshot.HasNativeSearchSemantics && !current.Snapshot.NativeSearchContainerPending
                && SafeForegroundSearchExecutor.IsReliableCandidate(current.Snapshot, windowHandle) ? current : null;
        }
        catch (Exception exception) when (IsUnavailableLocator(exception)) { return null; }
    }

    private static bool IsUnavailableLocator(Exception exception) => exception is ElementNotAvailableException or COMException
        or InvalidOperationException or ArgumentException or Win32Exception;

    internal static DesktopSearchControl? TryReadSoleNativeEdit(Func<string?> readCompleteNativeTree,
        Func<DesktopSearchControl?> readFirstUiaEdit, long windowHandle, bool primeUiaFirst = false)
    {
        try
        {
            // FindFirst requests the actual UIA descendant provider while CEF is starting.
            // FromHandle alone can leave its native document tree uninitialized. The result
            // is only a candidate; the complete native proof below remains mandatory.
            var control = primeUiaFirst ? readFirstUiaEdit() : null;
            var identity = readCompleteNativeTree();
            if (string.IsNullOrEmpty(identity)) return null;
            if (!primeUiaFirst) control = readFirstUiaEdit();
            // A native uniqueness proof is only an acceleration hint. The UIA control must still
            // pass the existing live parent-chain and native-input binding, with the exact same ID.
            return control?.Snapshot.OwnerWindowHandle == windowHandle
                && control.Snapshot.NativeSearchIdentity == identity ? control : null;
        }
        catch (Exception exception) when (exception is ElementNotAvailableException or COMException)
        { return null; }
    }

    public DesktopSearchControlSnapshot Read(DesktopSearchControl control) =>
        ReadToken(Token(control));

    public void Focus(DesktopSearchControl control) => Focus(control, () => { });

    public void Focus(DesktopSearchControl control, Action validateAtFocusBoundary)
    {
        var token = Token(control);
        var snapshot = ReadToken(token);
        if (snapshot.HasKeyboardFocus) return;
        validateAtFocusBoundary();
        FocusWithNativeFallback(snapshot.HasNativeSearchSemantics && snapshot.NativeSearchIdentity.Length > 0,
            () => token.Element.SetFocus(),
            () =>
            {
                validateAtFocusBoundary();
                return LegacyBrowserSearchSemantics.TryFocus(token.Element, token.WindowHandle, token.LegacyRoots, snapshot.NativeSearchIdentity);
            },
            () => TryInvokeBoundSearchFocus(token, snapshot, validateAtFocusBoundary));
    }

    internal static void FocusWithNativeFallback(bool independentlyBoundSearch, Action focus, Func<bool> focusNative,
        Func<bool>? invokeBoundSearch = null)
    {
        try { focus(); }
        catch (InvalidOperationException) when (independentlyBoundSearch)
        {
            if (!focusNative() && !(invokeBoundSearch?.Invoke() ?? false))
                throw new ElementNotAvailableException("原生搜索框没有确认取得输入焦点。");
        }
    }

    private bool TryInvokeBoundSearchFocus(AutomationToken token, DesktopSearchControlSnapshot expected, Action validateAtFocusBoundary)
    {
        bool IsBound(DesktopSearchControlSnapshot current) => current.HasNativeSearchSemantics
            && current.NativeSearchUsesContainer == expected.NativeSearchUsesContainer
            && (!expected.NativeSearchUsesContainer || current.Name == expected.Name)
            && current.NativeSearchIdentity == expected.NativeSearchIdentity && expected.NativeSearchIdentity.Length > 0
            && current.OwnerWindowHandle == expected.OwnerWindowHandle
            && current.AutomationId == expected.AutomationId && current.HelpText == expected.HelpText && current.LabeledBy == expected.LabeledBy
            && SafeForegroundSearchExecutor.IsReliableCandidate(current, expected.OwnerWindowHandle);
        if (!IsBound(ReadToken(token)) || GetForegroundWindowHandle() != expected.OwnerWindowHandle
            || !token.Element.TryGetCurrentPattern(InvokePattern.Pattern, out var rawPattern)) return false;
        validateAtFocusBoundary();
        if (!IsBound(ReadToken(token)) || GetForegroundWindowHandle() != expected.OwnerWindowHandle) return false;
        validateAtFocusBoundary();
        ((InvokePattern)rawPattern).Invoke();
        // Invoke is only on the same proven search input; it is not a result click or a pointer action.
        for (var attempt = 0; attempt < 6; attempt++)
        {
            validateAtFocusBoundary();
            var current = ReadToken(token);
            if (!IsBound(current) || GetForegroundWindowHandle() != expected.OwnerWindowHandle) return false;
            if (current.HasKeyboardFocus) return true;
            if (attempt < 5) Thread.Sleep(50);
        }
        return false;
    }

    public void SetValue(DesktopSearchControl control, string value)
    {
        var token = Token(control);
        if (!token.Element.TryGetCurrentPattern(ValuePattern.Pattern, out var rawPattern))
        {
            throw new ElementNotAvailableException();
        }

        ((ValuePattern)rawPattern).SetValue(value);
    }

    public virtual void SubmitEnter(
        DesktopSearchControl control,
        Action validateAtSendBoundary)
    {
        _ = Token(control);
        ArgumentNullException.ThrowIfNull(validateAtSendBoundary);
        validateAtSendBoundary();
        SendKeys.SendWait("{ENTER}");
    }

    private static DesktopSearchControlSnapshot ReadToken(AutomationToken token)
    {
        if (!IsDescendantOfBoundRoot(token))
        {
            throw new ElementNotAvailableException();
        }

        var element = token.Element;
        var supportsValue = element.TryGetCurrentPattern(ValuePattern.Pattern, out var rawPattern);
        var value = supportsValue ? ((ValuePattern)rawPattern).Current.Value ?? string.Empty : string.Empty;
        var isReadOnly = supportsValue && ((ValuePattern)rawPattern).Current.IsReadOnly;
        var bounds = element.Current.BoundingRectangle;
        var semantics = LegacyBrowserSearchSemantics.Read(element, token.WindowHandle, token.LegacyRoots);
        return new DesktopSearchControlSnapshot(
            RuntimeId(element),
            token.WindowHandle,
            element.Current.Name ?? string.Empty,
            element.Current.AutomationId ?? string.Empty,
            element.Current.HelpText ?? string.Empty,
            element.Current.LabeledBy?.Current.Name ?? string.Empty,
            element.Current.IsEnabled,
            element.Current.IsOffscreen,
            element.Current.IsPassword,
            isReadOnly,
            supportsValue,
            element.Current.HasKeyboardFocus || semantics?.HasKeyboardFocus == true,
            new DesktopSearchBounds(bounds.X, bounds.Y, bounds.Width, bounds.Height),
            value,
            semantics?.Identity ?? "",
            semantics?.IsSearch ?? false,
            semantics?.UsesSearchContainer ?? false,
            semantics?.SearchContainerPending ?? false);
    }

    private static bool IsDescendantOfBoundRoot(AutomationToken token)
    {
        var rootHandle = new IntPtr(token.Root.Current.NativeWindowHandle);
        if (rootHandle == IntPtr.Zero || GetAncestor(rootHandle, 2).ToInt64() != token.WindowHandle) return false;
        if (!string.Equals(RuntimeId(token.Root), token.RootRuntimeId, StringComparison.Ordinal))
        {
            return false;
        }

        var current = token.Element;
        for (var depth = 0; current is not null && depth < 128; depth++)
        {
            if (string.Equals(RuntimeId(current), token.RootRuntimeId, StringComparison.Ordinal))
            {
                return true;
            }

            current = TreeWalker.RawViewWalker.GetParent(current);
        }

        return false;
    }

    private static AutomationToken Token(DesktopSearchControl control) =>
        control.NativeReference as AutomationToken
        ?? throw new ElementNotAvailableException();

    private static string RuntimeId(AutomationElement element) =>
        string.Join(".", element.GetRuntimeId());

    [DllImport("user32.dll", EntryPoint = "IsWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool NativeIsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr window, uint flags);

    private sealed record AutomationToken(
        AutomationElement Root,
        string RootRuntimeId,
        AutomationElement Element,
        long WindowHandle,
        IReadOnlyList<LegacyBrowserSearchSemantics.Root> LegacyRoots);
}
