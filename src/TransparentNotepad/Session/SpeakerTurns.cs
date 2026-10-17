using System.Text.RegularExpressions;

namespace TransparentNotepad.Session;

/// <summary>Parses "[Speaker 1]: text" / "You: text" lines (LLM diarization output) into turns.</summary>
public static class SpeakerTurns
{
    private static readonly Regex Line = new(@"^\s*\[?\s*(Speaker\s*\d+|You|Them|Interviewer)\s*\]?\s*:\s*(.*)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static List<(string Label, string Text)> Parse(string text)
    {
        var turns = new List<(string Label, string Text)>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim(); if (line.Length == 0) continue;
            var m = Line.Match(line);
            if (m.Success)
            {
                var lab = Regex.Replace(m.Groups[1].Value.Trim(), @"\s+", " ");
                lab = lab.Equals("you", StringComparison.OrdinalIgnoreCase) ? "You" : lab.Equals("them", StringComparison.OrdinalIgnoreCase) ? "Them" : lab.Equals("interviewer", StringComparison.OrdinalIgnoreCase) ? "Interviewer" : "Speaker " + Regex.Match(lab, @"\d+").Value;
                turns.Add((lab, m.Groups[2].Value.Trim()));
            }
            else if (turns.Count > 0) turns[^1] = (turns[^1].Label, (turns[^1].Text + " " + line).Trim());   // wrapped continuation
        }
        return turns;
    }
}
