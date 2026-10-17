using TransparentNotepad.Native;
using TransparentNotepad.Session;

namespace TransparentNotepad.UI;

/// <summary>Read-only, word-wrapping rolling transcript. Finals are appended; partials live in a replaceable tail region.</summary>
public sealed class TranscriptView : RichTextBox
{
    private int _interimStart = -1;
    private static readonly Color You = Color.FromArgb(0, 102, 204), Them = Color.FromArgb(0, 128, 96), Gray = Color.FromArgb(130, 130, 130);

    public TranscriptView()
    {
        ReadOnly = true; BorderStyle = BorderStyle.None; BackColor = Color.White; ScrollBars = RichTextBoxScrollBars.Vertical;
        WordWrap = true; DetectUrls = false; TabStop = false; HideSelection = false; Dock = DockStyle.Fill;
        Font = new Font("Segoe UI", 10.5F);
    }

    private static Color LabelColor(string label) => label == "You" ? You : Them;

    private void Put(string text, Color color, FontStyle style)
    {
        SelectionStart = TextLength; SelectionLength = 0;
        SelectionColor = color;
        using var f = new Font(Font, style);
        SelectionFont = f;
        SelectedText = text;
    }

    private void RemoveInterim()
    {
        if (_interimStart < 0) return;
        Select(_interimStart, TextLength - _interimStart);
        SelectedText = "";
        _interimStart = -1;
    }

    private void ScrollEnd() { SelectionStart = TextLength; SelectionLength = 0; ScrollToCaret(); }

    public void AppendFinal(Commit c)
    {
        Win32.SetRedraw(Handle, false);
        try
        {
            RemoveInterim();
            if (c.NewParagraph)
            {
                if (TextLength > 0) Put("\n", ForeColor, FontStyle.Regular);
                Put($"[{c.Label}]: ", LabelColor(c.Label), FontStyle.Bold);
                Put(c.Text, Color.Black, FontStyle.Regular);
            }
            else Put(c.Text, Color.Black, FontStyle.Regular);
        }
        finally { Win32.SetRedraw(Handle, true); Invalidate(); }
        ScrollEnd();
    }

    public void SetInterim(IReadOnlyList<KeyValuePair<string, string>> partials)
    {
        Win32.SetRedraw(Handle, false);
        try
        {
            RemoveInterim();
            if (partials.Count == 0) return;
            _interimStart = TextLength;
            foreach (var p in partials)
            {
                if (TextLength > 0) Put("\n", Gray, FontStyle.Regular);
                Put($"[{p.Key}]: {p.Value}", Gray, FontStyle.Italic);
            }
        }
        finally { Win32.SetRedraw(Handle, true); Invalidate(); }
        ScrollEnd();
    }

    public void ResetView() { Clear(); _interimStart = -1; }
}
