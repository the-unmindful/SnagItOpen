using System.IO;
using SnagItOpen.Imaging.Export;
using SnagItOpen.Imaging.Import;
using SnagItOpen.Imaging.Rendering;
using SnagItOpen.Imaging.Threading;
using SnagItOpen.Storage;
using SnagItOpen.Storage.Assets;
using SnagItOpen.Storage.History;
using SnagItOpen.Storage.Projects;
using SnagItOpen.Storage.Recovery;
using SnagItOpen.Storage.Settings;
using SnagItOpen.Windows.Capture;
using SnagItOpen.Windows.Clipboard;

namespace SnagItOpen.App.Shell;

/// <summary>Composition root: constructs every service once with manual constructor injection.</summary>
public sealed class AppServices : IDisposable
{
    public AppServices(AppPaths paths)
    {
        Paths = paths;
        Paths.EnsureCreated();
        var clip = Path.Combine(paths.Cache, "clip");
        if (Directory.Exists(clip))
            foreach (var file in Directory.EnumerateFiles(clip, "*.png"))
                Storage.AtomicFile.TryDelete(file);
        Assets = new FileAssetStore(paths.Assets);
        Assets.CleanupTemporaryFiles();
        Imaging = new ImagingDispatcher();
        Importer = new ImageImporter(Assets, Imaging);
        Cache = new BitmapAssetCache(Assets);
        Renderer = new DocumentRenderer(Cache);
        Export = new ExportService(Renderer, Imaging);
        Projects = new ProjectStore(Assets);
        Clipboard = new ClipboardImageService();

        SettingsStore = new SettingsStore(paths.Settings);
        var s = SettingsStore.Load();
        Settings = s.Value;
        if (s.Warning is not null) StartupWarnings.Add(s.Warning);

        UiStateStore = new UiStateStore(paths.UiState);
        IsFirstRun = UiStateStore.IsFirstRun;
        var ui = UiStateStore.Load();
        UiState = ui.Value; // a corrupt ui-state.json silently falls back to defaults (backup kept)

        LayoutPresets = new LayoutPresetStore(paths.Presets);
        LayoutPresets.Load();
        if (LayoutPresets.LastWarning is { } lw) StartupWarnings.Add(lw);
        ToolStyles = new ToolStyleStore(paths.ToolStyles);
        ToolStyles.Load();
        AnnotationStyles = new AnnotationStyleStore(paths.AnnotationStyles);
        AnnotationStyles.Load();
        if (AnnotationStyles.LastWarning is { } aw) StartupWarnings.Add(aw);
        CapturePresets = new CapturePresetStore(paths.CapturePresets);
        CapturePresets.Load();
        if (CapturePresets.LastWarning is { } cw) StartupWarnings.Add(cw);
        LastRegion = new LastRegionStore(paths.LastRegion);

        History = new CaptureHistoryStore(paths.History, Assets);
        History.Load();
        if (History.LastWarning is { } hw) StartupWarnings.Add(hw);
        Recovery = new RecoveryStore(paths.Recovery);
        Autosave = new AutosaveScheduler(Recovery);
        Retention = new AssetRetentionService(Assets);
        Retention.AddOwner(() => History.ReferencedAssets());
        Retention.AddOwner(() => Recovery.ReferencedAssets());

        Monitors = new MonitorService();
        Windows = new WindowCatalog();
    }

    public AppPaths Paths { get; }
    public FileAssetStore Assets { get; }
    public ImagingDispatcher Imaging { get; }
    public ImageImporter Importer { get; }
    public BitmapAssetCache Cache { get; }
    public DocumentRenderer Renderer { get; }
    public ExportService Export { get; }
    public ProjectStore Projects { get; }
    public ClipboardImageService Clipboard { get; }
    public SettingsStore SettingsStore { get; }
    public AppSettings Settings { get; set; }
    public UiStateStore UiStateStore { get; }
    /// <summary>Remembered layout and one-time flags; update with <see cref="SaveUiState"/>.</summary>
    public UiState UiState { get; private set; }
    /// <summary>True when ui-state.json did not exist at startup.</summary>
    public bool IsFirstRun { get; }
    public LayoutPresetStore LayoutPresets { get; }
    public ToolStyleStore ToolStyles { get; }
    public AnnotationStyleStore AnnotationStyles { get; }
    public CapturePresetStore CapturePresets { get; }
    public LastRegionStore LastRegion { get; }
    public CaptureHistoryStore History { get; }
    public RecoveryStore Recovery { get; }
    public AutosaveScheduler Autosave { get; }
    public AssetRetentionService Retention { get; }
    public MonitorService Monitors { get; }
    public WindowCatalog Windows { get; }
    public List<string> StartupWarnings { get; } = [];

    public void SaveSettings(AppSettings settings)
    {
        Settings = settings.Sanitize();
        try { SettingsStore.Save(Settings); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { StartupWarnings.Add($"Settings could not be saved: {ex.Message}"); }
    }

    /// <summary>Replaces the UI state and writes ui-state.json (failures are logged, never thrown).</summary>
    public void SaveUiState(UiState state)
    {
        UiState = state.Sanitize();
        try { UiStateStore.Save(UiState); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log("UI state could not be saved: " + ex.Message); }
    }

    public void Log(string message)
    {
        try { File.AppendAllText(Path.Combine(Paths.Logs, "snagitopen.log"), $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        Autosave.Dispose();
        Monitors.Dispose();
        Imaging.Dispose();
    }
}
