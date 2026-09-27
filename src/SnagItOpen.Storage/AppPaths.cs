namespace SnagItOpen.Storage;

/// <summary>Runtime data folders under %LOCALAPPDATA%\SnagItOpen (overridable for tests/portable mode).</summary>
public sealed class AppPaths
{
    public AppPaths(string root)
    {
        Root = Path.GetFullPath(root);
    }

    public static AppPaths Default()
    {
        var env = Environment.GetEnvironmentVariable("SNAGITOPEN_DATA");
        if (!string.IsNullOrWhiteSpace(env)) return new AppPaths(env);
        return new AppPaths(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SnagItOpen"));
    }

    public string Root { get; }
    public string Settings => Path.Combine(Root, "settings.json");
    public string Presets => Path.Combine(Root, "presets.json");
    public string ToolStyles => Path.Combine(Root, "toolstyles.json");
    public string CapturePresets => Path.Combine(Root, "capture-presets.json");
    public string LastRegion => Path.Combine(Root, "last-region.json");
    public string Assets => Path.Combine(Root, "assets");
    public string History => Path.Combine(Root, "history");
    public string Recovery => Path.Combine(Root, "recovery");
    public string Cache => Path.Combine(Root, "cache");
    public string Logs => Path.Combine(Root, "logs");
    public string Temp => Path.Combine(Root, "temp");

    public void EnsureCreated()
    {
        foreach (var d in new[] { Root, Assets, History, Recovery, Cache, Logs, Temp }) Directory.CreateDirectory(d);
    }
}
