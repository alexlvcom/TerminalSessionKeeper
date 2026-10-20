// AlexLV Windows UI kit. Canonical copy: C:\projects\AlexLV.WindowsUi (see README.md).
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;
using WpfColor = System.Windows.Media.Color;

namespace AlexLV.WindowsUi;

/// <summary>
/// Draws a WinForms tray <see cref="ContextMenuStrip"/> in the active theme: raised background,
/// border, accent-tinted hover, Segoe UI text, Segoe Fluent icon glyphs and rounded corners.
/// Restyles itself when the theme changes. Keeps WinForms menu keyboard handling intact.
/// </summary>
public sealed class TrayMenuStyler : IDisposable
{
    private readonly ContextMenuStrip _menu;
    private readonly Dictionary<ToolStripItem, string> _glyphs = new();
    private readonly List<Image> _images = new();

    public TrayMenuStyler(ContextMenuStrip menu)
    {
        _menu = menu;
        _menu.Font = new Font("Segoe UI", 10f);
        _menu.ShowImageMargin = true;
        _menu.Padding = new Padding(4);
        _menu.Opened += (_, _) => ThemeManager.RoundCorners(_menu.Handle);
        ThemeManager.ThemeChanged += OnThemeChanged;
        Apply();
    }

    /// <summary>Gives an item an icon from Segoe Fluent Icons (for example "\uE72C" Refresh).</summary>
    public void SetGlyph(ToolStripItem item, string glyph)
    {
        _glyphs[item] = glyph;
        Apply();
    }

    private void OnThemeChanged(object? sender, EventArgs e) => Apply();

    public void Apply()
    {
        var colors = new Colors();
        _menu.Renderer = new Renderer(colors);
        _menu.BackColor = colors.Raised;
        _menu.ForeColor = colors.Text;

        foreach (var image in _images) image.Dispose();
        _images.Clear();
        foreach (ToolStripItem item in _menu.Items)
        {
            item.ForeColor = colors.Text;
            item.Padding = new Padding(2, 5, 2, 5);
            if (_glyphs.TryGetValue(item, out var glyph))
            {
                var image = RenderGlyph(glyph, colors.Muted);
                _images.Add(image);
                item.Image = image;
            }
        }
    }

    private static Image RenderGlyph(string glyph, Color color)
    {
        var bitmap = new Bitmap(16, 16);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        using var font = new Font("Segoe Fluent Icons", 11f, GraphicsUnit.Pixel);
        using var fallback = new Font("Segoe MDL2 Assets", 11f, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(color);
        var useFont = font.Name == "Segoe Fluent Icons" ? font : fallback;
        var size = graphics.MeasureString(glyph, useFont);
        graphics.DrawString(glyph, useFont, brush, (16 - size.Width) / 2 + 1, (16 - size.Height) / 2 + 1);
        return bitmap;
    }

    public void Dispose()
    {
        ThemeManager.ThemeChanged -= OnThemeChanged;
        foreach (var image in _images) image.Dispose();
        _images.Clear();
    }

    private sealed class Colors
    {
        public Color Raised = Convert(ThemeManager.ColorOf("Raised"));
        public Color Border = Convert(ThemeManager.ColorOf("Border"));
        public Color Text = Convert(ThemeManager.ColorOf("Text"));
        public Color Muted = Convert(ThemeManager.ColorOf("Muted"));
        public Color Accent = Convert(ThemeManager.ColorOf("Accent"));
        public Color Selection = Blend(Convert(ThemeManager.ColorOf("Accent")), Convert(ThemeManager.ColorOf("Raised")), ThemeManager.IsDark ? 0.22 : 0.14);

        private static Color Convert(WpfColor c) => Color.FromArgb(c.A, c.R, c.G, c.B);

        private static Color Blend(Color top, Color bottom, double amount) => Color.FromArgb(
            (int)(top.R * amount + bottom.R * (1 - amount)),
            (int)(top.G * amount + bottom.G * (1 - amount)),
            (int)(top.B * amount + bottom.B * (1 - amount)));
    }

    private sealed class Renderer : ToolStripProfessionalRenderer
    {
        private readonly Colors _c;

        public Renderer(Colors colors) : base(new Table(colors))
        {
            _c = colors;
            RoundedEdges = false;
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected || !e.Item.Enabled) return;
            var bounds = new Rectangle(2, 0, e.Item.Width - 4, e.Item.Height);
            using var path = Rounded(bounds, 5);
            using var brush = new SolidBrush(_c.Selection);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.FillPath(brush, path);
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? _c.Text : _c.Muted;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            // An accent checkmark on the menu itself, never the system's bright highlight square.
            using var font = new Font("Segoe Fluent Icons", 12f, GraphicsUnit.Pixel);
            using var fallback = new Font("Segoe MDL2 Assets", 12f, GraphicsUnit.Pixel);
            var useFont = font.Name == "Segoe Fluent Icons" ? font : fallback;
            var color = e.Item.Enabled ? _c.Accent : _c.Muted;
            TextRenderer.DrawText(e.Graphics, "", useFont, e.ImageRectangle, color,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            var y = e.Item.Height / 2;
            using var pen = new Pen(_c.Border);
            e.Graphics.DrawLine(pen, 8, y, e.Item.Width - 8, y);
        }

        protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
        {
            using var brush = new SolidBrush(_c.Raised);
            e.Graphics.FillRectangle(brush, e.AffectedBounds);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            using var pen = new Pen(_c.Border);
            e.Graphics.DrawRectangle(pen, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
        }

        private static GraphicsPath Rounded(Rectangle r, int radius)
        {
            var path = new GraphicsPath();
            var d = radius * 2;
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    private sealed class Table : ProfessionalColorTable
    {
        private readonly Colors _c;
        public Table(Colors colors) { _c = colors; UseSystemColors = false; }
        public override Color ToolStripDropDownBackground => _c.Raised;
        public override Color ImageMarginGradientBegin => _c.Raised;
        public override Color ImageMarginGradientMiddle => _c.Raised;
        public override Color ImageMarginGradientEnd => _c.Raised;
        public override Color MenuBorder => _c.Border;
        public override Color MenuItemBorder => Color.Transparent;
        public override Color MenuItemSelected => _c.Selection;
        public override Color SeparatorDark => _c.Border;
        public override Color SeparatorLight => _c.Border;
    }
}
