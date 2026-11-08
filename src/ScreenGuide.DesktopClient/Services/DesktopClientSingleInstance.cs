using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using ScreenGuide.DesktopProtocol;

namespace ScreenGuide.DesktopClient.Services;

public sealed record DesktopActivationRequest(bool ShowManagement, bool StartListening);

public sealed class DesktopClientSingleInstance : IDisposable
{
    private readonly object _activationSync = new();
    private readonly Mutex _mutex;
    private readonly CancellationTokenSource _stopping = new();
    private readonly string _activationPipe;
    private Task? _listener;
    private Action<DesktopActivationRequest>? _activationHandler;
    private DesktopActivationRequest? _pendingActivation;

    private DesktopClientSingleInstance(Mutex mutex)
    {
        _mutex = mutex;
        _activationPipe = ResolveActivationPipeName();
        _listener = ListenAsync(_stopping.Token);
    }

    public static DesktopClientSingleInstance? TryAcquire()
    {
        var mutex = new Mutex(
            initiallyOwned: false,
            ResolveMutexName());
        try
        {
            if (!mutex.WaitOne(TimeSpan.Zero))
            {
                mutex.Dispose();
                return null;
            }
        }
        catch (AbandonedMutexException)
        {
        }

        return new DesktopClientSingleInstance(mutex);
    }

    public void SetActivationRequestHandler(Action<DesktopActivationRequest> handler)
    {
        DesktopActivationRequest? pending;
        lock (_activationSync)
        {
            _activationHandler = handler;
            pending = _pendingActivation;
            _pendingActivation = null;
        }
        if (pending is not null) handler(pending);
    }

    public void SetActivationHandler(Action<bool> handler) =>
        SetActivationRequestHandler(request => handler(request.ShowManagement));
    public void SetActivationHandler(Action handler) => SetActivationRequestHandler(_ => handler());

    public static async Task<bool> TrySignalExistingAsync(
        CancellationToken cancellationToken = default, bool showManagement = false, bool startListening = false)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(
                ".",
                ResolveActivationPipeName(),
                PipeDirection.Out,
                PipeOptions.Asynchronous);
            await pipe.ConnectAsync(1000, cancellationToken).ConfigureAwait(false);
            // Legacy signals 1/2 remain non-capturing. Only an explicit voice activation uses 3.
            await pipe.WriteAsync(new byte[] { showManagement ? (byte)2 : startListening ? (byte)3 : (byte)1 }, cancellationToken).ConfigureAwait(false);
            await pipe.FlushAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (exception is IOException or TimeoutException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        _stopping.Cancel();
        _mutex.Dispose();
        _stopping.Dispose();
    }

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(
                    _activationPipe,
                    PipeDirection.In,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                var signal = new byte[1];
                if (await pipe.ReadAsync(signal, cancellationToken).ConfigureAwait(false) > 0)
                {
                    if (signal[0] is 1 or 2 or 3)
                    {
                        var request = new DesktopActivationRequest(signal[0] == 2, signal[0] == 3);
                        Action<DesktopActivationRequest>? handler;
                        lock (_activationSync)
                        {
                            handler = _activationHandler;
                            // Startup may still be loading settings. Keep only the latest opening intent.
                            if (handler is null) _pendingActivation = request;
                        }
                        handler?.Invoke(request);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (IOException)
            {
            }
        }
    }

    private static string ResolveActivationPipeName() =>
        ResolveHostPipeName() + ".Activate";

    private static string ResolveMutexName()
    {
        var customPipeName = Environment.GetEnvironmentVariable("SCREEN_GUIDE_PIPE_NAME");
        if (string.IsNullOrWhiteSpace(customPipeName))
        {
            return DesktopIpcEndpoint.CurrentUserMutexName("DesktopClient");
        }

        var hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(customPipeName.Trim())))[..16];
        return $"{DesktopIpcEndpoint.CurrentUserMutexName("DesktopClient")}.{hash}";
    }

    private static string ResolveHostPipeName()
    {
        var customPipeName = Environment.GetEnvironmentVariable("SCREEN_GUIDE_PIPE_NAME");
        return string.IsNullOrWhiteSpace(customPipeName)
            ? DesktopIpcEndpoint.CurrentUserPipeName()
            : customPipeName.Trim();
    }
}
