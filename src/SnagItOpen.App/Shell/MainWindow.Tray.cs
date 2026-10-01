using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SnagItOpen.App.Editor;
using SnagItOpen.App.Infrastructure;
using SnagItOpen.App.Library;
using SnagItOpen.Core.Capture;
using SnagItOpen.Core.Documents;
using SnagItOpen.Storage.Settings;
using SnagItOpen.Windows.Shell;
using Drawing = System.Drawing;
using CaptureMode = SnagItOpen.Core.Capture.CaptureMode;

namespace SnagItOpen.App.Shell;

public partial class MainWindow
{
    private IEnumerable<TrayMenuItem> TrayItems()
    {
        TrayMenuItem Item(string title, Action run, string? icon = null, string? gesture = null) => new(title, () => Dispatcher.BeginInvoke(run), Gesture: gesture,
            ImageFactory: icon is null ? null : size => TrayBitmap(TryFindResource(icon) as Geometry, null, size));
        string Gesture(string action) => ActiveHotkeys.TryGetValue(action, out var key) ? key.ToString() : "";
        yield return Item("Open editor", ShowEditor, "Icon.Import");
        yield return TrayMenuItem.Separator;
        yield return Item("Capture region", () => _ = RunCaptureAsync(CaptureMode.Region), "Icon.Capture", Gesture(HotkeyActions.Region));
        yield return Item("Capture window", () => _ = RunCaptureAsync(CaptureMode.Window), "Icon.CaptureWindow", Gesture(HotkeyActions.Window));
        yield return Item("Capture all monitors", () => _ = RunCaptureAsync(CaptureMode.AllMonitors), "Icon.CaptureScreen", Gesture(HotkeyActions.FullScreen));
        yield return Item("Last region", () => _ = RunCaptureAsync(CaptureMode.LastRegion), "Icon.Capture", Gesture(HotkeyActions.LastRegion));
        yield return Item("Scrolling capture", () => OnScrolling(this, new RoutedEventArgs()), "Icon.CaptureScroll", Gesture(HotkeyActions.Scrolling));
        if (_services.CapturePresets.Presets.Count > 0) yield return new TrayMenuItem("Capture presets", Children: _services.CapturePresets.Presets.Select(p => Item(p.Name, () => _ = RunPresetAsync(p), "Icon.Capture", p.Hotkey)).ToArray());
        yield return TrayMenuItem.Separator;
        yield return new TrayMenuItem("Recent captures", Children: _services.History.Entries.OrderByDescending(e => e.CapturedAt).Take(5).Select(entry =>
            new TrayMenuItem(entry.DisplayName, () => Dispatcher.BeginInvoke(() => { _vm.AddLibraryAssets([ImageAsset.Create(entry.AssetId, entry.Width, entry.Height)]); ShowEditor(); FitSoon(); }),
                ImageFactory: size => TrayBitmap(null, CaptureDrag.LoadThumbnail(_services.History.ThumbnailPath(entry)), size))).ToArray());
        yield return new TrayMenuItem($"Pinned images ({PinnedImageWindow.OpenWindows.Count})", Children:
        [Item("Close all", PinnedImageWindow.CloseAll, "Icon.Close"), Item("Disable click-through for all", PinnedImageWindow.DisableClickThroughForAll, "Icon.Lock")]);
        yield return TrayMenuItem.Separator;
        yield return Item("Settings…", () => OpenSettingsPage("General"), "Icon.Settings");
        yield return Item("Keyboard shortcuts…", () => { ShowEditor(); OnShortcuts(this, new RoutedEventArgs()); }, "Icon.More", "F1");
        yield return TrayMenuItem.Separator;
        yield return Item("Quit SnagItOpen", ExitApplication, "Icon.Close");
    }
    private void UpdateTray()
    {
        if (_tray is null) return;
        _tray.RefreshMenu(TrayItems()); _tray.SetTooltip(RegionGesture() is { Length: > 0 } gesture ? "SnagItOpen: " + gesture + " to capture" : "SnagItOpen: right-click to capture");
        Drawing.Color ColorFor(string key) => TryFindResource(key) is SolidColorBrush brush ? Drawing.Color.FromArgb(brush.Color.A, brush.Color.R, brush.Color.G, brush.Color.B) : Drawing.SystemColors.WindowText;
        _tray.SetTheme(new(ColorFor("Bg.Surface"), ColorFor("Text.Primary"), ColorFor("Bg.ControlHover"), ColorFor("Stroke.Divider"), ThemeService.Current?.Effective == EffectiveTheme.HighContrast));
    }
    private Drawing.Bitmap? TrayBitmap(Geometry? geometry, ImageSource? image, int size)
    {
        if (geometry is null && image is null) return null;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            if (image is not null) dc.DrawImage(image, new Rect(0, 0, size, size));
            else if (geometry is not null && !geometry.Bounds.IsEmpty)
            {
                var bounds = geometry.Bounds; double scale = (size - 3.0) / Math.Max(bounds.Width, bounds.Height);
                dc.PushTransform(new TranslateTransform((size - bounds.Width * scale) / 2 - bounds.X * scale, (size - bounds.Height * scale) / 2 - bounds.Y * scale));
                dc.PushTransform(new ScaleTransform(scale, scale));
                dc.DrawGeometry(null, new Pen(TryFindResource("Text.Primary") as Brush ?? SystemColors.WindowTextBrush, 1.4), geometry); dc.Pop(); dc.Pop();
            }
        }
        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream(); encoder.Save(stream); stream.Position = 0;
        using var decoded = new Drawing.Bitmap(stream); return new Drawing.Bitmap(decoded);
    }
}
