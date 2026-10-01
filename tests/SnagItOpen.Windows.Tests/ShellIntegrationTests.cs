using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SnagItOpen.App.Capture;
using SnagItOpen.App.Editor;
using SnagItOpen.App.Shell;
using SnagItOpen.Core.Documents.Annotations;
using SnagItOpen.Core.Geometry;
using SnagItOpen.Storage;
using SnagItOpen.Storage.Settings;

namespace SnagItOpen.Windows.Tests;

public sealed class ShellIntegrationTests
{
    private static readonly BlockingCollection<Action> Work = new();
    private static readonly Thread UiThread = StartThread();
    private static Thread StartThread()
    {
        var thread = new Thread(() =>
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            foreach (var name in new[] { "Tokens.Light", "Metrics", "Icons", "Controls" })
                app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/SnagItOpen;component/Themes/" + name + ".xaml", UriKind.Absolute) });
            foreach (var action in Work.GetConsumingEnumerable()) action();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return thread;
    }
    private static void WithWindow(Action<MainWindow, EditorViewModel> body)
    {
        _ = UiThread;
        var result = new TaskCompletionSource();
        Work.Add(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), "SnagItOpenShellTests", Guid.NewGuid().ToString("N"));
            MainWindow? window = null; EditorViewModel? vm = null;
            try
            {
                using var services = new AppServices(new AppPaths(root));
                services.Settings = services.Settings with { Hotkeys = [], ShowTrayIcon = false, CloseToTray = false };
                var capture = new CaptureCoordinator(services); vm = new EditorViewModel(services, capture);
                window = new MainWindow(services, vm, capture, shutdownOnClose: false);
                window.Show();
                var frame = new DispatcherFrame();
                window.Dispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false); Dispatcher.PushFrame(frame);
                window.Measure(new Size(1280, 820)); window.Arrange(new Rect(0, 0, 1280, 820)); window.UpdateLayout();
                body(window, vm); vm.LoadDocument(vm.Document, null, markSaved: true); window.Close(); result.SetResult();
            }
            catch (Exception e) { result.SetException(e); }
            finally
            {
                if (window is { IsVisible: true } && vm is not null) { vm.LoadDocument(vm.Document, null, markSaved: true); window.Close(); }
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        });
        result.Task.GetAwaiter().GetResult();
    }

    [Fact]
    public void Every_menu_leaf_and_toolbar_action_is_in_the_palette()
    {
        WithWindow((window, _) =>
        {
            window.RefreshCommandRegistry();
            var menu = Assert.IsType<Menu>(window.FindName("MainMenu"));
            void Check(ItemsControl parent)
            {
                foreach (var item in parent.Items.OfType<MenuItem>())
                {
                    if (item.Items.Count > 0) Check(item);
                    else if (!(item.Header?.ToString() ?? "").StartsWith('('))
                    {
                        var id = Assert.IsType<string>(item.Tag); Assert.NotNull(window.Commands.Find(id));
                    }
                }
            }
            Check(menu);
            Assert.Equal(17, window.Commands.All.Count(c => c.Id.StartsWith("tool:")));
            Assert.NotNull(window.Commands.Find("copy-file"));
            Assert.Contains(window.Commands.All, c => c.Title == "Drag out");
            Assert.Equal(6, window.Commands.All.Count(c => c.Id.StartsWith("settings:")));
        });
    }

    [Fact]
    public void Palette_selection_guards_and_layout_binding_survive_command_execution()
    {
        WithWindow((window, vm) =>
        {
            window.RefreshCommandRegistry();
            Assert.False(window.Commands.All.Single(c => c.Title == "Undo").CanExecute());
            Assert.False(window.Commands.All.Single(c => c.Title == "Duplicate").CanExecute());
            vm.AddAnnotation(new RectangleAnnotation { Bounds = new RectD(20, 30, 100, 80) });
            window.RefreshCommandRegistry();
            Assert.True(window.Commands.All.Single(c => c.Title.StartsWith("Undo", StringComparison.Ordinal)).CanExecute());
            var menu = Assert.IsType<Menu>(window.FindName("MainMenu"));
            var layout = menu.Items.OfType<MenuItem>().Single(m => m.Header.ToString() == "_Layout");
            var horizontal = layout.Items.OfType<MenuItem>().Single(m => m.Header.ToString() == "Combine _horizontally");
            window.Commands.Find(Assert.IsType<string>(horizontal.Tag))!.Execute();
            Assert.NotNull(horizontal.GetBindingExpression(MenuItem.IsCheckedProperty));
            vm.Mode = SnagItOpen.Core.Documents.LayoutMode.Vertical; window.UpdateLayout(); Assert.False(horizontal.IsChecked);
            vm.UpdateSelectedAnnotations(a => a with { Hidden = true }, "Hide");
            Assert.Equal(Visibility.Visible, Assert.IsType<SnagItOpen.App.Controls.InfoBar>(window.FindName("HiddenWarning")).Visibility);
        });
    }

    [Fact]
    public void Registry_gestures_are_dispatched_or_registered_globally()
    {
        WithWindow((window, vm) =>
        {
            window.RefreshCommandRegistry();
            var source = File.ReadAllText(Path.Combine(ThemeTokenTests.ThemesFolder(), "..", "Shell", "MainWindow.xaml.cs"));
            var dispatch = source[source.IndexOf("private void OnPreviewKeyDown", StringComparison.Ordinal)..source.IndexOf("private void CopyAnnotationsToClipboard", StringComparison.Ordinal)];
            foreach (var gesture in window.Commands.All.Select(c => c.Gesture).Where(g => g.Length > 0).Distinct())
            {
                if (ToolCatalog.All.Any(t => t.Shortcut.ToString() == gesture)) continue;
                if (vm.Services.Settings.Hotkeys.Any(h => h.Gesture == gesture)) continue;
                var key = gesture.Split('+').Last();
                string expected = key switch { "Del" => "Delete", "]" => "OemCloseBrackets", "[" => "OemOpenBrackets", "" => "OemPlus", "-" => "OemMinus", "0" => "D0", "1" => "D1", "2" => "D2", _ => key };
                Assert.Contains("Key." + expected, dispatch);
            }
            var menu = Assert.IsType<Menu>(window.FindName("MainMenu"));
            var capture = menu.Items.OfType<MenuItem>().Single(m => m.Header.ToString() == "_Capture");
            var region = capture.Items.OfType<MenuItem>().Single(m => m.Header.ToString() == "_Region");
            region.InputGestureText = "Ctrl+Shift+R";
            window.RefreshCommandRegistry();
            Assert.Equal("", region.InputGestureText);
            Assert.Equal("", window.Commands.Find(Assert.IsType<string>(region.Tag))!.Gesture);
        });
    }

    [Fact]
    public void Loaded_shell_controls_have_names_in_each_theme_and_narrow_layout()
    {
        WithWindow((window, _) =>
        {
            foreach (var theme in new[] { "Light", "Dark", "HighContrast" })
            {
                using var stream = File.OpenRead(Path.Combine(ThemeTokenTests.ThemesFolder(), "Tokens." + theme + ".xaml"));
                window.Resources.MergedDictionaries.Clear(); window.Resources.MergedDictionaries.Add((ResourceDictionary)System.Windows.Markup.XamlReader.Load(stream));
                Assert.Same(window.FindResource("Text.Primary"), window.Foreground);
                Assert.Same(window.FindResource("Bg.Window"), Assert.IsType<Menu>(window.FindName("MainMenu")).Background);
                foreach (var width in new[] { 1280, 1000, 800, 640 })
                {
                    window.Width = width; window.Measure(new Size(width, 820)); window.Arrange(new Rect(0, 0, width, 820)); window.UpdateLayout();
                    typeof(MainWindow).GetMethod("NameControls", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [window]);
                    foreach (var control in Descendants(window).OfType<FrameworkElement>().Where(c => c.Focusable && c.IsVisible))
                        Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(control)), control.GetType().Name + " " + control.Name);
                    var canvas = Assert.IsType<CanvasView>(window.FindName("Canvas"));
                    typeof(CanvasView).GetMethod("OnThemeChanged", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)!.Invoke(canvas, [null, EventArgs.Empty]);
                    canvas.UpdateLayout();
                    string? output = Environment.GetEnvironmentVariable("SNAGITOPEN_EVIDENCE");
                    if (!string.IsNullOrWhiteSpace(output))
                    {
                        Directory.CreateDirectory(output);
                        foreach (var scale in new[] { 1.0, 1.5, 2.0 })
                        {
                            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth * scale), (int)Math.Ceiling(window.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
                            bitmap.Render(window); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
                            using var file = File.Create(Path.Combine(output, $"shell-{theme}-{width}-{scale * 100:0}.png")); png.Save(file);
                        }
                    }
                }
            }
        });
    }

    [Fact]
    public void Button_icons_are_never_clipped_by_padding_at_any_width()
    {
        WithWindow((window, _) =>
        {
            foreach (var width in new[] { 1280, 800, 640 })
            {
                window.Width = width; window.Measure(new Size(width, 820)); window.Arrange(new Rect(0, 0, width, 820)); window.UpdateLayout();
                foreach (var button in Descendants(window).OfType<System.Windows.Controls.Primitives.ButtonBase>().Where(b => b.IsVisible))
                    foreach (var icon in Descendants(button).OfType<System.Windows.Shapes.Path>().Where(p => p.IsVisible && p.Data is not null))
                    {
                        var bounds = icon.TransformToAncestor(button).TransformBounds(new Rect(icon.RenderSize));
                        var inner = new Rect(button.Padding.Left + button.BorderThickness.Left, 0, Math.Max(0, button.ActualWidth - button.Padding.Left - button.Padding.Right - button.BorderThickness.Left - button.BorderThickness.Right), button.ActualHeight);
                        Assert.True(bounds.Left >= -0.5 && bounds.Right <= button.ActualWidth + 0.5 && bounds.Width <= inner.Width + 0.5,
                            $"{AutomationProperties.GetName(button)} at {width}: icon {bounds.Width:0.#} wide, content box {inner.Width:0.#}");
                    }
            }
        });
    }

    [Fact]
    public void Inspector_keeps_its_tree_during_property_edits_and_canvas_escape_then_tab_leaves()
    {
        WithWindow((window, vm) =>
        {
            var inspector = Assert.IsType<InspectorPanel>(Assert.IsType<ContentControl>(window.FindName("InspectorHost")).Content);
            vm.AddAnnotation(new RectangleAnnotation { Bounds = new RectD(10, 10, 80, 60) });
            int builds = inspector.RebuildCount;
            for (int i = 0; i < 10; i++) { vm.UpdateSelectedAnnotations(a => a with { StrokeWidth = 2 + i }, "Stroke"); inspector.Refresh(); }
            Assert.Equal(builds, inspector.RebuildCount);
            var canvas = Assert.IsType<CanvasView>(window.FindName("Canvas"));
            var method = typeof(MainWindow).GetMethod("OnPreviewKeyDown", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)!;
            var source = PresentationSource.FromVisual(canvas);
            var escape = new KeyEventArgs(Keyboard.PrimaryDevice, source!, 0, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            method.Invoke(window, [window, escape]); Assert.Empty(vm.SelectedAnnotations);
            var tab = new KeyEventArgs(Keyboard.PrimaryDevice, source!, 0, Key.Tab) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            method.Invoke(window, [window, tab]); Assert.False(tab.Handled);
        });
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i); yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}
