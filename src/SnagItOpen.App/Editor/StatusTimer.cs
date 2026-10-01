using System.Windows.Threading;

namespace SnagItOpen.App.Editor;

public interface IStatusTimer
{
    event Action? Elapsed;
    void Start(TimeSpan timeout);
    void Stop();
}

internal sealed class StatusTimer : IStatusTimer
{
    private readonly DispatcherTimer _timer = new();
    public StatusTimer() => _timer.Tick += (_, _) => { _timer.Stop(); Elapsed?.Invoke(); };
    public event Action? Elapsed;
    public void Start(TimeSpan timeout) { _timer.Stop(); _timer.Interval = timeout; _timer.Start(); }
    public void Stop() => _timer.Stop();
}
