using System.Drawing;
using System.IO;
using WinForms = System.Windows.Forms;

namespace SnagItOpen.Windows.Shell;

/// <summary>A tray entry. Null text is a separator; child entries form a submenu.</summary>
public sealed record TrayMenuItem(string? Text, Action? OnClick = null,
    IReadOnlyList<TrayMenuItem>? Children = null, string? Gesture = null, Bitmap? Image = null,
    Func<int, Bitmap?>? ImageFactory = null)
{
    public static readonly TrayMenuItem Separator = new((string?)null);
}

/// <summary>App-supplied theme values; Windows services never reference WPF.</summary>
public sealed record TrayThemeColors(Color Surface, Color Text, Color Hover, Color Border, bool HighContrast = false);

public sealed class TrayService : IDisposable
{
    private readonly WinForms.NotifyIcon _icon;
    private readonly Icon? _ownedIcon;
    private readonly List<Bitmap> _menuImages = [];
    private TrayThemeColors? _theme;

    public TrayService(string tooltip, IEnumerable<TrayMenuItem> items, Action onOpen)
    {
        _ownedIcon = TryLoadAppIcon();
        _icon = new WinForms.NotifyIcon { Icon = _ownedIcon ?? SystemIcons.Application, Visible = true };
        SetTooltip(tooltip);
        RefreshMenu(items);
        _icon.DoubleClick += (_, _) => onOpen();
    }

    public void SetTooltip(string tooltip) => _icon.Text = tooltip.Length > 63 ? tooltip[..63] : tooltip;
    public string Tooltip { get => _icon.Text; set => SetTooltip(value); }

    public void SetTheme(TrayThemeColors? colors)
    {
        _theme = colors;
        if (_icon.ContextMenuStrip is { } menu)
            menu.Renderer = colors is null || colors.HighContrast ? new WinForms.ToolStripSystemRenderer() : new ThemeRenderer(colors);
    }

    public void RefreshMenu(IEnumerable<TrayMenuItem> items)
    {
        var previous = _icon.ContextMenuStrip;
        var menu = new WinForms.ContextMenuStrip { ShowImageMargin = true, ShowCheckMargin = false };
        AddItems(menu.Items, items);
        menu.Opening += (_, _) => ScaleImages(menu);
        _icon.ContextMenuStrip = menu;
        previous?.Dispose();
        SetTheme(_theme);
    }

    private void AddItems(WinForms.ToolStripItemCollection collection, IEnumerable<TrayMenuItem> entries)
    {
        foreach (var entry in entries)
        {
            if (entry.Text is null) { collection.Add(new WinForms.ToolStripSeparator()); continue; }
            var item = new WinForms.ToolStripMenuItem(entry.Text) { Enabled = entry.OnClick is not null || entry.Children is { Count: > 0 }, Tag = entry,
                ShortcutKeyDisplayString = entry.Gesture ?? "", ShowShortcutKeys = true };
            if (entry.OnClick is { } action) item.Click += (_, _) => action();
            if (entry.Children is { } children) AddItems(item.DropDownItems, children);
            collection.Add(item);
        }
    }

    private void ScaleImages(WinForms.ContextMenuStrip menu)
    {
        foreach (var bitmap in _menuImages) bitmap.Dispose();
        _menuImages.Clear();
        int size = Math.Max(16, (int)Math.Round(16 * menu.DeviceDpi / 96.0));
        menu.ImageScalingSize = new Size(size, size);
        Scale(menu.Items);
        void Scale(WinForms.ToolStripItemCollection items)
        {
            foreach (WinForms.ToolStripItem item in items)
            {
                if (item.Tag is TrayMenuItem model)
                {
                    var bitmap = model.ImageFactory?.Invoke(size);
                    if (bitmap is null && model.Image is { } source) bitmap = new Bitmap(source, new Size(size, size));
                    item.Image = bitmap;
                    if (bitmap is not null) _menuImages.Add(bitmap);
                }
                if (item is WinForms.ToolStripMenuItem child) { child.DropDown.ImageScalingSize = new Size(size, size); Scale(child.DropDownItems); }
            }
        }
    }

    public void ShowBalloon(string title, string text)
    {
        try { _icon.ShowBalloonTip(3000, title, text, WinForms.ToolTipIcon.Info); }
        catch (InvalidOperationException) { }
    }

    private sealed class ThemeRenderer(TrayThemeColors theme) : WinForms.ToolStripProfessionalRenderer(new ThemeTable(theme))
    {
        protected override void OnRenderItemText(WinForms.ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? theme.Text : SystemColors.GrayText;
            base.OnRenderItemText(e);
        }
        protected override void OnRenderArrow(WinForms.ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = e.Item?.Enabled == false ? SystemColors.GrayText : theme.Text;
            base.OnRenderArrow(e);
        }
        protected override void OnRenderMenuItemBackground(WinForms.ToolStripItemRenderEventArgs e)
        {
            using var brush = new SolidBrush(e.Item.Selected ? theme.Hover : theme.Surface);
            e.Graphics.FillRectangle(brush, new Rectangle(Point.Empty, e.Item.Size));
        }
    }

    private sealed class ThemeTable(TrayThemeColors theme) : WinForms.ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => theme.Surface;
        public override Color ImageMarginGradientBegin => theme.Surface;
        public override Color ImageMarginGradientMiddle => theme.Surface;
        public override Color ImageMarginGradientEnd => theme.Surface;
        public override Color MenuItemSelected => theme.Hover;
        public override Color MenuItemBorder => theme.Border;
        public override Color MenuBorder => theme.Border;
        public override Color SeparatorDark => theme.Border;
        public override Color SeparatorLight => theme.Surface;
    }

    private static Icon? TryLoadAppIcon()
    {
        try
        {
            var path = Environment.ProcessPath;
            if (path is null) return null;
            return Icon.ExtractIcon(path, 0, WinForms.SystemInformation.SmallIconSize.Width) ?? Icon.ExtractAssociatedIcon(path);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException) { return null; }
    }

    public void Dispose()
    {
        _icon.Visible = false; _icon.ContextMenuStrip?.Dispose(); _icon.Dispose(); _ownedIcon?.Dispose();
        foreach (var bitmap in _menuImages) bitmap.Dispose();
        _menuImages.Clear();
    }
}
