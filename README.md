# Lexora

Personal, **offline** AI writing assistant for Windows. It underlines mistakes in any text box
(Chrome/Edge sites like ChatGPT, Notepad, Word, Teams, …), fixes them on click, and rewrites,
translates and optimizes prompts with a local AI model. Nothing you type leaves your PC.

## Install
1. Download `Lexora.exe` from the Releases page (or build it, see below) into `app\`.
2. Run `setup-engine.cmd` once. It downloads the engines (~3 GB: LanguageTool, Java 21, llama.cpp, Qwen3-4B) into `app\engine`.
3. Start `app\Lexora.exe`. Requires Windows 10/11 and the .NET 10 Desktop Runtime.

See [THIRD_PARTY.md](THIRD_PARTY.md) for the licenses of the downloaded components. Lexora is MIT-licensed.

## Folders
| Path | What |
|---|---|
| `app\Lexora.exe` | The app (starts with Windows) |
| `app\engine\LanguageTool` | Rule-based grammar engine (LanguageTool 6.6) |
| `app\engine\jre` | Java 21 runtime used only by LanguageTool |
| `app\engine\llm` | Local AI: llama.cpp (Vulkan) + Qwen3-4B-Instruct model |
| `src\Lexora` | C# source (.NET 10, WinForms, FlaUI) |
| `%AppData%\Lexora` | `settings.json`, `dictionary.txt`, `log.txt` |

## Use
- Type anywhere and pause ~1 second: mistakes get underlined (rose = correctness, blue = clarity, violet = style).
- Hover an underline: click the suggestion to fix it, **Remove** for unnecessary words, **Dismiss**,
  **Add to dictionary**, or **✦ Rewrite** for the whole sentence.
- **Ctrl+Alt+G**: grammar-check the selected text in any app (works where live underlines can't).
- **Ctrl+Alt+R**: Lexora AI on the selected text:
  - *Rewrite*: Improve · Shorter · Formal · Friendly · Fix grammar · To English (Roman Urdu → English)
  - *Prompt optimizer*: 🎮 Game Dev Prompt (senior game developer, 50 years) · 📈 ASO & UA Prompt
  - Tone check of your text; results stream in as the AI writes.
- Roman Urdu / non-English sentences are not underlined.
- Tray icon: live checking, AI on/off (off frees the ~2.5 GB the model uses), language, dictionary,
  start with Windows, exit. The look follows the Windows light/dark setting.

## Rebuild after code changes
```
cd src\Lexora
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:DebugType=none -o ..\..\app
```
Exit Lexora from the tray first. `Lexora.exe --selftest` opens test windows and writes results to `log.txt`.
`Lexora.exe --export-icon app.ico` regenerates the logo icon.

## Settings (`settings.json`)
`ExcludedApps` (process names to skip), `Language`, `DisabledRules` (LanguageTool rule IDs), `Port` (18081),
`MaxEngineMemoryMb`, `AiEnabled`, `AiPort` (18083), `AiContextSize`, `SkipNonEnglish`.
