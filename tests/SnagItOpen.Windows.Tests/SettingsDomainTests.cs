using System.IO;
using System.Globalization;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using SnagItOpen.App.Shell;
using SnagItOpen.App.Shell.Settings;
using SnagItOpen.Storage;
using SnagItOpen.Storage.Settings;

namespace SnagItOpen.Windows.Tests;

public sealed class SettingsDomainTests
{
    [Fact]
    public void Every_user_setting_and_capture_preset_field_has_an_editor()
    {
        WithServices(services =>
        {
            var window = new SettingsWindow(services);
            var expected = typeof(AppSettings).GetProperties().Where(p => p.Name != "Version" && !p.Name.EndsWith("Directory", StringComparison.Ordinal)).Select(p => p.Name);
            Assert.Empty(expected.Except(window.EditableSettingsFields));
            window.PresetsPage.AddPreset();
            Assert.Empty(typeof(CapturePreset).GetProperties().Select(p => p.Name).Except(window.PresetsPage.EditableFields));
            Assert.Equal(760, window.Width);
            Assert.Equal(560, window.Height);
            window.CancelChanges();
        });
    }

    [Fact]
    public void Search_finds_labels_across_pages_and_clear_restores_selected_page()
    {
        WithServices(services =>
        {
            var window = new SettingsWindow(services);
            window.SelectPage("General");
            window.SearchSettings("mouse cursor");
            Assert.Contains("Include the mouse cursor", window.VisibleSettingLabels);
            Assert.DoesNotContain("Start with Windows", window.VisibleSettingLabels);
            window.SearchSettings("background");
            Assert.Contains(window.VisibleSettingLabels, label => label.Contains("background", StringComparison.OrdinalIgnoreCase));
            window.SearchSettings("");
            Assert.Contains("Start with Windows", window.VisibleSettingLabels);
            window.CancelChanges();
        });
    }

    [Fact]
    public void Cancel_restores_actual_theme_preview_and_does_not_save_settings_or_presets()
    {
        WithServices(services =>
        {
            var theme = services.Settings.ThemeMode;
            var window = new SettingsWindow(services, previewTheme: mode => theme = mode);
            window.SelectPage("Appearance");
            var content = (FrameworkElement)window.Content;
            content.Measure(new Size(728, 500)); content.Arrange(new Rect(0, 0, 728, 500));
            var combo = Descendants(window.Content as DependencyObject ?? window).OfType<ComboBox>().Single(c => AutomationProperties.GetName(c) == "Theme");
            combo.SelectedIndex = 2;
            Assert.Equal(AppTheme.Dark, theme);
            window.PresetsPage.AddPreset();
            window.CancelChanges();
            Assert.Equal(services.Settings.ThemeMode, theme);
            Assert.Empty(services.CapturePresets.Presets);
            Assert.False(File.Exists(services.Paths.Settings));
            Assert.False(File.Exists(services.Paths.CapturePresets));
        });
    }

    [Fact]
    public void Preset_add_duplicate_rename_delete_and_reorder_persist_in_order()
    {
        WithServices(services =>
        {
            var page = new CapturePresetsPage([]);
            page.AddPreset();
            page.RenameSelected("First");
            page.DuplicateSelected();
            page.RenameSelected("Second");
            page.MoveSelected(-1);
            services.CapturePresets.Save(page.Presets);
            var reload = new CapturePresetStore(services.Paths.CapturePresets);
            reload.Load();
            Assert.Equal(new[] { "Second", "First" }, reload.Presets.Select(p => p.Name));
            page.DeleteSelected();
            services.CapturePresets.Save(page.Presets);
            reload.Load();
            Assert.Equal("First", Assert.Single(reload.Presets).Name);
        });
    }

    [Fact]
    public void File_name_template_example_updates_from_the_real_editor()
    {
        ThemeTokenTests.RunSta(() =>
        {
            var page = new CapturePresetsPage([]);
            page.AddPreset();
            page.Measure(new Size(500, 1200)); page.Arrange(new Rect(0, 0, 500, 1200));
            var template = Descendants(page).OfType<TextBox>().Single(b => AutomationProperties.GetName(b) == "File name template");
            template.Text = "Proof {n}";
            Assert.Equal("Proof {n}", Assert.Single(page.Presets).FileNameTemplate);
            Assert.Equal("Example: Proof 1.png", page.FileNameExample);
            return true;
        });
    }

    [Fact]
    public void Fractional_pixel_settings_are_invalid_before_save_and_do_not_truncate_the_draft()
    {
        ThemeTokenTests.RunSta(() =>
        {
            double draft = 640;
            var page = new SettingsPage("Capture");
            var width = page.Number("Width", "Width", draft, 1, 32767, value => draft = value);
            int changed = 0;
            page.Changed += () => changed++;
            width.Input.Text = 640.5.ToString(CultureInfo.CurrentCulture);
            Assert.False(page.NumbersValid);
            Assert.True(changed > 0);
            Assert.NotNull(page.CommitNumbers());
            Assert.Equal(640, draft);
            width.Input.Text = "641";
            Assert.True(page.NumbersValid);
            Assert.Null(page.CommitNumbers());
            Assert.Equal(641, draft);
            return true;
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Failed_preset_save_restores_settings_and_cancel_restores_preview(bool existingSettings)
    {
        WithServices(services =>
        {
            var original = services.Settings;
            if (existingSettings) services.SettingsStore.Save(original);
            Directory.CreateDirectory(services.Paths.CapturePresets);
            var theme = original.ThemeMode;
            var window = new SettingsWindow(services, previewTheme: mode => theme = mode);
            window.SelectPage("Appearance");
            var content = (FrameworkElement)window.Content;
            content.Measure(new Size(728, 500)); content.Arrange(new Rect(0, 0, 728, 500));
            var combo = Descendants(content).OfType<ComboBox>().Single(c => AutomationProperties.GetName(c) == "Theme");
            combo.SelectedIndex = 2;
            window.PresetsPage.AddPreset();
            typeof(SettingsWindow).GetMethod("Save", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
            Assert.Equal(original, services.Settings);
            Assert.Empty(services.CapturePresets.Presets);
            Assert.Equal(existingSettings, File.Exists(services.Paths.Settings));
            if (existingSettings) Assert.Equal(original.ThemeMode, services.SettingsStore.Load().Value.ThemeMode);
            Assert.Contains(Descendants(content).OfType<SnagItOpen.App.Controls.InfoBar>(), bar => bar.Title == "Settings were not saved" && bar.Visibility == Visibility.Visible);
            window.CancelChanges();
            Assert.Equal(original.ThemeMode, theme);
        });
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static void WithServices(Action<AppServices> body) => ThemeTokenTests.RunSta(() =>
    {
        string root = Path.Combine(Path.GetTempPath(), "SnagItOpenSettingsTests", Guid.NewGuid().ToString("N"));
        try { using var services = new AppServices(new AppPaths(root)); body(services); }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        return true;
    });
}
