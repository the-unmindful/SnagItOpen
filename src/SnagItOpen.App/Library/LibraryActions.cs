using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using SnagItOpen.App.Editor;
using SnagItOpen.App.Infrastructure;
using SnagItOpen.App.Shell;
using SnagItOpen.Core.Capture;
using SnagItOpen.Core.Documents;
using SnagItOpen.Core.Geometry;
using SnagItOpen.Imaging;
using SnagItOpen.Storage.History;

namespace SnagItOpen.App.Library;

internal static class LibraryActions
{
    public static void Add(AppServices services, EditorViewModel vm, IEnumerable<CaptureEntry> entries)
    {
        var assets = entries.Where(e => services.Assets.Contains(e.AssetId)).Select(e => ImageAsset.Create(e.AssetId, e.Width, e.Height)).ToList();
        if (assets.Count > 0) vm.AddLibraryAssets(assets);
    }

    public static void OpenNew(Window owner, AppServices services, EditorViewModel vm, IEnumerable<CaptureEntry> entries)
    {
        var selected = entries.ToList();
        if (selected.Count == 0) return;
        if (vm.IsDirty && Dialogs.Confirm(owner, "Open capture", "Replace the current composition? Unsaved changes will be discarded.", "Open capture", null) != MessageBoxResult.Yes) return;
        vm.NewDocument();
        Add(services, vm, selected);
        Application.Current.MainWindow?.Show();
        Application.Current.MainWindow?.Activate();
    }

    public static async Task CopyAsync(Window owner, AppServices services, CaptureEntry? entry)
    {
        if (entry is null) return;
        try
        {
            var bytes = await File.ReadAllBytesAsync(services.Assets.PathFor(entry.AssetId));
            var result = await services.Clipboard.CopyImageAsync(PixelBuffer.DecodeImage(bytes).FlattenOnto(Rgba32.White).ToBitmap(), bytes);
            if (!result.IsSuccess) Dialogs.Error(owner, result.Message ?? "Could not copy the capture.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException) { Dialogs.Error(owner, ex.Message); }
    }

    public static void Pin(Window owner, AppServices services, EditorViewModel vm, CaptureEntry? entry)
    {
        if (entry is null) return;
        try
        {
            var bitmap = PixelBuffer.DecodeImage(File.ReadAllBytes(services.Assets.PathFor(entry.AssetId))).ToBitmap();
            new PinnedImageWindow(bitmap, entry.DisplayName, services, () => Add(services, vm, [entry])).Show();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException) { Dialogs.Error(owner, ex.Message); }
    }

    public static void Delete(Window owner, AppServices services, EditorViewModel vm, IEnumerable<CaptureEntry> entries)
    {
        var selected = entries.ToList();
        if (selected.Count == 0) return;
        const string flag = "DeleteLibraryCaptures";
        if (!services.UiState.DontAskAgain.Contains(flag))
        {
            var content = new StackPanel();
            content.Children.Add(new TextBlock { Text = $"Delete {selected.Count} capture(s) from the library? Images used by open compositions and recovery drafts are kept.", TextWrapping = TextWrapping.Wrap, MaxWidth = 460 });
            var skip = new CheckBox { Content = "Don't ask again", Margin = new Thickness(0, 12, 0, 0) };
            System.Windows.Automation.AutomationProperties.SetName(skip, "Don't ask again before deleting library captures");
            content.Children.Add(skip);
            if (new DialogWindow(owner, "Delete captures", content, "Delete").ShowDialog() != true) return;
            if (skip.IsChecked == true) services.SaveUiState(services.UiState with { DontAskAgain = [.. services.UiState.DontAskAgain, flag] });
        }
        var protect = vm.ProtectedAssets();
        protect.UnionWith(services.Recovery.ReferencedAssets());
        services.History.Delete(selected.Select(e => e.Id).ToArray(), protect);
    }

    public static void Reveal(AppServices services, CaptureEntry? entry)
    {
        if (entry is null) return;
        string path = services.Assets.PathFor(entry.AssetId);
        if (!File.Exists(path)) return;
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false }); }
        catch (System.ComponentModel.Win32Exception ex) { services.Log(ex.Message); }
    }
}
