using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using SnagItOpen.App.Capture;
using SnagItOpen.App.Editor;
using SnagItOpen.App.Shell;
using SnagItOpen.Core.Capture;
using SnagItOpen.Core.Geometry;
using SnagItOpen.Imaging;
using SnagItOpen.Storage;
using SnagItOpen.Storage.History;
using SnagItOpen.Windows.Capture;

namespace SnagItOpen.Windows.Tests;

/// <summary>Real HWND and routed-input coverage for capture result, library and pin recovery.</summary>
[Collection("Process resources")]
public sealed class CaptureWindowIntegrationTests
{
    [Fact]
    public void Capture_toast_keeps_foreground_and_is_excluded_inside_capture_monitor_workarea()
    {
        WithServices((services, _) =>
        {
            var monitor = services.Monitors.GetMonitors().First();
            var item = new CaptureItem(PixelBuffer.Solid(12, 8, Rgba32.White), monitor.Bounds);
            int edits = 0, pins = 0;
            var window = Create("DesktopToastWindow", services, item, "Copied", (Action)(() => edits++), (Action)(() => pins++), null);
            try
            {
                AddResources(window);
                var foreground = GetForegroundWindow();
                window.Show(); Pump();
                AssertPassiveExcluded(window);
                Assert.False(window.IsActive);
                Assert.Equal(foreground, GetForegroundWindow());
                Assert.True(GetWindowRect(new WindowInteropHelper(window).Handle, out var rect));
                Assert.True(monitor.WorkArea.Contains(PixelRect.FromEdges(rect.Left, rect.Top, rect.Right, rect.Bottom)));
                Click(window, "Pin");
                Assert.Equal(1, pins); Assert.Equal(0, edits); Assert.False(window.IsVisible);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void Error_toast_details_runs_only_on_explicit_click_without_initial_activation()
    {
        WithServices((services, _) =>
        {
            int details = 0;
            var foreground = GetForegroundWindow();
            var window = (Window)TypeFor("DesktopToastWindow").GetMethod("ShowError")!.Invoke(null,
                [services, "Could not save the image.", (Action)(() => details++)])!;
            try
            {
                AddResources(window); Pump();
                AssertPassiveExcluded(window);
                Assert.Equal(foreground, GetForegroundWindow()); Assert.Equal(0, details);
                Click(window, "Details");
                Assert.Equal(1, details); Assert.False(window.IsVisible);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void Pin_recovery_remains_clickable_hides_with_pin_and_restores_input()
    {
        WithServices((services, _) =>
        {
            var type = TypeFor("PinnedImageWindow");
            var window = Create("PinnedImageWindow", PixelBuffer.Solid(120, 80, Rgba32.White).ToBitmap(), "Fixture", services, null);
            AddResources(window); window.ShowActivated = false;
            try
            {
                window.Show(); Pump();
                type.GetMethod("SetClickThrough")!.Invoke(window, [true]); Pump();
                Assert.True((bool)type.GetProperty("IsClickThrough")!.GetValue(window)!);
                Assert.True((ExtendedStyle(window) & 0x20) != 0);
                var tab = Field<Window>(window, "_recoveryTab");
                Assert.True(tab.IsVisible); AssertPassiveExcluded(tab);
                Assert.Equal(0, ExtendedStyle(tab) & 0x20);
                type.GetMethod("ResizeAt", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [1.5, new Point(0, 0)]);
                double zoomedWidth = window.Width;
                window.Hide(); Pump(); Assert.False(tab.IsVisible);
                window.Show(); Pump(); Assert.True(tab.IsVisible);
                Assert.Equal(zoomedWidth, window.Width); Assert.Equal(1.5, Field<double>(window, "_zoom"));
                window.WindowState = WindowState.Minimized; Pump(); Assert.False(tab.IsVisible);
                window.WindowState = WindowState.Normal; Pump(); Assert.True(tab.IsVisible);
                Assert.Equal(zoomedWidth, window.Width); Assert.Equal(1.5, Field<double>(window, "_zoom"));
                Assert.Single(((System.Collections.IEnumerable)type.GetProperty("OpenWindows")!.GetValue(null)!).Cast<Window>(), w => ReferenceEquals(w, window));
                Assert.IsType<Button>(tab.Content).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
                Assert.False((bool)type.GetProperty("IsClickThrough")!.GetValue(window)!);
                Assert.Equal(0, ExtendedStyle(window) & 0x20); Assert.False(tab.IsVisible);
            }
            finally { window.Close(); }
            type.GetMethod("SetClickThrough")!.Invoke(window, [true]);
            Assert.DoesNotContain(window, ((System.Collections.IEnumerable)type.GetProperty("OpenWindows")!.GetValue(null)!).Cast<Window>());
        });
    }

    [Fact]
    public void Stale_region_notice_is_scoped_to_selection_and_clears_on_cancel_or_commit()
    {
        WithServices((services, _) =>
        {
            var assembly = typeof(CanvasView).Assembly;
            var type = assembly.GetType("SnagItOpen.App.Capture.CaptureOverlayState")!;
            var picker = Enum.Parse(assembly.GetType("SnagItOpen.App.Capture.CapturePicker")!, "None");
            foreach (bool cancel in new[] { true, false })
            {
                var selection = new RegionSelection(new PixelRect(0, 0, 100, 100));
                var state = Activator.CreateInstance(type, services, selection, SnagItOpen.Core.Capture.CaptureMode.Region,
                    Array.Empty<WindowInfo>(), services.Monitors.GetMonitors(), picker)!;
                var notice = type.GetProperty("Notice")!;
                Assert.Null(notice.GetValue(state));
                notice.SetValue(state, "The display layout changed. Select a new region.");
                Assert.NotNull(notice.GetValue(state));
                if (cancel) selection.Cancel(); else selection.Confirm(new PixelRect(10, 10, 20, 20));
                Assert.Null(notice.GetValue(state));
            }
        });
    }

    [Fact]
    public void Library_search_calendar_pinned_context_and_view_persistence_use_real_controls()
    {
        WithServices((services, vm) =>
        {
            var today = AddEntry(services, "Today's diagram", DateTimeOffset.Now, pinned: false, color: new Rgba32(40, 50, 60, 255));
            var old = AddEntry(services, "Old diagram", DateTimeOffset.Now.AddDays(-8), pinned: true, color: new Rgba32(70, 80, 90, 255));
            var window = Create("LibraryWindow", services, vm, null);
            AddResources(window); window.ShowActivated = false;
            try
            {
                window.Show(); Pump();
                var list = Field<ListBox>(window, "_list"); var search = Field<TextBox>(window, "_search");
                Assert.Equal(2, list.Items.Count);
                search.Text = "Old 3x2"; Assert.Equal(old.Id, Entry(Assert.Single(list.Items.Cast<object>())).Id);
                search.Clear();
                Descendants(window).OfType<RadioButton>().Single(b => Equals(b.Content, "Today")).IsChecked = true;
                Assert.Equal(today.Id, Entry(Assert.Single(list.Items.Cast<object>())).Id);
                Descendants(window).OfType<RadioButton>().Single(b => Equals(b.Content, "Pinned")).IsChecked = true;
                Assert.Equal(old.Id, Entry(Assert.Single(list.Items.Cast<object>())).Id);
                list.SelectedIndex = 0;
                Assert.Single(list.SelectedItems.Cast<object>());
                list.ContextMenu!.Items.OfType<MenuItem>().Single(i => Equals(i.Header, "Pin / unpin in library"))
                    .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); Pump();
                Assert.False(services.History.Entries.Single(e => e.Id == old.Id).Pinned);
                Assert.Empty(list.Items.Cast<object>());
                Click(window, "List"); Assert.Equal("List", services.UiState.LibraryView);
                Assert.Equal("List", services.UiStateStore.Load().Value.LibraryView);
                Descendants(window).OfType<RadioButton>().Single(b => Equals(b.Content, "All")).IsChecked = true;
                Field<ComboBox>(window, "_sort").SelectedItem = "Oldest";
                Assert.Equal(old.Id, Entry(list.Items[0]).Id);
                Assert.Equal("Oldest", services.UiStateStore.Load().Value.LibrarySort);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void Library_enter_space_and_delete_preserve_multiselect_and_composition_assets()
    {
        WithServices((services, vm) =>
        {
            var first = AddEntry(services, "One", DateTimeOffset.Now, false, new Rgba32(20, 30, 40, 255));
            var second = AddEntry(services, "Two", DateTimeOffset.Now.AddDays(-1), false, new Rgba32(50, 60, 70, 255));
            services.SaveUiState(services.UiState with { DontAskAgain = ["DeleteLibraryCaptures"] });
            var window = Create("LibraryWindow", services, vm, null);
            AddResources(window); window.ShowActivated = false;
            try
            {
                window.Show(); Pump(); var list = Field<ListBox>(window, "_list");
                list.SelectedIndex = 0;
                Assert.True(Key(list, System.Windows.Input.Key.Space).Handled); Pump();
                var preview = Field<Window>(window, "_largePreview"); Assert.True(preview.IsVisible);
                Assert.True(Key(list, System.Windows.Input.Key.Space).Handled); Assert.False(preview.IsVisible);
                list.SelectedItems.Add(list.Items[1]);
                Assert.Equal(2, list.SelectedItems.Count);
                Assert.True(Key(list, System.Windows.Input.Key.Enter).Handled);
                Assert.Equal(2, vm.Document.Images.Length); Assert.Equal(2, list.SelectedItems.Count);
                Assert.True(Key(list, System.Windows.Input.Key.Delete).Handled); Pump();
                Assert.Empty(services.History.Entries); Assert.Empty(list.Items.Cast<object>());
                Assert.Equal(2, vm.Document.Images.Length);
                Assert.True(services.Assets.Contains(first.AssetId)); Assert.True(services.Assets.Contains(second.AssetId));
            }
            finally { window.Close(); }
        });
    }

    private static CaptureEntry AddEntry(AppServices services, string name, DateTimeOffset date, bool pinned, Rgba32 color)
    {
        var pixels = PixelBuffer.Solid(3, 2, color);
        var asset = services.Importer.ImportPixelsAsync(pixels).GetAwaiter().GetResult();
        var entry = services.History.Add(asset, pixels.EncodePng());
        services.History.Update(entry.Id, e => e with { Name = name, CapturedAt = date, Pinned = pinned });
        return services.History.Entries.Single(e => e.Id == entry.Id);
    }
    private static CaptureEntry Entry(object row) => (CaptureEntry)row.GetType().GetProperty("Entry")!.GetValue(row)!;
    private static Type TypeFor(string name) => typeof(CanvasView).Assembly.GetType("SnagItOpen.App.Shell." + name)!;
    private static Window Create(string name, params object?[] args) => (Window)Activator.CreateInstance(TypeFor(name), args)!;
    private static T Field<T>(object instance, string name) => (T)instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance)!;
    private static KeyEventArgs Key(UIElement target, System.Windows.Input.Key key)
    {
        var e = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(target)!, 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        target.RaiseEvent(e); return e;
    }
    private static void Click(Window window, string text) => Descendants(window).OfType<Button>().Single(b => Equals(b.Content, text)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i); yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
    private static void AddResources(Window window)
    {
        foreach (string name in new[] { "Tokens.Light", "Metrics", "Icons", "Controls" })
        {
            using var stream = File.OpenRead(Path.Combine(ThemeTokenTests.ThemesFolder(), name + ".xaml"));
            window.Resources.MergedDictionaries.Add((ResourceDictionary)XamlReader.Load(stream));
        }
    }
    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }
    private static void WithServices(Action<AppServices, EditorViewModel> body) => ThemeTokenTests.RunSta(() =>
    {
        string root = Path.Combine(Path.GetTempPath(), "SnagItOpenCaptureWindowTests", Guid.NewGuid().ToString("N"));
        try
        {
            using var services = new AppServices(new AppPaths(root));
            var vm = new EditorViewModel(services, new CaptureCoordinator(services));
            body(services, vm);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        return true;
    });
    private static long ExtendedStyle(Window window) => GetWindowLongPtr(new WindowInteropHelper(window).Handle, -20).ToInt64();
    private static void AssertPassiveExcluded(Window window)
    {
        Assert.False(window.ShowActivated);
        Assert.True((ExtendedStyle(window) & 0x08000000) != 0);
        Assert.True(GetWindowDisplayAffinity(new WindowInteropHelper(window).Handle, out uint affinity));
        Assert.Equal(0x11u, affinity);
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowDisplayAffinity(IntPtr hwnd, out uint affinity);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
}
