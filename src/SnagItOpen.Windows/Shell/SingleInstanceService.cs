using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;

namespace SnagItOpen.Windows.Shell;

/// <summary>Messages a second instance may send. Anything else is ignored; nothing is ever executed.</summary>
public sealed record InstanceMessage(string Kind, string? Path)
{
    public const int MaxBytes = 4096;
    public const string Activate = "activate", Open = "open";
    public static readonly string[] OpenableExtensions = [".sio", ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff"];

    public string Serialize() => Path is null ? Kind : Kind + "\n" + Path;

    public static InstanceMessage? TryParse(string? text)
    {
        if (string.IsNullOrEmpty(text) || Encoding.UTF8.GetByteCount(text) > MaxBytes) return null;
        var parts = text.Split('\n', 2);
        if (parts[0] == Activate && parts.Length == 1) return new InstanceMessage(Activate, null);
        if (parts[0] == Open && parts.Length == 2)
        {
            var p = parts[1].Trim();
            if (p.Length == 0 || p.IndexOfAny(System.IO.Path.GetInvalidPathChars()) >= 0 || !System.IO.Path.IsPathFullyQualified(p)) return null;
            if (!OpenableExtensions.Contains(System.IO.Path.GetExtension(p), StringComparer.OrdinalIgnoreCase)) return null;
            return new InstanceMessage(Open, p);
        }
        return null;
    }
}

/// <summary>
/// User-scoped single instance: a named mutex plus a current-user-only named pipe used to ask the
/// first instance to activate or open a file.
/// </summary>
public sealed class SingleInstanceService : IDisposable
{
    private readonly Mutex _mutex;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _cts = new();

    private SingleInstanceService(Mutex mutex, string pipeName)
    {
        _mutex = mutex;
        _pipeName = pipeName;
    }

    private static string Scope(string appId)
    {
        var sid = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        return appId + "-" + sid;
    }

    /// <summary>Returns the owner service, or null when another instance for this user is running.</summary>
    public static SingleInstanceService? TryAcquire(string appId)
    {
        var scope = Scope(appId);
        var m = new Mutex(initiallyOwned: true, @"Local\" + scope, out bool created);
        if (!created) { m.Dispose(); return null; }
        return new SingleInstanceService(m, scope);
    }

    public static bool SendToExisting(string appId, InstanceMessage message, TimeSpan timeout)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", Scope(appId), PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect((int)timeout.TotalMilliseconds);
            var bytes = Encoding.UTF8.GetBytes(message.Serialize());
            if (bytes.Length > InstanceMessage.MaxBytes) return false;
            client.Write(bytes);
            client.Flush();
            return true;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>Starts a background listener; <paramref name="onMessage"/> runs on a thread-pool thread.</summary>
    public void StartListening(Action<InstanceMessage> onMessage)
    {
        var ct = _cts.Token;
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(_pipeName, PipeDirection.In, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await server.WaitForConnectionAsync(ct);
                    var buf = new byte[InstanceMessage.MaxBytes + 1];
                    int total = 0, n;
                    using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    readCts.CancelAfter(TimeSpan.FromSeconds(2));
                    while (total < buf.Length && (n = await server.ReadAsync(buf.AsMemory(total), readCts.Token)) > 0) total += n;
                    if (total > InstanceMessage.MaxBytes) continue;
                    if (InstanceMessage.TryParse(Encoding.UTF8.GetString(buf, 0, total)) is { } msg) onMessage(msg);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch (Exception ex) when (ex is IOException or OperationCanceledException or UnauthorizedAccessException)
                {
                    await Task.Delay(200, CancellationToken.None);
                }
            }
        }, ct);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _mutex.ReleaseMutex(); } catch (ApplicationException) { }
        _mutex.Dispose();
        _cts.Dispose();
    }
}
