using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Windows.Forms;
using StealthDeck.Interop;

namespace StealthDeck.Services;

/// <summary>
/// Owns the notification-area icon and its dark context menu. No balloon/toast notifications
/// are ever raised, because those render as separate, capturable OS surfaces.
/// </summary>
public sealed class TrayIconService : IDisposable
{
    internal static readonly Color Background = Color.FromArgb(0x18, 0x18, 0x1B);
    internal static readonly Color Surface = Color.FromArgb(0x27, 0x27, 0x2A);
    internal static readonly Color BorderColor = Color.FromArgb(0x3F, 0x3F, 0x46);
    internal static readonly Color Foreground = Color.FromArgb(0xF4, 0xF4, 0xF5);
    internal static readonly Color Muted = Color.FromArgb(0xA1, 0xA1, 0xAA);
    internal static readonly Color Accent = Color.FromArgb(0x3B, 0x82, 0xF6);

    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _clickThroughItem;
    private readonly Font _menuFont;
    private readonly Icon _icon;
    private readonly IntPtr _hIcon;
    private bool _disposed;

    public TrayIconService(Action toggleVisibility, Action toggleClickThrough, Action showShortcuts, Action exit)
    {
        ArgumentNullException.ThrowIfNull(toggleVisibility);
        ArgumentNullException.ThrowIfNull(toggleClickThrough);
        ArgumentNullException.ThrowIfNull(showShortcuts);
        ArgumentNullException.ThrowIfNull(exit);

        (_icon, _hIcon) = CreateIcon();
        _menuFont = new Font("Segoe UI", 9f, FontStyle.Regular, GraphicsUnit.Point);

        var showHideItem = new ToolStripMenuItem("Show / Hide") { ShortcutKeyDisplayString = "Ctrl+Shift+H" };
        showHideItem.Click += (_, _) => toggleVisibility();

        _clickThroughItem = new ToolStripMenuItem("Toggle Click-Through") { ShortcutKeyDisplayString = "Ctrl+Shift+T" };
        _clickThroughItem.Click += (_, _) => toggleClickThrough();

        var shortcutsItem = new ToolStripMenuItem("Keyboard Shortcuts") { ShortcutKeyDisplayString = "Ctrl+Alt+Shift+/" };
        shortcutsItem.Click += (_, _) => showShortcuts();

        var exitItem = new ToolStripMenuItem("Exit");
        exitItem.Click += (_, _) => exit();

        _menu = new ContextMenuStrip
        {
            Renderer = new DarkMenuRenderer(),
            BackColor = Background,
            ForeColor = Foreground,
            Font = _menuFont,
            ShowImageMargin = false,
            ShowCheckMargin = true,
            Padding = new Padding(2),
        };
        _menu.Items.AddRange(new ToolStripItem[] { showHideItem, _clickThroughItem, shortcutsItem, new ToolStripSeparator(), exitItem });

        foreach (ToolStripItem item in _menu.Items)
        {
            item.ForeColor = Foreground;
            item.BackColor = Background;
            item.Padding = new Padding(4, 3, 4, 3);
        }

        _notifyIcon = new NotifyIcon
        {
            Icon = _icon,
            Text = "StealthDeck",
            ContextMenuStrip = _menu,
            Visible = true,
        };
        _notifyIcon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                toggleVisibility();
            }
        };
    }

    public void SetClickThroughChecked(bool isChecked)
    {
        if (!_disposed)
        {
            _clickThroughItem.Checked = isChecked;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _menu.Dispose();
        _menuFont.Dispose();
        _icon.Dispose();
        NativeMethods.DestroyIcon(_hIcon);
    }

    /// <summary>Draws a rounded accent tile with a white "S" — no external .ico asset required.</summary>
    private static (Icon Icon, IntPtr Handle) CreateIcon()
    {
        using var bitmap = new Bitmap(32, 32, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAlias;
            g.Clear(Color.Transparent);

            using GraphicsPath tile = RoundedRectangle(new RectangleF(1, 1, 30, 30), 8);
            using var fill = new SolidBrush(Accent);
            g.FillPath(fill, tile);

            using var font = new Font("Segoe UI", 20f, FontStyle.Bold, GraphicsUnit.Pixel);
            using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString("S", font, Brushes.White, new RectangleF(0, 1, 32, 32), format);
        }

        IntPtr hIcon = bitmap.GetHicon();
        return (Icon.FromHandle(hIcon), hIcon);
    }

    internal static GraphicsPath RoundedRectangle(RectangleF bounds, float radius)
    {
        float d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(bounds.Left, bounds.Top, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Top, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
    {
        public DarkMenuRenderer() : base(new DarkMenuColors())
        {
            RoundedEdges = false;
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Foreground : Muted;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            Rectangle r = e.ImageRectangle;
            if (r.Width <= 0 || r.Height <= 0)
            {
                return;
            }

            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            using (GraphicsPath box = RoundedRectangle(r, 3))
            using (var brush = new SolidBrush(Accent))
            {
                g.FillPath(brush, box);
            }

            using var pen = new Pen(Color.White, 1.8f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            g.DrawLines(pen, new[]
            {
                new PointF(r.Left + r.Width * 0.24f, r.Top + r.Height * 0.52f),
                new PointF(r.Left + r.Width * 0.43f, r.Top + r.Height * 0.70f),
                new PointF(r.Left + r.Width * 0.76f, r.Top + r.Height * 0.32f),
            });
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            int y = e.Item.Height / 2;
            using var pen = new Pen(BorderColor);
            e.Graphics.DrawLine(pen, 6, y, e.Item.Width - 6, y);
        }
    }

    private sealed class DarkMenuColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Background;
        public override Color MenuBorder => BorderColor;
        public override Color MenuItemBorder => Surface;
        public override Color MenuItemSelected => Surface;
        public override Color MenuItemSelectedGradientBegin => Surface;
        public override Color MenuItemSelectedGradientEnd => Surface;
        public override Color MenuItemPressedGradientBegin => Surface;
        public override Color MenuItemPressedGradientMiddle => Surface;
        public override Color MenuItemPressedGradientEnd => Surface;
        public override Color ImageMarginGradientBegin => Background;
        public override Color ImageMarginGradientMiddle => Background;
        public override Color ImageMarginGradientEnd => Background;
        public override Color SeparatorDark => BorderColor;
        public override Color SeparatorLight => BorderColor;
        public override Color CheckBackground => Accent;
        public override Color CheckSelectedBackground => Accent;
        public override Color CheckPressedBackground => Accent;
    }
}
