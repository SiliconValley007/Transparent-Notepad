using System.Text;

namespace TransparentNotepad.Session;

public static class MarkdownExporter
{
    /// <summary>Session Markdown: header, optional AI summary (decisions / action items), notes, speaker transcript.</summary>
    public static string Build(DateTime start, DateTime end, IReadOnlyList<(string Label, string Text, DateTime At)> lines,
        string notes, string? summaryMarkdown, string audioSource)
    {
        var sb = new StringBuilder();
        var mins = Math.Max(0, (int)Math.Round((end - start).TotalMinutes));
        sb.Append("# Meeting session - ").Append(start.ToString("yyyy-MM-dd HH:mm")).Append("\n\n");
        sb.Append("*Duration: ").Append(mins).Append(" min | Audio: ").Append(audioSource).Append(" | Exported with Transparent Notepad*\n\n");
        sb.Append(string.IsNullOrWhiteSpace(summaryMarkdown) ? "_No AI summary generated for this export._" : summaryMarkdown.Trim()).Append("\n\n");
        if (!string.IsNullOrWhiteSpace(notes))
        {
            sb.Append("## Notes\n\n");
            foreach (var l in notes.Trim().Replace("\r\n", "\n").Split('\n')) sb.Append("> ").Append(l).Append('\n');
            sb.Append('\n');
        }
        sb.Append("## Transcript\n\n");
        if (lines.Count == 0) sb.Append("_No speech was transcribed._\n");
        foreach (var l in lines) sb.Append("**[").Append(l.Label).Append("]:** ").Append(l.Text.Trim()).Append("\n\n");
        return sb.ToString().TrimEnd() + "\n";
    }
}

/// <summary>One Markdown log per session: Sessions/yyyy-MM-dd_HH-mm-ss.md, written atomically.</summary>
public sealed class SessionLog
{
    private readonly string _dir; private readonly Func<DateTime> _now;
    public DateTime? Started { get; private set; }
    public string? Path { get; private set; }

    public SessionLog(string dir, Func<DateTime>? now = null) { _dir = dir; _now = now ?? (() => DateTime.Now); }

    public void Begin(DateTime? at = null) { Started ??= at ?? _now(); }
    public void Reset() { Started = null; Path = null; }

    /// <summary>Writes (creates or overwrites) this session's file. Returns the path.</summary>
    public string Save(string markdown)
    {
        Directory.CreateDirectory(_dir);
        Begin();
        if (Path == null)
        {
            var baseName = Started!.Value.ToString("yyyy-MM-dd_HH-mm-ss"); var p = System.IO.Path.Combine(_dir, baseName + ".md"); int n = 1;
            while (File.Exists(p)) p = System.IO.Path.Combine(_dir, $"{baseName}_{n++}.md");   // never clobber another session
            Path = p;
        }
        var tmp = Path + ".tmp";
        File.WriteAllText(tmp, markdown, new UTF8Encoding(false));
        File.Move(tmp, Path, overwrite: true);
        return Path;
    }
}
