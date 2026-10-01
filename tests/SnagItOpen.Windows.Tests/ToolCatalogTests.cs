using System.IO;
using SnagItOpen.App.Editor;

namespace SnagItOpen.Windows.Tests;

public sealed class ToolCatalogTests
{
    [Fact]
    public void Every_tool_has_one_unique_shortcut_and_a_real_icon()
    {
        Assert.Equal(Enum.GetValues<ToolKind>().Order(), ToolCatalog.All.Select(t => t.Kind).Order());
        Assert.Equal(ToolCatalog.All.Count, ToolCatalog.All.Select(t => t.Shortcut).Distinct().Count());
        ThemeTokenTests.RunSta(() =>
        {
            using var stream = File.OpenRead(Path.Combine(ThemeTokenTests.ThemesFolder(), "Icons.xaml"));
            var icons = (System.Windows.ResourceDictionary)System.Windows.Markup.XamlReader.Load(stream);
            foreach (var tool in ToolCatalog.All) Assert.IsAssignableFrom<System.Windows.Media.Geometry>(icons[tool.Icon]);
            return true;
        });
    }
}
