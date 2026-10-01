using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using SnagItOpen.App.Infrastructure;

namespace SnagItOpen.App.Shell;

public sealed class AboutWindow : DialogWindow
{
    public string VersionText { get; }
    public AboutWindow(Window? owner, string dataFolder) : base(owner, "About SnagItOpen", new StackPanel(), "Close", null)
    {
        var assembly = typeof(AboutWindow).Assembly;
        VersionText = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? assembly.GetName().Version?.ToString() ?? "Unknown";
        var body = (StackPanel)Body;
        var logo = new Border { Width = 64, Height = 64, CornerRadius = new CornerRadius(12), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 16) };
        logo.SetResourceReference(Border.BackgroundProperty, "Accent.Brand");
        var glyph = new TextBlock { Text = "S", FontSize = 44, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }; glyph.SetResourceReference(TextBlock.ForegroundProperty, "Text.OnAccent"); logo.Child = glyph;
        body.Children.Add(logo); body.Children.Add(new TextBlock { Text = $"Version {VersionText}", FontWeight = FontWeights.SemiBold });
        body.Children.Add(new TextBlock { Text = "Local and offline", Margin = new Thickness(0, 8, 0, 4) });
        body.Children.Add(new TextBlock { Text = "MIT licence", Margin = new Thickness(0, 0, 0, 12) });
        var notices = new Button { Content = "Third-party notices", HorizontalAlignment = HorizontalAlignment.Left }; notices.Click += (_, _) => OpenNotices(); body.Children.Add(notices);
        var folder = new Button { Content = "Open data folder", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0) }; folder.Click += (_, _) => OpenPath(dataFolder); body.Children.Add(folder);
    }
    private void OpenNotices()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string path = Path.Combine(directory.FullName, "THIRD-PARTY-NOTICES.md");
            if (File.Exists(path)) { OpenPath(path); return; }
            directory = directory.Parent;
        }
        Dialogs.Info(this, "SnagItOpen uses the .NET and Windows Desktop runtime, distributed under the MIT licence by the .NET Foundation and Contributors. The application has no third-party runtime package dependencies. SnagItOpen is not affiliated with TechSmith or Snagit.");
    }
    private void OpenPath(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException) { Dialogs.Error(this, $"Could not open {path}: {ex.Message}"); }
    }
}
