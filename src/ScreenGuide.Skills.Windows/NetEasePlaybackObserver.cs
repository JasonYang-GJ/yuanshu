using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using Accessibility;

namespace ScreenGuide.Skills.Windows;

internal interface INetEasePlaybackObserver
{
    NetEasePlaybackObservation Read(long hwnd, CancellationToken cancellationToken);
}

internal sealed record NetEasePlaybackObservation(
    bool Readable, string Title, IReadOnlyList<string> Artists, bool Playing, bool IsPreview)
{
    public long WindowHandle { get; init; }
    public string FooterIdentity { get; init; } = string.Empty;
}

internal sealed record NetEasePlaybackNode(
    string Identity, long WindowHandle, int Role, int State, IReadOnlyDictionary<string, string> Attributes);

/// <summary>
/// Read-only adapter for the observed NetEase CEF footer. Footer identity is not a song ID.
/// No names outside a uniquely identified footer, input, capture, network or media commands.
/// </summary>
internal sealed class NetEasePlaybackObserver : INetEasePlaybackObserver
{
    private static readonly string[] AttributeKeys = ["id", "tag", "class", "display", "xml-roles", "explicit-name", "busy"];

    public NetEasePlaybackObservation Read(long hwnd, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (hwnd == 0 || !IsWindow(new IntPtr(hwnd))) return Unreadable(hwnd);
        try
        {
            GetWindowThreadProcessId(new IntPtr(hwnd), out var processId);
            using var process = Process.GetProcessById(checked((int)processId));
            if (!process.ProcessName.Equals("cloudmusic", StringComparison.OrdinalIgnoreCase)) return Unreadable(hwnd);
            var startedAt = process.StartTime.ToUniversalTime();
            // Request only control type to initialize the window's CEF accessibility bridge.
            // This does not focus a control or read account/recommendation text.
            var root = AutomationElement.FromHandle(new IntPtr(hwnd));
            _ = root.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));
            cancellationToken.ThrowIfCancellationRequested();
            var result = ObserveTree(hwnd,
                () => LegacyBrowserSearchSemantics.Open(hwnd).Select(item => item.Accessible).ToArray(),
                accessible => ReadMetadata(accessible, hwnd),
                accessible => LegacyBrowserSearchSemantics.TryReadCompleteSearchChildren(accessible, out var children) ? children : null,
                accessible => accessible.get_accName(0), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            GetWindowThreadProcessId(new IntPtr(hwnd), out var currentId);
            process.Refresh();
            return currentId == processId && !process.HasExited && process.StartTime.ToUniversalTime() == startedAt
                ? result : Unreadable(hwnd);
        }
        catch (Exception exception) when (exception is COMException or InvalidOperationException or ArgumentException
            or InvalidCastException or OverflowException or System.ComponentModel.Win32Exception or ElementNotAvailableException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Unreadable(hwnd);
        }
    }

    private static NetEasePlaybackNode? ReadMetadata(IAccessible accessible, long hwnd)
    {
        var native = LegacyBrowserSearchSemantics.ReadNode(accessible, hwnd);
        if (native is null) return null;
        var attributes = AttributeKeys.Where(native.Attributes.ContainsKey)
            .ToDictionary(key => key, key => native.Attributes[key], StringComparer.Ordinal);
        return new(native.Identity, hwnd, native.Role, native.State, attributes);
    }

    // The same algorithm is exercised with fake metadata and lazy name readers in tests.
    internal static NetEasePlaybackObservation ObserveTree<T>(long hwnd,
        Func<IReadOnlyList<T>> readRoots, Func<T, NetEasePlaybackNode?> readMetadata,
        Func<T, IReadOnlyList<T>?> readChildren, Func<T, string?> readName, CancellationToken cancellationToken = default)
    {
        try
        {
            if (hwnd == 0) return Unreadable(hwnd);
            var first = ReadSnapshot(FindFooter());
            var observed = Parse(first);
            var second = ReadSnapshot(FindFooter());
            var repeated = Parse(second);
            cancellationToken.ThrowIfCancellationRequested();
            return SameTree(first, second) && observed.Title == repeated.Title
                && observed.Artists.SequenceEqual(repeated.Artists, StringComparer.Ordinal)
                && observed.Playing == repeated.Playing && observed.IsPreview == repeated.IsPreview
                ? observed : Unreadable(hwnd);
        }
        catch (UnreadableFooterException) { return Unreadable(hwnd); }

        NetEasePlaybackNode Metadata(T native)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var node = readMetadata(native);
            Require(node is not null && node.WindowHandle == hwnd && !string.IsNullOrWhiteSpace(node.Identity)
                && node.Attributes.All(pair => pair.Key.Length <= 128 && pair.Value.Length <= 2048));
            return node!;
        }
        IReadOnlyList<T> Children(T native)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var children = readChildren(native);
            Require(children is not null && children.Count <= 128);
            return children!;
        }
        T FindFooter()
        {
            var roots = readRoots();
            Require(roots.Count is > 0 and < 16);
            var queue = new Queue<(T Native, int Depth)>();
            foreach (var native in roots) queue.Enqueue((native, 0));
            var visited = new HashSet<string>(StringComparer.Ordinal);
            var found = new List<T>();
            while (queue.Count > 0)
            {
                Require(visited.Count < 2048);
                var (native, depth) = queue.Dequeue();
                var metadata = Metadata(native);
                Require(visited.Add(metadata.Identity));
                if (Attribute(metadata, "tag") == "footer") { found.Add(native); continue; }
                // No descent into main pages, recommendations, account content or navigation.
                if (Attribute(metadata, "id").StartsWith("page_pc_", StringComparison.Ordinal)) continue;
                var children = Children(native);
                Require(depth < 16 || children.Count == 0);
                foreach (var child in children) queue.Enqueue((child, depth + 1));
            }
            Require(found.Count == 1);
            return found[0];
        }
        Snapshot<T> ReadSnapshot(T footer)
        {
            var visited = new HashSet<string>(StringComparer.Ordinal);
            return Build(footer, 0);
            Snapshot<T> Build(T native, int depth)
            {
                var metadata = Metadata(native);
                Require(visited.Count < 256 && visited.Add(metadata.Identity));
                var children = Children(native);
                Require(depth < 12 || children.Count == 0);
                return new(native, metadata, children.Select(child => Build(child, depth + 1)).ToArray());
            }
        }
        NetEasePlaybackObservation Parse(Snapshot<T> footer)
        {
            Live(footer);
            Require(footer.Node.Role == 20 && Attribute(footer.Node, "tag") == "footer"
                && Attribute(footer.Node, "xml-roles") == "contentinfo");
            var bar = Sole(footer.Children, node => Attribute(node.Node, "id") == "page_pc_mini_bar");
            var wrapper = Sole(bar.Children, node => HasClass(node.Node, "default-bar-wrapper"));
            var side = Sole(wrapper.Children, node => HasClass(node.Node, "side"));
            foreach (var node in new[] { bar, wrapper, side })
            {
                Live(node);
                Require(node.Node.Role == 20 && Attribute(node.Node, "tag") == "div");
            }
            var button = Sole(wrapper.Children, node => Attribute(node.Node, "id") == "btn_pc_minibar_play");
            Live(button);
            Require(button.Node.Role is 43 or 57 && Attribute(button.Node, "tag") == "button");
            var playName = Name(button, 32);
            var playing = playName is "pause" or "Pause" or "暂停";
            Require(playing || playName is "play" or "Play" or "播放");
            var icon = Sole(Descendants(button), node => HasClass(node.Node, "cmd-icon-play") || HasClass(node.Node, "cmd-icon-pause"));
            Live(icon);
            Require(icon.Node.Role == 40 && HasClass(icon.Node, playing ? "cmd-icon-pause" : "cmd-icon-play"));

            var titleContainer = Sole(side.Children, node => IsSpan(node, "block"));
            var title = Label(titleContainer, "block", 200);
            var artistContainers = side.Children.Where(node => IsSpan(node, "inline")).ToArray();
            Require(artistContainers.Length is > 0 and <= 16);
            var artists = artistContainers.Select(node => Label(node, "inline", 120)).ToArray();
            Require(artists.Distinct(StringComparer.Ordinal).Count() == artists.Length);
            Require(side.Children.All(node => node.Node.Role != 41));
            var preview = false;
            foreach (var tag in side.Children.Where(node => HasClass(node.Node, "cmd-tag")))
            {
                Live(tag);
                Require(tag.Node.Role == 20 && Attribute(tag.Node, "tag") == "div"
                    && Attribute(tag.Node, "explicit-name") == "true");
                var label = Name(tag, 32);
                var content = Sole(tag.Children, node => HasClass(node.Node, "cmd-tag-content"));
                Live(content);
                var text = Sole(content.Children, node => node.Node.Role == 41);
                Live(text);
                Require(text.Children.Count == 0);
                var tagText = Name(text, 24);
                Require(label == "Tag: " + tagText && tagText is "VIP" or "试听");
                preview |= tagText == "试听";
            }
            return new(true, title, artists, playing, preview) { WindowHandle = hwnd, FooterIdentity = footer.Node.Identity };
        }
        string Label(Snapshot<T> parent, string display, int maximum)
        {
            Live(parent);
            Require(parent.Children.Count == 1);
            var leaf = parent.Children[0];
            Live(leaf);
            Require(leaf.Node.Role == 41 && Attribute(leaf.Node, "display") == display && leaf.Children.Count == 0);
            return Name(leaf, maximum);
        }
        string Name(Snapshot<T> node, int maximum)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = readName(node.Native);
            Require(!string.IsNullOrWhiteSpace(name) && name.Length <= maximum && name == name.Trim()
                && !name.Any(character => char.IsControl(character) || char.GetUnicodeCategory(character) == UnicodeCategory.Format));
            return name!;
        }
    }

    private sealed record Snapshot<T>(T Native, NetEasePlaybackNode Node, IReadOnlyList<Snapshot<T>> Children);
    private sealed class UnreadableFooterException : Exception { }
    private static void Require(bool condition) { if (!condition) throw new UnreadableFooterException(); }
    private static string Attribute(NetEasePlaybackNode node, string key) => node.Attributes.GetValueOrDefault(key) ?? string.Empty;
    private static bool HasClass(NetEasePlaybackNode node, string token) => Attribute(node, "class")
        .Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(token, StringComparer.Ordinal);
    private static bool IsSpan<T>(Snapshot<T> node, string display) => node.Node.Role == 20
        && Attribute(node.Node, "tag") == "span" && Attribute(node.Node, "display") == display;
    private static void Live<T>(Snapshot<T> node) => Require((node.Node.State & (1 | 0x800 | 0x8000 | 0x10000)) == 0
        && Attribute(node.Node, "busy") != "true");
    private static Snapshot<T> Sole<T>(IEnumerable<Snapshot<T>> nodes, Func<Snapshot<T>, bool> matches)
    {
        var found = nodes.Where(matches).ToArray();
        Require(found.Length == 1);
        return found[0];
    }
    private static IEnumerable<Snapshot<T>> Descendants<T>(Snapshot<T> node) =>
        node.Children.SelectMany(child => new[] { child }.Concat(Descendants(child)));
    private static bool SameTree<T>(Snapshot<T> left, Snapshot<T> right) =>
        left.Node.Identity == right.Node.Identity && left.Node.WindowHandle == right.Node.WindowHandle
        && left.Node.Role == right.Node.Role && left.Node.State == right.Node.State
        && left.Node.Attributes.Count == right.Node.Attributes.Count
        && left.Node.Attributes.All(pair => right.Node.Attributes.TryGetValue(pair.Key, out var value) && pair.Value == value)
        && left.Children.Count == right.Children.Count
        && left.Children.Zip(right.Children).All(pair => SameTree(pair.First, pair.Second));
    private static NetEasePlaybackObservation Unreadable(long hwnd) => new(false, string.Empty, [], false, false) { WindowHandle = hwnd };

    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
}
