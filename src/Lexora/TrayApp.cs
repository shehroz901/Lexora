using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace Lexora;

/// <summary>Tray application: owns the engine, the watcher, the overlay/card and the global hotkey.</summary>
sealed class TrayApp : ApplicationContext
{
    const string AppName = "Lexora";
    const string HotkeyLabel = "Ctrl+Alt+G";
    const string RewriteHotkeyLabel = "Ctrl+Alt+R";

    readonly Settings _settings = Settings.Load();
    readonly PersonalDictionary _dictionary = new();
    readonly LanguageToolClient _client;
    readonly EngineServer _engine;
    readonly TextWatcher _watcher;
    readonly OverlayForm _overlay = new();
    readonly SuggestionCard _card = new();
    readonly NotifyIcon _tray;
    readonly ToolStripMenuItem _statusItem = new("Starting engine…") { Enabled = false };
    readonly System.Windows.Forms.Timer _hoverTimer = new() { Interval = 50 };
    readonly HotkeyWindow _hotkey, _rewriteHotkey;
    readonly AiServer _ai;
    readonly AiClient _aiClient;
    readonly ToolStripMenuItem _aiStatusItem = new("AI: off") { Enabled = false };

    IReadOnlyList<Mark> _marks = [];
    int _candidateId = -1;
    DateTime _candidateSince, _leftCardAt = DateTime.MaxValue;
    bool _selectionCheckRunning;

    readonly bool _selfTest;

    public TrayApp(bool selfTest = false)
    {
        _selfTest = selfTest;
        TextWatcher.AllowOwnProcess = selfTest;
        _client = new LanguageToolClient(_settings.Port, () => _settings.DisabledRules);
        _engine = new EngineServer(_settings.Port, _settings.MaxEngineMemoryMb);
        _ai = new AiServer(_settings.AiPort, _settings.AiContextSize);
        _aiClient = new AiClient(_settings.AiPort);
        _watcher = new TextWatcher(_settings, _dictionary, _client, _engine, new AiReviewer(_aiClient), _ai);

        // Create window handles up front so the worker thread can marshal onto the UI thread.
        _ = _overlay.Handle;
        _ = _card.Handle;

        _watcher.MarksChanged += marks => _overlay.BeginInvoke(() => OnMarksChanged(marks));
        _card.ReplaceClicked += (mark, value) => _watcher.Apply(mark.Id, value);
        _card.DismissClicked += mark => _watcher.Dismiss(mark.Id);
        _card.AddToDictionaryClicked += mark => _watcher.AddToDictionary(mark.Id);
        _card.RewriteClicked += mark => _ = RewriteSentenceAsync(mark);
        _card.AiAvailable = () => _settings.AiEnabled && _ai.IsReady;

        _tray = new NotifyIcon { Icon = Theme.LogoIcon(), Text = AppName, Visible = true, ContextMenuStrip = BuildMenu() };
        _tray.DoubleClick += (_, _) => Balloon(_engine.Status);

        _hotkey = new HotkeyWindow(Native.MOD_CONTROL | Native.MOD_ALT, 'G', () => _ = CheckSelectionAsync());
        _rewriteHotkey = new HotkeyWindow(Native.MOD_CONTROL | Native.MOD_ALT, 'R', () => _ = RewriteSelectionAsync());

#if !DEBUG
        if (_settings.StartWithWindows) AutoStart.Apply(true); // refresh the path in case the exe moved
#endif

        _hoverTimer.Tick += (_, _) => OnHoverTick();
        _hoverTimer.Start();

        _ = StartEngineAsync();
    }

    async Task StartEngineAsync()
    {
        bool ok = await _engine.StartAsync(_client);
        _statusItem.Text = _engine.Status;
        if (ok)
        {
            _watcher.Start();
            if (_settings.AiEnabled) _ = StartAiAsync();
            if (_selfTest) { _ = RunSelfTestAsync(); return; }
            Balloon($"Running. Mistakes get underlined as you type anywhere.\n{HotkeyLabel}: check selected text · {RewriteHotkeyLabel}: AI rewrite");
        }
        else
        {
            Balloon(_engine.Status, ToolTipIcon.Error);
        }
    }

    async Task StartAiAsync()
    {
        _aiStatusItem.Text = "AI: loading model…";
        bool ok = await _ai.StartAsync(_aiClient);
        _aiStatusItem.Text = _ai.Status;
        Log.Write($"[AI] {_ai.Status}");
        if (ok) _watcher.Recheck();
    }

    // ---- Tray menu ----

    ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip
        {
            Renderer = new ThemedMenuRenderer(),
            Font = Theme.UiFont(9.5f),
            Padding = new Padding(4),
        };
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category is not (Microsoft.Win32.UserPreferenceCategory.General or Microsoft.Win32.UserPreferenceCategory.Color)) return;
            Theme.Refresh(); // follow Windows light/dark switches
            menu.Invalidate();
        };

        var enabled = new ToolStripMenuItem("Live checking") { Checked = _settings.Enabled, CheckOnClick = true };
        enabled.CheckedChanged += (_, _) => { _settings.Enabled = enabled.Checked; _settings.Save(); _watcher.Recheck(); };

        var checkSelection = new ToolStripMenuItem("Check selected text") { ShortcutKeyDisplayString = HotkeyLabel };
        checkSelection.Click += (_, _) => Balloon($"Select text in any app, then press {HotkeyLabel}.");

        var ai = new ToolStripMenuItem("AI suggestions && rewrite") { Checked = _settings.AiEnabled, CheckOnClick = true };
        ai.CheckedChanged += (_, _) =>
        {
            _settings.AiEnabled = ai.Checked;
            _settings.Save();
            if (ai.Checked) _ = StartAiAsync();
            else { _ai.Stop(); _aiStatusItem.Text = _ai.Status; } // frees the GPU/RAM the model uses
            _watcher.Recheck();
        };

        var rewrite = new ToolStripMenuItem("AI rewrite selected text") { ShortcutKeyDisplayString = RewriteHotkeyLabel };
        rewrite.Click += (_, _) => Balloon($"Select text in any app, then press {RewriteHotkeyLabel}.");

        var language = new ToolStripMenuItem("Language");
        foreach (var (code, name) in new[] { ("en-US", "English (US)"), ("en-GB", "English (UK)"), ("en-CA", "English (Canada)"), ("en-AU", "English (Australia)") })
        {
            var item = new ToolStripMenuItem(name) { Checked = _settings.Language == code, Tag = code };
            item.Click += (_, _) =>
            {
                _settings.Language = code;
                _settings.Save();
                foreach (ToolStripMenuItem other in language.DropDownItems) other.Checked = (string)other.Tag! == code;
                _watcher.Recheck();
            };
            language.DropDownItems.Add(item);
        }

        var dictionary = new ToolStripMenuItem("Personal dictionary…");
        dictionary.Click += (_, _) => Process.Start(new ProcessStartInfo("notepad.exe", $"\"{Settings.DictionaryPath}\""));

        var startup = new ToolStripMenuItem("Start with Windows") { Checked = _settings.StartWithWindows, CheckOnClick = true };
        startup.CheckedChanged += (_, _) => { _settings.StartWithWindows = startup.Checked; _settings.Save(); AutoStart.Apply(startup.Checked); };

        var folder = new ToolStripMenuItem("Open settings folder");
        folder.Click += (_, _) => Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Settings.Folder}\""));

        var about = new ToolStripMenuItem("About Lexora");
        about.Click += (_, _) => MessageBox.Show($"Lexora {Application.ProductVersion.Split('+')[0]}\nDeveloped by Shozi901\nMIT License · https://github.com/shehroz901/Lexora",
            "About Lexora", MessageBoxButtons.OK, MessageBoxIcon.Information);

        var exit = new ToolStripMenuItem("Exit");
        exit.Click += (_, _) => Shutdown();

        menu.Items.AddRange([
            new ToolStripLabel(AppName, Theme.LogoBitmap(32)) { Font = Theme.DisplayFont(10.5f), Padding = new Padding(0, 4, 0, 4) },
            _statusItem,
            _aiStatusItem,
            new ToolStripSeparator(),
            enabled, checkSelection, ai, rewrite, language,
            new ToolStripSeparator(),
            dictionary, startup, folder, about,
            new ToolStripSeparator(),
            exit,
        ]);
        menu.Opening += (_, _) => { _statusItem.Text = _engine.Status; _aiStatusItem.Text = _ai.Status; _dictionary.Reload(); };
        return menu;
    }

    void Balloon(string text, ToolTipIcon icon = ToolTipIcon.Info) => _tray.ShowBalloonTip(4000, AppName, text, icon);

    // ---- Underlines + hover card ----

    void OnMarksChanged(IReadOnlyList<Mark> marks)
    {
        _marks = marks;
        if (_card.Mark != null && !marks.Any(m => m.Id == _card.Mark.Id)) _card.HideCard();
        _overlay.SetMarks(marks, _card.Mark?.Id ?? -1);
        if (_card.Visible) Native.BringToTopmost(_card.Handle); // keep the card above the underline layer
    }

    void OnHoverTick()
    {
        if (_marks.Count == 0)
        {
            if (_card.Visible) _card.HideCard();
            return;
        }

        Native.GetCursorPos(out var cursor);
        var over = _marks.FirstOrDefault(m => m.Rects.Any(r => new Rectangle(r.X, r.Y, r.Width, r.Height + 4).Contains(cursor)));

        if (_card.Visible && _card.Mark != null)
        {
            if (_card.Bounds.Contains(cursor) || over?.Id == _card.Mark.Id) { _leftCardAt = DateTime.MaxValue; return; }
            if (over != null) { ShowCard(over); return; }
            if (_leftCardAt == DateTime.MaxValue) _leftCardAt = DateTime.UtcNow;
            if ((DateTime.UtcNow - _leftCardAt).TotalMilliseconds > 350)
            {
                _card.HideCard();
                _overlay.SetMarks(_marks, -1);
            }
            return;
        }

        if (over == null || Control.MouseButtons != MouseButtons.None) { _candidateId = -1; return; }
        if (over.Id != _candidateId) { _candidateId = over.Id; _candidateSince = DateTime.UtcNow; return; }
        if ((DateTime.UtcNow - _candidateSince).TotalMilliseconds >= 200) ShowCard(over);
    }

    void ShowCard(Mark mark)
    {
        _overlay.SetMarks(_marks, mark.Id);
        _card.ShowFor(mark);
        _leftCardAt = DateTime.MaxValue;
    }

    // ---- Hotkey: check the selected text in any app ----

    /// <summary>Copies the selection of the foreground app (restoring the user's clipboard afterwards).</summary>
    async Task<(IntPtr Target, string Text)?> CopySelectionAsync(char hotkeyKey, string hotkeyLabel)
    {
        // Wait until the hotkey keys are released, otherwise our Ctrl+C would become Ctrl+Alt+C.
        for (int i = 0; i < 50 && (Native.IsKeyDown(Native.VK_MENU) || Native.IsKeyDown(Native.VK_SHIFT) || Native.IsKeyDown(hotkeyKey)); i++)
            await Task.Delay(30);

        var target = Native.GetForegroundWindow();
        var backup = ClipboardBackup.Take();
        string selected = "";
        try
        {
            TryClipboard(Clipboard.Clear);
            Native.PressCtrlPlus('C');
            for (int i = 0; i < 12 && selected.Length == 0; i++)
            {
                await Task.Delay(50);
                TryClipboard(() => selected = Clipboard.ContainsText() ? Clipboard.GetText() : "");
            }
        }
        finally { backup.Restore(); }

        if (selected.Trim().Length == 0) { Balloon($"Select some text first, then press {hotkeyLabel}."); return null; }
        return (target, selected);
    }

    async Task CheckSelectionAsync()
    {
        if (_selectionCheckRunning) return;
        if (!_engine.IsReady) { Balloon("The engine is still starting, try again in a few seconds."); return; }
        _selectionCheckRunning = true;
        try
        {
            if (await CopySelectionAsync('G', HotkeyLabel) is not var (target, selected)) return;

            var issues = await _client.CheckAsync(selected, _settings.Language);
            if (issues == null) { Balloon("Could not reach the grammar engine.", ToolTipIcon.Warning); return; }
            issues.RemoveAll(i => i.IsSpelling && _dictionary.Contains(i.ErrorText));
            if (issues.Count == 0) { Balloon("No issues found ✓"); return; }

            var form = new SelectionCheckForm(selected, issues);
            form.ReplaceRequested += text => _ = PasteIntoAsync(target, text);
            form.Show();
        }
        finally { _selectionCheckRunning = false; }
    }

    bool AiReadyOrExplain()
    {
        if (!_settings.AiEnabled) { Balloon("AI is turned off. Enable \"AI suggestions & rewrite\" in the tray menu."); return false; }
        if (!_ai.IsReady) { Balloon($"{_ai.Status}. Try again in a few seconds."); return false; }
        return true;
    }

    async Task RewriteSelectionAsync()
    {
        if (_selectionCheckRunning || !AiReadyOrExplain()) return;
        _selectionCheckRunning = true;
        try
        {
            if (await CopySelectionAsync('R', RewriteHotkeyLabel) is not var (target, selected)) return;
            var form = new AiRewriteForm(selected, _aiClient);
            form.ReplaceRequested += text => _ = PasteIntoAsync(target, text);
            form.Show();
        }
        finally { _selectionCheckRunning = false; }
    }

    /// <summary>"✨ Rewrite" on the card: rewrites the whole sentence around the underlined issue.</summary>
    async Task RewriteSentenceAsync(Mark mark)
    {
        if (!AiReadyOrExplain()) return;
        var target = Native.GetForegroundWindow(); // the card never takes focus, so this is the app being typed in
        if (await _watcher.CaptureSentence(mark.Id) is not var (id, sentence)) { Balloon("Could not read that sentence."); return; }

        var form = new AiRewriteForm(sentence, _aiClient);
        form.ReplaceRequested += async text =>
        {
            Native.SetForegroundWindow(target);
            await Task.Delay(250);
            _watcher.ReplaceSentence(id, text);
        };
        form.Show();
    }

    static async Task PasteIntoAsync(IntPtr target, string text)
    {
        var backup = ClipboardBackup.Take();
        TryClipboard(() => Clipboard.SetText(text));
        Native.SetForegroundWindow(target);
        await Task.Delay(200);
        Native.PressCtrlPlus('V');
        await Task.Delay(500);
        backup.Restore();
    }

    static void TryClipboard(Action action)
    {
        for (int i = 0; i < 5; i++)
        {
            try { action(); return; }
            catch (ExternalException) { Thread.Sleep(30); } // clipboard briefly locked by another app
        }
    }

    /// <summary>Copies every clipboard format so the user's clipboard survives our copy/paste.</summary>
    sealed class ClipboardBackup
    {
        DataObject? _data;

        public static ClipboardBackup Take()
        {
            var backup = new ClipboardBackup();
            TryClipboard(() =>
            {
                var source = Clipboard.GetDataObject();
                if (source == null) return;
                var copy = new DataObject();
                foreach (var format in source.GetFormats(false))
                {
                    try { if (source.GetData(format) is { } value) copy.SetData(format, value); } catch { }
                }
                backup._data = copy;
            });
            return backup;
        }

        public void Restore() => TryClipboard(() =>
        {
            if (_data != null && _data.GetFormats().Length > 0) Clipboard.SetDataObject(_data, copy: true);
            else Clipboard.Clear();
        });
    }

    // ---- Self-test: opens its own window with mistakes, logs what gets underlined, then exits ----

    async Task RunSelfTestAsync()
    {
        if (TextWatcher.SelfTestAutomationId != null)
        {
            await Task.Delay(15000);
            Log.Write($"[SelfTest] web: {_marks.Count} marks: " +
                      string.Join("; ", _marks.Select(m => $"'{m.Issue.ErrorText}'@{string.Join(",", m.Rects.Select(r => $"{r.X},{r.Y},{r.Width}x{r.Height}"))}")));
            await Task.Delay(3000);
            Shutdown();
            return;
        }

        var form = new Form
        {
            Text = "Lexora self-test", StartPosition = FormStartPosition.Manual,
            Location = new Point(Screen.PrimaryScreen!.WorkingArea.Left + 80, Screen.PrimaryScreen.WorkingArea.Top + 80),
            ClientSize = new Size(760, 200), TopMost = true,
        };
        var box = new TextBox
        {
            Dock = DockStyle.Fill, Multiline = true, Font = new Font("Segoe UI", 14f),
            Text = "I am in the just testing my tool. Their going to the libary tomorow. The weather are very nice today. Yar mujhe ye kaam aaj hi complete krna hai",
        };
        form.Controls.Add(box);
        TextWatcher.SelfTestTarget = box.Handle;
        form.Show();
        form.Activate();
        box.Focus();
        box.SelectionStart = box.TextLength;

        for (int i = 0; i < 240 && !_ai.IsReady; i++) await Task.Delay(500);
        await Task.Delay(8000); // AI pass runs after LanguageTool
        Log.Write($"[SelfTest] window at {form.Bounds}, AI ready={_ai.IsReady}, {_marks.Count} marks: " +
                  string.Join("; ", _marks.Select(m => $"{(m.Issue.IsAi ? "AI:" : "")}'{m.Issue.ErrorText}'->'{m.Issue.Replacements.FirstOrDefault()}'")));
        _hoverTimer.Stop(); // the mouse isn't over the word, so keep the card open for the screenshot
        if ((_marks.FirstOrDefault(m => m.Issue.IsAi) ?? _marks.FirstOrDefault()) is { } shown)
        {
            ShowCard(shown);
            Log.Write($"[SelfTest] card shown at {_card.Bounds}");
        }
        await Task.Delay(4000);
        _card.HideCard();
        _tray.ContextMenuStrip!.Show(new Point(900, 120));
        Log.Write("[SelfTest] menu shown at {_tray.ContextMenuStrip.Bounds}");
        await Task.Delay(2500);
        _tray.ContextMenuStrip.Close();

        var rewrite = new AiRewriteForm("yar kal meeting main discuss krein gy k project kab tak complete hoga", _aiClient);
        rewrite.Show();
        await Task.Delay(15000);
        Log.Write($"[SelfTest] rewrite shown at {rewrite.Bounds} | {rewrite.ToneText} | {rewrite.ResultText}");
        await Task.Delay(4000);
        rewrite.Close();

        foreach (var (mode, text) in new[]
                 {
                     ("🎮 Game Dev Prompt", "mujhe unity main ek endless runner ka player controller chahiye jo mobile pr smooth chaly, swipe se lane change ho or jump b ho"),
                     ("📈 ASO & UA Prompt", "meri hunting game ka play store listing improve krna hai or US main installs barhane hain kam budget main"),
                 })
        {
            var promptForm = new AiRewriteForm(text, _aiClient) { StartPosition = FormStartPosition.Manual, Location = new Point(80, 80) };
            promptForm.Show();
            await Task.Delay(3000);
            await promptForm.RunModeByNameAsync(mode);
            Log.Write($"[SelfTest] prompt '{mode}' at {promptForm.Bounds}:\n{promptForm.ResultText}");
            await Task.Delay(3000);
            promptForm.Close();
        }
        form.Close();
        Shutdown();
    }

    // ---- Misc ----

    void Shutdown()
    {
        _hoverTimer.Stop();
        _watcher.Stop();
        _engine.Stop();
        _hotkey.Dispose();
        _rewriteHotkey.Dispose();
        _ai.Stop();
        _tray.Visible = false;
        _tray.Dispose();
        _overlay.Close();
        _card.Close();
        ExitThread();
    }

    /// <summary>Hidden window that receives a system-wide hotkey.</summary>
    sealed class HotkeyWindow : NativeWindow, IDisposable
    {
        const int Id = 0x4721;
        readonly Action _onPressed;

        public HotkeyWindow(uint modifiers, char key, Action onPressed)
        {
            _onPressed = onPressed;
            CreateHandle(new CreateParams());
            Native.RegisterHotKey(Handle, Id, modifiers | Native.MOD_NOREPEAT, key);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_HOTKEY && (int)m.WParam == Id) _onPressed();
            base.WndProc(ref m);
        }

        public void Dispose()
        {
            Native.UnregisterHotKey(Handle, Id);
            DestroyHandle();
        }
    }
}
