using System.IO;
using WinForms = System.Windows.Forms;

namespace SnagItOpen.Windows.Shell;

/// <summary>A tray menu entry; <see cref="Text"/> null means separator.</summary>
public sealed record TrayMenuItem(string? Text, Action? OnClick = null)
{
    public static readonly TrayMenuItem Separator = new((string?)null, null);
}

/// <summary>Notification-area icon with a context menu. Dispose removes the icon.</summary>
public sealed class TrayService : IDisposable
{
    private readonly WinForms.NotifyIcon _icon;
    private readonly System.Drawing.Icon? _ownedIcon;

    public TrayService(string tooltip, IEnumerable<TrayMenuItem> items, Action onOpen)
    {
        _ownedIcon = TryLoadAppIcon();
        _icon = new WinForms.NotifyIcon
        {
            Text = tooltip.Length > 63 ? tooltip[..63] : tooltip,
            Icon = _ownedIcon ?? System.Drawing.SystemIcons.Application,
            ContextMenuStrip = BuildMenu(items),
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => onOpen();
    }

    public void ShowBalloon(string title, string text)
    {
        try { _icon.ShowBalloonTip(3000, title, text, WinForms.ToolTipIcon.Info); }
        catch (InvalidOperationException) { }
    }

    private static WinForms.ContextMenuStrip BuildMenu(IEnumerable<TrayMenuItem> items)
    {
        var menu = new WinForms.ContextMenuStrip();
        foreach (var it in items)
        {
            if (it.Text is null) { menu.Items.Add(new WinForms.ToolStripSeparator()); continue; }
            var mi = new WinForms.ToolStripMenuItem(it.Text) { Enabled = it.OnClick is not null };
            if (it.OnClick is { } a) mi.Click += (_, _) => a();
            menu.Items.Add(mi);
        }
        return menu;
    }

    private static System.Drawing.Icon? TryLoadAppIcon()
    {
        try
        {
            var p = Environment.ProcessPath;
            if (p is null) return null;
            // Pick the exe's icon frame at the tray's native size (DPI-aware) so it is not a blurry downscale of 32 px.
            var size = WinForms.SystemInformation.SmallIconSize.Width;
            return System.Drawing.Icon.ExtractIcon(p, 0, size) ?? System.Drawing.Icon.ExtractAssociatedIcon(p);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException) { return null; }
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.ContextMenuStrip?.Dispose();
        _icon.Dispose();
        _ownedIcon?.Dispose();
    }
}
