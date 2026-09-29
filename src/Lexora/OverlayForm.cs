namespace Lexora;

/// <summary>
/// Full-screen, transparent, click-through window that draws underlines under issues in other apps.
/// It never takes focus or mouse input, so the app underneath works normally.
/// </summary>
sealed class OverlayForm : Form
{
    static readonly Color Key = Color.FromArgb(255, 0, 255);
    IReadOnlyList<Mark> _marks = [];
    int _hoveredId = -1;

    public OverlayForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        BackColor = Key;
        TransparencyKey = Key;
        DoubleBuffered = true;
        Bounds = SystemInformation.VirtualScreen;
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= Native.WS_EX_LAYERED | Native.WS_EX_TRANSPARENT | Native.WS_EX_TOOLWINDOW |
                          Native.WS_EX_NOACTIVATE | Native.WS_EX_TOPMOST;
            return cp;
        }
    }

    // Coordinates are raw physical pixels; stop WinForms from rescaling this window between monitors.
    protected override void OnDpiChanged(DpiChangedEventArgs e) => e.Cancel = true;

    public void SetMarks(IReadOnlyList<Mark> marks, int hoveredId)
    {
        _marks = marks;
        _hoveredId = hoveredId;
        if (marks.Count == 0)
        {
            if (Visible) Hide();
            return;
        }
        if (Bounds != SystemInformation.VirtualScreen) Bounds = SystemInformation.VirtualScreen;
        if (!Visible) Show();
        Native.BringToTopmost(Handle);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        foreach (var mark in _marks)
        {
            using var brush = new SolidBrush(Theme.For(mark.Issue.Kind));
            foreach (var r in mark.Rects)
            {
                float scale = Native.ScaleAt(new Point(r.Left, r.Bottom));
                int thickness = Math.Max(2, (int)Math.Round((mark.Id == _hoveredId ? 3 : 2) * scale));
                g.FillRectangle(brush, r.Left - Left, r.Bottom - Top - thickness + 1, r.Width, thickness);
            }
        }
    }
}
