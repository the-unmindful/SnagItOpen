using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;
using SnagItOpen.Storage.Settings;

namespace SnagItOpen.App.Infrastructure;

/// <summary>The token set actually in use (high contrast always wins over the user's choice).</summary>
public enum EffectiveTheme { Light, Dark, HighContrast }

/// <summary>
/// Chooses the colour token dictionary (PRD 5.1/5.2), swaps it at runtime, follows the Windows
/// light/dark and high-contrast settings, and sets the dark title bar on every window.
/// All chrome must reference tokens with DynamicResource / SetResourceReference so a swap is live.
/// </summary>
public sealed class ThemeService : IDisposable
{
    private const string TokenPathPrefix = "/Themes/Tokens.";
    private readonly Application _app;
    private AppTheme _mode;
    private bool _disposed;

    public static ThemeService? Current { get; private set; }

    public ThemeService(Application app, AppTheme mode)
    {
        _app = app;
        _mode = mode;
        Current = this;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        SystemParameters.StaticPropertyChanged += OnSystemParameterChanged;
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnWindowLoaded));
        Apply();
    }

    public AppTheme Mode => _mode;
    public EffectiveTheme Effective { get; private set; } = EffectiveTheme.Light;

    /// <summary>False when Windows "Show animations" is off; every animation must then be instant.</summary>
    public static bool AnimationsEnabled => SystemParameters.ClientAreaAnimation;

    /// <summary>Raised on the UI thread after the token dictionary has been swapped.</summary>
    public event EventHandler? ThemeChanged;

    public void SetMode(AppTheme mode)
    {
        if (_mode == mode) return;
        _mode = mode;
        Apply();
    }

    /// <summary>Resolves the theme to use for a mode, given the system state.</summary>
    public static EffectiveTheme Resolve(AppTheme mode, bool highContrast, bool systemUsesLight) =>
        highContrast ? EffectiveTheme.HighContrast
        : mode switch
        {
            AppTheme.Light => EffectiveTheme.Light,
            AppTheme.Dark => EffectiveTheme.Dark,
            _ => systemUsesLight ? EffectiveTheme.Light : EffectiveTheme.Dark,
        };

    public static Uri TokenUri(EffectiveTheme t) =>
        new($"pack://application:,,,/SnagItOpen;component{TokenPathPrefix}{t}.xaml", UriKind.Absolute);

    /// <summary>Reads HKCU ...\Personalize\AppsUseLightTheme (missing or unreadable means light).</summary>
    public static bool SystemUsesLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is not int v || v != 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            return true;
        }
    }

    public void Apply()
    {
        if (_disposed) return;
        var target = Resolve(_mode, SystemParameters.HighContrast, SystemUsesLightTheme());
        var merged = _app.Resources.MergedDictionaries;
        int index = -1;
        for (int i = 0; i < merged.Count; i++)
            if (merged[i].Source?.OriginalString.Contains(TokenPathPrefix, StringComparison.OrdinalIgnoreCase) == true) { index = i; break; }

        // High contrast is always reloaded: its values are read from SystemColors at load time.
        bool same = index >= 0 && target == Effective && target != EffectiveTheme.HighContrast
            && merged[index].Source?.OriginalString.Contains(TokenPathPrefix + target + ".", StringComparison.OrdinalIgnoreCase) == true;
        if (!same)
        {
            var dict = new ResourceDictionary { Source = TokenUri(target) };
            if (index >= 0) merged[index] = dict;
            else merged.Insert(0, dict);
        }
        Effective = target;
        foreach (Window w in _app.Windows) ApplyTitleBar(w);
        ThemeChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is Window w) ApplyTitleBar(w);
    }

    private void ApplyTitleBar(Window w)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd == IntPtr.Zero) return;
        int dark = Effective == EffectiveTheme.Dark ? 1 : 0;
        try { _ = DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int)); }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
    }

    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color
            or UserPreferenceCategory.Accessibility or UserPreferenceCategory.VisualStyle)
            _app.Dispatcher.BeginInvoke(Apply);
    }

    private void OnSystemParameterChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemParameters.HighContrast))
            _app.Dispatcher.BeginInvoke(Apply);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        SystemParameters.StaticPropertyChanged -= OnSystemParameterChanged;
        if (ReferenceEquals(Current, this)) Current = null;
    }

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
