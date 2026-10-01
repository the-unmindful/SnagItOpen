using System.IO;
using SnagItOpen.App.Capture;
using SnagItOpen.App.Controls;
using SnagItOpen.App.Editor;
using SnagItOpen.App.Shell;
using SnagItOpen.Storage;
namespace SnagItOpen.Windows.Tests;
public sealed class StatusTimerTests
{
    private sealed class FakeTimer : IStatusTimer
    {
        public event Action? Elapsed;
        public TimeSpan Timeout; public bool Running;
        public void Start(TimeSpan timeout) { Timeout = timeout; Running = true; }
        public void Stop() => Running = false;
        public void Fire() { if (Running) { Running = false; Elapsed?.Invoke(); } }
    }
    [Fact]
    public void Status_clears_after_eight_seconds_and_errors_persist_until_replaced()
    {
        ThemeTokenTests.RunSta(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), "SnagItOpenStatusTests", Guid.NewGuid().ToString("N"));
            try
            {
                using var services = new AppServices(new AppPaths(root)); var clock = new FakeTimer();
                var vm = new EditorViewModel(services, new CaptureCoordinator(services), clock);
                vm.Status = "Saved"; Assert.Equal(TimeSpan.FromSeconds(8), clock.Timeout); clock.Fire(); Assert.Null(vm.Status);
                vm.SetStatus(NotificationKind.Error, "Copy failed"); clock.Fire(); Assert.Equal("Copy failed", vm.Status);
                vm.Status = "Copied"; clock.Fire(); Assert.Null(vm.Status);
                vm.SetStatus(NotificationKind.Error, "Repeated"); vm.SetStatus(NotificationKind.Info, "Repeated"); Assert.True(clock.Running); clock.Fire(); Assert.Null(vm.Status);
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
            return true;
        });
    }
}
