using System.IO;
using System.Windows;
using System.Windows.Threading;
using SnagItOpen.App.Capture;
using SnagItOpen.App.Editor;
using SnagItOpen.App.Shell;
using SnagItOpen.Storage;
using SnagItOpen.Windows.Shell;

namespace SnagItOpen.App;

/// <summary>
/// Startup: single-instance check (forwarding an activate/open request to the running instance),
/// service composition, crash-recovery offer, then the editor window.
/// </summary>
public partial class App : Application
{
    private const string AppId = "SnagItOpen";
    private SingleInstanceService? _instance;
    private AppServices? _services;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var file = e.Args.FirstOrDefault(a => !a.StartsWith('-') && File.Exists(a));
        var fullFile = file is null ? null : Path.GetFullPath(file);

        _instance = SingleInstanceService.TryAcquire(AppId);
        if (_instance is null)
        {
            var msg = fullFile is null
                ? new InstanceMessage(InstanceMessage.Activate, null)
                : InstanceMessage.TryParse(InstanceMessage.Open + "\n" + fullFile) ?? new InstanceMessage(InstanceMessage.Activate, null);
            SingleInstanceService.SendToExisting(AppId, msg, TimeSpan.FromSeconds(2));
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnUnhandled;
        try
        {
            _services = new AppServices(AppPaths.Default());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show($"SnagItOpen could not create its data folder:\n\n{ex.Message}", "SnagItOpen", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        var capture = new CaptureCoordinator(_services);
        var vm = new EditorViewModel(_services, capture);
        _services.Retention.AddOwner(vm.ProtectedAssets);
        var window = new MainWindow(_services, vm, capture);
        MainWindow = window;
        // "--tray" (used by Start with Windows) starts hidden: hotkeys and the tray icon work, no window.
        bool startHidden = e.Args.Any(a => string.Equals(a, "--tray", StringComparison.OrdinalIgnoreCase)) && fullFile is null;
        if (startHidden) window.StartInTray();
        else window.Show();

        foreach (var w in _services.StartupWarnings) vm.Status = w;
        OfferRecovery(vm, window);
        _instance.StartListening(m => Dispatcher.BeginInvoke(() => window.HandleInstanceMessage(m)));
        if (fullFile is not null) _ = window.OpenPathsAsync([fullFile]);

        // Remove orphaned session assets in the background (grace period protects fresh imports).
        _ = Task.Run(() =>
        {
            try { _services.Retention.Collect(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _services.Log("Asset cleanup: " + ex.Message); }
        });
    }

    private void OfferRecovery(EditorViewModel vm, Window owner)
    {
        var candidates = _services!.Recovery.FindCandidates();
        if (candidates.Count == 0) return;
        var c = candidates[0];
        var s = c.Snapshot;
        var r = MessageBox.Show(owner,
            $"SnagItOpen found an autosaved composition from {s.SavedAt:g} ({s.Document.Images.Length} image(s)).\n\n" +
            "Recover it? It opens as an unsaved composition; any original project file is left unchanged.\n\n" +
            "Choose No to discard the draft.",
            "Recover unsaved work", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (r == MessageBoxResult.Yes)
        {
            vm.LoadDocument(s.Document, null, markSaved: false);
            vm.Status = "Recovered autosaved work. Save it to keep it.";
        }
        else if (r == MessageBoxResult.No)
        {
            foreach (var cand in candidates) _services.Recovery.Discard(cand.Snapshot.DocumentId);
        }
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _services?.Log("Unhandled: " + e.Exception);
        // Try to keep an autosave of the current work before reporting.
        try { _services?.Autosave.FlushAsync().Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
        MessageBox.Show($"An unexpected error occurred:\n\n{e.Exception.Message}\n\nYour work was autosaved where possible. Details are in the log folder.",
            "SnagItOpen", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _instance?.Dispose();
        _services?.Dispose();
        base.OnExit(e);
    }
}
