using SnagItOpen.Storage.Settings;

namespace SnagItOpen.Windows.Tests;

public class SettingsMigrationTests
{
    [Fact]
    public void Version1_moves_region_to_printscreen_and_enables_close_to_tray()
    {
        var v1 = new AppSettings { Version = 1, CloseToTray = false }.WithHotkey(HotkeyActions.Region, "Ctrl+Shift+1");
        var s = v1.Sanitize();
        Assert.Equal(AppSettings.CurrentVersion, s.Version);
        Assert.True(s.CloseToTray);
        Assert.Equal("PrintScreen", s.GestureFor(HotkeyActions.Region));
    }

    [Fact]
    public void Version1_custom_region_shortcut_is_kept()
    {
        var v1 = new AppSettings { Version = 1 }.WithHotkey(HotkeyActions.Region, "Ctrl+Alt+R");
        Assert.Equal("Ctrl+Alt+R", v1.Sanitize().GestureFor(HotkeyActions.Region));
    }

    [Fact]
    public void Current_version_choices_are_not_overridden()
    {
        var s = new AppSettings { CloseToTray = false }.WithHotkey(HotkeyActions.Region, "Ctrl+Shift+1").Sanitize();
        Assert.False(s.CloseToTray);
        Assert.Equal("Ctrl+Shift+1", s.GestureFor(HotkeyActions.Region));
    }

    [Fact]
    public void Defaults_use_printscreen_for_region()
    {
        Assert.Equal("PrintScreen", new AppSettings().GestureFor(HotkeyActions.Region));
        Assert.True(new AppSettings().CloseToTray);
    }
}
