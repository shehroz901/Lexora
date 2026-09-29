using System.Collections.Concurrent;
using System.Diagnostics;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Patterns;
using FlaUI.UIA3;

namespace Lexora;

/// <summary>An issue that is currently underlined on screen. Rects are in physical screen pixels.</summary>
sealed record Mark(int Id, Issue Issue, Rectangle[] Rects);

/// <summary>
/// Background loop that follows the focused text field in any app via UI Automation,
/// sends its text to LanguageTool after typing pauses, and publishes on-screen positions of the issues.
/// All UI Automation calls happen on this one worker thread.
/// </summary>
sealed class TextWatcher
{
    const int TickMs = 150;
    const int IdleBeforeCheckMs = 600;
    const int MaxTextLength = 20000;
    const int MaxMarks = 80;

    static readonly HashSet<string> Browsers = new(StringComparer.OrdinalIgnoreCase)
        { "chrome", "msedge", "firefox", "brave", "opera", "vivaldi", "arc" };

    sealed class Tracked(int id, Issue issue, ITextRange range)
    {
        public int Id = id; public Issue Issue = issue; public ITextRange Range = range;
    }

    readonly Settings _settings;
    readonly PersonalDictionary _dictionary;
    readonly LanguageToolClient _client;
    readonly EngineServer _engine;
    readonly ConcurrentQueue<Action> _requests = new();
    readonly HashSet<string> _dismissed = [];
    readonly Dictionary<int, string> _processNames = [];
    readonly int _ownPid = Environment.ProcessId;
    Thread? _thread;
    volatile bool _stop;

    UIA3Automation? _automation;
    AutomationElement? _element;
    int[] _elementRuntimeId = [];
    string? _lastFocusReason;
    ITextPattern? _textPattern;
    int _elementPid;
    string? _lastText, _lastCheckedText, _pendingText;
    DateTime _lastChange;
    Task<List<Issue>?>? _pendingCheck;
    readonly List<Tracked> _tracked = [];
    int _nextId;

    // AI pass (runs after LanguageTool, on the same text)
    readonly AiReviewer _aiReviewer;
    readonly AiServer _aiServer;
    IReadOnlyList<Issue> _lastLtIssues = [];
    Task<List<Issue>>? _pendingAi;
    string? _aiText, _aiCheckedText;

    // Sentences captured for the AI Rewrite window, by id
    readonly Dictionary<int, (ITextRange Range, string Text)> _sentences = [];
    IReadOnlyList<Mark> _published = [];

    /// <summary>Self-test mode: also check text fields in this app's own windows.</summary>
    public static bool AllowOwnProcess;

    /// <summary>Self-test mode: watch this window handle instead of the focused element.</summary>
    public static IntPtr SelfTestTarget;

    /// <summary>Self-test mode: watch the element with this AutomationId (HTML id in browsers) instead of the focused one.</summary>
    public static string? SelfTestAutomationId;
    AutomationElement? _selfTestElement;

    /// <summary>Raised on the worker thread whenever the set of on-screen marks changes.</summary>
    public event Action<IReadOnlyList<Mark>>? MarksChanged;

    public TextWatcher(Settings settings, PersonalDictionary dictionary, LanguageToolClient client, EngineServer engine,
        AiReviewer aiReviewer, AiServer aiServer)
    {
        _settings = settings; _dictionary = dictionary; _client = client; _engine = engine;
        _aiReviewer = aiReviewer; _aiServer = aiServer;
    }

    public void Start()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "TextWatcher" };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    public void Stop() => _stop = true;

    // ---- Requests from the UI thread (executed on the worker thread) ----

    public void Apply(int markId, string replacement) => _requests.Enqueue(() => DoApply(markId, replacement));

    public void Dismiss(int markId) => _requests.Enqueue(() =>
    {
        if (_tracked.Find(t => t.Id == markId) is { } t) { _dismissed.Add(t.Issue.DismissKey); _tracked.Remove(t); }
    });

    public void AddToDictionary(int markId) => _requests.Enqueue(() =>
    {
        if (_tracked.Find(t => t.Id == markId) is not { } t) return;
        _dictionary.Add(t.Issue.ErrorText);
        _tracked.RemoveAll(x => x.Issue.IsSpelling && string.Equals(x.Issue.ErrorText, t.Issue.ErrorText, StringComparison.OrdinalIgnoreCase));
    });

    /// <summary>Forces a fresh check (e.g. after changing language or re-enabling).</summary>
    public void Recheck() => _requests.Enqueue(() => { _lastCheckedText = _aiCheckedText = null; _tracked.Clear(); });

    /// <summary>Remembers the sentence around a mark so the Rewrite window can replace it later.</summary>
    public Task<(int Id, string Text)?> CaptureSentence(int markId)
    {
        var result = new TaskCompletionSource<(int, string)?>();
        _requests.Enqueue(() =>
        {
            (int, string)? captured = null;
            try
            {
                if (_tracked.Find(t => t.Id == markId) is { } t && _lastText is { } text && _textPattern != null)
                {
                    var span = TextTools.SplitSentences(text).FirstOrDefault(s => t.Issue.Offset >= s.Start && t.Issue.Offset < s.Start + s.Length);
                    if (span.Length > 0)
                    {
                        var sentence = text.Substring(span.Start, span.Length);
                        var issue = new Issue(span.Start, span.Length, sentence, "", "", [], "", "", "", "");
                        if (RangeFor(_textPattern.DocumentRange, text, issue) is { } range)
                        {
                            _sentences[++_nextId] = (range, sentence);
                            captured = (_nextId, sentence);
                        }
                    }
                }
            }
            finally { result.SetResult(captured); }
        });
        return result.Task;
    }

    /// <summary>Replaces a captured sentence (the target app must be in the foreground again).</summary>
    public void ReplaceSentence(int sentenceId, string newText) => _requests.Enqueue(() =>
    {
        if (!_sentences.Remove(sentenceId, out var s)) return;
        if (s.Range.GetText(s.Text.Length + 1) != s.Text) { Log.Write("[Rewrite] sentence changed, not replacing"); return; }
        if (Native.ForegroundProcessId() != _elementPid) { Log.Write("[Rewrite] target app not in foreground"); return; }
        s.Range.Select();
        Thread.Sleep(40);
        Native.TypeText(newText);
        _lastChange = DateTime.UtcNow;
    });

    // ---- Worker loop ----

    void Run()
    {
        _automation = new UIA3Automation();
        while (!_stop)
        {
            try { Tick(); }
            catch (Exception ex)
            {
                Log.Write($"[TextWatcher] {ex.GetType().Name}: {ex.Message}");
                ForgetElement();
            }
            Thread.Sleep(TickMs);
        }
        ForgetElement();
        Publish([]);
        _automation.Dispose();
    }

    void Tick()
    {
        while (_requests.TryDequeue(out var request))
        {
            try { request(); } catch (Exception ex) { Log.Write($"[TextWatcher] request failed: {ex.Message}"); }
        }

        if (!_settings.Enabled || !_engine.IsReady) { ForgetElement(); return; }

        var (focused, reason) = GetEditableFocusedElement();
        if (reason != _lastFocusReason)
        {
            _lastFocusReason = reason;
            Log.Write($"[Focus] {reason}");
        }
        if (focused == null) { ForgetElement(); return; }

        var runtimeId = focused.Properties.RuntimeId.ValueOrDefault ?? [];
        if (_element == null || !runtimeId.SequenceEqual(_elementRuntimeId))
        {
            ForgetElement();
            _element = focused;
            _elementRuntimeId = runtimeId;
            _elementPid = focused.Properties.ProcessId.ValueOrDefault;
            _textPattern = focused.Patterns.Text.Pattern;
        }

        var document = _textPattern!.DocumentRange;
        string text = document.GetText(MaxTextLength + 1) ?? "";
        if (text.Length > MaxTextLength) { _tracked.Clear(); Publish([]); return; }

        if (text != _lastText)
        {
            _lastText = text;
            _lastChange = DateTime.UtcNow;
            DropStaleMarks();
        }

        if (_pendingCheck == null && text != _lastCheckedText &&
            (DateTime.UtcNow - _lastChange).TotalMilliseconds >= IdleBeforeCheckMs)
        {
            if (text.Trim().Length < 2) { _lastCheckedText = text; _tracked.Clear(); }
            else { _pendingText = text; _pendingCheck = _client.CheckAsync(text, _settings.Language); }
        }

        if (_pendingCheck is { IsCompleted: true } done)
        {
            _pendingCheck = null;
            if (_pendingText == _lastText) // ignore results for text that has changed since
            {
                _lastCheckedText = _pendingText;
                if (done.Result is { } issues)
                {
                    _lastLtIssues = issues;
                    if (_settings.SkipNonEnglish) issues = TextTools.RemoveForeign(_pendingText!, issues);
                    LocateIssues(document, _pendingText!, issues, append: false);
                    Log.Write($"[Check] {_pendingText!.Length} chars → {issues.Count} issues, {_tracked.Count} located on screen");
                }
                else Log.Write("[Check] engine request failed");
            }
        }

        RunAiPass(document);
        PublishCurrentRects();
    }

    void RunAiPass(ITextRange document)
    {
        if (!_settings.AiEnabled || !_aiServer.IsReady) return;

        if (_pendingAi == null && _pendingCheck == null && _lastText != null &&
            _lastCheckedText == _lastText && _aiCheckedText != _lastText)
        {
            _aiText = _lastText;
            _pendingAi = _aiReviewer.ReviewAsync(_lastText, _lastLtIssues);
        }

        if (_pendingAi is { IsCompleted: true } done)
        {
            _pendingAi = null;
            if (_aiText != _lastText) return; // text changed meanwhile; per-sentence results are cached for the next pass
            _aiCheckedText = _aiText;
            if (!done.IsCompletedSuccessfully) { Log.Write($"[AI] review failed: {done.Exception?.GetBaseException().Message}"); return; }
            int before = _tracked.Count;
            LocateIssues(document, _aiText!, done.Result, append: true);
            Log.Write($"[AI] {done.Result.Count} suggestions, {_tracked.Count - before} new on screen");
        }
    }

    /// <summary>Returns the focused element if it is an editable text field we should check, plus a log-friendly reason.</summary>
    (AutomationElement? Element, string Reason) GetEditableFocusedElement()
    {
        var el = SelfTestTarget != IntPtr.Zero ? _automation!.FromHandle(SelfTestTarget)
               : SelfTestAutomationId != null ? (_selfTestElement ??= FindSelfTestElement())
               : _automation!.FocusedElement();
        if (el == null) return (null, "nothing focused");

        int pid = el.Properties.ProcessId.ValueOrDefault;
        if (pid == 0 || (pid == _ownPid && !AllowOwnProcess)) return (null, "own window");

        string process = ProcessName(pid);
        var controlType = el.Properties.ControlType.ValueOrDefault;
        string what = $"{process} / {controlType}";

        if (el.Properties.IsPassword.ValueOrDefault) return (null, $"{what}: skipped (password field)");
        if (_settings.ExcludedApps.Contains(process, StringComparer.OrdinalIgnoreCase)) return (null, $"{what}: skipped (excluded app)");
        if (!el.Patterns.Text.IsSupported) return (null, $"{what}: skipped (no TextPattern, use the hotkey here)");

        bool hasValue = el.Patterns.Value.IsSupported;
        if (hasValue && el.Patterns.Value.Pattern.IsReadOnly.ValueOrDefault) return (null, $"{what}: skipped (read-only)");

        bool editable = controlType == ControlType.Edit
                        || hasValue
                        || (controlType == ControlType.Document && !Browsers.Contains(process)); // Word, Notepad…
        return editable ? (el, $"{what}: checking") : (null, $"{what}: skipped (not an editable field)");
    }

    AutomationElement? FindSelfTestElement() =>
        _automation!.GetDesktop().FindAllChildren()
            .Where(w => (w.Properties.Name.ValueOrDefault ?? "").Contains("Checker web test"))
            .Select(w => w.FindFirstDescendant(cf => cf.ByAutomationId(SelfTestAutomationId!)))
            .FirstOrDefault(e => e != null);

    string ProcessName(int pid)
    {
        if (!_processNames.TryGetValue(pid, out var name))
        {
            try { name = Process.GetProcessById(pid).ProcessName; } catch { name = ""; }
            _processNames[pid] = name;
        }
        return name;
    }

    void ForgetElement()
    {
        _element = null;
        _textPattern = null;
        _lastText = _lastCheckedText = _pendingText = null;
        _pendingCheck = null;
        _pendingAi = null;
        _aiText = _aiCheckedText = null;
        _sentences.Clear();
        _tracked.Clear();
        Publish([]);
    }

    /// <summary>After an edit, keep only the underlines whose text is still exactly where it was.</summary>
    void DropStaleMarks()
    {
        _tracked.RemoveAll(t =>
        {
            try { return t.Range.GetText(t.Issue.Length + 1) != t.Issue.ErrorText; }
            catch { return true; }
        });
    }

    /// <summary>
    /// Turns character offsets into UI Automation text ranges. With append, keeps the current marks
    /// and skips new issues that overlap them (LanguageTool marks win over AI marks).
    /// </summary>
    void LocateIssues(ITextRange document, string text, List<Issue> issues, bool append)
    {
        if (!append) _tracked.Clear();
        var existing = _tracked.Select(t => t.Issue).ToList();
        var wanted = issues
            .Where(i => !_dismissed.Contains(i.DismissKey))
            .Where(i => !(i.IsSpelling && _dictionary.Contains(i.ErrorText)))
            .Where(i => !existing.Any(e => i.Offset < e.Offset + e.Length && e.Offset < i.Offset + i.Length))
            .OrderBy(i => i.Offset)
            .Take(Math.Max(0, MaxMarks - _tracked.Count));

        // Walk a collapsed cursor forward through the document instead of re-counting from the start for each issue.
        var cursor = document.Clone();
        cursor.MoveEndpointByRange(TextPatternRangeEndpoint.End, cursor, TextPatternRangeEndpoint.Start);
        int cursorOffset = 0;
        bool cursorValid = true;

        foreach (var issue in wanted)
        {
            ITextRange? range = null;
            if (cursorValid)
            {
                int delta = issue.Offset - cursorOffset;
                if (cursor.Move(TextUnit.Character, delta) == delta)
                {
                    cursorOffset = issue.Offset;
                    var candidate = cursor.Clone();
                    candidate.MoveEndpointByUnit(TextPatternRangeEndpoint.End, TextUnit.Character, issue.Length);
                    if (candidate.GetText(issue.Length + 1) == issue.ErrorText) range = candidate;
                }
                else cursorValid = false;
            }
            range ??= FindNthOccurrence(document, text, issue);
            if (range != null) _tracked.Add(new Tracked(++_nextId, issue, range));
        }
        _tracked.Sort((x, y) => x.Issue.Offset.CompareTo(y.Issue.Offset));
    }

    /// <summary>Text range for a single span of the document text.</summary>
    static ITextRange? RangeFor(ITextRange document, string text, Issue span)
    {
        var range = document.Clone();
        range.MoveEndpointByRange(TextPatternRangeEndpoint.End, range, TextPatternRangeEndpoint.Start);
        if (range.Move(TextUnit.Character, span.Offset) == span.Offset)
        {
            range.MoveEndpointByUnit(TextPatternRangeEndpoint.End, TextUnit.Character, span.Length);
            if (range.GetText(span.Length + 1) == span.ErrorText) return range;
        }
        return FindNthOccurrence(document, text, span);
    }

    /// <summary>Fallback when character offsets don't line up (some apps count line breaks differently).</summary>
    static ITextRange? FindNthOccurrence(ITextRange document, string text, Issue issue)
    {
        int occurrence = 0;
        for (int i = text.IndexOf(issue.ErrorText, StringComparison.Ordinal);
             i >= 0 && i < issue.Offset;
             i = text.IndexOf(issue.ErrorText, i + issue.ErrorText.Length, StringComparison.Ordinal))
            occurrence++;

        var search = document.Clone();
        for (int n = 0; n <= occurrence; n++)
        {
            var found = search.FindText(issue.ErrorText, false, false);
            if (found == null) return null;
            if (n == occurrence) return found;
            search.MoveEndpointByRange(TextPatternRangeEndpoint.Start, found, TextPatternRangeEndpoint.End);
        }
        return null;
    }

    void PublishCurrentRects()
    {
        if (_tracked.Count == 0) { Publish([]); return; }

        var bounds = _element!.Properties.BoundingRectangle.ValueOrDefault;
        var marks = new List<Mark>(_tracked.Count);
        foreach (var t in _tracked)
        {
            Rectangle[] rects;
            try { rects = t.Range.GetBoundingRectangles(); } catch { continue; }
            var visible = rects
                .Select(r => bounds.IsEmpty ? r : Rectangle.Intersect(r, bounds))
                .Where(r => r.Width > 0 && r.Height > 0)
                .ToArray();
            if (visible.Length > 0) marks.Add(new Mark(t.Id, t.Issue, visible));
        }
        Publish(marks);
    }

    void Publish(IReadOnlyList<Mark> marks)
    {
        if (SameMarks(marks, _published)) return;
        _published = marks;
        MarksChanged?.Invoke(marks);
    }

    static bool SameMarks(IReadOnlyList<Mark> a, IReadOnlyList<Mark> b) =>
        a.Count == b.Count && a.Zip(b).All(p => p.First.Id == p.Second.Id && p.First.Rects.SequenceEqual(p.Second.Rects));

    void DoApply(int markId, string replacement)
    {
        var t = _tracked.Find(x => x.Id == markId);
        if (t == null || _element == null) return;
        if (t.Range.GetText(t.Issue.Length + 1) != t.Issue.ErrorText) return; // text changed underneath us
        if (Native.ForegroundProcessId() != _elementPid) return;               // never type into the wrong window

        t.Range.Select();
        Thread.Sleep(40);
        if (replacement.Length == 0) Native.PressKey(Native.VK_DELETE);
        else Native.TypeText(replacement);

        _tracked.Remove(t);
        _lastChange = DateTime.UtcNow;
    }
}
