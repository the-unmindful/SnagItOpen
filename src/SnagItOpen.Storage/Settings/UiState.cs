namespace SnagItOpen.Storage.Settings;

/// <summary>Saved editor window bounds in device-independent units (as WPF reports them).</summary>
public sealed record WindowPlacement(double Left, double Top, double Width, double Height, bool Maximized)
{
    /// <summary>Native desktop pixels avoid ambiguous global WPF coordinates across different monitor scales.</summary>
    public SnagItOpen.Core.Geometry.PixelRect? PhysicalBounds { get; init; }
}

/// <summary>A rectangle in device-independent units, used to check a placement against the current screens.</summary>
public readonly record struct ScreenArea(double Left, double Top, double Width, double Height);

/// <summary>
/// Remembered UI layout and one-time flags (ui-state.json, PRD 5.7). Kept apart from settings.json
/// so a corrupt file can never affect real preferences. Missing or invalid values fall back to defaults.
/// </summary>
public sealed record UiState
{
    public const int MaxRecentProjects = 10;
    public const int MaxRecentCommands = 20;
    public const double MinPanelWidth = 120, MaxPanelWidth = 900;
    public const double MinWindowWidth = 640, MinWindowHeight = 420, MaxWindowSize = 20_000;

    public WindowPlacement? Window { get; init; }
    public double LeftPanelWidth { get; init; } = 220;
    public double RightPanelWidth { get; init; } = 300;
    public bool LeftPanelCollapsed { get; init; }
    public bool RightPanelCollapsed { get; init; }
    /// <summary>Show the tools as a horizontal toolbar instead of the left rail (user decision, PRD section 11).</summary>
    public bool ClassicToolbar { get; init; }
    /// <summary>Open/closed state of inspector sections, keyed by section title.</summary>
    public Dictionary<string, bool> SectionsOpen { get; init; } = new(StringComparer.Ordinal);
    public string LibraryView { get; init; } = "Grid";
    public string LibrarySort { get; init; } = "Newest";
    public bool CaptureGalleryCollapsed { get; init; }
    public string[] RecentProjects { get; init; } = [];
    public string[] RecentCommands { get; init; } = [];
    public string[] TipsShown { get; init; } = [];
    public string[] DontAskAgain { get; init; } = [];

    /// <summary>Clamps sizes, drops nulls and duplicates, and limits list lengths.</summary>
    public UiState Sanitize()
    {
        static string[] Clean(string[]? items, int max) =>
            (items ?? []).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase).Take(max).ToArray();

        static double Width(double v, double fallback) =>
            double.IsFinite(v) ? Math.Clamp(v, MinPanelWidth, MaxPanelWidth) : fallback;

        var w = Window;
        if (w is not null)
        {
            bool valid = double.IsFinite(w.Left) && double.IsFinite(w.Top) && double.IsFinite(w.Width) && double.IsFinite(w.Height)
                && Math.Abs(w.Left) < MaxWindowSize * 4 && Math.Abs(w.Top) < MaxWindowSize * 4;
            w = valid
                ? w with
                {
                    Width = Math.Clamp(w.Width, MinWindowWidth, MaxWindowSize),
                    Height = Math.Clamp(w.Height, MinWindowHeight, MaxWindowSize),
                    PhysicalBounds = w.PhysicalBounds is { } physical && !physical.IsEmpty && physical.Width <= MaxWindowSize * 4 && physical.Height <= MaxWindowSize * 4 && Math.Abs((long)physical.X) <= MaxWindowSize * 4 && Math.Abs((long)physical.Y) <= MaxWindowSize * 4 ? physical : null,
                }
                : null;
        }

        var sections = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var kv in SectionsOpen ?? new Dictionary<string, bool>())
            if (!string.IsNullOrWhiteSpace(kv.Key) && sections.Count < 200) sections[kv.Key] = kv.Value;

        return this with
        {
            Window = w,
            LeftPanelWidth = Width(LeftPanelWidth, 220),
            RightPanelWidth = Width(RightPanelWidth, 300),
            SectionsOpen = sections,
            LibraryView = LibraryView is "Grid" or "List" ? LibraryView : "Grid",
            LibrarySort = LibrarySort is "Newest" or "Oldest" or "Name" or "Size" ? LibrarySort : "Newest",
            RecentProjects = Clean(RecentProjects, MaxRecentProjects),
            RecentCommands = Clean(RecentCommands, MaxRecentCommands),
            TipsShown = Clean(TipsShown, 100),
            DontAskAgain = Clean(DontAskAgain, 100),
        };
    }

    /// <summary>
    /// True when enough of the window's title bar (at least 100 x 30 DIP of its top strip) lies on a current screen,
    /// so restoring it cannot leave the window unreachable.
    /// </summary>
    public static bool IsPlacementVisible(WindowPlacement p, IReadOnlyList<ScreenArea> screens)
    {
        const double minW = 100, minH = 30;
        foreach (var s in screens)
        {
            double l = Math.Max(p.Left, s.Left), r = Math.Min(p.Left + p.Width, s.Left + s.Width);
            double t = Math.Max(p.Top, s.Top), b = Math.Min(p.Top + Math.Min(p.Height, minH), s.Top + s.Height);
            if (r - l >= minW && b - t >= minH - 0.5) return true;
        }
        return false;
    }

    /// <summary>Puts a project path first in the recent list (case-insensitive, limited to the maximum).</summary>
    public UiState WithRecentProject(string path) => this with
    {
        RecentProjects = new[] { path }.Concat((RecentProjects ?? []).Where(p => !string.Equals(p, path, StringComparison.OrdinalIgnoreCase)))
            .Take(MaxRecentProjects).ToArray(),
    };

    public UiState WithoutRecentProject(string path) => this with
    {
        RecentProjects = (RecentProjects ?? []).Where(p => !string.Equals(p, path, StringComparison.OrdinalIgnoreCase)).ToArray(),
    };

    public UiState WithRecentCommand(string id) => this with
    {
        RecentCommands = new[] { id }.Concat((RecentCommands ?? []).Where(c => c != id)).Take(MaxRecentCommands).ToArray(),
    };

    public bool HasShownTip(string tip) => (TipsShown ?? []).Contains(tip, StringComparer.Ordinal);

    public UiState WithTipShown(string tip) => HasShownTip(tip) ? this : this with { TipsShown = [.. TipsShown ?? [], tip] };
}

/// <summary>ui-state.json persistence: corrupt or missing files give defaults and never block startup.</summary>
public sealed class UiStateStore
{
    private readonly JsonFileStore<UiState> _file;

    public UiStateStore(string path) =>
        _file = new JsonFileStore<UiState>(path, () => new UiState(), s => s.Sanitize());

    public string Path => _file.Path;
    /// <summary>True when there was no ui-state.json before this run (used for first-run tips).</summary>
    public bool IsFirstRun => !File.Exists(Path);
    public LoadOutcome<UiState> Load() => _file.Load();
    public void Save(UiState s) => _file.Save(s.Sanitize());
}
