using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Automation;
using ScreenGuide.Skills.Abstractions;

namespace ScreenGuide.Skills.Windows;

// A narrow native-object-model adapter, not a general Office automation surface.
// Only this already-bound Word window's short, plain main story is exposed.
internal static class WordDesktopTaskBody
{
    internal const string IdentityStateKey = "officeDocument=";

    internal static bool IsCandidate(DesktopTaskWindow window, DesktopTaskElementMetadata metadata) =>
        IsTrustedWindow(window)
        && metadata.ControlType == "Edit" && metadata.AutomationId == "Body"
        && metadata.ProcessId == window.ProcessId && DesktopTaskSafety.Usable(metadata);

    private static bool IsTrustedWindow(DesktopTaskWindow window) =>
        window.FrameIdentity is null && window.ClassName == "OpusApp"
        && window.ProcessName.Equals("WINWORD", StringComparison.OrdinalIgnoreCase)
        && IsTrustedExecutable(window.ExecutablePath);

    internal static bool IsPageContainer(DesktopTaskWindow window, DesktopTaskElementMetadata metadata)
    {
        const string prefix = "UIA_AutomationId_Word_Page_";
        return IsTrustedWindow(window) && metadata.ProcessId == window.ProcessId
            && metadata.ControlType == "Custom" && DesktopTaskSafety.Usable(metadata)
            && metadata.AutomationId.StartsWith(prefix, StringComparison.Ordinal)
            && metadata.AutomationId.Length > prefix.Length
            && metadata.AutomationId[prefix.Length] is >= '1' and <= '9'
            && metadata.AutomationId.AsSpan(prefix.Length).IndexOfAnyExceptInRange('0', '9') < 0;
    }

    internal static bool IsPageTextMirror(DesktopTaskWindow window, DesktopTaskElementMetadata page,
        IReadOnlyList<DesktopTaskElementMetadata> directChildren)
    {
        if (!IsPageContainer(window, page)) return false;
        var bodies = directChildren.Where(child => child.AutomationId == "Body").ToArray();
        return bodies.Length == 1 && IsCandidate(window, bodies[0])
            && !bodies[0].RuntimeId.SequenceEqual(page.RuntimeId);
    }

    internal static bool IsTrustedExecutable(string path)
    {
        if (!Path.IsPathFullyQualified(path)) return false;
        var full = Path.GetFullPath(path);
        foreach (var folder in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
        {
            var root = Environment.GetFolderPath(folder);
            if (string.IsNullOrEmpty(root)) continue;
            foreach (var relative in new[] { @"Microsoft Office\Root\Office16\WINWORD.EXE", @"Microsoft Office\Office16\WINWORD.EXE" })
                if (string.Equals(full, Path.Combine(root, relative), StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    internal static DesktopTaskElementDetails Read(AutomationElement body, AutomationElement root,
        DesktopTaskWindow window, int maximumTextLength)
    {
        try
        {
            using var session = NativeSession.Open(body, root, window);
            return ToDetails(session.Read(maximumTextLength));
        }
        catch (Exception exception) when (NativeFailure(exception)) { throw Unavailable(); }
    }

    internal static void SetText(AutomationElement body, AutomationElement root, DesktopTaskWindow window,
        DesktopTaskControl expected, string text, Action verifyContext, Action requireActionBoundary)
    {
        try
        {
            using var session = NativeSession.Open(body, root, window);
            SetText(session, expected, text, verifyContext, requireActionBoundary);
        }
        catch (Exception exception) when (NativeFailure(exception)) { throw Unavailable(); }
    }

    internal static DesktopTaskElementDetails ToDetails(WordDesktopTaskBodySnapshot snapshot)
    {
        if (!IsHash(snapshot.Identity) || snapshot.Text.Length > DesktopTaskSafety.MaximumValue)
            throw Unavailable();
        return new(snapshot.Text, !snapshot.Writable, IdentityStateKey + snapshot.Identity,
            snapshot.Writable && !snapshot.Truncated ? [DesktopTaskActionKind.SetText] : [], snapshot.Truncated);
    }

    internal static void SetText(IWordDesktopTaskBodySession session, DesktopTaskControl expected,
        string text, Action verifyContext, Action requireActionBoundary)
    {
        DesktopTaskSafety.RequireText(text);
        // Word normalizes paragraph separators. The public value uses LF; reject
        // other encodings before writing so the shared worker can compare exactly.
        if (text.Contains('\r') || text.Contains('\uFFFC'))
            throw DesktopTaskSafety.Error("word_body_text_unsupported", "这段文字含有当前正文接口不支持的段落或对象字符，未写入。");
        var identities = expected.State.Split(';').Where(value => value.StartsWith(IdentityStateKey, StringComparison.Ordinal)).ToArray();
        if (expected.ControlType != "Edit" || expected.AutomationId != "Body" || expected.IsReadOnly
            || !expected.Actions.Contains(DesktopTaskActionKind.SetText) || identities.Length != 1
            || !IsHash(identities[0][IdentityStateKey.Length..])) throw Changed();
        var identity = identities[0][IdentityStateKey.Length..];
        RequireExpected(session.Read(DesktopTaskSafety.MaximumValue), identity, expected.Value);
        verifyContext();
        // Context observation can take time and perform other COM reads. Rebind
        // the same document and range, including protection and complete old text.
        RequireExpected(session.Read(DesktopTaskSafety.MaximumValue), identity, expected.Value);
        session.Write(text, requireActionBoundary);
        var after = session.Read(DesktopTaskSafety.MaximumValue);
        if (after.Identity != identity || after.Truncated || after.Text != text)
            throw DesktopTaskSafety.Error("word_body_not_verified", "正文已提交，但未准确读回同一文档中的文字，已停止后续操作。");
    }

    private static void RequireExpected(WordDesktopTaskBodySnapshot snapshot, string identity, string oldText)
    {
        if (!snapshot.Writable || snapshot.Truncated || snapshot.Identity != identity || snapshot.Text != oldText)
            throw Changed();
    }

    internal static bool CanWrite(WordDesktopTaskBodyProtection protection, string rawText, bool truncated) =>
        !truncated && !protection.ReadOnly && protection.ProtectionType == -1
        && !protection.TrackRevisions && !protection.HasVbaProject && protection.DocumentType == 0
        && protection.Revisions == 0 && protection.Tables == 0 && protection.InlineShapes == 0
        && protection.Shapes == 0 && protection.Fields == 0 && protection.FormFields == 0
        && protection.ContentControls == 0 && protection.Hyperlinks == 0 && protection.Subdocuments == 0
        && protection.Footnotes == 0 && protection.Endnotes == 0 && protection.Sections == 1
        && !rawText.Contains('\uFFFC') && !rawText.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t'));

    internal static (int Start, int End, bool Truncated) ReadRange(int start, int end, int maximumTextLength)
    {
        if (start != 0 || end < 1 || maximumTextLength is < 1 or > DesktopTaskSafety.MaximumValue) throw Unavailable();
        // The final paragraph marker belongs to Word, not to the editable value.
        var length = end - 1;
        return (0, Math.Min(length, maximumTextLength), length > maximumTextLength);
    }

    internal static string ReadTextRange(int start, int end, Func<string?> readNativeText)
    {
        if (start != 0 || end < start || end > DesktopTaskSafety.MaximumValue) throw Unavailable();
        // Word returns VT_EMPTY/null for a collapsed, empty body Range. Its
        // validated zero length is sufficient; other null properties still fail.
        return start == end ? string.Empty : readNativeText() ?? throw Unavailable();
    }

    internal static string Identity(DesktopTaskWindow window, long documentHandle, long wordWindowHandle,
        string fullName, string name, DateTime created)
    {
        if (documentHandle == 0 || wordWindowHandle != window.Handle || string.IsNullOrEmpty(fullName)
            || fullName.Length > 4096 || string.IsNullOrEmpty(name) || name.Length > 255 || created.Year < 1900)
            throw Unavailable();
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            window.Handle, window.ProcessId, window.ProcessStartedAtUtc, window.ExecutablePath,
            documentHandle, wordWindowHandle, fullName, name, CreatedTicks = created.Ticks
        })));
    }

    private static bool IsHash(string value) => value.Length == 64 && value.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F');
    private static bool NativeFailure(Exception exception) => exception is COMException or TargetInvocationException
        or InvalidComObjectException or InvalidCastException or MissingMethodException or ArgumentException;
    private static DesktopTaskException Unavailable() => DesktopTaskSafety.Error("word_body_unavailable", "无法可靠读取当前 Word 正文接口，未使用键盘或其他文档作为替代。");
    private static DesktopTaskException Changed() => DesktopTaskSafety.Error("word_body_changed", "Word 文档身份、正文或保护状态已变化，未覆盖内容。");

    private sealed class NativeSession : IWordDesktopTaskBodySession
    {
        private readonly AutomationElement body;
        private readonly AutomationElement root;
        private readonly DesktopTaskWindow window;
        private readonly long documentHandle;
        private readonly object wordWindow;
        private object? document;
        private object? range;
        private bool readyToWrite;

        private NativeSession(AutomationElement body, AutomationElement root, DesktopTaskWindow window,
            long documentHandle, object wordWindow)
        {
            this.body = body;
            this.root = root;
            this.window = window;
            this.documentHandle = documentHandle;
            this.wordWindow = wordWindow;
        }

        internal static NativeSession Open(AutomationElement body, AutomationElement root, DesktopTaskWindow window)
        {
            if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA) throw Unavailable();
            RequireBodyBinding(body, root, window);
            long documentHandle = 0;
            var cursor = body;
            for (var depth = 0; cursor is not null && depth < 64; depth++)
            {
                var nativeHandle = cursor.Current.NativeWindowHandle;
                if (nativeHandle != 0 && ClassName(new IntPtr(nativeHandle)) == "_WwG")
                {
                    documentHandle = nativeHandle;
                    break;
                }
                if (cursor.GetRuntimeId().SequenceEqual(root.GetRuntimeId())) break;
                cursor = TreeWalker.RawViewWalker.GetParent(cursor);
            }
            RequireDocumentWindow(window, documentHandle);
            var iid = new Guid("00020400-0000-0000-C000-000000000046"); // IID_IDispatch, never Application/ROT lookup.
            object? wordWindow = null;
            try
            {
                var result = AccessibleObjectFromWindow(new IntPtr(documentHandle), 0xFFFFFFF0, ref iid, out wordWindow);
                if (result != 0 || wordWindow is null) throw Unavailable();
                var session = new NativeSession(body, root, window, documentHandle, wordWindow);
                session.RequireBinding();
                return session;
            }
            catch { Release(wordWindow); throw; }
        }

        public WordDesktopTaskBodySnapshot Read(int maximumTextLength)
        {
            readyToWrite = false;
            Release(range); range = null;
            Release(document); document = null;
            RequireBinding();
            document = Get(wordWindow, "Document");
            var fullName = Convert.ToString(Get(document, "FullName"), CultureInfo.InvariantCulture) ?? string.Empty;
            var name = Convert.ToString(Get(document, "Name"), CultureInfo.InvariantCulture) ?? string.Empty;
            var created = CreationTime(document);
            var identity = Identity(window, documentHandle, WindowHandle(wordWindow), fullName, name, created);
            var protection = ReadProtection(document);
            range = Get(document, "Content");
            if (Convert.ToInt32(Get(range, "StoryType"), CultureInfo.InvariantCulture) != 1) throw Unavailable();
            var span = ReadRange(Convert.ToInt32(Get(range, "Start"), CultureInfo.InvariantCulture),
                Convert.ToInt32(Get(range, "End"), CultureInfo.InvariantCulture), maximumTextLength);
            // Shrink the native range before requesting any text, even for large documents.
            Call(range, "SetRange", span.Start, span.End);
            var rawText = ReadTextRange(span.Start, span.End,
                () => Convert.ToString(Get(range, "Text"), CultureInfo.InvariantCulture));
            var truncated = span.Truncated || rawText.Length > maximumTextLength;
            if (rawText.Length > maximumTextLength) rawText = rawText[..maximumTextLength];
            var text = rawText.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
            readyToWrite = CanWrite(protection, rawText, truncated);
            RequireSameDocument();
            RequireBinding();
            return new(identity, text, readyToWrite, truncated);
        }

        public void Write(string text, Action requireActionBoundary)
        {
            if (!readyToWrite || document is null || range is null) throw Changed();
            readyToWrite = false;
            // The held Range is the complete main story minus its mandatory final
            // marker, acquired by the read immediately after verifyContext.
            RequireSameDocument();
            RequireBinding();
            requireActionBoundary();
            Set(range, "Text", text.Replace('\n', '\r'));
        }

        private void RequireBinding()
        {
            RequireBodyBinding(body, root, window);
            RequireDocumentWindow(window, documentHandle);
            if (WindowHandle(wordWindow) != window.Handle) throw Changed();
        }

        private void RequireSameDocument()
        {
            object? current = null;
            IntPtr expectedIdentity = IntPtr.Zero, actualIdentity = IntPtr.Zero;
            try
            {
                current = Get(wordWindow, "Document");
                expectedIdentity = Marshal.GetIUnknownForObject(document ?? throw Changed());
                actualIdentity = Marshal.GetIUnknownForObject(current);
                if (expectedIdentity != actualIdentity) throw Changed();
            }
            finally
            {
                if (expectedIdentity != IntPtr.Zero) Marshal.Release(expectedIdentity);
                if (actualIdentity != IntPtr.Zero) Marshal.Release(actualIdentity);
                Release(current);
            }
        }

        public void Dispose()
        {
            readyToWrite = false;
            Release(range); range = null;
            Release(document); document = null;
            Release(wordWindow);
        }
    }

    private static void RequireBodyBinding(AutomationElement body, AutomationElement root, DesktopTaskWindow expected)
    {
        var current = new WindowsDesktopTaskNativeRuntime().ReadWindow(expected.Handle);
        if (!DesktopTaskSafety.SameWindow(expected, current) || !IsTrustedExecutable(current.ExecutablePath)
            || current.ClassName != "OpusApp" || current.FrameIdentity is not null
            || !current.ProcessName.Equals("WINWORD", StringComparison.OrdinalIgnoreCase)
            || !WindowsDesktopTaskNativeRuntime.Native.Belongs(body, root, current)) throw Changed();
        var information = body.Current;
        if (information.ProcessId != current.ProcessId || information.AutomationId != "Body"
            || information.ControlType != ControlType.Edit || information.IsPassword) throw Changed();
    }

    private static void RequireDocumentWindow(DesktopTaskWindow window, long handle)
    {
        var hwnd = new IntPtr(handle);
        _ = WindowsDesktopTaskNativeRuntime.Native.GetWindowThreadProcessId(hwnd, out var processId);
        if (handle == 0 || !WindowsDesktopTaskNativeRuntime.Native.IsWindow(hwnd) || processId != window.ProcessId
            || ClassName(hwnd) != "_WwG" || !WindowsDesktopTaskNativeRuntime.Native.IsChild(new IntPtr(window.Handle), hwnd)
            || WindowsDesktopTaskNativeRuntime.Native.GetAncestor(hwnd, 2).ToInt64() != window.Handle) throw Changed();
    }

    private static string ClassName(IntPtr handle)
    {
        var value = new StringBuilder(256);
        _ = WindowsDesktopTaskNativeRuntime.Native.GetClassName(handle, value, value.Capacity);
        return value.ToString();
    }

    private static long WindowHandle(object wordWindow)
    {
        var value = Convert.ToInt64(Get(wordWindow, "Hwnd"), CultureInfo.InvariantCulture);
        return value < 0 ? unchecked((uint)value) : value;
    }

    private static DateTime CreationTime(object document)
    {
        object? properties = null, property = null;
        try
        {
            properties = Get(document, "BuiltInDocumentProperties");
            property = Get(properties, "Item", 11); // wdPropertyTimeCreated, not a user-defined property.
            return Convert.ToDateTime(Get(property, "Value"), CultureInfo.InvariantCulture);
        }
        finally { Release(property); Release(properties); }
    }

    private static WordDesktopTaskBodyProtection ReadProtection(object document) => new(
        Convert.ToBoolean(Get(document, "ReadOnly"), CultureInfo.InvariantCulture),
        Convert.ToInt32(Get(document, "ProtectionType"), CultureInfo.InvariantCulture),
        Convert.ToBoolean(Get(document, "TrackRevisions"), CultureInfo.InvariantCulture),
        Convert.ToBoolean(Get(document, "HasVBProject"), CultureInfo.InvariantCulture),
        Convert.ToInt32(Get(document, "Type"), CultureInfo.InvariantCulture),
        Count(document, "Revisions"), Count(document, "Tables"), Count(document, "InlineShapes"),
        Count(document, "Shapes"), Count(document, "Fields"), Count(document, "FormFields"),
        Count(document, "ContentControls"), Count(document, "Hyperlinks"), Count(document, "Subdocuments"),
        Count(document, "Footnotes"), Count(document, "Endnotes"), Count(document, "Sections"));

    private static int Count(object document, string name)
    {
        object? collection = null;
        try
        {
            collection = Get(document, name);
            return Convert.ToInt32(Get(collection, "Count"), CultureInfo.InvariantCulture);
        }
        finally { Release(collection); }
    }

    private static object Get(object target, string name, params object[] arguments) =>
        target.GetType().InvokeMember(name, BindingFlags.GetProperty, null, target, arguments, CultureInfo.InvariantCulture)
            ?? throw Unavailable();
    private static void Call(object target, string name, params object[] arguments) =>
        _ = target.GetType().InvokeMember(name, BindingFlags.InvokeMethod, null, target, arguments, CultureInfo.InvariantCulture);
    private static void Set(object target, string name, object value) =>
        _ = target.GetType().InvokeMember(name, BindingFlags.SetProperty, null, target, [value], CultureInfo.InvariantCulture);
    private static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value)) _ = Marshal.ReleaseComObject(value);
    }

    [DllImport("oleacc.dll")]
    private static extern int AccessibleObjectFromWindow(IntPtr hwnd, uint objectId, ref Guid iid,
        [MarshalAs(UnmanagedType.Interface)] out object? value);
}

internal sealed record WordDesktopTaskBodySnapshot(string Identity, string Text, bool Writable, bool Truncated = false);

internal sealed record WordDesktopTaskBodyProtection(bool ReadOnly = false, int ProtectionType = -1,
    bool TrackRevisions = false, bool HasVbaProject = false, int DocumentType = 0, int Revisions = 0,
    int Tables = 0, int InlineShapes = 0, int Shapes = 0, int Fields = 0, int FormFields = 0,
    int ContentControls = 0, int Hyperlinks = 0, int Subdocuments = 0, int Footnotes = 0, int Endnotes = 0, int Sections = 1);

internal interface IWordDesktopTaskBodySession : IDisposable
{
    WordDesktopTaskBodySnapshot Read(int maximumTextLength);
    void Write(string text, Action requireActionBoundary);
}
