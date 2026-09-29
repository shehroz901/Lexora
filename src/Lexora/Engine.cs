using System.Diagnostics;
using System.Text.Json;

namespace Lexora;

enum IssueKind { Correctness, Clarity, Style }

sealed record Issue(
    int Offset, int Length, string ErrorText,
    string Message, string ShortMessage, IReadOnlyList<string> Replacements,
    string RuleId, string CategoryId, string CategoryName, string IssueType)
{
    /// <summary>LanguageTool's dictionary-based spell checker (the only rule "Add to dictionary" applies to).</summary>
    public bool IsSpelling => RuleId.StartsWith("MORFOLOGIK", StringComparison.OrdinalIgnoreCase);

    public IssueKind Kind => CategoryId switch
    {
        "REDUNDANCY" or "PLAIN_ENGLISH" or "WORDINESS" or "STYLE" or "AI_CLARITY" => IssueKind.Clarity,
        "TYPOGRAPHY" or "CREATIVE_WRITING" or "REPETITIONS_STYLE" => IssueKind.Style,
        _ => IssueKind.Correctness,
    };

    public bool IsAi => IssueType == "ai";

    public string Title => IsSpelling ? "Spelling" : CategoryName;

    /// <summary>Key used to remember "Dismiss" for the rest of the session.</summary>
    public string DismissKey => $"{RuleId}|{ErrorText}";
}

/// <summary>Starts the bundled LanguageTool server (local Java process) and reports when it is ready.</summary>
sealed class EngineServer(int port, int maxMemoryMb)
{
    Process? _process;
    public volatile bool IsReady;
    public string Status { get; private set; } = "Starting engine…";

    public static string? FindEngineFolder()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            foreach (var candidate in new[] { Path.Combine(dir.FullName, "engine"), Path.Combine(dir.FullName, "app", "engine") })
                if (File.Exists(Path.Combine(candidate, "LanguageTool", "languagetool-server.jar")) &&
                    File.Exists(Path.Combine(candidate, "jre", "bin", "java.exe")))
                    return candidate;
        }
        return null;
    }

    public async Task<bool> StartAsync(LanguageToolClient client)
    {
        if (await client.PingAsync())
            return Ready("Engine ready");

        var folder = FindEngineFolder();
        if (folder == null)
        {
            Status = "Engine folder not found (needs engine\\LanguageTool and engine\\jre next to the app)";
            return false;
        }

        var ltDir = Path.Combine(folder, "LanguageTool");
        var psi = new ProcessStartInfo(Path.Combine(folder, "jre", "bin", "java.exe"))
        {
            WorkingDirectory = ltDir,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in new[]
                 {
                     "-Xms64m", $"-Xmx{maxMemoryMb}m", "-XX:+UseSerialGC",
                     "-cp", Path.Combine(ltDir, "languagetool-server.jar"),
                     "org.languagetool.server.HTTPServer", "--port", port.ToString(),
                 })
            psi.ArgumentList.Add(arg);

        _process = Process.Start(psi);
        if (_process == null) { Status = "Could not start Java"; return false; }
        Native.TieLifetimeToThisProcess(_process);

        for (int i = 0; i < 120; i++)
        {
            await Task.Delay(500);
            if (_process.HasExited) { Status = $"Engine stopped (exit code {_process.ExitCode})"; return false; }
            if (await client.PingAsync())
            {
                await client.CheckAsync("This is a warm up sentence.", "en-US"); // loads models so the first real check is fast
                return Ready("Engine ready");
            }
        }
        Status = "Engine did not start in time";
        return false;
    }

    bool Ready(string status) { Status = status; IsReady = true; return true; }

    public void Stop()
    {
        try { if (_process is { HasExited: false }) _process.Kill(entireProcessTree: true); } catch { }
    }
}

/// <summary>Client for the local LanguageTool HTTP API (POST /v2/check).</summary>
sealed class LanguageToolClient(int port, Func<IReadOnlyList<string>> disabledRules)
{
    readonly HttpClient _http = new() { BaseAddress = new Uri($"http://127.0.0.1:{port}/"), Timeout = TimeSpan.FromSeconds(20) };

    public async Task<bool> PingAsync()
    {
        try { return (await _http.GetAsync("v2/languages")).IsSuccessStatusCode; }
        catch { return false; }
    }

    /// <returns>The issues found, or null if the engine could not be reached.</returns>
    public async Task<List<Issue>?> CheckAsync(string text, string language)
    {
        try
        {
            var form = new Dictionary<string, string> { ["text"] = text, ["language"] = language };
            var disabled = disabledRules();
            if (disabled.Count > 0) form["disabledRules"] = string.Join(',', disabled);

            using var response = await _http.PostAsync("v2/check", new FormUrlEncodedContent(form));
            if (!response.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

            var issues = new List<Issue>();
            foreach (var m in doc.RootElement.GetProperty("matches").EnumerateArray())
            {
                int offset = m.GetProperty("offset").GetInt32(), length = m.GetProperty("length").GetInt32();
                if (length <= 0 || offset < 0 || offset + length > text.Length) continue;

                var rule = m.GetProperty("rule");
                var category = rule.GetProperty("category");
                issues.Add(new Issue(
                    offset, length, text.Substring(offset, length),
                    m.GetProperty("message").GetString() ?? "",
                    m.TryGetProperty("shortMessage", out var sm) ? sm.GetString() ?? "" : "",
                    m.GetProperty("replacements").EnumerateArray()
                        .Select(r => r.GetProperty("value").GetString() ?? "").Take(5).ToList(),
                    rule.GetProperty("id").GetString() ?? "",
                    category.GetProperty("id").GetString() ?? "",
                    category.GetProperty("name").GetString() ?? "",
                    rule.TryGetProperty("issueType", out var it) ? it.GetString() ?? "" : ""));
            }
            return issues;
        }
        catch
        {
            return null;
        }
    }
}
