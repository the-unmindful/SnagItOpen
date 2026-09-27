using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media.Imaging;
using SnagItOpen.App.Capture;
using SnagItOpen.App.Infrastructure;
using SnagItOpen.App.Shell;
using SnagItOpen.Core;
using SnagItOpen.Core.Capture;
using SnagItOpen.Core.Documents;
using SnagItOpen.Core.Documents.Annotations;
using SnagItOpen.Core.Editing;
using SnagItOpen.Core.Geometry;
using SnagItOpen.Core.Layout;
using SnagItOpen.Imaging;
using SnagItOpen.Storage.Settings;

namespace SnagItOpen.App.Editor;

/// <summary>Row in the ordered image list (layout order, numbered from 1).</summary>
public sealed class ImageListItemViewModel : ObservableObject
{
    public ImageListItemViewModel(ImageLayer layer, int number, BitmapSource? thumbnail)
    {
        Layer = layer;
        Number = number;
        Thumbnail = thumbnail;
    }

    public ImageLayer Layer { get; }
    public Guid Id => Layer.Id;
    public int Number { get; }
    public BitmapSource? Thumbnail { get; }
    public string Title => Layer.Name ?? $"Image {Number}";
    public string Dimensions => $"{Layer.Bounds.Width} × {Layer.Bounds.Height}" + (Layer.Visible ? "" : " (hidden)");
    public string AccessibleName => $"{Number}. {Title}, {Dimensions}";
}

/// <summary>
/// Editor state: the document history, selection, file path and every user command. Document changes
/// go through <see cref="Commit"/> so each edit is one validated, undoable step.
/// </summary>
public sealed class EditorViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly CaptureCoordinator _capture;
    private string? _status = "Drop images here or paste a screenshot.";
    private bool _busy;
    private string? _projectPath;
    private DocumentState _savedState;
    private HashSet<Guid> _selectedImages = [];
    private HashSet<Guid> _selectedAnnotations = [];
    private CancellationTokenSource? _importCts;

    public EditorViewModel(AppServices services, CaptureCoordinator capture)
    {
        _services = services;
        _capture = capture;
        var initial = DocumentOps.Reflow(DocumentState.CreateEmpty() with
        {
            Layout = services.Settings.DefaultLayout,
            Background = services.Settings.DefaultBackground,
        });
        History = new History(initial);
        _savedState = initial;
        History.Changed += OnDocumentChanged;
        RebuildList();
    }

    public History History { get; }
    public DocumentState Document => History.Current;
    public AppServices Services => _services;
    public ObservableCollection<ImageListItemViewModel> Images { get; } = [];

    /// <summary>Raised whenever the canvas should redraw (document, selection or preview changes).</summary>
    public event Action? CanvasInvalidated;
    /// <summary>Raised for errors that deserve a dialog rather than only a status message.</summary>
    public event Action<string>? ErrorRaised;
    /// <summary>Raised after the selection changes (the image list mirrors it).</summary>
    public event Action? SelectionChanged;

    /// <summary>Transient preview shown during a gesture; null when showing the committed document.</summary>
    public DocumentState? PreviewState { get; private set; }
    public DocumentState Displayed => PreviewState ?? Document;

    public string? Status { get => _status; set => Set(ref _status, value); }
    public bool IsBusy { get => _busy; private set { if (Set(ref _busy, value)) System.Windows.Input.CommandManager.InvalidateRequerySuggested(); } }
    public bool IsEmpty => Document.Images.Length == 0 && Document.Annotations.Length == 0;
    public bool HasContent => Document.HasVisibleContent;
    public bool IsDirty => !ReferenceEquals(Document, _savedState);
    public string? ProjectPath => _projectPath;
    public string Title => $"{(_projectPath is null ? Document.Name : Path.GetFileNameWithoutExtension(_projectPath))}{(IsDirty ? " *" : "")} - SnagItOpen";
    public string CanvasSize => $"{Document.ExportArea.Width} × {Document.ExportArea.Height} px";
    public string? UndoLabel => History.UndoLabel is { } l ? $"Undo {l}" : "Undo";
    public string? RedoLabel => History.RedoLabel is { } l ? $"Redo {l}" : "Redo";

    public IReadOnlySet<Guid> SelectedImages => _selectedImages;
    public IReadOnlySet<Guid> SelectedAnnotations => _selectedAnnotations;
    public Guid? PrimaryImage => _selectedImages.Count == 1 ? _selectedImages.First() : null;
    public Annotation? PrimaryAnnotation => _selectedAnnotations.Count == 1 ? Document.FindAnnotation(_selectedAnnotations.First()) : null;
    public ImageLayer? SelectedLayer => PrimaryImage is { } id ? Document.FindImage(id) : null;

    // ------------------------------------------------------------------ layout properties (bound by the panel)

    public LayoutMode Mode
    {
        get => Document.Layout.Mode;
        set { if (value != Document.Layout.Mode) Commit(value switch { LayoutMode.Free => "Free canvas", LayoutMode.Vertical => "Combine vertically", _ => "Combine horizontally" }, d => DocumentOps.SetMode(d, value)); }
    }

    public bool IsVertical { get => Mode == LayoutMode.Vertical; set { if (value) Mode = LayoutMode.Vertical; } }
    public bool IsHorizontal { get => Mode == LayoutMode.Horizontal; set { if (value) Mode = LayoutMode.Horizontal; } }
    public bool IsFree { get => Mode == LayoutMode.Free; set { if (value) Mode = LayoutMode.Free; } }
    public bool IsAutoLayout => Mode != LayoutMode.Free;

    public int Gap
    {
        get => Document.Layout.Gap;
        set => SetLayoutValue("Gap", o => o with { Gap = value }, value is >= 0 and <= Limits.MaxGap, $"Gap must be 0–{Limits.MaxGap}.");
    }

    public int Padding
    {
        get => Document.Layout.Padding;
        set => SetLayoutValue("Padding", o => o with { Padding = value }, value is >= 0 and <= Limits.MaxPadding, $"Padding must be 0–{Limits.MaxPadding}.");
    }

    public CrossAlignment Alignment
    {
        get => Document.Layout.Alignment;
        set => SetLayoutValue("Alignment", o => o with { Alignment = value }, Enum.IsDefined(value), "Invalid alignment.");
    }

    public bool MatchSize
    {
        get => Document.Layout.Scale == ScaleMode.MatchCrossAxis;
        set => SetLayoutValue(value ? "Match size" : "Original size", o => o with { Scale = value ? ScaleMode.MatchCrossAxis : ScaleMode.Original }, true, "");
    }

    public bool AllowUpscale
    {
        get => Document.Layout.AllowUpscale;
        set => SetLayoutValue("Upscale", o => o with { AllowUpscale = value }, true, "");
    }

    /// <summary>Explicit match target; 0 means "largest image".</summary>
    public int TargetSize
    {
        get => Document.Layout.TargetCrossPixels ?? 0;
        set => SetLayoutValue("Target size", o => o with { TargetCrossPixels = value <= 0 ? null : value }, value is >= 0 and <= Limits.MaxDimension, $"Target must be 0–{Limits.MaxDimension}.");
    }

    public string MatchLabel => Mode == LayoutMode.Horizontal ? "Match height" : "Match width";

    public string BackgroundHex
    {
        get => Document.Background.ToHex();
        set
        {
            if (!Rgba32.TryParse(value, out var c)) { Status = "Background must be #RRGGBB or #RRGGBBAA."; OnPropertyChanged(); return; }
            if (c != Document.Background) Commit("Background", d => DocumentOps.SetBackground(d, c));
        }
    }

    public bool TransparentBackground
    {
        get => Document.Background.A == 0;
        set => Commit("Background", d => DocumentOps.SetBackground(d, value ? Rgba32.Transparent : Rgba32.White));
    }

    public bool AutoCanvas => Document.AutoCanvas;

    private void SetLayoutValue(string label, Func<LayoutOptions, LayoutOptions> change, bool valid, string error)
    {
        if (!valid) { Status = error; OnAllPropertiesChanged(); return; }
        var next = change(Document.Layout);
        if (next == Document.Layout) return;
        Commit(label, d => DocumentOps.SetLayout(d, next));
    }

    // ------------------------------------------------------------------ core commit / preview

    /// <summary>Validates and records one edit. Errors become status messages and leave the document unchanged.</summary>
    public bool Commit(string label, Func<DocumentState, DocumentState> edit)
    {
        try
        {
            var next = edit(Document);
            if (ReferenceEquals(next, Document)) return false;
            return History.Execute(label, next);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or LayoutLimitException or InvalidDocumentException or OverflowException)
        {
            Status = ex.Message;
            OnAllPropertiesChanged();
            return false;
        }
    }

    public void SetPreview(DocumentState? preview)
    {
        PreviewState = preview;
        CanvasInvalidated?.Invoke();
    }

    public void Undo() { if (History.Undo()) Status = "Undone."; }
    public void Redo() { if (History.Redo()) Status = "Redone."; }

    private void OnDocumentChanged(DocumentState doc)
    {
        PreviewState = null;
        _selectedImages.RemoveWhere(id => doc.FindImage(id) is null);
        _selectedAnnotations.RemoveWhere(id => doc.FindAnnotation(id) is null);
        RebuildList();
        if (IsDirty && !IsEmpty) _services.Autosave.NotifyChanged(doc, _projectPath);
        OnAllPropertiesChanged();
        CanvasInvalidated?.Invoke();
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
    }

    private void RebuildList()
    {
        Images.Clear();
        int n = 1;
        foreach (var l in Document.ImagesInLayoutOrder())
        {
            BitmapSource? thumb = null;
            try { thumb = _services.Cache.GetThumbnail(l.AssetId, 96); } catch (AssetNotFoundException) { }
            Images.Add(new ImageListItemViewModel(l, n++, thumb));
        }
    }

    // ------------------------------------------------------------------ selection

    public void Select(IEnumerable<Guid> images, IEnumerable<Guid>? annotations = null)
    {
        _selectedImages = images.ToHashSet();
        _selectedAnnotations = (annotations ?? []).ToHashSet();
        // Numeric geometry, crop and annotation fields all depend on the selection.
        OnAllPropertiesChanged();
        SelectionChanged?.Invoke();
        CanvasInvalidated?.Invoke();
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
    }

    public void ToggleImage(Guid id)
    {
        var s = _selectedImages.ToHashSet();
        if (!s.Add(id)) s.Remove(id);
        Select(s, _selectedAnnotations);
    }

    public void ClearSelection() => Select([], []);

    public void SelectAll() => Select(Document.Images.Where(i => i.Visible).Select(i => i.Id), Document.Annotations.Select(a => a.Id));

    public bool HasSelection => _selectedImages.Count > 0 || _selectedAnnotations.Count > 0;

    public string SelectionInfo
    {
        get
        {
            if (SelectedLayer is { } l) return $"({l.Bounds.X}, {l.Bounds.Y})  {l.Bounds.Width} × {l.Bounds.Height}";
            if (PrimaryAnnotation is { } a) return $"{a.Kind}";
            int n = _selectedImages.Count + _selectedAnnotations.Count;
            return n == 0 ? "" : $"{n} selected";
        }
    }

    // ------------------------------------------------------------------ numeric geometry for the selected image

    public int SelX { get => SelectedLayer?.Bounds.X ?? 0; set => EditBounds(b => b with { X = value }); }
    public int SelY { get => SelectedLayer?.Bounds.Y ?? 0; set => EditBounds(b => b with { Y = value }); }
    public int SelWidth { get => SelectedLayer?.Bounds.Width ?? 0; set => EditBounds(b => ResizeGeometry.WithWidth(b, value, KeepAspect)); }
    public int SelHeight { get => SelectedLayer?.Bounds.Height ?? 0; set => EditBounds(b => ResizeGeometry.WithHeight(b, value, KeepAspect)); }
    public bool KeepAspect { get; set; } = true;

    private void EditBounds(Func<PixelRect, PixelRect> f)
    {
        if (SelectedLayer is not { } l) return;
        var nb = f(l.Bounds);
        if (nb == l.Bounds) return;
        Commit("Resize", d => DocumentOps.SetBounds(d, l.Id, nb));
    }

    public int CropLeft { get => SelectedLayer?.SourceCrop.X ?? 0; set => EditCrop(c => PixelRect.FromEdges(value, c.Y, c.Right, c.Bottom)); }
    public int CropTop { get => SelectedLayer?.SourceCrop.Y ?? 0; set => EditCrop(c => PixelRect.FromEdges(c.X, value, c.Right, c.Bottom)); }
    public int CropRight { get => SelectedLayer?.SourceCrop.Right ?? 0; set => EditCrop(c => PixelRect.FromEdges(c.X, c.Y, value, c.Bottom)); }
    public int CropBottom { get => SelectedLayer?.SourceCrop.Bottom ?? 0; set => EditCrop(c => PixelRect.FromEdges(c.X, c.Y, c.Right, value)); }

    private void EditCrop(Func<PixelRect, PixelRect> f)
    {
        if (SelectedLayer is not { } l) return;
        var c = f(l.SourceCrop);
        if (c.Width < 1 || c.Height < 1) { Status = "Crop must keep at least one pixel."; OnAllPropertiesChanged(); return; }
        Commit("Crop", d => DocumentOps.Crop(d, l.Id, c));
    }

    // ------------------------------------------------------------------ import / paste

    public async Task ImportFilesAsync(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return;
        _importCts?.Cancel();
        var cts = _importCts = new CancellationTokenSource();
        IsBusy = true;
        var ok = new List<(ImageAsset, ImageLayer)>();
        var failed = new List<string>();
        try
        {
            for (int i = 0; i < paths.Count; i++)
            {
                Status = $"Importing {i + 1} of {paths.Count}…";
                var r = await _services.Importer.ImportFileAsync(paths[i], cts.Token);
                if (r.IsCanceled) { Status = "Import canceled."; break; }
                if (r.IsSuccess) ok.Add((r.Value!.Asset, NewLayer(r.Value.Asset, r.Value.Name, ok.Count)));
                else failed.Add(r.Message ?? Path.GetFileName(paths[i]));
            }
            if (ok.Count > 0)
            {
                Commit(ok.Count == 1 ? "Import image" : $"Import {ok.Count} images", d => DocumentOps.AddImages(d, ok));
                _services.Settings = _services.Settings with { LastImportDirectory = Path.GetDirectoryName(paths[^1]) };
            }
            ReportBatch(ok.Count, failed);
        }
        finally
        {
            if (ReferenceEquals(_importCts, cts)) _importCts = null;
            IsBusy = false;
        }
    }

    public void CancelImport() => _importCts?.Cancel();

    private void ReportBatch(int ok, List<string> failed)
    {
        if (failed.Count == 0) { if (ok > 0) Status = ok == 1 ? "Imported 1 image." : $"Imported {ok} images."; return; }
        Status = $"Imported {ok}, failed {failed.Count}: {string.Join("; ", failed.Take(3))}";
        if (ok == 0 || failed.Count > 0) ErrorRaised?.Invoke($"{failed.Count} file(s) could not be imported:\n\n{string.Join("\n", failed.Take(10))}");
    }

    /// <summary>
    /// New layer for an asset. Free mode places it beside the current content (or at the viewport center
    /// supplied by the view); layout modes reflow anyway.
    /// </summary>
    private ImageLayer NewLayer(ImageAsset asset, string? name, int batchIndex)
    {
        var d = Document;
        var p = new PixelPoint(0, 0);
        if (d.Layout.Mode == LayoutMode.Free)
        {
            if (FreePlacementHint is { } hint) p = new PixelPoint(hint.X - asset.Width / 2 + batchIndex * 24, hint.Y - asset.Height / 2 + batchIndex * 24);
            else
            {
                var c = DocumentBounds.ContentBounds(d);
                p = c.IsEmpty ? new PixelPoint(batchIndex * 24, batchIndex * 24) : new PixelPoint((int)Math.Ceiling(c.Right) + 16, (int)Math.Floor(c.Y) + batchIndex * 24);
            }
        }
        return ImageLayer.ForAsset(asset, p.X, p.Y, name);
    }

    /// <summary>Document point at the viewport center, set by the canvas view.</summary>
    public PixelPoint? FreePlacementHint { get; set; }

    public async Task PasteAsync()
    {
        var r = await _services.Clipboard.ReadAsync();
        if (!r.IsSuccess) { Status = r.Message; return; }
        var c = r.Value!;
        if (c.ImageBytes is { } bytes)
        {
            IsBusy = true;
            try
            {
                var imp = await _services.Importer.ImportBytesAsync(bytes, "Pasted image");
                if (!imp.IsSuccess) { Status = imp.Message; return; }
                var asset = imp.Value!.Asset;
                Commit("Paste image", d => DocumentOps.AddImages(d, [(asset, NewLayer(asset, "Pasted image", 0))]));
                Status = $"Pasted {asset.Width} × {asset.Height} image.";
            }
            finally { IsBusy = false; }
        }
        else if (c.ImageFiles.Count > 0) await ImportFilesAsync(c.ImageFiles);
        else Status = c.HasOtherData ? "The clipboard does not contain an image." : "The clipboard is empty.";
    }

    // ------------------------------------------------------------------ image list operations

    public void RemoveSelected()
    {
        if (!HasSelection) return;
        var imgs = _selectedImages.ToArray();
        var anns = _selectedAnnotations.ToArray();
        var order = Document.LayoutOrder.ToList();
        int idx = imgs.Length == 1 ? order.IndexOf(imgs[0]) : -1;
        if (Commit(imgs.Length + anns.Length == 1 ? "Delete" : $"Delete {imgs.Length + anns.Length} items",
                d => DocumentOps.RemoveAnnotations(DocumentOps.RemoveImages(d, imgs), anns)))
        {
            // Choose a nearby image so keyboard users keep their place.
            var rest = Document.LayoutOrder;
            Select(idx >= 0 && rest.Length > 0 ? [rest[Math.Min(idx, rest.Length - 1)]] : []);
        }
    }

    public void MoveInOrder(Guid id, int delta)
    {
        int i = Array.IndexOf(Document.LayoutOrder, id);
        if (i < 0) return;
        int ni = Math.Clamp(i + delta, 0, Document.LayoutOrder.Length - 1);
        if (ni == i) return;
        Commit(delta < 0 ? "Move up" : "Move down", d => DocumentOps.MoveInLayout(d, id, ni));
    }

    public void MoveToIndex(Guid id, int index) => Commit("Reorder", d => DocumentOps.MoveInLayout(d, id, index));

    public void Duplicate()
    {
        if (_selectedImages.Count == 0) return;
        Guid[] created = [];
        if (Commit("Duplicate", d => { var (nd, ids) = DocumentOps.Duplicate(DocumentOps.SetMode(d, LayoutMode.Free), _selectedImages); created = ids; return nd; }))
            Select(created);
    }

    public void ZOrder(DocumentOps.ZMove move)
    {
        if (_selectedImages.Count > 0) Commit("Arrange", d => DocumentOps.ChangeZOrder(d, _selectedImages, move));
        if (_selectedAnnotations.Count > 0) Commit("Arrange", d => DocumentOps.ChangeAnnotationZOrder(d, _selectedAnnotations, move));
    }

    public void Nudge(int dx, int dy)
    {
        if (!HasSelection) return;
        Commit("Nudge", d => DocumentOps.Move(d, _selectedImages, _selectedAnnotations, dx, dy));
    }

    public void ToggleVisible(Guid id)
    {
        var l = Document.FindImage(id);
        if (l is not null) Commit(l.Visible ? "Hide image" : "Show image", d => DocumentOps.SetVisible(d, id, !l.Visible));
    }

    public void RenameImage(Guid id, string? name) => Commit("Rename", d => DocumentOps.Rename(d, id, name));

    public void ResetCrop() { if (SelectedLayer is { } l) Commit("Reset crop", d => DocumentOps.ResetCrop(d, l.Id)); }

    public void Rotate(int q) { if (_selectedImages.Count > 0) Commit(q > 0 ? "Rotate right" : "Rotate left", d => DocumentOps.Rotate(d, _selectedImages, q)); }
    public void Flip(bool horizontal) { if (_selectedImages.Count > 0) Commit(horizontal ? "Flip horizontal" : "Flip vertical", d => DocumentOps.Flip(d, _selectedImages, horizontal)); }

    public void FitCanvas() => Commit("Fit canvas", d => DocumentOps.FitCanvas(d));

    public void SetCanvas(PixelRect r) => Commit("Canvas size", d => DocumentOps.SetExportArea(d, r));

    public void ScaleDocument(double factor) => Commit($"Scale {factor:P0}", d => DocumentOps.ScaleDocument(d, factor));

    public void ApplyPreset(LayoutPreset p) =>
        Commit($"Preset {p.Name}", d => DocumentOps.SetBackground(DocumentOps.SetLayout(d, p.Layout), p.Background));

    public void RenumberSteps() => Commit("Renumber steps", DocumentOps.RenumberSteps);

    public void SetEdge(EdgeStyle? e)
    {
        if (_selectedImages.Count == 0) { Status = "Select an image first."; return; }
        Commit("Edge effect", d => DocumentOps.SetEdge(d, _selectedImages, e));
    }

    public void AddAnnotation(Annotation a)
    {
        if (Commit($"Add {a.Kind.ToLowerInvariant()}", d => DocumentOps.AddAnnotation(d, a))) Select([], [a.Id]);
    }

    public void UpdateAnnotation(Annotation a, string label = "Edit annotation") => Commit(label, d => DocumentOps.UpdateAnnotation(d, a));

    public void UpdateSelectedAnnotations(Func<Annotation, Annotation> f, string label)
    {
        if (_selectedAnnotations.Count > 0) Commit(label, d => DocumentOps.UpdateAnnotations(d, _selectedAnnotations, f));
    }

    public void AddEffect(Guid layerId, ImageEffect effect) => Commit(effect.Kind == ImageEffectKind.Blur ? "Blur" : "Pixelate", d => DocumentOps.AddEffect(d, layerId, effect));

    public void ClearEffects() { if (SelectedLayer is { } l && l.Effects.Length > 0) Commit("Remove effects", d => DocumentOps.SetEffects(d, l.Id, [])); }

    // ------------------------------------------------------------------ output

    private async Task<PixelBuffer?> FlattenAsync()
    {
        if (!HasContent) { Status = "There is nothing to copy yet."; return null; }
        try { return await _services.Export.RenderAsync(Document); }
        catch (InvalidOperationException ex) { Status = ex.Message; ErrorRaised?.Invoke(ex.Message); return null; }
        catch (OutOfMemoryException) { Status = "Not enough memory to render. Try a smaller canvas."; return null; }
    }

    public async Task CopyImageAsync()
    {
        IsBusy = true;
        try
        {
            var px = await FlattenAsync();
            if (px is null) return;
            var png = await _services.Imaging.InvokeAsync(px.EncodePng);
            var opaque = await _services.Imaging.InvokeAsync(() => px.FlattenOnto(Document.Background.A == 255 ? Document.Background : Rgba32.White).ToBitmap());
            var r = await _services.Clipboard.CopyImageAsync(opaque, png);
            Status = r.IsSuccess ? $"Copied {px.Width} × {px.Height} image." : r.Message;
        }
        finally { IsBusy = false; }
    }

    public async Task<bool> ExportAsync(string path, ExportOptions options)
    {
        IsBusy = true;
        try
        {
            Status = "Exporting…";
            var r = await _services.Export.ExportAsync(Document, path, options);
            if (r.IsSuccess)
            {
                Status = $"Exported {r.Value!.Width} × {r.Value.Height} to {Path.GetFileName(r.Value.Path)}.";
                _services.Settings = _services.Settings with { LastExportDirectory = Path.GetDirectoryName(r.Value.Path) };
                return true;
            }
            Status = r.IsCanceled ? "Export canceled." : r.Message;
            if (!r.IsCanceled) ErrorRaised?.Invoke(r.Message ?? "Export failed.");
            return false;
        }
        finally { IsBusy = false; }
    }

    // ------------------------------------------------------------------ projects

    public async Task<bool> SaveProjectAsync(string path)
    {
        IsBusy = true;
        var doc = Document;
        using var protect = _services.Retention.Protect(doc.Assets.Select(a => a.Id));
        try
        {
            Status = "Saving project…";
            byte[]? preview = null;
            if (doc.HasVisibleContent && ExportBudgetOk(doc))
            {
                try { preview = await _services.Imaging.InvokeAsync(() => EncodeThumb(doc)); }
                catch (Exception ex) when (ex is InvalidOperationException or OutOfMemoryException) { }
            }
            var r = await _services.Projects.SaveAsync(doc, path, preview);
            if (!r.IsSuccess)
            {
                Status = r.IsCanceled ? "Save canceled." : r.Message;
                if (!r.IsCanceled) ErrorRaised?.Invoke(r.Message ?? "Save failed.");
                return false;
            }
            _projectPath = r.Value;
            _savedState = doc;
            _services.Autosave.MarkClean(doc.Id);
            _services.Settings = _services.Settings with { LastProjectDirectory = Path.GetDirectoryName(r.Value) };
            Status = $"Saved {Path.GetFileName(r.Value)}. The project keeps original image pixels, including cropped or redacted areas.";
            OnAllPropertiesChanged();
            return true;
        }
        finally { IsBusy = false; }
    }

    private static bool ExportBudgetOk(DocumentState d) => Imaging.Export.ExportBudget.Check(d) is null;

    private byte[] EncodeThumb(DocumentState doc)
    {
        var bmp = _services.Renderer.RenderThumbnail(doc, 512);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    public async Task<bool> OpenProjectAsync(string path)
    {
        IsBusy = true;
        try
        {
            var r = await _services.Projects.LoadAsync(path);
            if (!r.IsSuccess)
            {
                // The active composition is untouched on failure.
                Status = r.Message;
                if (!r.IsCanceled) ErrorRaised?.Invoke($"Could not open {Path.GetFileName(path)}:\n\n{r.Message}");
                return false;
            }
            LoadDocument(r.Value!, Path.GetFullPath(path), markSaved: true);
            Status = $"Opened {Path.GetFileName(path)}.";
            return true;
        }
        finally { IsBusy = false; }
    }

    /// <summary>Replaces the document and clears history (new/open/recover).</summary>
    public void LoadDocument(DocumentState doc, string? path, bool markSaved)
    {
        if (!IsDirty || IsEmpty) _services.Autosave.MarkClean(Document.Id);
        _projectPath = path;
        _selectedImages = [];
        _selectedAnnotations = [];
        History.Reset(doc);
        _savedState = markSaved ? doc : DocumentState.CreateEmpty();
        OnAllPropertiesChanged();
    }

    public void NewDocument()
    {
        _services.Autosave.MarkClean(Document.Id);
        LoadDocument(DocumentOps.Reflow(DocumentState.CreateEmpty() with
        {
            Layout = _services.Settings.DefaultLayout,
            Background = _services.Settings.DefaultBackground,
        }), null, markSaved: true);
        Status = "New composition.";
    }

    public void DiscardRecovery() => _services.Autosave.MarkClean(Document.Id);

    // ------------------------------------------------------------------ capture routing

    /// <summary>
    /// Adds captured images per destination as one undoable step. Returns false when nothing was added.
    /// </summary>
    public async Task<bool> AddCapturesAsync(IReadOnlyList<CaptureItem> items, CaptureDestination destination, bool combineVertical = true)
    {
        if (items.Count == 0) return false;
        IsBusy = true;
        try
        {
            var assets = new List<ImageAsset>();
            foreach (var it in items)
            {
                var a = await _services.Importer.ImportPixelsAsync(it.Pixels);
                assets.Add(a);
                if (_services.Settings.SaveCapturesToHistory) AddToHistory(a, it.Pixels);
            }

            if (destination == CaptureDestination.CopyOnly)
            {
                var px = items[0].Pixels;
                var png = await _services.Imaging.InvokeAsync(px.EncodePng);
                var opaque = await _services.Imaging.InvokeAsync(() => px.FlattenOnto(Rgba32.White).ToBitmap());
                var r = await _services.Clipboard.CopyImageAsync(opaque, png);
                Status = r.IsSuccess ? $"Copied {px.Width} × {px.Height} capture." : r.Message;
                return false;
            }

            if (destination == CaptureDestination.NewDocument)
            {
                if (IsDirty && !IsEmpty) _services.Autosave.NotifyChanged(Document, _projectPath);
                var baseDoc = DocumentState.CreateEmpty() with
                {
                    Layout = _services.Settings.DefaultLayout with { Mode = items.Count > 1 && !combineVertical ? LayoutMode.Horizontal : LayoutMode.Vertical },
                    Background = _services.Settings.DefaultBackground,
                };
                var nd = DocumentOps.AddImages(baseDoc, assets.Select((a, i) => (a, ImageLayer.ForAsset(a, 0, 0, $"Capture {i + 1}"))).ToArray());
                LoadDocument(nd, null, markSaved: false);
                Status = $"Captured {DescribeSizes(assets)}.";
                return true;
            }

            var ok = Commit(items.Count == 1 ? "Capture" : $"Capture {items.Count} images", d =>
            {
                var doc = destination switch
                {
                    CaptureDestination.AppendBelow when d.Layout.Mode != LayoutMode.Vertical => DocumentOps.SetMode(d, LayoutMode.Vertical),
                    CaptureDestination.AppendRight when d.Layout.Mode != LayoutMode.Horizontal => DocumentOps.SetMode(d, LayoutMode.Horizontal),
                    _ => d,
                };
                var list = new List<(ImageAsset, ImageLayer)>();
                for (int i = 0; i < assets.Count; i++)
                {
                    ImageLayer layer;
                    if (destination == CaptureDestination.AddToCanvas && doc.Layout.Mode == LayoutMode.Free)
                        layer = FreeCaptureLayer(doc, assets[i], i);
                    else layer = ImageLayer.ForAsset(assets[i], 0, 0, "Capture");
                    list.Add((assets[i], layer));
                }
                return DocumentOps.AddImages(doc, list);
            });
            if (ok)
            {
                var added = Document.LayoutOrder.TakeLast(items.Count).ToArray();
                Select(added);
                Status = $"Captured {DescribeSizes(assets)}.";
            }
            return ok;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Status = $"Capture could not be stored: {ex.Message}";
            return false;
        }
        finally { IsBusy = false; }
    }

    private ImageLayer FreeCaptureLayer(DocumentState doc, ImageAsset a, int i)
    {
        // Next to the current selection, else at the viewport center.
        if (SelectedLayer is { } sel)
            return ImageLayer.ForAsset(a, sel.Bounds.Right + 16 + i * 24, sel.Bounds.Y + i * 24, "Capture");
        var c = FreePlacementHint ?? new PixelPoint(doc.ExportArea.X + doc.ExportArea.Width / 2, doc.ExportArea.Y + doc.ExportArea.Height / 2);
        return ImageLayer.ForAsset(a, c.X - a.Width / 2 + i * 24, c.Y - a.Height / 2 + i * 24, "Capture");
    }

    private static string DescribeSizes(IReadOnlyList<ImageAsset> a) =>
        a.Count == 1 ? $"{a[0].Width} × {a[0].Height}" : $"{a.Count} images";

    private void AddToHistory(ImageAsset asset, PixelBuffer px)
    {
        try
        {
            double s = Math.Min(1.0, 256.0 / Math.Max(px.Width, px.Height));
            byte[]? thumb = null;
            try { thumb = _services.Imaging.InvokeAsync(() => EncodeScaled(px, s)).GetAwaiter().GetResult(); }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException) { }
            _services.History.Add(asset, thumb, "capture");
            var policy = new Storage.History.RetentionPolicy(_services.Settings.HistoryMaxCount, _services.Settings.HistoryMaxMegabytes * 1024L * 1024);
            _services.History.ApplyRetention(policy, ProtectedAssets());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _services.Log($"History add failed: {ex.Message}");
        }
    }

    private static byte[] EncodeScaled(PixelBuffer px, double s)
    {
        BitmapSource src = px.ToBitmap();
        if (s < 1) { var t = new TransformedBitmap(src, new System.Windows.Media.ScaleTransform(s, s)); t.Freeze(); src = t; }
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(src));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    /// <summary>Assets referenced by the open document and its undo history (never deleted by cleanup).</summary>
    public ISet<string> ProtectedAssets() => Storage.Assets.AssetReferences.Of(History.AllStates());

    /// <summary>
    /// Adds library captures in the given order as one undoable step. In Free mode with a drop point
    /// they are centered there (staggered); otherwise they follow normal placement / layout order.
    /// </summary>
    public void AddLibraryAssets(IReadOnlyList<ImageAsset> assets, PixelPoint? dropPoint = null)
    {
        if (assets.Count == 0) return;
        var layers = new List<(ImageAsset, ImageLayer)>();
        bool ok = Commit(assets.Count == 1 ? "Add capture" : $"Add {assets.Count} captures", d =>
        {
            layers = assets.Select((a, i) => (a, dropPoint is { } p && d.Layout.Mode == LayoutMode.Free
                ? ImageLayer.ForAsset(a, p.X - a.Width / 2 + i * 24, p.Y - a.Height / 2 + i * 24, "Capture")
                : NewLayer(a, "Capture", i))).ToList();
            return DocumentOps.AddImages(d, layers);
        });
        if (!ok) return;
        Select(layers.Select(l => l.Item2.Id));
        Status = assets.Count == 1 ? $"Added {assets[0].Width} × {assets[0].Height} capture." : $"Added {assets.Count} captures.";
    }
}
