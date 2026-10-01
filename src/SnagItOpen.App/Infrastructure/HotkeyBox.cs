using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SnagItOpen.Windows.Shell;

namespace SnagItOpen.App.Infrastructure;

/// <summary>
/// Shortcut recorder: click it, then press the key combination. Esc cancels recording, Backspace/Delete
/// clears. PrintScreen is recorded on key-up because Windows never sends a key-down for it.
/// </summary>
public sealed class HotkeyBox : DockPanel
{
    private readonly TextBox _box;
    private readonly Button _clear;
    private string _gesture;
    private bool _recording;

    public HotkeyBox(string gesture, string name)
    {
        _gesture = gesture ?? "";
        _box = new TextBox
        {
            IsReadOnly = true, IsReadOnlyCaretVisible = false, Cursor = Cursors.Hand,
            VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(4, 2, 4, 2),
            ToolTip = "Click, then press the keys you want (e.g. PrintScreen, Ctrl+PrintScreen, Ctrl+Shift+5). Backspace clears.",
        };
        AutomationProperties.SetName(_box, name + " shortcut. Press the keys to record.");
        _clear = new Button { Content = "✕", Width = 24, Margin = new Thickness(4, 0, 0, 0), ToolTip = "No shortcut" };
        AutomationProperties.SetName(_clear, "Clear " + name + " shortcut");
        _clear.Click += (_, _) => { _gesture = ""; StopRecording(); Changed?.Invoke(); };
        SetDock(_clear, Dock.Right);
        Children.Add(_clear);
        Children.Add(_box);

        _box.GotKeyboardFocus += (_, _) => { _recording = true; Show(); };
        _box.LostKeyboardFocus += (_, _) => StopRecording();
        _box.PreviewMouseLeftButtonDown += (_, e) => { if (!_box.IsKeyboardFocused) { _box.Focus(); e.Handled = true; } };
        _box.PreviewKeyDown += OnKeyDown;
        _box.PreviewKeyUp += OnKeyUp;
        Show();
    }

    /// <summary>Canonical gesture text ("" = none).</summary>
    public string Gesture => _gesture;

    public event Action? Changed;

    private static Key RealKey(KeyEventArgs e) => e.Key switch
    {
        Key.System => e.SystemKey,
        Key.ImeProcessed => e.ImeProcessedKey,
        Key.DeadCharProcessed => e.DeadCharProcessedKey,
        _ => e.Key,
    };

    private static bool IsModifier(Key k) => k is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
        or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin;

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (!_recording) return;
        var key = RealKey(e);
        var mods = Keyboard.Modifiers;
        e.Handled = true;
        if (key == Key.Tab && mods == ModifierKeys.None) { e.Handled = false; return; } // keep Tab for navigation
        if (key == Key.Escape && mods == ModifierKeys.None) { StopRecording(); Keyboard.ClearFocus(); return; }
        if (key is Key.Back or Key.Delete && mods == ModifierKeys.None) { _gesture = ""; Show(); Changed?.Invoke(); return; }
        if (IsModifier(key)) { ShowPartial(mods); return; }
        if (key == Key.Snapshot) return; // handled on key-up
        Record(mods, key);
    }

    private void OnKeyUp(object sender, KeyEventArgs e)
    {
        if (!_recording) return;
        var key = RealKey(e);
        if (key == Key.Snapshot) { e.Handled = true; Record(Keyboard.Modifiers, key); return; }
        if (IsModifier(key)) Show();
    }

    private void Record(ModifierKeys mods, Key key)
    {
        var text = new HotkeyGesture(mods & ~ModifierKeys.None, key).ToString();
        if (!HotkeyGesture.TryParse(text, out var g))
        {
            _box.Text = "Add Ctrl, Alt, Shift or Win";
            _box.SetResourceReference(TextBox.ForegroundProperty, "Status.Error");
            return;
        }
        _gesture = g.ToString();
        Show();
        Changed?.Invoke();
    }

    private void ShowPartial(ModifierKeys mods)
    {
        var parts = new List<string>();
        if (mods.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (mods.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (mods.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (mods.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        _box.Text = string.Join("+", parts) + "+…";
        _box.SetResourceReference(TextBox.ForegroundProperty, "Text.Secondary");
    }

    private void StopRecording()
    {
        _recording = false;
        Show();
    }

    private void Show()
    {
        _box.SetResourceReference(TextBox.ForegroundProperty, "Text.Primary");
        _box.Text = _recording ? (_gesture.Length == 0 ? "Press keys…" : _gesture + "   (press new keys)") : _gesture.Length == 0 ? "None" : _gesture;
        _box.SetResourceReference(TextBox.BackgroundProperty, _recording ? "Accent.SelectSoft" : "Bg.Surface");
        if (!_recording && _gesture.Length == 0) _box.SetResourceReference(TextBox.ForegroundProperty, "Text.Secondary");
    }
}
