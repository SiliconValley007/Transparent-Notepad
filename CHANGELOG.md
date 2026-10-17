# Changelog

## 2.1.0
- **Zero-key out of the box**: Windows built-in speech recognition starts instantly while local Whisper (`tiny`) downloads; AI chain configured keys -> auto-probed Ollama -> opt-in keyless cloud (Pollinations) -> offline heuristic.
- **Stealth**: no-activate window mode, capture exclusion for main window, dialogs and popup menus, unified `Win32.ApplyCaptureExclusion`.
- Dual-stream attribution: mic = **[You]**, system audio = **[Interviewer]** (configurable); shared testable `FrameAssembler` guarantees silence frames when loopback is idle.
- WebView2 runtime-missing no longer crashes (inline fallback), transparent background; hotkey conflicts are non-modal toasts.
- Fixed: transcript buffer was not updated when no `FinalCommitted` subscriber existed.
- Native runtime pruning (win-x64 only), release now ships win-x64; new keyless/E2E/Windows-stealth test suites; CI publish check.

## 2.0.0
- Live transcription: Deepgram / Groq Whisper / local Whisper.net with automatic fallback; lock-free audio pipeline, noise gate, device recovery.
- AI failover engine (Groq → Gemini → OpenRouter → Ollama), screen vision, diarization, Markdown export and session logs.
- Structured redacting logger, hardened model download, config validation, repository restructure with xUnit tests and CI/release workflows.
