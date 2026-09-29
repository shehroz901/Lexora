using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Lexora;

/// <summary>Starts the bundled llama.cpp server (engine\llm) with the local model, fully offline.</summary>
sealed class AiServer(int port, int contextSize)
{
    Process? _process;
    public volatile bool IsReady;
    public string Status { get; private set; } = "AI: off";

    public static (string Server, string Model)? FindFiles()
    {
        var engine = EngineServer.FindEngineFolder();
        if (engine == null) return null;
        var dir = Path.Combine(engine, "llm");
        var server = Path.Combine(dir, "llama-server.exe");
        var model = Directory.Exists(dir) ? Directory.GetFiles(dir, "*.gguf").FirstOrDefault() : null;
        return File.Exists(server) && model != null ? (server, model) : null;
    }

    public async Task<bool> StartAsync(AiClient client)
    {
        if (IsReady || _process is { HasExited: false }) return IsReady;
        Status = "AI: loading model…";
        if (await client.PingAsync()) return Ready();

        if (FindFiles() is not { } files) { Status = "AI: model not found in engine\\llm"; return false; }

        var psi = new ProcessStartInfo(files.Server)
        {
            WorkingDirectory = Path.GetDirectoryName(files.Server)!,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in new[] { "-m", files.Model, "--host", "127.0.0.1", "--port", port.ToString(), "-c", contextSize.ToString(), "-np", "1" })
            psi.ArgumentList.Add(arg);

        _process = Process.Start(psi);
        if (_process == null) { Status = "AI: could not start"; return false; }
        Native.TieLifetimeToThisProcess(_process);

        for (int i = 0; i < 240; i++)
        {
            await Task.Delay(500);
            if (_process.HasExited) { Status = $"AI: stopped (exit code {_process.ExitCode})"; return false; }
            if (await client.PingAsync())
            {
                await client.ProofreadAsync("This is a warm up sentence."); // first request compiles GPU shaders (slow once)
                return Ready();
            }
        }
        Status = "AI: did not start in time";
        return false;
    }

    bool Ready() { IsReady = true; Status = "AI: ready"; return true; }

    public void Stop()
    {
        IsReady = false;
        Status = "AI: off";
        try { if (_process is { HasExited: false }) _process.Kill(entireProcessTree: true); } catch { }
        _process = null;
    }
}

/// <summary>Client for llama.cpp's OpenAI-compatible chat endpoint, with the prompts the app uses.</summary>
sealed class AiClient(int port)
{
    readonly HttpClient _http = new() { BaseAddress = new Uri($"http://127.0.0.1:{port}/"), Timeout = TimeSpan.FromSeconds(120) };

    // Long answers (prompt optimizer) can take a minute on a laptop GPU: no fixed timeout, cancellation is explicit.
    readonly HttpClient _streamHttp = new() { BaseAddress = new Uri($"http://127.0.0.1:{port}/"), Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>Streams the answer token by token (onDelta runs on a background thread). Returns the full text, or null on error.</summary>
    public async Task<string?> StreamChatAsync(IEnumerable<(string Role, string Content)> messages, int maxTokens, float temperature,
        Action<string> onDelta, CancellationToken ct)
    {
        try
        {
            var body = JsonSerializer.Serialize(new
            {
                messages = messages.Select(m => new { role = m.Role, content = m.Content }),
                temperature,
                max_tokens = maxTokens,
                stream = true,
                cache_prompt = true,
            });
            using var request = new HttpRequestMessage(HttpMethod.Post, "v1/chat/completions")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            using var response = await _streamHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode) return null;

            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(ct));
            var full = new StringBuilder();
            while (await reader.ReadLineAsync(ct) is { } line)
            {
                if (!line.StartsWith("data: ")) continue;
                var data = line[6..];
                if (data == "[DONE]") break;
                using var chunk = JsonDocument.Parse(data);
                var choices = chunk.RootElement.GetProperty("choices");
                if (choices.GetArrayLength() == 0) continue;
                if (choices[0].GetProperty("delta").TryGetProperty("content", out var content) && content.GetString() is { Length: > 0 } delta)
                {
                    full.Append(delta);
                    onDelta(delta);
                }
            }
            return Regex.Replace(full.ToString(), @"<think>.*?(</think>|$)", "", RegexOptions.Singleline).Trim();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Log.Write($"[AI] stream failed: {ex.Message}");
            return null;
        }
    }

    public async Task<bool> PingAsync()
    {
        try { return (await _http.GetAsync("health")).IsSuccessStatusCode; }
        catch { return false; }
    }

    public async Task<string?> ChatAsync(IEnumerable<(string Role, string Content)> messages, int maxTokens, float temperature = 0f,
        CancellationToken ct = default)
    {
        try
        {
            var body = JsonSerializer.Serialize(new
            {
                messages = messages.Select(m => new { role = m.Role, content = m.Content }),
                temperature,
                max_tokens = maxTokens,
                stream = false,
                cache_prompt = true,
            });
            using var response = await _http.PostAsync("v1/chat/completions", new StringContent(body, Encoding.UTF8, "application/json"), ct);
            if (!response.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var content = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
            content = Regex.Replace(content, @"<think>.*?</think>", "", RegexOptions.Singleline);
            return content.Trim();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Write($"[AI] request failed: {ex.Message}");
            return null;
        }
    }

    const string ProofreadSystem =
        "You are a careful English proofreader. Fix grammar, spelling, wrong words and punctuation, and delete words that are " +
        "unnecessary, duplicated or make the sentence ungrammatical. Keep the author's meaning, wording and tone: make the smallest " +
        "possible change and never rephrase a sentence that is already correct. If the text is not English (for example Roman Urdu " +
        "or Hindi written in Latin letters), return it exactly unchanged. Reply with only the corrected text.";

    static readonly (string, string)[] ProofreadExamples =
    [
        ("We will meet in the at the office tomorrow.", "We will meet at the office tomorrow."),
        ("She dont like the the movie.", "She doesn't like the movie."),
        ("This is a good plan.", "This is a good plan."),
        ("kal meeting main discuss krein gy", "kal meeting main discuss krein gy"),
    ];

    /// <summary>Returns the minimally corrected sentence (or the same text if it is fine / not English).</summary>
    public Task<string?> ProofreadAsync(string sentence, CancellationToken ct = default)
    {
        var messages = new List<(string, string)> { ("system", ProofreadSystem) };
        foreach (var (wrong, right) in ProofreadExamples) { messages.Add(("user", wrong)); messages.Add(("assistant", right)); }
        messages.Add(("user", sentence));
        return ChatAsync(messages, maxTokens: Math.Min(400, sentence.Length + 64), ct: ct);
    }

    public static readonly RewriteMode[] RewriteModes =
    [
        new("Improve", "Rewrite the text so it is clear, fluent and natural English. Fix all mistakes. Keep the meaning and roughly the same length."),
        new("Shorter", "Rewrite the text to be shorter and more concise. Remove filler words. Keep the meaning."),
        new("Formal", "Rewrite the text in a polite, professional and formal tone suitable for work."),
        new("Friendly", "Rewrite the text in a warm, friendly and casual tone."),
        new("Fix grammar", "Correct only grammar, spelling and punctuation. Change as little as possible."),
        new("To English", "Translate the text into clear, natural English. The text may be Roman Urdu (Urdu written in English letters), " +
                          "Urdu, Hindi or a mix with English. Translate the meaning exactly and keep every detail. Roman Urdu hints: " +
                          "kal = tomorrow when the verb is future (gy/ga/gi, karenge, hoga) and yesterday when it is past (tha/thi, kiya, gaya); " +
                          "aaj = today; parson = the day after tomorrow or the day before yesterday; abhi = right now; krein gy / karenge = will do; " +
                          "hoga = will be; yar/yaar = hey, friend; k / ke = that / of; kab tak = by when."),

        new("🎮 Game Dev Prompt",
            "You are an elite prompt engineer AND a professional game developer with 50 years of experience shipping games " +
            "(game engines such as Unity and Unreal, C#/C++, gameplay systems, clean architecture and design patterns, " +
            "performance and memory optimization for mobile/PC/console, physics, UI, tools, build pipelines, monetization and live ops). " +
            "The user wrote a rough request (it may be Roman Urdu, Urdu, Hindi or broken English). Turn it into one clear, optimized prompt " +
            "in English that the user will paste into an AI coding assistant. Use this structure with short headings:\n" +
            "Role: begin with \"Act as a senior game developer with 50 years of professional experience.\"\n" +
            "Context: the project/feature, using only facts from the request; write [placeholders] for important missing details (engine version, platform, genre).\n" +
            "Task: precise, numbered requirements.\n" +
            "Technical requirements: architecture, performance, memory, edge cases and testing that a veteran would insist on for this task.\n" +
            "Output format: exactly what the assistant should return (e.g. complete scripts with file names, step-by-step setup, explanation).\n" +
            "Keep every detail the user gave. Do not answer or solve the task yourself.",
            IsPrompt: true),
        new("📈 ASO & UA Prompt",
            "You are an elite prompt engineer AND a professional App Store Optimization (ASO) and User Acquisition (UA) expert who has grown " +
            "top-grossing mobile games and apps on Google Play and the App Store (keyword research and ranking, titles, subtitles, short and " +
            "long descriptions, icons, screenshots, preview videos, A/B tests, ratings and reviews, localization, paid UA on Google Ads, Meta, " +
            "TikTok, Unity and AppLovin, ad creatives, CPI, IPM, ROAS, retention and LTV). The user wrote a rough request (it may be Roman Urdu, " +
            "Urdu, Hindi or broken English). Turn it into one clear, optimized prompt in English that the user will paste into an AI assistant. " +
            "Use this structure with short headings:\n" +
            "Role: begin with \"Act as a world-class ASO and User Acquisition strategist with 15+ years of experience scaling top-grossing mobile games and apps.\"\n" +
            "Context: the app/game, store(s), audience and goal, using only facts from the request; write [placeholders] for important missing details (app name, genre, target countries, competitors, budget).\n" +
            "Task: precise, numbered deliverables.\n" +
            "Constraints: only the rules of the store(s) in the request. Google Play fields: title 30, short description 80, full description 4000 characters " +
            "(Google Play has NO subtitle and NO keyword field; keywords go naturally into the title and descriptions). App Store fields: title 30, " +
            "subtitle 30, keyword field 100 characters. No keyword stuffing.\n" +
            "Output format: exactly what the assistant should return (e.g. tables of keywords with volume/difficulty, 3 title variants, creative concepts with hooks).\n" +
            "Keep every detail the user gave. Do not answer or solve the task yourself.",
            IsPrompt: true),
    ];

    public Task<string?> RewriteAsync(string text, RewriteMode mode, Action<string> onDelta, CancellationToken ct = default) =>
        mode.IsPrompt
            ? StreamChatAsync([
                ("system", mode.Instruction + " Reply with only the optimized prompt, ready to paste: no introduction, no notes after it."),
                ("user", text),
              ], maxTokens: 1400, temperature: 0.4f, onDelta, ct)
            : StreamChatAsync([
                ("system", "You are an expert writing assistant. " + mode.Instruction +
                           " Reply with only the rewritten text: no quotes, no explanations, no options."),
                ("user", text),
              ], maxTokens: Math.Min(1500, text.Length + 200), temperature: 0.3f, onDelta, ct);

    /// <summary>Detects whether the text is English and describes its tone in a few words.</summary>
    public async Task<(bool IsEnglish, string Tone)?> AnalyzeAsync(string text, CancellationToken ct = default)
    {
        var reply = await ChatAsync([
            ("system", "Analyze the user's message. Reply with only JSON like {\"english\": true, \"tone\": \"friendly, confident\"}. " +
                       "\"english\" is false if the message is mostly Roman Urdu, Urdu, Hindi or another language. " +
                       "\"tone\" is 2-3 lowercase adjectives describing how the message sounds to a reader (e.g. polite, rude, casual, " +
                       "formal, confident, unsure, urgent, friendly, neutral)."),
            ("user", text.Length > 1500 ? text[..1500] : text),
        ], maxTokens: 40, ct: ct);
        if (reply == null) return null;
        try
        {
            int start = reply.IndexOf('{'), end = reply.LastIndexOf('}');
            using var doc = JsonDocument.Parse(reply[start..(end + 1)]);
            return (doc.RootElement.GetProperty("english").GetBoolean(), doc.RootElement.GetProperty("tone").GetString() ?? "");
        }
        catch { return null; }
    }
}

/// <summary>A rewrite option in the AI Rewrite window. Prompt modes turn rough notes into optimized AI prompts.</summary>
sealed record RewriteMode(string Name, string Instruction, bool IsPrompt = false);

/// <summary>Finds issues LanguageTool misses (e.g. unnecessary words) by asking the local AI to proofread each sentence.</summary>
sealed class AiReviewer(AiClient client)
{
    const int MaxNewSentencesPerPass = 8;
    readonly Dictionary<string, string> _cache = [];

    public async Task<List<Issue>> ReviewAsync(string text, IReadOnlyList<Issue> ltIssues)
    {
        var issues = new List<Issue>();
        int calls = 0;
        foreach (var (start, length) in TextTools.SplitSentences(text))
        {
            var sentence = text.Substring(start, length);
            int words = TextTools.CountWords(sentence);
            if (words < 3 || words > 60 || TextTools.IsForeign(start, length, words, ltIssues)) continue;

            string? corrected;
            lock (_cache) _cache.TryGetValue(sentence, out corrected);
            if (corrected == null)
            {
                if (calls++ >= MaxNewSentencesPerPass) break;
                corrected = await client.ProofreadAsync(sentence);
                if (corrected == null) continue;
                lock (_cache)
                {
                    if (_cache.Count > 1000) _cache.Clear();
                    _cache[sentence] = corrected;
                }
            }
            issues.AddRange(TextTools.DiffToIssues(sentence, corrected, start));
        }
        return issues;
    }
}

static class TextTools
{
    static readonly Regex WordRegex = new(@"[\p{L}\p{N}']+", RegexOptions.Compiled);
    static readonly Regex TokenRegex = new(@"[\p{L}\p{N}']+|\s+|[^\p{L}\p{N}'\s]", RegexOptions.Compiled);

    public static int CountWords(string s) => WordRegex.Matches(s).Count;

    /// <summary>Splits text into sentences (start, length), trimmed of surrounding whitespace.</summary>
    public static List<(int Start, int Length)> SplitSentences(string text)
    {
        var result = new List<(int, int)>();
        int start = 0;
        for (int i = 0; i <= text.Length; i++)
        {
            bool end = i == text.Length || (text[i] is '\n' or '\r' && LineBreakEndsSentence(text, i)) ||
                       (".!?".Contains(text[i]) && (i + 1 == text.Length || char.IsWhiteSpace(text[i + 1])));
            if (!end) continue;
            int stop = i < text.Length && text[i] is not ('\n' or '\r') ? i + 1 : i;
            int s = start, e = stop;
            while (s < e && char.IsWhiteSpace(text[s])) s++;
            while (e > s && char.IsWhiteSpace(text[e - 1])) e--;
            if (e > s) result.Add((s, e - s));
            start = stop;
        }
        return result;
    }

    /// <summary>
    /// "I have created an app⏎when you write…" is one sentence: a line break followed by a lowercase word
    /// continues the sentence, unless the line before already ended with punctuation or it is a blank line.
    /// </summary>
    static bool LineBreakEndsSentence(string text, int i)
    {
        int before = i - 1;
        while (before >= 0 && text[before] is ' ' or '\t') before--;
        if (before < 0 || ".!?:;".Contains(text[before]) || text[before] is '\n' or '\r') return true;

        int after = i;
        int breaks = 0;
        while (after < text.Length && char.IsWhiteSpace(text[after]))
        {
            if (text[after] == '\n') breaks++;
            after++;
        }
        if (after >= text.Length || breaks > 1) return true;
        return !char.IsLower(text[after]);
    }

    /// <summary>A sentence where most words are "misspelled" is almost certainly not English (e.g. Roman Urdu).</summary>
    public static bool IsForeign(int start, int length, int words, IReadOnlyList<Issue> ltIssues)
    {
        if (words < 2) return false;
        int misspelled = ltIssues.Count(i => i.IsSpelling && i.Offset >= start && i.Offset < start + length);
        return misspelled >= 2 && misspelled >= words * 0.4;
    }

    /// <summary>Drops LanguageTool issues inside sentences that are not English.</summary>
    public static List<Issue> RemoveForeign(string text, List<Issue> issues)
    {
        var foreign = SplitSentences(text)
            .Where(s => IsForeign(s.Start, s.Length, CountWords(text.Substring(s.Start, s.Length)), issues))
            .ToList();
        if (foreign.Count == 0) return issues;
        return issues.Where(i => !foreign.Any(s => i.Offset >= s.Start && i.Offset < s.Start + s.Length)).ToList();
    }

    /// <summary>Word-level diff between a sentence and the AI's correction, turned into individual suggestions.</summary>
    public static List<Issue> DiffToIssues(string original, string corrected, int baseOffset)
    {
        var result = new List<Issue>();
        corrected = corrected.Trim().Trim('"', '“', '”');
        if (corrected.Length == 0 || corrected == original || corrected.Length > original.Length * 2 + 20) return result;

        var a = TokenRegex.Matches(original).Select(m => (m.Value, m.Index)).ToList();
        var b = TokenRegex.Matches(corrected).Select(m => m.Value).ToList();

        // Longest common subsequence on tokens (whitespace tokens compare equal to each other).
        static bool Same(string x, string y) => x == y || (string.IsNullOrWhiteSpace(x) && string.IsNullOrWhiteSpace(y));
        var lcs = new int[a.Count + 1, b.Count + 1];
        for (int i = a.Count - 1; i >= 0; i--)
            for (int j = b.Count - 1; j >= 0; j--)
                lcs[i, j] = Same(a[i].Value, b[j]) ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);

        // A big rewrite (or a translation) is not a "small fix": skip it here, the Rewrite window handles that.
        int wordsA = a.Count(t => !string.IsNullOrWhiteSpace(t.Value)), wordsB = b.Count(t => !string.IsNullOrWhiteSpace(t));
        int wordsKept = CountKeptWords(a.Select(t => t.Value).ToList(), b);
        int changedWords = (wordsA - wordsKept) + (wordsB - wordsKept);
        if (wordsA == 0 || changedWords > Math.Max(3, (wordsA + wordsB) * 0.55)) return result;

        // Collect edit hunks as token ranges: a[DelStart..DelEnd) was replaced by b[InsStart..InsEnd).
        var hunks = new List<(int DelStart, int DelEnd, int InsStart, int InsEnd)>();
        int ia = 0, ib = 0;
        while (ia < a.Count || ib < b.Count)
        {
            if (ia < a.Count && ib < b.Count && Same(a[ia].Value, b[ib])) { ia++; ib++; continue; }

            int delStart = ia, insStart = ib;
            while ((ia < a.Count || ib < b.Count) && !(ia < a.Count && ib < b.Count && Same(a[ia].Value, b[ib])))
            {
                if (ib >= b.Count || (ia < a.Count && lcs[ia + 1, ib] >= lcs[ia, ib + 1])) ia++;
                else ib++;
            }
            // "should of went" → "should have gone": edits separated by one space become one suggestion.
            if (hunks.Count > 0 && hunks[^1] is var last && last.DelEnd + 1 == delStart && last.InsEnd + 1 == insStart &&
                string.IsNullOrWhiteSpace(a[last.DelEnd].Value) && last.DelEnd > last.DelStart && ia > delStart)
                hunks[^1] = (last.DelStart, ia, last.InsStart, ib);
            else
                hunks.Add((delStart, ia, insStart, ib));
        }

        foreach (var h in hunks)
        {
            var issue = MakeIssue(original, a, h.DelStart, h.DelEnd, string.Concat(b.Skip(h.InsStart).Take(h.InsEnd - h.InsStart)), baseOffset);
            if (issue != null) result.Add(issue);
        }
        return result;
    }

    static int CountKeptWords(List<string> a, List<string> b)
    {
        var lcs = new int[a.Count + 1, b.Count + 1];
        for (int i = a.Count - 1; i >= 0; i--)
            for (int j = b.Count - 1; j >= 0; j--)
                lcs[i, j] = a[i] == b[j] && !string.IsNullOrWhiteSpace(a[i]) ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
        return lcs[0, 0];
    }

    const int MaxWordsRemoved = 3, MaxWordsPerEdit = 4;

    static readonly HashSet<string> AuxiliaryVerbs = new(StringComparer.OrdinalIgnoreCase)
        { "is", "are", "was", "were", "be", "been", "am", "has", "have", "had", "do", "does", "did", "a", "an", "the",
          "their", "there", "they're", "its", "it's", "your", "you're", "then", "than", "to", "too", "of" };

    /// <summary>
    /// "gives"→"give", "is"→"are", "their"→"they're" are grammar fixes; "mistakes"→"errors" is only a word choice.
    /// </summary>
    static bool SameWordFamily(string a, string b)
    {
        var wa = a.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var wb = b.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (wa.Length != wb.Length) return true;                               // structural edit
        if (wa.Concat(wb).Any(AuxiliaryVerbs.Contains)) return true;           // "of went" → "have gone", "is" → "are"
        for (int i = 0; i < wa.Length; i++)
        {
            string x = wa[i].ToLowerInvariant(), y = wb[i].ToLowerInvariant();
            if (x == y) continue;
            int prefix = 0;
            while (prefix < Math.Min(x.Length, y.Length) && x[prefix] == y[prefix]) prefix++;
            bool related = prefix >= Math.Min(3, Math.Min(x.Length, y.Length)) || (AuxiliaryVerbs.Contains(x) && AuxiliaryVerbs.Contains(y));
            if (!related) return false;
        }
        return true;
    }

    static Issue? MakeIssue(string original, List<(string Value, int Index)> a, int delStart, int delEnd, string inserted, int baseOffset)
    {
        int start = delStart < a.Count ? a[delStart].Index : original.Length;
        int end = delEnd > delStart ? a[delEnd - 1].Index + a[delEnd - 1].Value.Length : start;
        string deleted = original[start..end];
        if (string.IsNullOrWhiteSpace(deleted) && string.IsNullOrWhiteSpace(inserted)) return null; // spacing only
        int deletedWords = CountWords(deleted), insertedWords = CountWords(inserted);
        if (deletedWords > MaxWordsPerEdit || insertedWords > MaxWordsPerEdit) return null;

        // Pure deletion: "I am in the just testing" -> remove "in the ". Only a few words: removing a clause changes the meaning.
        if (string.IsNullOrWhiteSpace(inserted))
        {
            if (deletedWords > MaxWordsRemoved) return null;
            return AiIssue(baseOffset + start, deleted, "", "AI_CLARITY", "Unnecessary words",
                $"“{deleted.Trim()}” seems unnecessary here. Remove it to make the sentence clearer.");
        }

        // Pure insertion: attach it to the previous word so there is something to underline.
        if (deleted.Length == 0)
        {
            int prev = delStart - 1;
            while (prev >= 0 && string.IsNullOrWhiteSpace(a[prev].Value)) prev--;
            if (prev >= 0)
            {
                int s = a[prev].Index;
                string span = original[s..start];
                if (insertedWords == 0)
                    return AiIssue(baseOffset + s, span, span + inserted, "AI_GRAMMAR", "Punctuation",
                        $"Add “{inserted.Trim()}” here.");
                return AiIssue(baseOffset + s, span, span + inserted, "AI_GRAMMAR", "Missing word",
                    $"It looks like “{inserted.Trim()}” is missing here.");
            }
            int next = delStart;
            while (next < a.Count && string.IsNullOrWhiteSpace(a[next].Value)) next++;
            if (next >= a.Count) return null;
            return AiIssue(baseOffset + a[next].Index, a[next].Value, inserted + a[next].Value, "AI_GRAMMAR", "Missing word",
                $"It looks like “{inserted.Trim()}” is missing here.");
        }

        // Replacement: trim whitespace at the edges of both sides.
        int lead = deleted.Length - deleted.TrimStart().Length;
        string del = deleted.Trim(), ins = inserted.Trim();
        if (string.Equals(del, ins, StringComparison.OrdinalIgnoreCase))
            return AiIssue(baseOffset + start + lead, del, ins, "AI_GRAMMAR", "Capitalization",
                $"Change “{del}” to “{ins}”.");
        if (!SameWordFamily(del, ins))
            return AiIssue(baseOffset + start + lead, del, ins, "AI_CLARITY", "Word choice",
                $"“{ins}” may be a better word here than “{del}”. Optional.");
        return AiIssue(baseOffset + start + lead, del, ins, "AI_GRAMMAR", "Grammar",
            $"Consider changing “{del}” to “{ins}”.");
    }

    static Issue AiIssue(int offset, string errorText, string replacement, string categoryId, string categoryName, string message) =>
        new(offset, errorText.Length, errorText, message, "", [replacement], "AI_" + categoryName.ToUpperInvariant().Replace(' ', '_'),
            categoryId, categoryName, "ai");
}
