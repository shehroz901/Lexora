# Downloads LanguageTool, a Java runtime, llama.cpp and the Qwen model into app\engine (skips what already exists).
param([string]$Target = (Join-Path $PSScriptRoot 'app\engine'))
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$tmp = Join-Path $env:TEMP 'lexora-setup'
New-Item -ItemType Directory -Force $Target, $tmp | Out-Null

function Get-File($url, $out) {
    Write-Host "Downloading $url"
    Invoke-WebRequest -Uri $url -OutFile $out -UseBasicParsing
}

# 1. LanguageTool
$lt = Join-Path $Target 'LanguageTool'
if (-not (Test-Path "$lt\languagetool-server.jar")) {
    Get-File 'https://languagetool.org/download/LanguageTool-6.6.zip' "$tmp\lt.zip"
    Expand-Archive "$tmp\lt.zip" "$tmp\lt" -Force
    Remove-Item $lt -Recurse -Force -ErrorAction SilentlyContinue
    Move-Item (Get-ChildItem "$tmp\lt" -Directory | Select-Object -First 1).FullName $lt
}

# 2. Java 21 runtime (only used by LanguageTool)
$jre = Join-Path $Target 'jre'
if (-not (Test-Path "$jre\bin\java.exe")) {
    Get-File 'https://api.adoptium.net/v3/binary/latest/21/ga/windows/x64/jre/hotspot/normal/eclipse' "$tmp\jre.zip"
    Expand-Archive "$tmp\jre.zip" "$tmp\jre" -Force
    Remove-Item $jre -Recurse -Force -ErrorAction SilentlyContinue
    Move-Item (Get-ChildItem "$tmp\jre" -Directory | Select-Object -First 1).FullName $jre
}

# 3. llama.cpp (Vulkan build: works on NVIDIA, AMD and Intel GPUs, falls back to CPU) + Qwen3 model
$llm = Join-Path $Target 'llm'
New-Item -ItemType Directory -Force $llm | Out-Null
if (-not (Test-Path "$llm\llama-server.exe")) {
    Get-File 'https://github.com/ggml-org/llama.cpp/releases/download/b11224/llama-b11224-bin-win-vulkan-x64.zip' "$tmp\llama.zip"
    Expand-Archive "$tmp\llama.zip" $llm -Force
}
if (-not (Test-Path "$llm\model.gguf")) {
    Get-File 'https://huggingface.co/unsloth/Qwen3-4B-Instruct-2507-GGUF/resolve/main/Qwen3-4B-Instruct-2507-Q4_K_M.gguf' "$llm\model.gguf"
}

Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
Write-Host "`nDone. Engine installed in $Target"
