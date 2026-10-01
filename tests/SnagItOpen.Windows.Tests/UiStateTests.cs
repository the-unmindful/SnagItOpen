using System.IO;
using SnagItOpen.Storage.Settings;

namespace SnagItOpen.Windows.Tests;

public sealed class UiStateTests
{
    [Fact]
    public void Round_trip_preserves_layout_and_one_time_flags()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sio-ui-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            var store = new UiStateStore(Path.Combine(dir, "ui-state.json"));
            Assert.True(store.IsFirstRun);
            var state = new UiState { ClassicToolbar = true, LeftPanelWidth = 260, SectionsOpen = new() { ["Canvas"] = false } }
                .WithRecentProject("example.sio").WithTipShown("first-run");
            store.Save(state);
            Assert.False(store.IsFirstRun);
            var loaded = store.Load();
            Assert.Null(loaded.Warning);
            Assert.True(loaded.Value.ClassicToolbar);
            Assert.Equal(260, loaded.Value.LeftPanelWidth);
            Assert.False(loaded.Value.SectionsOpen["Canvas"]);
            Assert.True(loaded.Value.HasShownTip("first-run"));
            Assert.Equal("example.sio", Assert.Single(loaded.Value.RecentProjects));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Corruption_does_not_block_startup_or_touch_preferences()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sio-ui-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "ui-state.json");
            File.WriteAllText(path, "{broken");
            File.WriteAllText(Path.Combine(dir, "settings.json"), "preferences");
            var loaded = new UiStateStore(path).Load();
            Assert.NotNull(loaded.Warning);
            Assert.Equal(220, loaded.Value.LeftPanelWidth);
            Assert.Single(Directory.GetFiles(dir, "*.bak"));
            Assert.Equal("preferences", File.ReadAllText(Path.Combine(dir, "settings.json")));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Placement_requires_reachable_title_bar_on_current_monitors()
    {
        ScreenArea[] screens = [new(0, 0, 1920, 1080), new(-1280, 0, 1280, 1024)];
        Assert.True(UiState.IsPlacementVisible(new(-500, 20, 800, 600, false), screens));
        Assert.False(UiState.IsPlacementVisible(new(3000, 20, 800, 600, false), screens));
        Assert.False(UiState.IsPlacementVisible(new(10, -590, 800, 600, false), screens));
        Assert.Null(new UiState { Window = new(double.NaN, 0, 800, 600, false) }.Sanitize().Window);
    }

    [Fact]
    public void Recent_projects_are_unique_capped_and_removable()
    {
        var state = new UiState();
        for (int i = 0; i < 15; i++) state = state.WithRecentProject($"project{i}.sio");
        state = state.WithRecentProject("PROJECT14.sio");
        Assert.Equal(10, state.RecentProjects.Length);
        Assert.Equal("PROJECT14.sio", state.RecentProjects[0]);
        Assert.Equal(9, state.WithoutRecentProject("project14.sio").RecentProjects.Length);
        Assert.Single(state.WithTipShown("tip").WithTipShown("tip").TipsShown);
    }
}
