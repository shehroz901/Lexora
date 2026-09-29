namespace Lexora;

/// <summary>
/// Lexora AI window: Improve / Shorter / Formal / Friendly / Fix grammar / To English (Roman Urdu),
/// prompt optimizers (Game Dev, ASO &amp; UA), and a tone check of the original text.
/// </summary>
sealed class AiRewriteForm : Form
{
    readonly string _original;
    readonly AiClient _ai;
    readonly TextBox _result = new() { Multiline = true, ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.None };
    readonly Label _tone = new() { AutoSize = true, Margin = new Padding(2, 8, 0, 0) };
    readonly Label _status = new() { AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(2, 12, 0, 0) };
    readonly List<(PillButton Button, RewriteMode Mode)> _modes = [];
    readonly PillButton _replace = new("Replace in app   Ctrl+Enter", PillButton.Kind.Primary) { Enabled = false };
    CancellationTokenSource? _running;
    bool _userPicked; // a mode clicked by the user wins over the automatic first run

    public event Action<string>? ReplaceRequested;

    public string ResultText => _result.Text;
    public string ToneText => _tone.Text;

    public AiRewriteForm(string original, AiClient ai)
    {
        _original = original.Trim();
        _ai = ai;

        Text = "Lexora AI";
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96, 96);
        Font = Theme.UiFont(10f);
        ClientSize = new Size(720, 720);
        MinimumSize = new Size(520, 560);
        TopMost = true;
        KeyPreview = true;
        Theme.Apply(this);

        _tone.ForeColor = Theme.Muted;
        _tone.Font = Theme.UiFont(9f);
        _tone.Text = "◌  Checking tone…";
        _status.ForeColor = Theme.BrandText;
        _status.Font = Theme.UiFont(9.5f, FontStyle.Bold);
        _result.Font = Theme.UiFont(10.5f);

        var originalBox = new TextBox
        {
            Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.None,
            Font = Theme.UiFont(10f), Text = _original.Replace("\r\n", "\n").Replace("\n", Environment.NewLine),
        };

        var rewriteGroup = ModeGroup(AiClient.RewriteModes.Where(m => !m.IsPrompt));
        var promptGroup = ModeGroup(AiClient.RewriteModes.Where(m => m.IsPrompt));

        var copy = new PillButton("Copy");
        var close = new PillButton("Close", PillButton.Kind.Ghost);
        _replace.Click += (_, _) => DoReplace();
        copy.Click += (_, _) =>
        {
            if (_result.Text.Length == 0) return;
            Clipboard.SetText(_result.Text);
            _status.Text = "✓ Copied";
        };
        close.Click += (_, _) => Close();

        var actions = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, Margin = Padding.Empty, WrapContents = false };
        actions.Controls.AddRange([_replace, copy, close]);
        var bottom = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true, Margin = new Padding(0, 10, 0, 0) };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.Controls.Add(_status, 0, 0);
        bottom.Controls.Add(actions, 1, 0);

        var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(18, 12, 18, 14), BackColor = Theme.Bg };
        void Row(Control c, SizeType type = SizeType.AutoSize, float size = 0)
        {
            body.RowStyles.Add(new RowStyle(type, size));
            body.Controls.Add(c, 0, body.RowStyles.Count - 1);
        }
        Row(new SectionLabel("Your text"));
        Row(new RoundedPanel(originalBox), SizeType.Percent, 26);
        Row(_tone);
        Row(new SectionLabel("Rewrite"));
        Row(rewriteGroup);
        Row(new SectionLabel("Prompt optimizer"));
        Row(promptGroup);
        Row(new SectionLabel("Result · you can edit it"));
        Row(new RoundedPanel(_result), SizeType.Percent, 74);
        Row(bottom);

        Controls.Add(body);
        Controls.Add(new GradientHeader("Lexora AI", "Rewrite, translate and optimize prompts — fully offline"));

        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape) Close();
            if (e.KeyCode == Keys.Enter && e.Control) { e.SuppressKeyPress = true; DoReplace(); }
        };
        FormClosed += (_, _) => _running?.Cancel();
        Shown += async (_, _) => { Activate(); await AnalyzeThenRewriteAsync(); };
    }

    FlowLayoutPanel ModeGroup(IEnumerable<RewriteMode> modes)
    {
        var panel = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true, Margin = Padding.Empty, BackColor = Theme.Bg };
        foreach (var mode in modes)
        {
            var button = new PillButton(mode.Name, PillButton.Kind.Toggle);
            button.Click += (_, _) => { _userPicked = true; _ = RunModeAsync(mode); };
            _modes.Add((button, mode));
            panel.Controls.Add(button);
        }
        return panel;
    }

    /// <summary>Checks tone + language first; non-English text (e.g. Roman Urdu) starts with "To English", English with "Improve".</summary>
    async Task AnalyzeThenRewriteAsync()
    {
        var analysis = await _ai.AnalyzeAsync(_original);
        if (IsDisposed) return;
        bool english = analysis?.IsEnglish ?? true;
        _tone.Text = analysis is { } a
            ? $"◉  Tone: {a.Tone}" + (a.IsEnglish ? "" : "     ·     Roman Urdu / non-English detected")
            : "◉  Tone: unknown";
        if (!_userPicked) await RunModeAsync(_modes.First(m => m.Mode.Name == (english ? "Improve" : "To English")).Mode);
    }

    async Task RunModeAsync(RewriteMode mode)
    {
        _running?.Cancel();
        var cts = _running = new CancellationTokenSource();
        foreach (var (button, m) in _modes) button.Selected = m == mode;
        _status.Text = mode.IsPrompt ? $"✦ Writing {mode.Name.Split(' ', 2).Last()}…" : $"✦ {mode.Name}: writing…";
        _replace.Enabled = false;
        _result.Clear();
        try
        {
            // Show the answer as it is written (long prompts take a while on a laptop GPU).
            var text = await _ai.RewriteAsync(_original, mode, delta => BeginInvoke(() =>
            {
                if (!cts.IsCancellationRequested && !IsDisposed)
                    _result.AppendText(delta.Replace("\r\n", "\n").Replace("\n", Environment.NewLine));
            }), cts.Token);
            if (cts.IsCancellationRequested || IsDisposed) return;
            if (text == null) { _status.Text = "AI did not answer. Is it on? (tray menu)"; return; }
            if (mode.IsPrompt) text = StripMetaNotes(text);
            _result.Text = text.Trim().Trim('"').Replace("\r\n", "\n").Replace("\n", Environment.NewLine);
            _status.Text = "";
            _replace.Enabled = true;
            _replace.Focus();
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>Small models sometimes end with a note like "(Only return this prompt – no extra text.)"; drop it.</summary>
    static string StripMetaNotes(string text)
    {
        var lines = text.TrimEnd().Split('\n').ToList();
        while (lines.Count > 1 && lines[^1].Trim() is var last &&
               (last.Length == 0 || last.StartsWith('(') && last.EndsWith(')') && last.Contains("prompt", StringComparison.OrdinalIgnoreCase)))
            lines.RemoveAt(lines.Count - 1);
        return string.Join('\n', lines);
    }

    /// <summary>Used by the self-test to exercise a mode without clicking.</summary>
    public Task RunModeByNameAsync(string name)
    {
        _userPicked = true;
        return RunModeAsync(_modes.First(m => m.Mode.Name == name).Mode);
    }

    void DoReplace()
    {
        if (!_replace.Enabled || _result.Text.Trim().Length == 0) return;
        ReplaceRequested?.Invoke(_result.Text.Replace("\r\n", "\n"));
        Close();
    }
}
