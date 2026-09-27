using System.Windows.Threading;

namespace SnagItOpen.Imaging.Threading;

/// <summary>
/// One dedicated STA thread with a WPF <see cref="Dispatcher"/> for decode/render/encode work.
/// Jobs run serially. Exceptions propagate to the caller's task and never stop the dispatcher.
/// Cancellation completes the caller's task promptly; a job already running may finish, but its
/// result is discarded.
/// </summary>
public sealed class ImagingDispatcher : IDisposable
{
    private readonly Thread _thread;
    private readonly Dispatcher _dispatcher;
    private volatile bool _disposed;

    public ImagingDispatcher(string name = "SnagItOpen imaging")
    {
        Dispatcher? d = null;
        using var ready = new ManualResetEventSlim();
        _thread = new Thread(() =>
        {
            d = Dispatcher.CurrentDispatcher;
            ready.Set();
            Dispatcher.Run();
        })
        { IsBackground = true, Name = name };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        ready.Wait();
        _dispatcher = d!;
    }

    public bool IsDisposed => _disposed;
    public int ManagedThreadId => _thread.ManagedThreadId;
    public bool CheckAccess() => _dispatcher.CheckAccess();

    public Task<T> InvokeAsync<T>(Func<T> func, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(func);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (ct.IsCancellationRequested) return Task.FromCanceled<T>(ct);
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenRegistration reg = default;
        if (ct.CanBeCanceled) reg = ct.Register(() => tcs.TrySetCanceled(ct));
        _dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
        {
            try
            {
                if (ct.IsCancellationRequested) { tcs.TrySetCanceled(ct); return; }
                tcs.TrySetResult(func());
            }
            catch (OperationCanceledException)
            {
                tcs.TrySetCanceled(ct.IsCancellationRequested ? ct : new CancellationToken(true));
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
            finally
            {
                reg.Dispose();
            }
        }));
        return tcs.Task;
    }

    public Task InvokeAsync(Action action, CancellationToken ct = default) =>
        InvokeAsync(() => { action(); return true; }, ct);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _dispatcher.InvokeShutdown(); } catch (InvalidOperationException) { }
        _thread.Join(TimeSpan.FromSeconds(5));
    }
}
