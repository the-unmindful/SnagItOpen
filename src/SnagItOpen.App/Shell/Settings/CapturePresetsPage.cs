using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Microsoft.Win32;
using SnagItOpen.App.Controls;
using SnagItOpen.App.Infrastructure;
using SnagItOpen.Core.Capture;
using SnagItOpen.Core.Documents;
using SnagItOpen.Core.Geometry;
using SnagItOpen.Storage.Settings;

namespace SnagItOpen.App.Shell.Settings;

/// <summary>Staged capture recipes. No store is mutated until the owning settings window saves.</summary>
public sealed class CapturePresetsPage : StackPanel
{
    private List<CapturePreset> _presets;
    private readonly ListBox _list = new() { MinHeight = 64, MaxHeight = 140 };
    private readonly ContentControl _formHost = new();
    private readonly InfoBar _error = new() { Kind = NotificationKind.Warning, Title = "Check this preset", CanClose = false, Visibility = Visibility.Collapsed };
    private readonly TextBlock _example = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 12) };
    private SettingsPage? _form;
    private bool _syncing;
    private int _selected = -1;
    private string _query = "";
    private readonly LastRegion? _lastRegion;
    private readonly string _monitorFingerprint;
    public event Action? Changed;
    public IReadOnlyList<CapturePreset> Presets => _presets.ToArray();
    public IReadOnlySet<string> EditableFields => (_form?.Rows ?? []).Where(r => r.Field is not null).Select(r => r.Field!).ToHashSet(StringComparer.Ordinal);
    public string FileNameExample => _example.Text;
    public string SearchLabels => "capture presets name mode shape delay cursor destination fixed size aspect ratio shortcut output folder file name template format copy stored region";

    public CapturePresetsPage(IEnumerable<CapturePreset> presets, LastRegion? lastRegion = null, string monitorFingerprint = "")
    {
        _presets = presets.ToList();
        _lastRegion = lastRegion; _monitorFingerprint = monitorFingerprint;
        AutomationProperties.SetName(_list, "Capture presets");
        _list.SelectionChanged += (_, _) =>
        {
            if (_syncing) return;
            int index = _list.SelectedIndex;
            if (!TryCommitCurrent(out var error)) { ShowError(error); RefreshList(); return; }
            _selected = index; BuildForm();
        };
        Children.Add(_list);
        var buttons = new WrapPanel { Margin = new Thickness(0, 6, 0, 10) };
        void AddButton(string label, Action action)
        {
            var button = new Button { Content = label, Margin = new Thickness(0, 0, 4, 4), Padding = new Thickness(8, 3, 8, 3) };
            AutomationProperties.SetName(button, label + " capture preset");
            button.Click += (_, _) =>
            {
                try { action(); }
                catch (ArgumentException ex) { ShowError(ex.Message); }
                catch (InvalidOperationException ex) { ShowError(ex.Message); }
            };
            buttons.Children.Add(button);
        }
        AddButton("Add", AddPreset);
        AddButton("Duplicate", DuplicateSelected);
        AddButton("Rename", () =>
        {
            if (_selected < 0) return;
            string? name = Dialogs.Prompt(Window.GetWindow(this), "Rename capture preset", "Name", Current.Name);
            if (name is not null) RenameSelected(name);
        });
        AddButton("Delete", DeleteSelected);
        AddButton("Move up", () => MoveSelected(-1));
        AddButton("Move down", () => MoveSelected(1));
        Children.Add(buttons);
        Children.Add(_error);
        Children.Add(_formHost);
        _example.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");
        _selected = _presets.Count > 0 ? 0 : -1;
        RefreshList(); BuildForm();
    }

    private CapturePreset Current => _presets[_selected];
    private void Update(Func<CapturePreset, CapturePreset> change)
    {
        if (_selected < 0) return;
        var before = Current;
        _presets[_selected] = change(before);
        if (before.Name != Current.Name) RefreshList();
        UpdateExample();
        ShowError(Current.Validate());
        Changed?.Invoke();
    }
    private void RefreshList()
    {
        _syncing = true;
        try { _list.ItemsSource = _presets.Select(p => p.Name).ToArray(); _list.SelectedIndex = _selected; }
        finally { _syncing = false; }
    }
    private string UniqueName(string basis)
    {
        if (!_presets.Any(p => p.Name.Equals(basis, StringComparison.OrdinalIgnoreCase))) return basis;
        for (int n = 2; ; n++)
        {
            string name = basis[..Math.Min(basis.Length, 55)] + " " + n;
            if (!_presets.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) return name;
        }
    }
    private void RequireValid()
    {
        if (!TryCommitCurrent(out var error)) throw new ArgumentException(error);
    }
    public void AddPreset()
    {
        RequireValid();
        _presets.Add(new CapturePreset { Name = UniqueName("Preset") });
        _selected = _presets.Count - 1; RefreshList(); BuildForm(); Changed?.Invoke();
    }
    public void DuplicateSelected()
    {
        if (_selected < 0) return;
        RequireValid();
        _presets.Add(Current with { Name = UniqueName(Current.Name[..Math.Min(Current.Name.Length, 55)] + " copy"), Hotkey = "" });
        _selected = _presets.Count - 1; RefreshList(); BuildForm(); Changed?.Invoke();
    }
    public void RenameSelected(string name)
    {
        if (_selected < 0) return;
        name = name.Trim();
        if (name.Length is 0 or > 64) throw new ArgumentException("Name must be 1–64 characters.");
        if (_presets.Where((_, i) => i != _selected).Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("A preset already has that name.");
        Update(p => p with { Name = name }); BuildForm();
    }
    public void DeleteSelected()
    {
        if (_selected < 0) return;
        _presets.RemoveAt(_selected); _selected = Math.Min(_selected, _presets.Count - 1);
        RefreshList(); BuildForm(); Changed?.Invoke();
    }
    public void MoveSelected(int direction)
    {
        int target = _selected + direction;
        if (_selected < 0 || target < 0 || target >= _presets.Count) return;
        RequireValid();
        _presets = CapturePresetStore.Reorder(_presets, _selected, target).ToList(); _selected = target;
        RefreshList(); Changed?.Invoke();
    }
    public bool TryCommitCurrent(out string? error)
    {
        error = _form?.CommitNumbers();
        if (error is not null) return false;
        error = _presets.Select(p => p.Validate() is { } e ? $"{p.Name}: {e}" : null).FirstOrDefault(e => e is not null);
        if (error is null && _presets.GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1)) error = "Capture preset names must be unique.";
        return error is null;
    }
    public bool NumbersValid => _form?.NumbersValid ?? true;
    public bool IsValid => NumbersValid && _presets.All(p => p.Validate() is null) && !_presets.GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1);
    public void Search(string query) { _query = query; _form?.Filter(query); }
    private void ShowError(string? message)
    {
        _error.Message = message ?? ""; _error.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
    }
    private void UpdateExample()
    {
        if (_selected < 0) { _example.Text = ""; return; }
        try
        {
            string path = CapturePresetStore.ResolveOutputPath(Current with { OutputFolder = Current.OutputFolder ?? Path.GetTempPath() }, DateTime.Now, _ => false);
            _example.Text = "Example: " + Path.GetFileName(path);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException) { _example.Text = "Enter a valid file name template."; }
    }

    private void BuildForm()
    {
        _formHost.Content = null;
        if (_selected < 0) { _form = null; _formHost.Content = new TextBlock { Text = "Add a preset to save a capture recipe.", TextWrapping = TextWrapping.Wrap }; ShowError(null); return; }
        var p = Current;
        var form = new SettingsPage("Preset details"); _form = form;
        form.Text(nameof(p.Name), "Name", p.Name, v => Update(x => x with { Name = v }));
        form.Choice(nameof(p.Mode), "Capture mode", p.Mode, Enum.GetValues<CaptureMode>(), v => Update(x => x with { Mode = v }));
        form.Choice(nameof(p.Shape), "Selection shape", p.Shape, Enum.GetValues<CaptureShape>(), v => Update(x => x with { Shape = v }));
        form.Choice(nameof(p.DelaySeconds), "Delay", p.DelaySeconds, CaptureOptions.AllowedDelays, v => Update(x => x with { DelaySeconds = v }), v => v == 0 ? "No delay" : $"{v} seconds");
        form.Check(nameof(p.IncludeCursor), "Include the mouse cursor", p.IncludeCursor, v => Update(x => x with { IncludeCursor = v }));
        form.Choice(nameof(p.Destination), "Destination", p.Destination, Enum.GetValues<CaptureDestination>(), v => Update(x => x with { Destination = v }));

        NumberBox width = null!, height = null!;
        var fixedSize = form.Check(nameof(p.FixedSize), "Use a fixed capture size", p.FixedSize is not null, v =>
        {
            width.IsEnabled = height.IsEnabled = v;
            Update(x => x with { FixedSize = v ? new PixelSize((int)width.Value, (int)height.Value) : null });
        });
        width = form.Number(nameof(p.FixedSize), "Fixed width (pixels)", p.FixedSize?.Width ?? 640, 1, Limits.MaxDimension, v => { if (fixedSize.IsChecked == true) Update(x => x with { FixedSize = new PixelSize((int)v, (int)height.Value) }); });
        height = form.Number(nameof(p.FixedSize), "Fixed height (pixels)", p.FixedSize?.Height ?? 480, 1, Limits.MaxDimension, v => { if (fixedSize.IsChecked == true) Update(x => x with { FixedSize = new PixelSize((int)width.Value, (int)v) }); });
        width.IsEnabled = height.IsEnabled = fixedSize.IsChecked == true;
        NumberBox aspect = null!;
        var aspectOn = form.Check(nameof(p.AspectRatio), "Constrain the aspect ratio", p.AspectRatio is not null, v => { aspect.IsEnabled = v; Update(x => x with { AspectRatio = v ? aspect.Value : null }); });
        aspect = form.Number(nameof(p.AspectRatio), "Aspect ratio (width / height)", p.AspectRatio ?? 16d / 9, 0.011, 100, v => { if (aspectOn.IsChecked == true) Update(x => x with { AspectRatio = v }); }, integer: false, step: 0.01);
        aspect.IsEnabled = aspectOn.IsChecked == true;
        var hotkey = new HotkeyBox(p.Hotkey, "Capture preset " + p.Name);
        hotkey.Changed += () => Update(x => x with { Hotkey = hotkey.Gesture });
        form.Add(nameof(p.Hotkey), "Global shortcut", hotkey);

        var folderRow = new DockPanel();
        var browse = new Button { Content = "Browse…", Margin = new Thickness(6, 0, 0, 0) };
        DockPanel.SetDock(browse, Dock.Right); folderRow.Children.Add(browse);
        var folder = new TextBox { Text = p.OutputFolder ?? "" }; folderRow.Children.Add(folder);
        folder.TextChanged += (_, _) => Update(x => x with { OutputFolder = string.IsNullOrWhiteSpace(folder.Text) ? null : folder.Text.Trim() });
        browse.Click += (_, _) => { var picker = new OpenFolderDialog { Title = "Capture output folder" }; if (picker.ShowDialog(Window.GetWindow(this)) == true) folder.Text = picker.FolderName; };
        AutomationProperties.SetName(folder, "Capture output folder"); AutomationProperties.SetName(browse, "Browse for capture output folder");
        form.Add(nameof(p.OutputFolder), "Save to folder (optional)", folderRow);
        form.Text(nameof(p.FileNameTemplate), "File name template", p.FileNameTemplate, v => Update(x => x with { FileNameTemplate = v }));
        form.Note("Use {date}, {time} and {n}. Existing files are kept and numbered automatically.");
        // Reusing the example requires detaching it from the preceding form before adding it here.
        if (_example.Parent is Panel old) old.Children.Remove(_example);
        form.Children.Add(_example);
        form.Choice(nameof(p.OutputFormat), "File format", p.OutputFormat, Enum.GetValues<QuickOutputFormat>(), v => Update(x => x with { OutputFormat = v }), v => v == QuickOutputFormat.Jpeg ? "JPEG" : "PNG");
        form.Check(nameof(p.CopyToClipboard), "Also copy the capture to clipboard", p.CopyToClipboard, v => Update(x => x with { CopyToClipboard = v }));

        NumberBox rx = null!, ry = null!, rw = null!, rh = null!;
        TextBox fingerprint = null!;
        void UpdateRegion() => Update(x => x.Region is null ? x : x with { Region = new LastRegion(new PixelRect((int)rx.Value, (int)ry.Value, (int)rw.Value, (int)rh.Value), fingerprint.Text) });
        var regionOn = form.Check(nameof(p.Region), "Use a stored region", p.Region is not null, v =>
        {
            rx.IsEnabled = ry.IsEnabled = rw.IsEnabled = rh.IsEnabled = fingerprint.IsEnabled = v;
            Update(x => x with { Region = v ? new LastRegion(new PixelRect((int)rx.Value, (int)ry.Value, (int)rw.Value, (int)rh.Value), fingerprint.Text) : null });
        });
        var remembered = p.Region ?? _lastRegion;
        rx = form.Number(nameof(p.Region), "Stored region X", remembered?.Bounds.X ?? 0, -Limits.MaxDimension, Limits.MaxDimension, _ => UpdateRegion());
        ry = form.Number(nameof(p.Region), "Stored region Y", remembered?.Bounds.Y ?? 0, -Limits.MaxDimension, Limits.MaxDimension, _ => UpdateRegion());
        rw = form.Number(nameof(p.Region), "Stored region width", remembered?.Bounds.Width ?? 640, 1, Limits.MaxDimension, _ => UpdateRegion());
        rh = form.Number(nameof(p.Region), "Stored region height", remembered?.Bounds.Height ?? 480, 1, Limits.MaxDimension, _ => UpdateRegion());
        fingerprint = form.Text(nameof(p.Region), "Stored monitor layout", remembered?.TopologyFingerprint ?? _monitorFingerprint, _ => UpdateRegion());
        rx.IsEnabled = ry.IsEnabled = rw.IsEnabled = rh.IsEnabled = fingerprint.IsEnabled = regionOn.IsChecked == true;
        form.Note("Stored regions use physical pixels and their original monitor layout. Leave this off for an interactive selection.");
        form.Changed += () => Changed?.Invoke();
        form.Filter(_query); _formHost.Content = form;
        UpdateExample(); ShowError(p.Validate());
    }
}
