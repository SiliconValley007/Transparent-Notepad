# Transparent Notepad — AI Meeting Assistant

[![build](https://github.com/SiliconValley007/Transparent-Notepad/actions/workflows/build.yml/badge.svg)](https://github.com/SiliconValley007/Transparent-Notepad/actions/workflows/build.yml)
[![license: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

A native **C# / .NET 8 WinForms** overlay for Windows 10/11: live transcription of your mic and system audio, private notes, a screen-aware AI assistant, and Markdown session logs — in a window that is **excluded from screen capture** (Zoom, Teams, Meet, Discord, OBS) while staying visible to you.
**Free to run (BYOK):** every user brings their own free API keys, so everyone has their own limits. No backend, no telemetry — all calls go from your PC straight to the provider you configure.

## Zero-key quickstart (100% free)
1. Download `TransparentNotepad-<version>-win-x64.zip` from **Releases**, unzip, run `TransparentNotepad.exe`.
2. Press **● Listen**. Speech works immediately with **no key and no setup**: Windows' built-in recognizer starts instantly while the local Whisper model (`tiny`, ~75 MB) downloads once in the background, then the app upgrades to Whisper automatically.
3. Press `Ctrl+Enter` for an AI answer. With no key the first request asks for **one-time consent** to use the free anonymous cloud AI (Pollinations, no account). Decline and the app stays fully offline (local Ollama if installed, otherwise a built-in heuristic).
4. Optional upgrades: free Groq / Gemini / OpenRouter keys (⚙ Settings) for better quality and speed; Deepgram for the lowest latency.

**AI chain (automatic, in this order):** your configured cloud keys (Groq → Gemini → OpenRouter) → local **Ollama** (auto-detected, installed model picked automatically) → **keyless free cloud** (opt-in) → **offline heuristic** (latest question / action-item keywords). 429/5xx are retried with backoff, failed providers cool down, answers are never spliced across providers.

## Features
- **Live transcription** (NAudio/WASAPI mic + loopback, 16 kHz). Mic + system audio are transcribed as separate streams: **[You]** (mic) and **[Interviewer]** (system audio; label configurable). Engine chain (**Auto**, or choose one):
  1. **Deepgram** WebSocket (`nova-3`, interim results, `endpointing=200`, ~300 ms) with reconnect + audio replay
  2. **Groq Whisper** (`whisper-large-v3-turbo`, REST) with rate-limit-aware sliding-window partials
  3. **Local Whisper.net** — offline, no key (first run downloads ~150 MB)
  Each tier falls back to the next automatically.
- **Speaker diarization**: Deepgram `[Speaker N]` turns (multi-speaker results are split per turn); **AI ▾ → Label speakers (AI)** adds turns for Groq/local engines and refuses to apply them if the wording changed.
- **AI with auto-failover**: Groq → Gemini → OpenRouter → Ollama (any OpenAI-compatible API; Anthropic optional). Exponential-backoff retries on 429/5xx, provider cooldowns, first-token and total timeouts, never splices two providers into one answer.
- **Screen vision** (`Ctrl+Shift+S`): in-memory JPEG of the foreground monitor (never includes this window, never written to disk) + recent transcript → Gemini / OpenRouter / Groq vision.
- **Prompts**: instant assist, follow-ups, real-time action items & speaker notes, comprehensive recap, raw-audio cleanup (word-for-word).
- **Export**: *Copy MD* (summary, decisions, action items, notes, transcript) and automatic session logs in `%AppData%\TransparentNotepad\Sessions\yyyy-MM-dd_HH-mm-ss.md`.
- **Resilient audio**: lock-free ring buffer, adaptive voice gate (−45 dBFS), automatic recovery from headset unplug / default-device or sample-rate changes, loopback → WaveIn fallback.
- **Native UX**: borderless draggable window, edge-resize, F11 full screen, per-monitor-v2 DPI, fluid layout (3 → 2 → 1 columns), opacity, always-on-top, hidden from Alt+Tab, DPAPI-encrypted keys, notepad + WebView2 browser tab.

## Stealth (Cluely-style)
- **Capture exclusion** (`SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)`) on the main window, Settings dialog and all popup menus; re-verified every 3 s. Toggle in Settings → *Hide from screen sharing*.
- **No-activate mode** (`WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW`): clicking the overlay or pressing hotkeys never steals focus, so web apps in Zoom/Teams/browser tests don't see a window blur. Focus is taken only while you type in the notes box or the browser tab, and released afterwards. Toggle in Settings → *Never steal focus*.
- Screen questions (`Ctrl+Shift+S`) never include the overlay: it is excluded from capture and, if stealth is off, blanked for the capture.
- Not hidden: native file dialogs, message boxes and tooltips (separate OS windows).

## Prerequisites
| | Requirement |
|---|---|
| OS | Windows 10 2004+ or Windows 11, x64 (older Windows shows a black box instead of hiding) |
| Users | Nothing else — release zips are self-contained. Browser tab needs the WebView2 Runtime (preinstalled on current Windows). |
| Developers | [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0), Git; Visual Studio 2022 (".NET desktop development") optional |

## Quick start (users)
1. Download `TransparentNotepad-<version>-win-x64.zip` from **Releases**, verify the `.sha256`, unzip, run `TransparentNotepad.exe` (unsigned build: SmartScreen → *More info → Run anyway*).
2. ⚙ **Settings → AI providers & keys** → *Get a free … API key* → paste → tick/order providers.
3. ⚙ **Speech & audio** → keep **Auto**, pick the audio source → **● Listen**.

### Free API keys
| Service | Used for | Key | Cost |
|---|---|---|---|
| Groq | AI + Groq Whisper | https://console.groq.com/keys | free tier, rate-limited |
| Google Gemini | AI + vision (primary) | https://aistudio.google.com/apikey | free tier, rate-limited |
| OpenRouter *(optional)* | AI + vision fallback | https://openrouter.ai/keys | `:free` models |
| Deepgram *(optional)* | fastest streaming speech | https://console.deepgram.com | trial credit, then paid |
| Ollama *(optional)* | local AI, no key | https://ollama.com/download | free, on your PC |

Free-tier terms and model names change — edit *Model* / *Vision model* / *Base URL* in Settings if one is retired. With no keys the app still transcribes locally and can answer through Ollama.

## Configuration guide
| Setting (⚙) | Meaning |
|---|---|
| Speech engine | `Auto` (Deepgram → Groq → Local, first with a key), or force `Deepgram` / `Groq` / `Local` |
| Audio source | Mic + system / Mic / System audio |
| Local model | `tiny` (fast, ~75 MB) or `base` (more accurate, ~150 MB) |
| System audio label | Speaker label for system audio when mic + system are captured separately (default **Interviewer**) |
| Stealth / No-activate / Keyless cloud | See *Stealth* and *Zero-key quickstart* |
| Language | `en` (fast English model), other ISO codes, or `multi` |
| Pause to finalize / Live partials | End-of-utterance pause (ms) and interim cadence for Groq/Local (partials self-throttle) |
| Fall back to local | Switch to local Whisper if a cloud engine fails |
| Providers list | ▲▼ order = failover order; checkbox = enabled; per-provider key, model, **vision model**, base URL |
| Blank window while capturing | Always hide the window during screen capture (otherwise only if capture-exclusion isn't active) |

Files (Windows user profile): `%LOCALAPPDATA%\TransparentNotepad\` → `config.json` (keys are DPAPI-encrypted), `models\`, `logs\app-YYYYMMDD.log` (structured, secrets redacted, 7-day retention), `error_log.txt`; `%AppData%\TransparentNotepad\Sessions\` → session Markdown.

**Hotkeys:** `Ctrl+Enter` assist · `Ctrl+Shift+S` ask about screen · `Ctrl+Shift+H` hide/show · `Esc` hide · `F11` full screen · `Ctrl+↑/↓` opacity · `Ctrl+N/O/S`, `Ctrl+Alt+S` file ops. Global hotkeys override those shortcuts in other apps while running.

## Build, test, publish
```powershell
git clone https://github.com/SiliconValley007/Transparent-Notepad.git
cd Transparent-Notepad
dotnet build TransparentNotepad.sln -c Release
dotnet test  tests/TransparentNotepad.Tests -c Release          # platform-neutral core suite (xUnit)
dotnet run   --project src/TransparentNotepad -c Release
dotnet publish src/TransparentNotepad -c Release -r win-x64   --self-contained true -p:PublishSingleFile=true -o publish/win-x64
```
Set `CI=true` locally to treat warnings as errors exactly like the pipeline. NuGet: NAudio, Whisper.net (+Runtime), Microsoft.Web.WebView2, System.Security.Cryptography.ProtectedData.

**Releases:** `git tag v2.0.0 && git push origin v2.0.0` → `release.yml` tests, publishes a self-contained win-x64 build and attaches the zip + SHA-256 file to a GitHub Release (tags with `-suffix` become pre-releases).

## Architecture
See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md). In short: `Audio` (NAudio capture) → `Session` (ring buffer, gate, transcript, export) → `Transcription` (Deepgram / Groq / local engines) → `MainForm` (UI) ↔ `Ai` (prompts, failover LLM client) with `Native` (Win32), `Config` (DPAPI settings) and `Logging` as cross-cutting layers. Everything except `Audio`, `Native`, `UI`, `MainForm` is platform-neutral and unit-tested.

## Notes & limits
- Capture exclusion needs Windows 10 2004+; native file dialogs and message boxes are separate windows and may be visible when sharing.
- Latency: Deepgram ≈ 300 ms; Groq/Local finalize after the pause setting plus engine time.
- Record/transcribe only where you have consent; screenshots and transcripts go only to the providers you enable.

## License
[MIT](LICENSE)
