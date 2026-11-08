using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using ScreenGuide.Skills.Abstractions;
using Image = System.Windows.Controls.Image;
using Window = System.Windows.Window;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace ScreenGuide.Vision.Windows;

/// <summary>Local review of already-consented pixels. This class never captures or sends input.</summary>
public sealed class WindowsDesktopVisualTargetPreview : IDesktopVisualTargetPreview
{
    public async Task<IDesktopVisualPreviewLease> ShowAsync(DesktopVisualObservation observation,
        DesktopVisualProposal proposal, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(observation);
        token.ThrowIfCancellationRequested();
        DesktopVisualImage.RequireSnapshot(observation.Snapshot, DateTimeOffset.UtcNow);
        DesktopVisualImage.RequireAction(observation.Snapshot, proposal);
        var bytes = observation.PngBytes.ToArray();
        if (bytes.Length > DesktopVisualImage.MaximumImageBytes
            || Convert.ToHexString(SHA256.HashData(bytes)) != observation.Snapshot.ImageSha256)
        {
            CryptographicOperations.ZeroMemory(bytes);
            throw Failed();
        }
        var lease = new PreviewLease(observation.Snapshot, proposal, bytes);
        try
        {
            lease.Start(token);
            await lease.Ready.WaitAsync(TimeSpan.FromSeconds(10), token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (!lease.IsVisible) throw Failed();
            return lease;
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    private static DesktopTaskException Failed() =>
        DesktopVisualImage.Error("preview_unavailable", "精确目标预览未能保持完整可见，本次没有继续操作。");

    private sealed class PreviewLease(DesktopVisualSnapshot snapshot, DesktopVisualProposal proposal, byte[] png)
        : IDesktopVisualPreviewLease
    {
        private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private byte[]? _png = png;
        private Dispatcher? _dispatcher;
        private Window? _window;
        private Image? _image;
        private CancellationTokenRegistration _cancellation;
        private long _handle;
        private int _disposed;
        private int _rendered;
        public Task Ready => _ready.Task;

        public bool IsVisible => Volatile.Read(ref _disposed) == 0 && Volatile.Read(ref _rendered) != 0
            && IsUnobscured(Interlocked.Read(ref _handle));

        internal void Start(CancellationToken token)
        {
            _cancellation = token.Register(() =>
            {
                _ready.TrySetCanceled(token);
                // Cancellation cannot fail because a UI thread is already shutting down.
                try { Dispose(); } catch (Exception) { }
            });
            var thread = new Thread(Run) { IsBackground = true, Name = "ScreenGuide visual target preview" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }

        private void Run()
        {
            byte[]? bytes = null;
            try
            {
                var dispatcher = Dispatcher.CurrentDispatcher;
                Volatile.Write(ref _dispatcher, dispatcher);
                bytes = Interlocked.Exchange(ref _png, null);
                if (Volatile.Read(ref _disposed) != 0 || bytes is null) return;
                BitmapImage decoded;
                try
                {
                    using var stream = new MemoryStream(bytes, writable: false);
                    decoded = new BitmapImage();
                    decoded.BeginInit();
                    decoded.CacheOption = BitmapCacheOption.OnLoad;
                    decoded.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
                    decoded.StreamSource = stream;
                    decoded.EndInit();
                    decoded.Freeze();
                    if (decoded.PixelWidth != snapshot.ImageWidth || decoded.PixelHeight != snapshot.ImageHeight)
                        throw Failed();
                }
                finally { CryptographicOperations.ZeroMemory(bytes); bytes = null; }
                if (Volatile.Read(ref _disposed) != 0) return;
                var window = BuildWindow(decoded);
                _window = window;
                window.SourceInitialized += (_, _) => Interlocked.Exchange(ref _handle, new WindowInteropHelper(window).Handle.ToInt64());
                window.ContentRendered += (_, _) =>
                {
                    Volatile.Write(ref _rendered, 1);
                    if (IsVisible) _ready.TrySetResult();
                    else { _ready.TrySetException(Failed()); Close(); }
                };
                window.Closed += (_, _) =>
                {
                    Volatile.Write(ref _rendered, 0);
                    Interlocked.Exchange(ref _disposed, 1);
                    Interlocked.Exchange(ref _handle, 0);
                    if (_image is not null) _image.Source = null;
                    window.Content = null;
                    _ready.TrySetException(Failed());
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
                };
                if (Volatile.Read(ref _disposed) != 0) return;
                window.Show();
                if (Volatile.Read(ref _disposed) != 0) Close();
                Dispatcher.Run();
            }
            catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
            {
                _ready.TrySetException(Failed());
            }
            finally
            {
                Volatile.Write(ref _rendered, 0);
                Interlocked.Exchange(ref _disposed, 1);
                if (bytes is not null) CryptographicOperations.ZeroMemory(bytes);
                var abandoned = Interlocked.Exchange(ref _png, null);
                if (abandoned is not null) CryptographicOperations.ZeroMemory(abandoned);
                try { Close(); } catch (Exception) { }
                if (_image is not null) _image.Source = null;
                _image = null;
                _window = null;
                _cancellation.Unregister();
            }
        }

        private Window BuildWindow(BitmapImage decoded)
        {
            var root = new DockPanel { LastChildFill = true, Margin = new Thickness(14) };
            var title = new TextBlock
            {
                Text = "请核对红框与十字中心：这是模型推测的位置。",
                FontSize = 17, FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap, MaxHeight = 52, Margin = new Thickness(0, 0, 0, 8)
            };
            DockPanel.SetDock(title, Dock.Top); root.Children.Add(title);
            var description = new TextBlock
            {
                Text = $"拟操作：{ActionName(proposal.Action)}「{Compact(proposal.Target, 80)}」\n只确认红框中的位置；预览本身不会点击或输入。请到元枢的步骤许可中确认或拒绝。",
                FontSize = 14, TextWrapping = TextWrapping.Wrap, MaxHeight = 72, Margin = new Thickness(0, 0, 0, 10)
            };
            DockPanel.SetDock(description, Dock.Top); root.Children.Add(description);
            var footer = new TextBlock
            {
                Text = $"{Compact(snapshot.Window.Title, 100)}\n完整窗口截图 · 红框坐标 ({proposal.Bounds!.X}, {proposal.Bounds.Y})，大小 {proposal.Bounds.Width} × {proposal.Bounds.Height} 像素\n关闭或遮挡预览后，这一步不会继续。图片不保存。",
                FontSize = 12, Foreground = Brushes.DimGray, TextWrapping = TextWrapping.Wrap, MaxHeight = 70,
                Margin = new Thickness(0, 10, 0, 0)
            };
            DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
            var canvas = new Canvas { Width = snapshot.ImageWidth, Height = snapshot.ImageHeight, ClipToBounds = true };
            _image = new Image { Source = decoded, Width = snapshot.ImageWidth, Height = snapshot.ImageHeight, Stretch = Stretch.Fill };
            canvas.Children.Add(_image);
            var bounds = proposal.Bounds;
            var rectangle = new Rectangle
            {
                Width = bounds.Width, Height = bounds.Height, Stroke = Brushes.Red, StrokeThickness = 3,
                Fill = new SolidColorBrush(Color.FromArgb(32, 255, 0, 0)), IsHitTestVisible = false
            };
            Canvas.SetLeft(rectangle, bounds.X); Canvas.SetTop(rectangle, bounds.Y); canvas.Children.Add(rectangle);
            var source = DesktopVisualImage.MapBounds(snapshot, bounds);
            var x = (source.X + source.Width / 2) * (double)snapshot.ImageWidth / snapshot.SourceWidth;
            var y = (source.Y + source.Height / 2) * (double)snapshot.ImageHeight / snapshot.SourceHeight;
            canvas.Children.Add(new Line { X1 = x - 10, X2 = x + 10, Y1 = y, Y2 = y, Stroke = Brushes.Red, StrokeThickness = 2, IsHitTestVisible = false });
            canvas.Children.Add(new Line { X1 = x, X2 = x, Y1 = y - 10, Y2 = y + 10, Stroke = Brushes.Red, StrokeThickness = 2, IsHitTestVisible = false });
            root.Children.Add(new Border
            {
                BorderBrush = Brushes.LightGray, BorderThickness = new Thickness(1), Background = Brushes.Black,
                Child = new Viewbox { Stretch = Stretch.Uniform, StretchDirection = StretchDirection.Both, Child = canvas }
            });
            var area = SystemParameters.WorkArea;
            var width = Math.Min(650, Math.Max(360, area.Width - 40));
            var height = Math.Min(650, Math.Max(340, area.Height - 40));
            return new Window
            {
                Title = "元枢·看图目标确认", Width = width, Height = height,
                MinWidth = Math.Min(360, width), MinHeight = Math.Min(340, height),
                Left = area.Left + 12, Top = area.Top + 12,
                Content = root, Background = Brushes.White,
                ShowActivated = false, ShowInTaskbar = true, Topmost = true,
                WindowStartupLocation = WindowStartupLocation.Manual, ResizeMode = ResizeMode.CanResize,
                UseLayoutRounding = true
            };
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _disposed, 1);
            Volatile.Write(ref _rendered, 0);
            _ready.TrySetException(Failed());
            var bytes = Interlocked.Exchange(ref _png, null);
            if (bytes is not null) CryptographicOperations.ZeroMemory(bytes);
            _cancellation.Unregister();
            var dispatcher = Volatile.Read(ref _dispatcher);
            if (dispatcher is null || dispatcher.HasShutdownFinished) return;
            if (dispatcher.CheckAccess()) Close();
            else
            {
                try { dispatcher.Invoke(Close, DispatcherPriority.Send, CancellationToken.None, TimeSpan.FromSeconds(2)); }
                catch (TaskCanceledException) when (dispatcher.HasShutdownStarted) { }
                catch (InvalidOperationException) when (dispatcher.HasShutdownStarted) { }
            }
        }

        private void Close()
        {
            _window?.Close();
            if (_dispatcher is { HasShutdownStarted: false } dispatcher)
                dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
        }
    }

    private static string ActionName(DesktopVisualActionKind action) => action switch
    {
        DesktopVisualActionKind.Click => "单击", DesktopVisualActionKind.DoubleClick => "双击",
        DesktopVisualActionKind.TypeText => "点击后追加文字", DesktopVisualActionKind.ScrollUp => "向上滚动",
        DesktopVisualActionKind.ScrollDown => "向下滚动", _ => "操作"
    };

    private static string Compact(string text, int length) =>
        new(text.Take(length).Select(character => char.IsWhiteSpace(character) ? ' ' : character).ToArray());

    private static bool IsUnobscured(long handle)
    {
        var window = new IntPtr(handle);
        if (window == IntPtr.Zero || !IsWindowVisible(window) || IsIconic(window)) return false;
        var previous = SetThreadDpiAwarenessContext(new IntPtr(-4));
        if (previous == IntPtr.Zero) return false;
        try
        {
            if (!ReadBounds(window, out var bounds) || IsCloaked(window)
                || !ReadContainingWorkArea(bounds, out var workArea)) return false;
            var above = GetWindow(window, 3); // GW_HWNDPREV: every top-level window above this preview.
            for (var count = 0; above != IntPtr.Zero; count++, above = GetWindow(above, 3))
            {
                if (count >= 512) return false;
                if (!IsWindowVisible(above) || IsIconic(above) || IsCloaked(above)) continue;
                if (!ReadBounds(above, out var other)) return false;
                // Conservative for transparent/irregular overlays: never report an
                // unseen target as reviewable merely because our Window.IsVisible is true.
                if (other.Right > bounds.Left && other.Left < bounds.Right
                    && other.Bottom > bounds.Top && other.Top < bounds.Bottom) return false;
            }
            // Neither window position nor monitor geometry is cached. Re-read
            // both so dragging or a display change during this check invalidates it.
            return IsWindowVisible(window) && !IsIconic(window)
                && ReadBounds(window, out var current) && SameRectangle(current, bounds)
                && ReadContainingWorkArea(current, out var currentWorkArea) && SameRectangle(currentWorkArea, workArea);
        }
        finally { _ = SetThreadDpiAwarenessContext(previous); }
    }

    private static bool ReadBounds(IntPtr window, out NativeRect rectangle)
    {
        if (DwmGetWindowAttribute(window, 9, out rectangle, Marshal.SizeOf<NativeRect>()) != 0
            && !GetWindowRect(window, out rectangle)) return false;
        return rectangle.Right > rectangle.Left && rectangle.Bottom > rectangle.Top;
    }
    private static bool IsCloaked(IntPtr window) =>
        DwmGetWindowAttribute(window, 14, out int cloaked, sizeof(int)) == 0 && cloaked != 0;

    private static bool ReadContainingWorkArea(NativeRect bounds, out NativeRect workArea)
    {
        workArea = default;
        // MONITOR_DEFAULTTONULL rejects a completely off-screen window. The
        // complete rectangle must then fit this ONE monitor's current work area;
        // a union of multiple displays does not prove the target was visible.
        var monitor = MonitorFromRect(ref bounds, 0);
        var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info)) return false;
        workArea = info.WorkArea;
        return IsInsideVisibleWorkArea(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom,
            workArea.Left, workArea.Top, workArea.Right, workArea.Bottom);
    }

    internal static bool IsInsideVisibleWorkArea(int left, int top, int right, int bottom,
        int workLeft, int workTop, int workRight, int workBottom) =>
        right > left && bottom > top && workRight > workLeft && workBottom > workTop
        && left >= workLeft && top >= workTop && right <= workRight && bottom <= workBottom;

    private static bool SameRectangle(NativeRect left, NativeRect right) =>
        left.Left == right.Left && left.Top == right.Top && left.Right == right.Right && left.Bottom == right.Bottom;

    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo
    { public uint Size; public NativeRect Monitor, WorkArea; public uint Flags; }
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window, uint command);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out NativeRect bounds);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromRect(ref NativeRect rectangle, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out NativeRect value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);
}
