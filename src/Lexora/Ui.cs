using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Lexora;

/// <summary>Lexora design tokens. Follows the Windows light/dark app theme.</summary>
static class Theme
{
    // Brand: indigo → violet
    public static readonly Color BrandStart = Color.FromArgb(99, 102, 241);
    public static readonly Color BrandEnd = Color.FromArgb(168, 85, 247);
    public static readonly Color Brand = Color.FromArgb(129, 92, 246);

    // Issue colors
    public static readonly Color Correctness = Color.FromArgb(244, 63, 94);
    public static readonly Color Clarity = Color.FromArgb(59, 130, 246);
    public static readonly Color Style = Color.FromArgb(168, 85, 247);
    public static readonly Color Success = Color.FromArgb(16, 185, 129);

    public static bool Dark { get; private set; } = DetectDark();

    public static Color Bg => Dark ? Color.FromArgb(21, 21, 30) : Color.White;
    public static Color Surface => Dark ? Color.FromArgb(32, 32, 45) : Color.FromArgb(245, 245, 251);
    public static Color SurfaceHover => Dark ? Color.FromArgb(45, 45, 62) : Color.FromArgb(235, 235, 247);
    public static Color Border => Dark ? Color.FromArgb(55, 55, 74) : Color.FromArgb(225, 225, 238);
    public static Color Text => Dark ? Color.FromArgb(238, 238, 246) : Color.FromArgb(26, 26, 40);
    public static Color Muted => Dark ? Color.FromArgb(152, 152, 176) : Color.FromArgb(104, 104, 128);
    public static Color BrandText => Dark ? Color.FromArgb(180, 160, 255) : Color.FromArgb(99, 70, 230);

    public static Color For(IssueKind kind) => kind switch
    {
        IssueKind.Clarity => Clarity,
        IssueKind.Style => Style,
        _ => Correctness,
    };

    /// <summary>A soft background version of a color, blended onto the current background.</summary>
    public static Color Tint(Color c, float amount = 0.16f) => Color.FromArgb(
        (int)(Bg.R + (c.R - Bg.R) * amount), (int)(Bg.G + (c.G - Bg.G) * amount), (int)(Bg.B + (c.B - Bg.B) * amount));

    public static void Refresh() => Dark = DetectDark();

    static bool DetectDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch { return false; }
    }

    static readonly string FontFamilyName =
        FontFamily.Families.Any(f => f.Name == "Segoe UI Variable Text") ? "Segoe UI Variable Text" : "Segoe UI";
    static readonly string DisplayFamilyName =
        FontFamily.Families.Any(f => f.Name == "Segoe UI Variable Display") ? "Segoe UI Variable Display" : "Segoe UI";

    /// <summary>UI font in points (for auto-scaled forms).</summary>
    public static Font UiFont(float points, FontStyle style = FontStyle.Regular) => new(FontFamilyName, points, style);

    /// <summary>Heading font in points.</summary>
    public static Font DisplayFont(float points, FontStyle style = FontStyle.Bold) => new(DisplayFamilyName, points, style);

    /// <summary>UI font in pixels (for custom-painted, manually scaled surfaces).</summary>
    public static Font PixelFont(float pixels, FontStyle style = FontStyle.Regular) => new(FontFamilyName, pixels, style, GraphicsUnit.Pixel);

    public static LinearGradientBrush BrandBrush(RectangleF r) =>
        new(new RectangleF(r.X - 1, r.Y - 1, r.Width + 2, r.Height + 2), BrandStart, BrandEnd, LinearGradientMode.ForwardDiagonal);

    public static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 0) { path.AddRectangle(r); return path; }
        path.AddArc(r.Left, r.Top, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>The Lexora mark: gradient squircle, a bold "L" and a sparkle.</summary>
    public static void DrawLogo(Graphics g, RectangleF r)
    {
        var oldMode = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var path = Rounded(r, r.Width * 0.27f))
        using (var brush = BrandBrush(r))
            g.FillPath(brush, path);

        float u = r.Width / 16f, x = r.X, y = r.Y;
        g.FillPolygon(Brushes.White, new PointF[]
        {
            new(x + 4.4f * u, y + 3.6f * u), new(x + 7.0f * u, y + 3.6f * u), new(x + 7.0f * u, y + 10.0f * u),
            new(x + 11.6f * u, y + 10.0f * u), new(x + 11.6f * u, y + 12.4f * u), new(x + 4.4f * u, y + 12.4f * u),
        });
        g.FillPolygon(Brushes.White, Sparkle(new PointF(x + 11.4f * u, y + 5.0f * u), 2.5f * u));
        g.SmoothingMode = oldMode;
    }

    public static PointF[] Sparkle(PointF c, float radius)
    {
        var points = new PointF[8];
        for (int i = 0; i < 8; i++)
        {
            double angle = Math.PI / 4 * i - Math.PI / 2;
            float r = i % 2 == 0 ? radius : radius * 0.28f;
            points[i] = new PointF(c.X + (float)Math.Cos(angle) * r, c.Y + (float)Math.Sin(angle) * r);
        }
        return points;
    }

    public static Bitmap LogoBitmap(int size)
    {
        var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        DrawLogo(g, new RectangleF(0.5f, 0.5f, size - 1, size - 1));
        return bmp;
    }

    public static Icon LogoIcon(int size = 64)
    {
        using var bmp = LogoBitmap(size);
        return Icon.FromHandle(bmp.GetHicon());
    }

    /// <summary>Writes a multi-size .ico (PNG entries) of the logo, used as the exe icon.</summary>
    public static void ExportIco(string path)
    {
        int[] sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];
        var pngs = sizes.Select(s =>
        {
            using var bmp = LogoBitmap(s);
            using var ms = new MemoryStream();
            bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            return ms.ToArray();
        }).ToList();

        using var w = new BinaryWriter(File.Create(path));
        w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)sizes.Length);
        int offset = 6 + 16 * sizes.Length;
        for (int i = 0; i < sizes.Length; i++)
        {
            byte dim = (byte)(sizes[i] >= 256 ? 0 : sizes[i]);
            w.Write(dim); w.Write(dim); w.Write((byte)0); w.Write((byte)0);
            w.Write((ushort)1); w.Write((ushort)32); w.Write(pngs[i].Length); w.Write(offset);
            offset += pngs[i].Length;
        }
        foreach (var png in pngs) w.Write(png);
    }

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>Themes a normal window: background, text and the Windows 11 title bar.</summary>
    public static void Apply(Form form)
    {
        form.BackColor = Bg;
        form.ForeColor = Text;
        form.Icon = LogoIcon(32);
        form.HandleCreated += (_, _) =>
        {
            int dark = Dark ? 1 : 0;
            DwmSetWindowAttribute(form.Handle, 20, ref dark, sizeof(int));           // immersive dark title bar
            int caption = ColorTranslator.ToWin32(Bg);
            DwmSetWindowAttribute(form.Handle, 35, ref caption, sizeof(int));        // caption color (Win11)
            int border = ColorTranslator.ToWin32(Border);
            DwmSetWindowAttribute(form.Handle, 34, ref border, sizeof(int));         // border color (Win11)
        };
    }
}

/// <summary>Rounded, custom-painted button in the Lexora style.</summary>
sealed class PillButton : Button
{
    public enum Kind { Primary, Secondary, Ghost, Toggle }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Kind Look { get; set; } = Kind.Secondary;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Selected { get => _selected; set { _selected = value; Invalidate(); } }

    bool _selected, _hover, _down;

    public PillButton(string text, Kind look = Kind.Secondary)
    {
        Text = text;
        Look = look;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        Cursor = Cursors.Hand;
        Padding = new Padding(14, 7, 14, 7);
        Margin = new Padding(0, 0, 8, 8);
        AutoSize = true;
        UseMnemonic = false;
        Font = Theme.UiFont(9.5f, look == Kind.Primary ? FontStyle.Bold : FontStyle.Regular);
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        var text = TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        return new Size(text.Width + Padding.Horizontal + 4, text.Height + Padding.Vertical);
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = _down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Bg);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        using var path = Theme.Rounded(r, Height / 2f);

        Color textColor;
        bool filled = Look == Kind.Primary || (Look == Kind.Toggle && Selected);
        if (filled && Enabled)
        {
            using var brush = Theme.BrandBrush(r);
            g.FillPath(brush, path);
            if (_hover || _down)
            {
                using var shine = new SolidBrush(Color.FromArgb(_down ? 40 : 25, Color.White));
                g.FillPath(shine, path);
            }
            textColor = Color.White;
        }
        else
        {
            var fill = Look switch
            {
                Kind.Ghost => _hover ? Theme.Surface : Parent?.BackColor ?? Theme.Bg,
                Kind.Toggle => _hover ? Theme.SurfaceHover : Theme.Tint(Theme.Brand, Theme.Dark ? 0.18f : 0.10f),
                _ => _hover ? Theme.SurfaceHover : Theme.Surface,
            };
            using var brush = new SolidBrush(Enabled ? fill : Theme.Surface);
            g.FillPath(brush, path);
            if (Look == Kind.Secondary)
            {
                using var pen = new Pen(Theme.Border);
                g.DrawPath(pen, path);
            }
            textColor = !Enabled ? Theme.Muted : Look switch
            {
                Kind.Toggle => Theme.BrandText,
                Kind.Ghost => Theme.Muted,
                _ => Theme.Text,
            };
        }
        TextRenderer.DrawText(g, Text, Font, ClientRectangle, textColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
    }
}

/// <summary>Rounded "card" that hosts a borderless control (text box, list) with padding.</summary>
sealed class RoundedPanel : Panel
{
    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    static extern int SetWindowTheme(IntPtr hwnd, string? subAppName, string? subIdList);

    readonly Control _inner;

    public RoundedPanel(Control inner)
    {
        _inner = inner;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Padding = new Padding(12, 10, 8, 10);
        Margin = new Padding(0, 0, 0, 4);
        Dock = DockStyle.Fill;
        BackColor = Theme.Bg;
        inner.Dock = DockStyle.Fill;
        inner.BackColor = Theme.Surface;
        inner.ForeColor = Theme.Text;
        inner.HandleCreated += (_, _) => { if (Theme.Dark) SetWindowTheme(inner.Handle, "DarkMode_Explorer", null); };
        inner.GotFocus += (_, _) => Invalidate();
        inner.LostFocus += (_, _) => Invalidate();
        Controls.Add(inner);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Bg);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        using var path = Theme.Rounded(r, 10);
        using var fill = new SolidBrush(Theme.Surface);
        g.FillPath(fill, path);
        bool focused = _inner.Focused && _inner is TextBox { ReadOnly: false };
        using var pen = new Pen(focused ? Theme.Brand : Theme.Border, focused ? 1.6f : 1f);
        g.DrawPath(pen, path);
    }
}

/// <summary>Gradient banner with the logo, a title and a subtitle.</summary>
sealed class GradientHeader : Control
{
    readonly string _title, _subtitle;

    public GradientHeader(string title, string subtitle)
    {
        _title = title;
        _subtitle = subtitle;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Dock = DockStyle.Top;
        Height = 68;
        Font = Theme.DisplayFont(13f);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        using (var brush = new LinearGradientBrush(ClientRectangle, Theme.BrandStart, Theme.BrandEnd, LinearGradientMode.Horizontal))
            g.FillRectangle(brush, ClientRectangle);

        // Soft decorative sparkles on the right
        using (var faint = new SolidBrush(Color.FromArgb(38, Color.White)))
        {
            g.FillPolygon(faint, Theme.Sparkle(new PointF(Width - Height * 0.9f, Height * 0.42f), Height * 0.34f));
            g.FillPolygon(faint, Theme.Sparkle(new PointF(Width - Height * 0.42f, Height * 0.72f), Height * 0.17f));
        }

        float logo = Height * 0.56f, pad = Height * 0.22f;
        using (var shadow = new SolidBrush(Color.FromArgb(40, 0, 0, 40)))
        using (var shadowPath = Theme.Rounded(new RectangleF(pad + 1, (Height - logo) / 2 + 2, logo, logo), logo * 0.27f))
            g.FillPath(shadow, shadowPath);
        using (var ring = new Pen(Color.FromArgb(90, Color.White), 1.5f))
        using (var ringPath = Theme.Rounded(new RectangleF(pad, (Height - logo) / 2, logo, logo), logo * 0.27f))
        {
            Theme.DrawLogo(g, new RectangleF(pad, (Height - logo) / 2, logo, logo));
            g.DrawPath(ring, ringPath);
        }

        using var sub = Theme.UiFont(9f);
        int textX = (int)(pad * 2 + logo);
        var titleSize = TextRenderer.MeasureText(_title, Font);
        int top = (Height - titleSize.Height - sub.Height) / 2;
        TextRenderer.DrawText(g, _title, Font, new Point(textX, top), Color.White, TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, _subtitle, sub, new Point(textX + 1, top + titleSize.Height), Color.FromArgb(225, 225, 255), TextFormatFlags.NoPadding);
    }
}

/// <summary>Small muted section label.</summary>
sealed class SectionLabel : Label
{
    public SectionLabel(string text)
    {
        Text = text.ToUpperInvariant();
        AutoSize = true;
        ForeColor = Theme.Muted;
        Font = Theme.UiFont(8f, FontStyle.Bold);
        Margin = new Padding(2, 10, 0, 6);
    }
}

/// <summary>Tray menu in the Lexora style (follows light/dark).</summary>
sealed class ThemedMenuRenderer() : ToolStripProfessionalRenderer(new Palette())
{
    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? Theme.Text : Theme.Muted;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        e.ArrowColor = Theme.Muted;
        base.OnRenderArrow(e);
    }

    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = e.ImageRectangle;
        r.Inflate(-1, -1);
        using (var path = Theme.Rounded(r, 4))
        using (var brush = Theme.BrandBrush(r))
            g.FillPath(brush, path);
        using var pen = new Pen(Color.White, Math.Max(1.6f, r.Width / 9f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        g.DrawLines(pen, [
            new PointF(r.Left + r.Width * 0.26f, r.Top + r.Height * 0.52f),
            new PointF(r.Left + r.Width * 0.43f, r.Top + r.Height * 0.68f),
            new PointF(r.Left + r.Width * 0.75f, r.Top + r.Height * 0.34f),
        ]);
    }

    sealed class Palette : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Theme.Bg;
        public override Color ImageMarginGradientBegin => Theme.Bg;
        public override Color ImageMarginGradientMiddle => Theme.Bg;
        public override Color ImageMarginGradientEnd => Theme.Bg;
        public override Color MenuBorder => Theme.Border;
        public override Color MenuItemBorder => Theme.SurfaceHover;
        public override Color MenuItemSelected => Theme.SurfaceHover;
        public override Color MenuItemSelectedGradientBegin => Theme.SurfaceHover;
        public override Color MenuItemSelectedGradientEnd => Theme.SurfaceHover;
        public override Color MenuItemPressedGradientBegin => Theme.Surface;
        public override Color MenuItemPressedGradientEnd => Theme.Surface;
        public override Color SeparatorDark => Theme.Border;
        public override Color SeparatorLight => Theme.Bg;
        public override Color CheckBackground => Theme.Brand;
        public override Color CheckSelectedBackground => Theme.Brand;
        public override Color CheckPressedBackground => Theme.Brand;
    }
}
