namespace Lexora;

/// <summary>
/// Window shown by the Ctrl+Alt+G hotkey: lists issues in the selected text and builds the corrected version.
/// Works in any app, even ones where live underlines are not possible.
/// </summary>
sealed class SelectionCheckForm : Form
{
    readonly string _original;
    readonly List<Issue> _issues;
    readonly CheckedListBox _list = new() { CheckOnClick = true, IntegralHeight = false, BorderStyle = BorderStyle.None };
    readonly TextBox _result = new() { Multiline = true, ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.None };

    public event Action<string>? ReplaceRequested;

    public SelectionCheckForm(string original, List<Issue> issues)
    {
        _original = original;
        _issues = issues.Where(i => i.Replacements.Count > 0).ToList();

        Text = "Lexora — Grammar check";
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96, 96);
        Font = Theme.UiFont(10f);
        ClientSize = new Size(660, 600);
        MinimumSize = new Size(460, 420);
        TopMost = true;
        KeyPreview = true;
        Theme.Apply(this);

        _list.Font = Theme.UiFont(10f);
        _result.Font = Theme.UiFont(10.5f);
        foreach (var issue in _issues)
        {
            string fix = issue.Replacements[0].Trim().Length == 0 ? "(remove)" : issue.Replacements[0];
            _list.Items.Add($"{issue.ErrorText.Trim()}  →  {fix}      {issue.Title}", true);
        }
        _list.ItemCheck += (_, _) => BeginInvoke(RebuildResult);

        var replace = new PillButton("Replace in app   Ctrl+Enter", PillButton.Kind.Primary);
        var copy = new PillButton("Copy");
        var close = new PillButton("Close", PillButton.Kind.Ghost);
        replace.Click += (_, _) => DoReplace();
        copy.Click += (_, _) => { Clipboard.SetText(_result.Text); Close(); };
        close.Click += (_, _) => Close();

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, WrapContents = false, Margin = new Padding(0, 10, 0, 0) };
        buttons.Controls.AddRange([replace, copy, close]);

        var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(18, 12, 18, 14), BackColor = Theme.Bg };
        void Row(Control c, SizeType type = SizeType.AutoSize, float size = 0)
        {
            body.RowStyles.Add(new RowStyle(type, size));
            body.Controls.Add(c, 0, body.RowStyles.Count - 1);
        }
        Row(new SectionLabel("Suggestions · untick to skip"));
        Row(new RoundedPanel(_list), SizeType.Percent, 45);
        Row(new SectionLabel("Corrected text · you can edit it"));
        Row(new RoundedPanel(_result), SizeType.Percent, 55);
        Row(buttons);

        Controls.Add(body);
        Controls.Add(new GradientHeader("Grammar check", $"{issues.Count} issue{(issues.Count == 1 ? "" : "s")} found in your selection"));

        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape) Close();
            if (e.KeyCode == Keys.Enter && e.Control) { e.SuppressKeyPress = true; DoReplace(); }
        };

        RebuildResult();
        Shown += (_, _) => { Activate(); replace.Focus(); };
    }

    void RebuildResult()
    {
        var text = _original;
        // Apply from the end so earlier offsets stay valid; skip overlapping issues.
        int limit = int.MaxValue;
        for (int i = _issues.Count - 1; i >= 0; i--)
        {
            var issue = _issues[i];
            if (!_list.GetItemChecked(i) || issue.Offset + issue.Length > limit) continue;
            text = text.Remove(issue.Offset, issue.Length).Insert(issue.Offset, issue.Replacements[0]);
            limit = issue.Offset;
        }
        _result.Text = text.Replace("\r\n", "\n").Replace("\n", Environment.NewLine);
    }

    void DoReplace()
    {
        ReplaceRequested?.Invoke(_result.Text);
        Close();
    }
}
