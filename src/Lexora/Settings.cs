using System.Text.Json;
using Microsoft.Win32;

namespace Lexora;

/// <summary>User settings + personal dictionary, stored in %AppData%\Lexora.</summary>
sealed class Settings
{
    public bool Enabled { get; set; } = true;
    public string Language { get; set; } = "en-US";
    public int Port { get; set; } = 18081;
    public bool StartWithWindows { get; set; } = true;
    public int MaxEngineMemoryMb { get; set; } = 768;

    /// <summary>Local AI (llama.cpp + model in engine\llm): extra suggestions like unnecessary words, plus rewrites.</summary>
    public bool AiEnabled { get; set; } = true;
    public int AiPort { get; set; } = 18083;
    public int AiContextSize { get; set; } = 4096;

    /// <summary>Don't underline sentences that are not English (e.g. Roman Urdu).</summary>
    public bool SkipNonEnglish { get; set; } = true;

    /// <summary>Process names (without .exe) where live checking is turned off.</summary>
    public List<string> ExcludedApps { get; set; } = ["KeePass", "KeePassXC", "1Password", "Bitwarden", "WindowsTerminal", "cmd", "powershell", "devenv"];

    public List<string> DisabledRules { get; set; } = ["WHITESPACE_RULE"];

    public static string Folder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Lexora");

    /// <summary>Folder used before the app was renamed to Lexora; its settings and dictionary are migrated once.</summary>
    static string LegacyFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MyGrammarChecker");

    static string SettingsPath => Path.Combine(Folder, "settings.json");
    public static string DictionaryPath => Path.Combine(Folder, "dictionary.txt");

    static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static Settings Load()
    {
        Directory.CreateDirectory(Folder);
        foreach (var file in new[] { "settings.json", "dictionary.txt" })
        {
            string legacy = Path.Combine(LegacyFolder, file), current = Path.Combine(Folder, file);
            if (File.Exists(legacy) && !File.Exists(current)) File.Copy(legacy, current);
        }
        try
        {
            if (File.Exists(SettingsPath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsPath)) ?? new Settings();
        }
        catch (Exception ex)
        {
            // Corrupt file: keep a copy so the user's exclusions/rules aren't silently lost, then fall back to defaults.
            try { File.Copy(SettingsPath, SettingsPath + ".bad", overwrite: true); } catch { }
            Log.Write($"[Settings] could not read settings.json ({ex.Message}); kept a copy as settings.json.bad");
        }
        var fresh = new Settings();
        fresh.Save();
        return fresh;
    }

    public void Save() => File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, JsonOptions));
}

sealed class PersonalDictionary
{
    readonly HashSet<string> _words = new(StringComparer.OrdinalIgnoreCase);

    public PersonalDictionary() => Reload();

    public void Reload()
    {
        lock (_words)
        {
            _words.Clear();
            if (!File.Exists(Settings.DictionaryPath)) File.WriteAllText(Settings.DictionaryPath, "");
            foreach (var line in File.ReadAllLines(Settings.DictionaryPath))
                if (line.Trim() is { Length: > 0 } w) _words.Add(w);
        }
    }

    public bool Contains(string word) { lock (_words) return _words.Contains(word.Trim()); }

    public void Add(string word)
    {
        word = word.Trim();
        lock (_words)
            if (_words.Add(word)) File.AppendAllLines(Settings.DictionaryPath, [word]);
    }
}

static class AutoStart
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "Lexora";
    const string LegacyValueName = "MyGrammarChecker";

    public static void Apply(bool enable)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key == null) return;
        key.DeleteValue(LegacyValueName, throwOnMissingValue: false);
        if (enable) key.SetValue(ValueName, $"\"{Environment.ProcessPath}\"");
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
