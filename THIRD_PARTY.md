# Third-party components

Lexora's own code is MIT-licensed. It does **not** redistribute the components below in this repository:
`setup-engine.cmd` downloads them from their official sources onto your machine, and Lexora runs them as
separate local processes.

| Component | Used for | License | Source |
|---|---|---|---|
| LanguageTool 6.6 | Grammar/spelling rules | LGPL-2.1 | https://languagetool.org |
| Eclipse Temurin JRE 21 | Runs LanguageTool | GPL-2.0 + Classpath Exception | https://adoptium.net |
| llama.cpp (b11224, Vulkan) | Runs the local model | MIT | https://github.com/ggml-org/llama.cpp |
| Qwen3-4B-Instruct-2507 (Q4_K_M GGUF) | Local AI rewrites/proofreading | Apache-2.0 | https://huggingface.co/Qwen/Qwen3-4B-Instruct-2507 |
| FlaUI.UIA3 | UI Automation (NuGet) | MIT | https://github.com/FlaUI/FlaUI |

Lexora is an independent project and is not affiliated with Grammarly, LanguageTool, or Alibaba/Qwen.
