namespace Lexora;

/// <summary>Regression checks for sentence splitting and AI diff suggestions (writes PASS/FAIL lines to log.txt).</summary>
static class DiffSelfTest
{
    public static void Run()
    {
        int failed = 0;
        void Check(string name, bool ok, string detail)
        {
            if (!ok) failed++;
            Log.Write($"[DiffTest] {(ok ? "PASS" : "FAIL")} {name}: {detail}");
        }

        static string Show(List<Issue> issues) =>
            string.Join(" | ", issues.Select(i => $"'{i.ErrorText}'->'{i.Replacements[0]}' ({i.CategoryName})"));

        // The screenshot case: a model answer that drops a whole clause must NOT become "Remove".
        var dropped = TextTools.DiffToIssues(
            "when you write something wrong, it will auto-detect the mistakes and gives suggestions for spelling and grammar mistakes.",
            "When you write something wrong, and gives suggestions for spelling and grammar mistakes.", 0);
        Check("no clause removal", !dropped.Any(i => i.Replacements[0].Trim().Length == 0 && TextTools.CountWords(i.ErrorText) > 3), Show(dropped));

        // The real correction for that sentence: small, correct fixes.
        var good = TextTools.DiffToIssues(
            "when you write something wrong, it will auto-detect the mistakes and gives suggestions for spelling and grammar mistakes.",
            "When you write something wrong, it will auto-detect the mistakes and give suggestions for spelling and grammar errors.", 0);
        Check("small fixes kept", good.Any(i => i.ErrorText == "gives" && i.Replacements[0] == "give" && i.CategoryName == "Grammar"), Show(good));
        Check("labels", good.Any(i => i.ErrorText == "mistakes" && i.CategoryName == "Word choice" && i.Kind == IssueKind.Clarity)
                        && good.Any(i => i.ErrorText == "when" && i.CategoryName == "Capitalization"), Show(good));

        var grammar = TextTools.DiffToIssues("The weather are nice and I should of went.", "The weather is nice and I should have gone.", 0);
        Check("grammar labels", grammar.All(i => i.CategoryName == "Grammar"), Show(grammar));

        // The original request: extra words get a Remove suggestion.
        var extra = TextTools.DiffToIssues("I am in the just testing my tool.", "I am just testing my tool.", 0);
        Check("unnecessary words", extra.Count == 1 && extra[0].ErrorText.Trim() == "in the" && extra[0].Replacements[0] == "", Show(extra));

        // Shift+Enter inside a sentence keeps it one sentence; a real new sentence still splits.
        const string chat = "I'm just testing something.\n I have created an Autocorrection app\nwhen you write something wrong, it works.\nNew line here.";
        var sentences = TextTools.SplitSentences(chat).Select(s => chat.Substring(s.Start, s.Length)).ToList();
        Check("line-break continuation", sentences.Count == 3 && sentences[1].StartsWith("I have") && sentences[1].EndsWith("it works."),
            string.Join(" || ", sentences.Select(s => s.Replace("\n", "⏎"))));

        Log.Write($"[DiffTest] done, {failed} failed");
    }
}
