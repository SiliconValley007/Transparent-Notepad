using System.Text;

namespace TransparentNotepad.Session;

/// <summary>Result of committing a final segment: either a new line (with label) or text appended to the current line.</summary>
public readonly record struct Commit(bool NewParagraph, string Label, string Text);

/// <summary>Holds the rolling transcript; merges consecutive same-speaker finals within a time window.</summary>
public sealed class TranscriptBuffer
{
    private readonly TimeSpan _mergeWindow;
    private readonly Func<DateTime> _now;
    private readonly List<(string Label, StringBuilder Text, DateTime At)> _lines = new();
    private readonly Dictionary<string, string> _interim = new();
    private readonly object _gate = new();

    public TranscriptBuffer(TimeSpan? mergeWindow = null, Func<DateTime>? now = null)
    { _mergeWindow = mergeWindow ?? TimeSpan.FromSeconds(6); _now = now ?? (() => DateTime.UtcNow); }

    public Commit CommitFinal(string label, string text)
    {
        lock (_gate)
        {
            _interim.Remove(label);
            var now = _now();
            if (_lines.Count > 0 && _lines[^1].Label == label && now - _lines[^1].At < _mergeWindow)
            {
                var last = _lines[^1];
                last.Text.Append(' ').Append(text);
                _lines[^1] = (last.Label, last.Text, now);
                return new Commit(false, label, " " + text);
            }
            _lines.Add((label, new StringBuilder(text), now));
            return new Commit(true, label, text);
        }
    }

    public void SetInterim(string label, string text) { lock (_gate) _interim[label] = text; }
    public IReadOnlyList<KeyValuePair<string, string>> Interim() { lock (_gate) return _interim.ToList(); }

    public IReadOnlyList<(string Label, string Text, DateTime At)> Snapshot()
    { lock (_gate) return _lines.Select(l => (l.Label, l.Text.ToString(), l.At)).ToList(); }

    /// <summary>Replace all finalized lines (e.g. after AI speaker labelling). Each turn stays its own line.</summary>
    public void ReplaceAll(IEnumerable<(string Label, string Text)> turns)
    {
        lock (_gate)
        {
            _lines.Clear(); _interim.Clear(); var now = _now();
            foreach (var (label, text) in turns) _lines.Add((label, new StringBuilder(text), now));
        }
    }

    public void Clear() { lock (_gate) { _lines.Clear(); _interim.Clear(); } }

    /// <summary>Plain-text transcript for the LLM (finals + any in-flight partials).</summary>
    public string ToText(bool includeInterim = true)
    {
        lock (_gate)
        {
            var sb = new StringBuilder();
            foreach (var l in _lines) sb.Append(l.Label).Append(": ").Append(l.Text).Append('\n');
            if (includeInterim) foreach (var kv in _interim) sb.Append(kv.Key).Append(": ").Append(kv.Value).Append('\n');
            return sb.ToString().TrimEnd();
        }
    }
}
