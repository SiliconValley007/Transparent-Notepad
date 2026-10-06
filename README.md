# Transparent Notepad

A lightweight, high-contrast C# Windows Forms application that leverages the Win32 API (`SetWindowDisplayAffinity`) to protect confidential notes by rendering the window invisible to screen recording, streaming, and screen-sharing utilities (such as Zoom, Teams, or OBS)[cite: 19, 20].

## Features

- **Screen Share Protection**: Excluded from window captures via Win32 `WDA_EXCLUDEFROMCAPTURE`[cite: 19, 20].
- **High-Contrast Pure White UI**: Clean, light layout with crisp black text rendering across all menus and text controls.
- **Dynamic Transparency & HUD Toast**: Adjust form opacity on-the-fly with instant non-intrusive floating status notifications.
- **File Operations**: Unsaved modification markers (`*`), file prompts before exit/new document creation, and drag-and-drop text file loading[cite: 19, 20].
- **Multi-Monitor Safe**: Automatic workspace boundary checking to prevent windows from opening off-screen[cite: 19].

## Prerequisites

- .NET 8.0 SDK / .NET 10.0 SDK (or later)
- Windows 10 / 11
- Microsoft Edge WebView2 Runtime

## Build & Run

```bash
dotnet build
dotnet run
dotnet publish -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true
```

### To install WebView

```bash
dotnet add package Microsoft.Web.WebView2
```

**Configuration file location**(this is where opacity is persisted)
C:\Users\<YourUsername>\AppData\Local\TransparentNotepad\config.json
A
**Unhandled exception logging** (this is where unhandled errors are logged{same directory where opacity is persisted})
%LOCALAPPDATA%\TransparentNotepad\error_log.txt

## Keyboard Shortcuts

| **Shortcut** | **Action** |
| --- | --- |
| Ctrl + N | Create a new document |
| Ctrl + O | Open a text file |
| Ctrl + S | Save current file |
| Ctrl + Shift + S | Save file as... |
| Ctrl + Up | Increase window opacity |
| Ctrl + Down | Decrease window opacity |
| Ctrl + Shift + H | Global hotkey to hide/restore window |
| Esc | Quick hide window |
