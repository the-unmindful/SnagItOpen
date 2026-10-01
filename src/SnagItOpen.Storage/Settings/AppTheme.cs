namespace SnagItOpen.Storage.Settings;

/// <summary>
/// User theme choice. System follows the Windows light/dark app setting.
/// Windows high contrast always wins over this choice (PRD R-E9.4).
/// Named AppTheme to avoid a clash with WPF's System.Windows.ThemeMode.
/// </summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<AppTheme>))]
public enum AppTheme { System, Light, Dark }
