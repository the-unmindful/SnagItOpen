using System.IO;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;
using SnagItOpen.App.Infrastructure;
using SnagItOpen.Storage.Settings;

namespace SnagItOpen.Windows.Tests;

/// <summary>U01: every token dictionary defines the same keys, and every value is a Brush or Color.</summary>
public sealed class ThemeTokenTests
{
    private static readonly string[] Themes = ["Light", "Dark", "HighContrast"];

    internal static string ThemesFolder()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SnagItOpen.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "src", "SnagItOpen.App", "Themes");
    }

    internal static T RunSta<T>(Func<T> f)
    {
        T result = default!;
        Exception? error = null;
        var t = new Thread(() =>
        {
            try { result = f(); }
            catch (Exception ex) { error = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error is not null) throw new InvalidOperationException("STA body failed: " + error.Message, error);
        return result;
    }

    private static ResourceDictionary LoadTokens(string theme)
    {
        using var s = File.OpenRead(Path.Combine(ThemesFolder(), $"Tokens.{theme}.xaml"));
        return (ResourceDictionary)XamlReader.Load(s);
    }

    [Fact]
    public void All_token_dictionaries_have_the_same_keys_and_brush_or_color_values()
    {
        var keySets = RunSta(() =>
        {
            var sets = new Dictionary<string, HashSet<string>>();
            foreach (var theme in Themes)
            {
                var d = LoadTokens(theme);
                var keys = new HashSet<string>();
                foreach (var k in d.Keys)
                {
                    var key = Assert.IsType<string>(k);
                    var v = d[k];
                    Assert.True(v is Brush || v is Color, $"{theme}: {key} is {v?.GetType().Name ?? "null"}");
                    keys.Add(key);
                }
                sets[theme] = keys;
            }
            return sets;
        });

        var light = keySets["Light"];
        Assert.True(light.Count >= 29, $"Light has only {light.Count} tokens");
        foreach (var theme in Themes)
        {
            var missing = light.Except(keySets[theme]).ToList();
            var extra = keySets[theme].Except(light).ToList();
            Assert.True(missing.Count == 0 && extra.Count == 0,
                $"{theme}: missing [{string.Join(", ", missing)}], extra [{string.Join(", ", extra)}]");
        }
    }

    [Theory]
    [InlineData(AppTheme.System, false, true, EffectiveTheme.Light)]
    [InlineData(AppTheme.System, false, false, EffectiveTheme.Dark)]
    [InlineData(AppTheme.Light, false, false, EffectiveTheme.Light)]
    [InlineData(AppTheme.Dark, false, true, EffectiveTheme.Dark)]
    [InlineData(AppTheme.Light, true, true, EffectiveTheme.HighContrast)]
    [InlineData(AppTheme.Dark, true, false, EffectiveTheme.HighContrast)]
    public void Resolve_prefers_high_contrast_then_user_choice_then_system(AppTheme mode, bool hc, bool sysLight, EffectiveTheme expected) =>
        Assert.Equal(expected, ThemeService.Resolve(mode, hc, sysLight));
}
