using System.Windows;
using System.Windows.Controls;
using SnagItOpen.App.Shell;
using SnagItOpen.Storage.Settings;

namespace SnagItOpen.App.Controls;

public sealed class InspectorSection : Expander
{
    private Func<UiState>? _read;
    private Action<UiState>? _save;
    private string _key = "";
    public InspectorSection()
    {
        // Implicit styles are not inherited by subclasses: opt in to the themed Expander explicitly.
        SetResourceReference(StyleProperty, typeof(Expander));
        Margin = new Thickness(0, 2, 0, 0); SetResourceReference(ForegroundProperty, "Text.Primary");
        Expanded += (_, _) => Persist(); Collapsed += (_, _) => Persist();
    }
    public InspectorSection(AppServices services, string key, string title, bool expanded = true) : this()
    {
        Header = new TextBlock { Text = title, FontWeight = FontWeights.SemiBold };
        ConfigurePersistence(() => services.UiState, services.SaveUiState, key, expanded);
    }
    public void ConfigurePersistence(Func<UiState> read, Action<UiState> save, string key, bool expanded = true)
    {
        _save = null; _read = read; _key = key; IsExpanded = read().SectionsOpen.TryGetValue(key, out bool open) ? open : expanded; _save = save;
    }
    private void Persist()
    {
        if (_read is null || _save is null || string.IsNullOrWhiteSpace(_key)) return;
        var state = _read(); var sections = new Dictionary<string, bool>(state.SectionsOpen, StringComparer.Ordinal) { [_key] = IsExpanded };
        _save(state with { SectionsOpen = sections });
    }
}
