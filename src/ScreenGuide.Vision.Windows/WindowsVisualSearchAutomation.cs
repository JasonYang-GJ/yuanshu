using System.Drawing;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using ScreenGuide.Skills.Windows;
using ScreenGuide.Vision.Abstractions;

namespace ScreenGuide.Vision.Windows;

/// <summary>Single-window local visual search. Images and OCR are call-local and never sent or saved.</summary>
public sealed class WindowsVisualSearchAutomation : IVisualSearchAutomation
{
    private readonly IWindowCaptureService _capture;
    private readonly IForegroundWindowContextProvider _windows;
    private readonly IVisualSearchConsent _consent;
    private readonly IVisualSearchInputDriver _input;
    private readonly Func<byte[], CancellationToken, Task<VisualSearchField>> _find;
    private readonly Func<byte[], VisualSearchRect, CancellationToken, Task<IReadOnlyList<VisualSearchText>>> _read;
    private readonly Func<byte[], VisualSearchRect, CancellationToken, bool> _matchingControl;
    private readonly IVisualSearchResultWindows? _resultWindows;

    public WindowsVisualSearchAutomation(IWindowCaptureService capture,
        IForegroundWindowContextProvider windows, IVisualSearchConsent consent, VisualSearchImageReader reader)
        : this(capture, windows, consent, new WindowsVisualSearchInputDriver(),
            reader.FindUniqueSearchFieldAsync, reader.ReadAsync, null, new WindowsVisualSearchResultWindowVerifier()) { }

    internal WindowsVisualSearchAutomation(IWindowCaptureService capture,
        IForegroundWindowContextProvider windows, IVisualSearchConsent consent, IVisualSearchInputDriver input,
        Func<byte[], CancellationToken, Task<VisualSearchField>> find,
        Func<byte[], VisualSearchRect, CancellationToken, Task<IReadOnlyList<VisualSearchText>>> read,
        Func<byte[], VisualSearchRect, CancellationToken, bool>? matchingControl = null,
        IVisualSearchResultWindows? resultWindows = null)
    {
        (_capture, _windows, _consent, _input, _find, _read) = (capture, windows, consent, input, find, read);
        _matchingControl = matchingControl ?? VisualSearchImageReader.HasMatchingControlRegion;
        _resultWindows = resultWindows;
    }

    public async Task<ApplicationSearchWorkflowResult> SearchAsync(ForegroundWindowSnapshot window, string query,
        CancellationToken cancellationToken = default, Action? validateTarget = null)
    {
        query = DesktopSearchQuery.NormalizeAndValidate(query);
        CheckIdentity();
        // The Host suspends this same execution until the exact window/query is visibly accepted.
        // No pixels, pointer movement, or input are allowed before it completes.
        await _consent.RequestAsync(window, query, cancellationToken).ConfigureAwait(false);
        CheckIdentity();
        _input.Activate(window.WindowHandle, cancellationToken);
        Check();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        var token = deadline.Token;
        var geometry = _input.GetBounds(window.WindowHandle);
        var identified = await IdentifyAsync().ConfigureAwait(false);
        using var before = identified.Frame;
        var field = identified.Field;
        ValidateField(field.Bounds, geometry);
        var resultsRegion = ResultsRegion(field.Bounds, geometry);
        var previousResults = await ReadResultsAsync(before.Bytes, resultsRegion).ConfigureAwait(false);
        using (var fresh = await ObserveAsync().ConfigureAwait(false))
        {
            if (!SameFieldPixels(before.Bytes, fresh.Bytes, field.Bounds)
                || !_matchingControl(fresh.Bytes, field.Bounds, token)) throw Changed();
        }
        Check();
        var click = _input.Click(window.WindowHandle, geometry, field.Bounds, Check, token);
        await Task.Delay(100, token).ConfigureAwait(false);
        Check();
        // Re-read the same visible, identified search field after focus. A disappeared or
        // relocated field is not permission to type into whatever now has keyboard focus.
        using (var focused = await ObserveAsync().ConfigureAwait(false))
        {
            // A caret can overlap the placeholder or reveal an existing query. The
            // native caret inside the just-identified field is independent focus proof;
            // do not require the placeholder to remain readable after the click.
            if (!_matchingControl(focused.Bytes, field.Bounds, token)) throw Changed();
            ValidateField(field.Bounds, geometry);
            resultsRegion = ResultsRegion(field.Bounds, geometry);
        }
        // A top-level HWND alone cannot distinguish a search edit from a chat composer.
        // Require a blinking insertion caret inside this field before the first key.
        var nativeCaretVerified = false;
        using (var initialFocus = await ObserveAsync().ConfigureAwait(false))
        {
            var focusTimer = System.Diagnostics.Stopwatch.StartNew();
            nativeCaretVerified = _input.HasSearchCaret(window.WindowHandle, geometry, field.Bounds);
            var caretVerified = nativeCaretVerified;
            while (!caretVerified && focusTimer.Elapsed < TimeSpan.FromSeconds(2))
            {
                await Task.Delay(160, token).ConfigureAwait(false);
                using var nextFocus = await ObserveAsync().ConfigureAwait(false);
                if (!_matchingControl(nextFocus.Bytes, field.Bounds, token)) throw Changed();
                nativeCaretVerified = _input.HasSearchCaret(window.WindowHandle, geometry, field.Bounds);
                if (nativeCaretVerified) { caretVerified = true; break; }
                if (HasCaretBlink(initialFocus.Bytes, nextFocus.Bytes, field.Bounds))
                {
                    caretVerified = true;
                    break;
                }
            }
            if (!caretVerified) throw new DesktopSearchException(DesktopSearchErrorCodes.FocusChanged,
                "未能确认输入光标位于搜索框内，因此没有输入任何内容。");
        }
        _input.ReplaceText(window.WindowHandle, query, click, CheckBound, token);
        var started = System.Diagnostics.Stopwatch.StartNew();
        var queryVerified = false;
        var resultWindowConsidered = false;
        while (started.Elapsed < TimeSpan.FromSeconds(8))
        {
            await Task.Delay(200, token).ConfigureAwait(false);
            using var after = await ObserveAsync().ConfigureAwait(false);
            var inputText = await _read(after.Bytes, field.Bounds, token).ConfigureAwait(false);
            queryVerified = inputText.Any(item => MatchesInput(item, query, field.Bounds));
            if (!queryVerified) continue;
            var resultText = await ReadResultsAsync(after.Bytes, resultsRegion).ConfigureAwait(false);
            var state = VerifyResults(query, previousResults, resultText);
            if (state is null && !resultWindowConsidered && _resultWindows is not null)
            {
                var popup = _resultWindows.Find(window, geometry, field.Bounds);
                if (popup is not null)
                {
                    resultWindowConsidered = true;
                    _resultWindows.Verify(window, popup);
                    // A different HWND receives a distinct visible permission. The
                    // original Turn stays alive without repeating clicks or text entry.
                    deadline.CancelAfter(Timeout.InfiniteTimeSpan);
                    await _consent.RequestResultReadAsync(window, popup, query, cancellationToken).ConfigureAwait(false);
                    deadline.CancelAfter(TimeSpan.FromSeconds(12));
                    started.Restart();
                    Check();
                    _resultWindows.Verify(window, popup);
                    var popupTimer = System.Diagnostics.Stopwatch.StartNew();
                    do
                    {
                        Check();
                        _resultWindows.Verify(window, popup);
                        var popupBounds = _input.GetBounds(popup.WindowHandle);
                        using var popupFrame = await _capture.CaptureAsync(
                            WindowsVisualSearchResultWindowVerifier.Target(popup), token).ConfigureAwait(false);
                        Check();
                        _resultWindows.Verify(window, popup);
                        if (_input.GetBounds(popup.WindowHandle) != popupBounds
                            || popupFrame.PixelWidth != popupBounds.Width || popupFrame.PixelHeight != popupBounds.Height
                            || popupFrame.CaptureTechnology != "Windows.GraphicsCapture.SingleHwnd") throw Changed();
                        var popupBytes = popupFrame.PngBytes.ToArray();
                        try
                        {
                            var items = await ReadResultsAsync(popupBytes, new(0, 0, popupFrame.PixelWidth, popupFrame.PixelHeight))
                                .ConfigureAwait(false);
                            state = VerifyResults(query, [], items);
                        }
                        finally { CryptographicOperations.ZeroMemory(popupBytes); }
                        if (state is null) await Task.Delay(200, token).ConfigureAwait(false);
                    } while (state is null && popupTimer.Elapsed < TimeSpan.FromSeconds(3));
                    var stillSameQuery = false;
                    for (var attempt = 0; attempt < 4 && !stillSameQuery; attempt++)
                    {
                        using var recheck = await ObserveAsync().ConfigureAwait(false);
                        var currentQuery = await _read(recheck.Bytes, field.Bounds, token).ConfigureAwait(false);
                        stillSameQuery = currentQuery.Any(item => MatchesInput(item, query, field.Bounds));
                        if (!stillSameQuery) await Task.Delay(350, token).ConfigureAwait(false);
                    }
                    if (!stillSameQuery) throw Changed();
                    _resultWindows.Verify(window, popup);
                }
            }
            Check();
            if (state is not null)
                return new(true, true, true, state.Value, false, false,
                    state.Value
                        ? "已在搜索框输入关键词，并核对到没有匹配结果；没有打开结果。"
                        : "已在搜索框输入关键词，并核对到对应搜索结果；没有打开结果。")
                { SubmissionMode = "LiveFilter" };
        }
        throw new DesktopSearchException(queryVerified
                ? "visual_search_results_unverified" : DesktopSearchErrorCodes.WriteVerificationFailed,
            queryVerified ? "已核对搜索框内的关键词，但还无法核对结果；没有按回车或打开结果。"
                : "无法回读确认搜索框内的关键词，已停止；没有按回车或发送消息。");

        void CheckIdentity()
        {
            cancellationToken.ThrowIfCancellationRequested();
            WindowIdentityContract.RequireMatch(window, _windows.ResolveWindow(window.WindowHandle));
        }
        async Task<IReadOnlyList<VisualSearchText>> ReadResultsAsync(byte[] png, VisualSearchRect region)
        {
            var rows = await _read(png, region, token).ConfigureAwait(false);
            if (rows.Any(row => MatchesQuery(row.Text, query))) return rows;
            var headings = rows.Where(row => IsResultHeading(row.Text)).ToArray();
            if (headings.Length == 0) return rows;
            var labels = rows.Where(row => row.Bounds.Height is >= 8 and <= 60 && row.Bounds.Width >= 16
                && headings.Any(head => row.Bounds.Y > head.Bounds.Y + head.Bounds.Height
                    && row.Bounds.Y < head.Bounds.Y + 160))
                .Select(row => row.Bounds).Distinct().Take(6).ToArray();
            var refined = rows.ToList();
            foreach (var label in labels)
            {
                var left = Math.Max(region.X, label.X - 5);
                var top = Math.Max(region.Y, label.Y - 5);
                var right = Math.Min(region.X + region.Width, label.X + label.Width + 5);
                var bottom = Math.Min(region.Y + region.Height, label.Y + label.Height + 5);
                if (right <= left || bottom <= top) continue;
                // Re-read an observed result label at its own scale and background.
                // This does not substitute expected text or accept fuzzy matches.
                refined.AddRange(await _read(png, new(left, top, right - left, bottom - top), token).ConfigureAwait(false));
            }
            return refined;
        }
        void Check()
        {
            CheckIdentity();
            _input.RequireForeground(window.WindowHandle);
            validateTarget?.Invoke();
        }
        void CheckBound()
        {
            Check();
            if (_input.GetBounds(window.WindowHandle) != geometry) throw Changed();
            if (nativeCaretVerified && !_input.HasSearchCaret(window.WindowHandle, geometry, field.Bounds))
                throw new DesktopSearchException(DesktopSearchErrorCodes.FocusChanged,
                    "输入光标已经离开搜索框，已停止输入。");
        }
        async Task<(VisualFrame Frame, VisualSearchField Field)> IdentifyAsync()
        {
            for (var attempt = 0; ; attempt++)
            {
                var frame = await ObserveAsync().ConfigureAwait(false);
                try { return (frame, await _find(frame.Bytes, token).ConfigureAwait(false)); }
                catch (DesktopSearchException error) when (error.Code == DesktopSearchErrorCodes.CandidateNotFound && attempt < 2)
                {
                    frame.Dispose();
                    // A blinking caret can cross a placeholder glyph. Reobserve without
                    // any input; ambiguity and all other failures are never retried.
                    await Task.Delay(350, token).ConfigureAwait(false);
                }
                catch { frame.Dispose(); throw; }
            }
        }
        async Task<VisualFrame> ObserveAsync()
        {
            token.ThrowIfCancellationRequested();
            Check();
            if (_input.GetBounds(window.WindowHandle) != geometry) throw Changed();
            using var frame = await _capture.CaptureAsync(new(window.WindowHandle, window.WindowTitle,
                window.ProcessName, window.ProcessId, window.ProcessStartTimeUtc, DateTimeOffset.UtcNow), token)
                .ConfigureAwait(false);
            Check();
            if (_input.GetBounds(window.WindowHandle) != geometry
                || frame.PixelWidth != geometry.Width || frame.PixelHeight != geometry.Height
                || frame.CaptureTechnology != "Windows.GraphicsCapture.SingleHwnd") throw Changed();
            return new(frame.PngBytes.ToArray());
        }
    }

    internal static bool MatchesQuery(string observed, string query, bool allowSearchIcon = false)
    {
        var normalized = observed.Normalize(NormalizationForm.FormKC).Trim();
        if (!query.Any(char.IsWhiteSpace)) normalized = string.Concat(normalized.Where(c => !char.IsWhiteSpace(c)));
        // A magnifying-glass icon may be recognized as Q. Only discard it when the
        // remaining text exactly equals the requested query, never by fuzzy matching.
        return normalized == query || allowSearchIcon && (normalized == "Q" + query || normalized == "〇" + query);
    }

    internal static bool MatchesInput(VisualSearchText observed, string query, VisualSearchRect field)
    {
        if (MatchesQuery(observed.Text, query)) return true;
        if (observed.Words.Count == 0) return false;
        var words = observed.Words.ToList();
        if (words.Any(word => word.Bounds.Width <= 0 || word.Bounds.Height <= 0
            || word.Bounds.X < field.X || word.Bounds.Y < field.Y
            || word.Bounds.X + (long)word.Bounds.Width > field.X + (long)field.Width
            || word.Bounds.Y + (long)word.Bounds.Height > field.Y + (long)field.Height)) return false;
        bool Separated(VisualSearchText left, VisualSearchText right) =>
            right.Bounds.X - (left.Bounds.X + (long)left.Bounds.Width) >= Math.Max(3, field.Height / 8)
            && Math.Abs(left.Bounds.Y + left.Bounds.Height / 2d - right.Bounds.Y - right.Bounds.Height / 2d)
                <= field.Height / 3d;
        bool AtLeft(VisualSearchText word) => word.Bounds.X >= field.X
            && word.Bounds.X + word.Bounds.Width <= field.X + field.Height
            && Compact(word.Text) is "Q" or "0" or "〇";
        bool AtRight(VisualSearchText word) => word.Bounds.X >= field.X + field.Width - field.Height
            && word.Bounds.X + word.Bounds.Width <= field.X + field.Width
            && Compact(word.Text) is "0" or "〇" or "×" or "x" or "X";
        if (words.Count > 1 && AtLeft(words[0]) && Separated(words[0], words[1])) words.RemoveAt(0);
        if (words.Count > 1 && AtRight(words[^1]) && Separated(words[^2], words[^1])) words.RemoveAt(words.Count - 1);
        if (words.Count == 0 || words.Count == observed.Words.Count) return false;
        return MatchesQuery(string.Join(query.Any(char.IsWhiteSpace) ? " " : "", words.Select(word => word.Text)), query);
    }

    internal static bool? VerifyResults(string query, IReadOnlyList<VisualSearchText> before,
        IReadOnlyList<VisualSearchText> after)
    {
        var emptyLabels = new[] { "没有搜索结果", "无搜索结果", "没有找到相关结果", "未找到结果", "No results", "No results found" };
        bool IsNew(VisualSearchText item) => !before.Any(old => Compact(old.Text) == Compact(item.Text)
            && Math.Abs(old.Bounds.X - item.Bounds.X) < 10 && Math.Abs(old.Bounds.Y - item.Bounds.Y) < 10);
        var empty = after.FirstOrDefault(item => emptyLabels.Any(label =>
            string.Equals(Compact(label), Compact(item.Text), StringComparison.OrdinalIgnoreCase)));
        if (empty is not null && IsNew(empty)) return true;
        var heading = after.FirstOrDefault(item => IsResultHeading(item.Text));
        if (heading is null) return null;
        var result = after.FirstOrDefault(item => MatchesQuery(item.Text, query)
            && item.Bounds.Y > heading.Bounds.Y + heading.Bounds.Height);
        return result is not null && (IsNew(heading) || IsNew(result)) ? false : null;
    }

    private static bool IsResultHeading(string text) =>
        new[] { "搜索结果", "联系人", "功能", "群聊", "Contacts", "Results", "Search results" }
            .Any(label => string.Equals(Compact(label), Compact(text), StringComparison.OrdinalIgnoreCase));

    private static string Compact(string text) => string.Concat(text.Normalize(NormalizationForm.FormKC)
        .Where(c => !char.IsWhiteSpace(c)));

    private static VisualSearchRect ResultsRegion(VisualSearchRect field, VisualSearchWindowBounds window)
    {
        var left = Math.Max(0, field.X - field.Height * 2);
        var right = Math.Min(window.Width, field.X + field.Width + field.Height * 2);
        var top = field.Y + field.Height + 2;
        return new(left, top, right - left, Math.Min(600, window.Height - top));
    }

    private static void ValidateField(VisualSearchRect field, VisualSearchWindowBounds window)
    {
        if (field.X < 0 || field.Y < 0 || field.Width < 64 || field.Height < 16
            || field.Width > 700 || field.Height > 100 || field.Y + field.Height >= window.Height
            || field.X + field.Width > window.Width) throw Changed();
    }

    internal static bool SameFieldPixels(byte[] before, byte[] after, VisualSearchRect region)
    {
        using var firstStream = new MemoryStream(before);
        using var secondStream = new MemoryStream(after);
        using var first = new Bitmap(firstStream);
        using var second = new Bitmap(secondStream);
        if (first.Size != second.Size) return false;
        var changed = 0;
        int left = int.MaxValue, top = int.MaxValue, right = -1, bottom = -1;
        for (var y = region.Y; y < region.Y + region.Height; y++)
        for (var x = region.X; x < region.X + region.Width; x++)
            if (first.GetPixel(x, y).ToArgb() != second.GetPixel(x, y).ToArgb())
            {
                changed++;
                left = Math.Min(left, x); right = Math.Max(right, x);
                top = Math.Min(top, y); bottom = Math.Max(bottom, y);
            }
        // An already focused field may blink while OCR runs. Permit only a complete
        // thin insertion-caret difference; include even weak pixel changes in its box.
        var caretHeight = bottom - top + 1;
        var onlyCaret = changed > 0 && right - left + 1 is >= 1 and <= 4
            && caretHeight >= Math.Max(8, region.Height / 3) && caretHeight <= region.Height - 4
            && changed >= caretHeight * .8 && left >= region.X + 2 && right < region.X + region.Width - 2
            && top >= region.Y + 2 && bottom < region.Y + region.Height - 2;
        return changed <= region.Width * region.Height / 100 || onlyCaret;
    }

    internal static bool HasCaretBlink(byte[] before, byte[] after, VisualSearchRect region)
    {
        using var a = new MemoryStream(before);
        using var b = new MemoryStream(after);
        using var first = new Bitmap(a);
        using var second = new Bitmap(b);
        if (first.Size != second.Size) return false;
        int left = int.MaxValue, top = int.MaxValue, right = -1, bottom = -1, count = 0;
        for (var y = region.Y; y < region.Y + region.Height; y++)
        for (var x = region.X; x < region.X + region.Width; x++)
        {
            var one = first.GetPixel(x, y); var two = second.GetPixel(x, y);
            if (Math.Max(Math.Abs(one.R - two.R), Math.Max(Math.Abs(one.G - two.G), Math.Abs(one.B - two.B))) < 60) continue;
            left = Math.Min(left, x); right = Math.Max(right, x);
            top = Math.Min(top, y); bottom = Math.Max(bottom, y); count++;
        }
        var width = right - left + 1; var height = bottom - top + 1;
        return count > 0 && width is >= 1 and <= 4 && height >= Math.Max(8, region.Height / 3)
            && height <= region.Height - 4 && count >= height * .8
            && left >= region.X + 2 && right < region.X + region.Width - 2
            && top >= region.Y + 2 && bottom < region.Y + region.Height - 2;
    }

    private static DesktopSearchException Changed() => new(DesktopSearchErrorCodes.TargetChanged,
        "窗口、搜索框或画面位置已经变化，已停止本次视觉搜索。");

    private sealed class VisualFrame(byte[] bytes) : IDisposable
    {
        public byte[] Bytes { get; } = bytes;
        public void Dispose() => CryptographicOperations.ZeroMemory(Bytes);
    }
}
