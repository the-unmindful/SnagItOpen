using System.Globalization;
using System.Windows.Input;
using System.Windows.Interop;
using SnagItOpen.Core;
using static SnagItOpen.Windows.Native.NativeMethods;

namespace SnagItOpen.Windows.Shell;

/// <summary>A key combination such as "Ctrl+Shift+1" or "PrintScreen".</summary>
public readonly record struct HotkeyGesture(ModifierKeys Modifiers, Key Key)
{
    /// <summary>Win32 MOD_* flags (ModifierKeys values match MOD_ALT/CONTROL/SHIFT/WIN) plus MOD_NOREPEAT.</summary>
    public uint Win32Modifiers => (uint)Modifiers | MOD_NOREPEAT;
    public uint VirtualKey => (uint)KeyInterop.VirtualKeyFromKey(Key);

    public override string ToString()
    {
        var parts = new List<string>();
        if (Modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(KeyName(Key));
        return string.Join("+", parts);
    }

    private static string KeyName(Key k) => k switch
    {
        >= Key.D0 and <= Key.D9 => ((int)(k - Key.D0)).ToString(CultureInfo.InvariantCulture),
        Key.Snapshot => "PrintScreen",
        _ => k.ToString(),
    };

    public static bool TryParse(string? text, out HotkeyGesture gesture)
    {
        gesture = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var mods = ModifierKeys.None;
        Key? key = null;
        foreach (var raw in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl" or "control": mods |= ModifierKeys.Control; continue;
                case "shift": mods |= ModifierKeys.Shift; continue;
                case "alt": mods |= ModifierKeys.Alt; continue;
                case "win" or "windows": mods |= ModifierKeys.Windows; continue;
            }
            if (key is not null) return false;
            key = ParseKey(raw);
            if (key is null) return false;
        }
        if (key is not { } kk || kk == Key.None) return false;
        // Require a modifier except for keys that are unambiguous on their own.
        if (mods == ModifierKeys.None && kk is not (Key.Snapshot or >= Key.F13 and <= Key.F24)) return false;
        gesture = new HotkeyGesture(mods, kk);
        return true;
    }

    private static Key? ParseKey(string s)
    {
        if (s.Length == 1 && char.IsAsciiDigit(s[0])) return Key.D0 + (s[0] - '0');
        if (s.Length == 1 && char.IsAsciiLetter(s[0])) return Enum.Parse<Key>(s.ToUpperInvariant());
        if (s.Equals("PrintScreen", StringComparison.OrdinalIgnoreCase) || s.Equals("PrtSc", StringComparison.OrdinalIgnoreCase)) return Key.Snapshot;
        return Enum.TryParse<Key>(s, ignoreCase: true, out var k) && Enum.IsDefined(k) ? k : null;
    }
}

/// <summary>Registers hotkeys with the OS; fakeable for lifecycle tests.</summary>
public interface IHotkeyRegistrar
{
    bool Register(int id, uint modifiers, uint vk);
    void Unregister(int id);
    event Action<int>? HotkeyPressed;
}

/// <summary>
/// Named global hotkeys with transactional rebinding: a replacement registers before the old binding
/// is released, so a conflict keeps the previous working binding.
/// </summary>
public sealed class GlobalHotkeyService : IDisposable
{
    private readonly IHotkeyRegistrar _registrar;
    private readonly Dictionary<string, (int Id, HotkeyGesture Gesture)> _bindings = new(StringComparer.Ordinal);
    private int _nextId = 0x5100;

    public GlobalHotkeyService(IHotkeyRegistrar registrar)
    {
        _registrar = registrar;
        _registrar.HotkeyPressed += OnPressed;
    }

    public event Action<string>? Pressed;

    public IReadOnlyDictionary<string, HotkeyGesture> Bindings =>
        _bindings.ToDictionary(kv => kv.Key, kv => kv.Value.Gesture);

    /// <summary>Binds or clears (<paramref name="gesture"/> null) a named hotkey.</summary>
    public OpResult<bool> Bind(string name, HotkeyGesture? gesture)
    {
        _bindings.TryGetValue(name, out var old);
        bool hadOld = _bindings.ContainsKey(name);
        if (gesture is null)
        {
            if (hadOld) { _registrar.Unregister(old.Id); _bindings.Remove(name); }
            return OpResult<bool>.Ok(true);
        }
        var g = gesture.Value;
        if (hadOld && old.Gesture == g) return OpResult<bool>.Ok(true);
        foreach (var (other, b) in _bindings)
            if (other != name && b.Gesture == g)
                return OpResult<bool>.Fail(ErrorCode.HotkeyUnavailable, $"{g} is already used for {other}.");
        int id = _nextId++;
        if (!_registrar.Register(id, g.Win32Modifiers, g.VirtualKey))
            return OpResult<bool>.Fail(ErrorCode.HotkeyUnavailable,
                $"{g} is in use by another application." + (hadOld ? $" Keeping {old.Gesture}." : ""));
        if (hadOld) _registrar.Unregister(old.Id);
        _bindings[name] = (id, g);
        return OpResult<bool>.Ok(true);
    }

    private void OnPressed(int id)
    {
        foreach (var (name, b) in _bindings)
            if (b.Id == id) { Pressed?.Invoke(name); return; }
    }

    public void Dispose()
    {
        foreach (var b in _bindings.Values) _registrar.Unregister(b.Id);
        _bindings.Clear();
        _registrar.HotkeyPressed -= OnPressed;
        (_registrar as IDisposable)?.Dispose();
    }
}

/// <summary>Real registrar using a message-only window. Create on the UI thread.</summary>
public sealed class Win32HotkeyRegistrar : IHotkeyRegistrar, IDisposable
{
    private readonly HwndSource _source;
    private readonly HashSet<int> _ids = [];

    public Win32HotkeyRegistrar()
    {
        _source = new HwndSource(new HwndSourceParameters("SnagItOpen.Hotkeys")
        {
            ParentWindow = new IntPtr(-3), // HWND_MESSAGE
            WindowStyle = 0,
            Width = 0,
            Height = 0,
        });
        _source.AddHook(Hook);
    }

    public event Action<int>? HotkeyPressed;

    public bool Register(int id, uint modifiers, uint vk)
    {
        if (!RegisterHotKey(_source.Handle, id, modifiers, vk)) return false;
        _ids.Add(id);
        return true;
    }

    public void Unregister(int id)
    {
        if (_ids.Remove(id)) UnregisterHotKey(_source.Handle, id);
    }

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY)
        {
            handled = true;
            HotkeyPressed?.Invoke(wParam.ToInt32());
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        foreach (var id in _ids.ToList()) UnregisterHotKey(_source.Handle, id);
        _ids.Clear();
        _source.RemoveHook(Hook);
        _source.Dispose();
    }
}
