using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace Lexora;

/// <summary>
/// Hover card: category, message, clickable replacements, Dismiss / Add to dictionary / Rewrite.
/// Custom-painted and non-activating, so the app you are typing in keeps keyboard focus.
/// </summary>
sealed class SuggestionCard : Form
{
    enum ActionKind { Replace, Dismiss, AddToDictionary, Rewrite }
    sealed record HitRegion(Rectangle Bounds, ActionKind Action, string Value = "");

    readonly List<HitRegion> _regions = [];
    HitRegion? _hovered;
    float _scale = 1f;

    public Mark? Mark { get; private set; }

    public event Action<Mark, string>? ReplaceClicked;
    public event Action<Mark>? DismissClicked;
    public event Action<Mark>? AddToDictionaryClicked;
    public event Action<Mark>? RewriteClicked;

    /// <summary>Whether to offer the AI "Rewrite" action.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Func<bool> AiAvailable { get; set; } = () => false;

    public SuggestionCard()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        BackColor = Theme.Bg;
        DoubleBuffered = true;
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW | Native.WS_EX_TOPMOST;
            cp.ClassStyle |= Native.CS_DROPSHADOW;
            return cp;
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_MOUSEACTIVATE) { m.Result = Native.MA_NOACTIVATE; return; }
        base.WndProc(ref m);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.RoundCorners(Handle);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e) => e.Cancel = true; // we size ourselves in physical pixels

    int Px(float v) => (int)Math.Round(v * _scale);
    Font MakeFont(float size, FontStyle style = FontStyle.Regular) => Theme.PixelFont(size * _scale, style);

    public void ShowFor(Mark mark)
    {
        Mark = mark;
        BackColor = Theme.Bg; // the Windows theme may have changed
        var anchor = mark.Rects[0];
        _scale = Native.ScaleAt(anchor.Location);

        var size = LayoutCard(null);
        var area = Screen.FromPoint(anchor.Location).WorkingArea;
        int x = Math.Clamp(anchor.Left - Px(12), area.Left + 4, Math.Max(area.Left + 4, area.Right - size.Width - 4));
        int y = anchor.Bottom + Px(8);
        if (y + size.Height > area.Bottom) y = anchor.Top - size.Height - Px(8);

        Bounds = new Rectangle(x, y, size.Width, size.Height);
        _hovered = null;
        if (!Visible) Show();
        Native.BringToTopmost(Handle);
        Invalidate();
    }

    public void HideCard()
    {
        Mark = null;
        if (Visible) Hide();
    }

    protected override void OnPaint(PaintEventArgs e) => LayoutCard(e.Graphics);

    /// <summary>Lays out (and paints, when g is given) the card. Returns its size in pixels.</summary>
    Size LayoutCard(Graphics? g)
    {
        _regions.Clear();
        if (Mark == null) return Size.Empty;
        var issue = Mark.Issue;
        var kindColor = Theme.For(issue.Kind);
        int stripe = Px(4), pad = Px(16), width = Px(340), left = stripe + pad, inner = width - left - pad, y = pad;

        using var small = MakeFont(11.5f, FontStyle.Bold);
        using var badgeFont = MakeFont(10.5f, FontStyle.Bold);
        using var body = MakeFont(13.5f);
        using var chipFont = MakeFont(14.5f, FontStyle.Bold);
        using var strike = MakeFont(14.5f, FontStyle.Strikeout);
        using var footer = MakeFont(12.5f);
        var wrap = TextFormatFlags.NoPadding | TextFormatFlags.WordBreak;

        if (g != null)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.Clear(Theme.Bg);
            using var stripeBrush = new SolidBrush(kindColor);
            g.FillRectangle(stripeBrush, 0, 0, stripe, Height);
            using var border = new Pen(Theme.Border);
            g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
        }

        // Header: category pill + AI badge
        string category = issue.Title.ToUpperInvariant();
        var catSize = TextRenderer.MeasureText(category, small, Size.Empty, TextFormatFlags.NoPadding);
        int pillH = catSize.Height + Px(8), dot = Px(7);
        var catPill = new Rectangle(left, y, catSize.Width + dot + Px(22), pillH);
        if (g != null)
        {
            using var pillBrush = new SolidBrush(Theme.Tint(kindColor, Theme.Dark ? 0.22f : 0.12f));
            using var pillPath = Theme.Rounded(catPill, pillH / 2f);
            g.FillPath(pillBrush, pillPath);
            using var dotBrush = new SolidBrush(kindColor);
            g.FillEllipse(dotBrush, catPill.Left + Px(9), catPill.Top + (pillH - dot) / 2f, dot, dot);
            TextRenderer.DrawText(g, category, small, new Point(catPill.Left + Px(9) + dot + Px(6), catPill.Top + Px(4)), kindColor, TextFormatFlags.NoPadding);
        }
        if (issue.IsAi)
        {
            const string label = "✦ AI";
            var badgeSize = TextRenderer.MeasureText(label, badgeFont, Size.Empty, TextFormatFlags.NoPadding);
            var badge = new Rectangle(catPill.Right + Px(8), y, badgeSize.Width + Px(16), pillH);
            if (g != null)
            {
                using var path = Theme.Rounded(badge, pillH / 2f);
                using var brush = Theme.BrandBrush(badge);
                g.FillPath(brush, path);
                TextRenderer.DrawText(g, label, badgeFont, badge, Color.White,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
        }
        y += pillH + Px(14);

        // Suggestion row: struck-through original → chips
        if (issue.Replacements.Count > 0)
        {
            int x = left;
            string shownOriginal = issue.ErrorText.Trim();
            bool isRemoval = issue.Replacements[0].Trim().Length == 0;
            var originalSize = TextRenderer.MeasureText(shownOriginal, strike, Size.Empty, TextFormatFlags.NoPadding);
            int chipH = chipFont.Height + Px(10);
            if (originalSize.Width < inner / 2)
            {
                if (g != null)
                    TextRenderer.DrawText(g, shownOriginal, strike, new Point(x, y + (chipH - originalSize.Height) / 2),
                        isRemoval ? Theme.Correctness : Theme.Muted, TextFormatFlags.NoPadding);
                x += originalSize.Width + Px(8);
                if (g != null)
                    TextRenderer.DrawText(g, "→", body, new Point(x, y + (chipH - body.Height) / 2), Theme.Muted, TextFormatFlags.NoPadding);
                x += TextRenderer.MeasureText("→", body, Size.Empty, TextFormatFlags.NoPadding).Width + Px(8);
            }

            bool first = true;
            foreach (var replacement in issue.Replacements.Take(4))
            {
                bool removal = replacement.Trim().Length == 0;
                string label = removal ? "Remove" : replacement;
                int chipW = TextRenderer.MeasureText(label, chipFont, Size.Empty, TextFormatFlags.NoPadding).Width + Px(24);
                if (x + chipW > left + inner && x > left) { x = left; y += chipH + Px(8); }
                var chip = new Rectangle(x, y, Math.Min(chipW, inner), chipH);
                _regions.Add(new HitRegion(chip, ActionKind.Replace, replacement));
                if (g != null) DrawChip(g, chip, label, chipFont, first, removal, _hovered?.Bounds == chip);
                x += chip.Width + Px(8);
                first = false;
            }
            y += chipH + Px(14);
        }

        // Explanation
        string message = issue.Message.Replace("\n", " ");
        var msgSize = TextRenderer.MeasureText(message, body, new Size(inner, int.MaxValue), wrap);
        if (g != null)
            TextRenderer.DrawText(g, message, body, new Rectangle(left, y, inner, msgSize.Height), Theme.Text, wrap);
        y += msgSize.Height + Px(14);

        // Divider + footer actions
        if (g != null)
        {
            using var line = new Pen(Theme.Border);
            g.DrawLine(line, left, y, width - pad, y);
        }
        y += Px(8);
        int fx = left - Px(8);
        int footerH = footer.Height + Px(12);
        foreach (var (icon, label, action) in FooterActions(issue, AiAvailable()))
        {
            string text = $"{icon}  {label}";
            var sz = TextRenderer.MeasureText(text, footer, Size.Empty, TextFormatFlags.NoPadding);
            var rect = new Rectangle(fx, y, sz.Width + Px(16), footerH);
            _regions.Add(new HitRegion(rect, action));
            if (g != null)
            {
                bool hot = _hovered?.Bounds == rect;
                if (hot)
                {
                    using var hotBg = new SolidBrush(Theme.Surface);
                    using var path = Theme.Rounded(rect, Px(7));
                    g.FillPath(hotBg, path);
                }
                var color = action == ActionKind.Rewrite ? Theme.BrandText : hot ? Theme.Text : Theme.Muted;
                TextRenderer.DrawText(g, text, footer, rect, color,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
            fx += rect.Width + Px(4);
        }
        y += footerH + Px(10);

        return new Size(width, y);
    }

    void DrawChip(Graphics g, Rectangle chip, string label, Font font, bool primary, bool removal, bool hot)
    {
        using var path = Theme.Rounded(chip, Px(8));
        Color textColor;
        if (primary)
        {
            if (removal)
            {
                using var fill = new SolidBrush(Theme.Correctness);
                g.FillPath(fill, path);
            }
            else
            {
                using var fill = Theme.BrandBrush(chip);
                g.FillPath(fill, path);
            }
            if (hot)
            {
                using var shine = new SolidBrush(Color.FromArgb(35, Color.White));
                g.FillPath(shine, path);
            }
            textColor = Color.White;
        }
        else
        {
            using var fill = new SolidBrush(hot ? Theme.SurfaceHover : Theme.Surface);
            g.FillPath(fill, path);
            using var pen = new Pen(Theme.Border);
            g.DrawPath(pen, path);
            textColor = Theme.Text;
        }
        TextRenderer.DrawText(g, label, font, chip, textColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
    }

    static IEnumerable<(string Icon, string Label, ActionKind Action)> FooterActions(Issue issue, bool ai)
    {
        yield return ("✕", "Dismiss", ActionKind.Dismiss);
        if (issue.IsSpelling) yield return ("＋", "Add to dictionary", ActionKind.AddToDictionary);
        if (ai) yield return ("✦", "Rewrite", ActionKind.Rewrite);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var hit = _regions.Find(r => r.Bounds.Contains(e.Location));
        if (hit != _hovered)
        {
            _hovered = hit;
            Cursor = hit != null ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        if (_hovered != null) { _hovered = null; Invalidate(); }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || Mark is not { } mark) return;
        var hit = _regions.Find(r => r.Bounds.Contains(e.Location));
        if (hit == null) return;
        HideCard();
        switch (hit.Action)
        {
            case ActionKind.Replace: ReplaceClicked?.Invoke(mark, hit.Value); break;
            case ActionKind.Dismiss: DismissClicked?.Invoke(mark); break;
            case ActionKind.AddToDictionary: AddToDictionaryClicked?.Invoke(mark); break;
            case ActionKind.Rewrite: RewriteClicked?.Invoke(mark); break;
        }
    }
}
