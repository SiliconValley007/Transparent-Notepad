# Contributing

1. Fork, create a branch, make focused changes.
2. `dotnet build TransparentNotepad.sln -c Release` must be warning-free (`CI=true` makes warnings errors) and `dotnet test tests/TransparentNotepad.Tests` must pass.
3. Add or update tests in `tests/TransparentNotepad.Tests/CoreTests.cs` for any change in `Ai`, `Session`, `Transcription` or `Logging`.
4. Never commit build output, binaries, API keys, `config.json` or logs (`.gitignore` covers them).
5. Open a PR using the template; describe user-visible changes and update `CHANGELOG.md`.
