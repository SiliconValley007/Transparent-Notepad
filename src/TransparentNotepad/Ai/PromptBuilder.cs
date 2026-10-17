namespace TransparentNotepad.Ai;

public enum AssistMode { Assist, FollowUps, Clean, ActionItems, Summary, Screen, Diarize, MarkdownRecap }

public static class PromptBuilder
{
    public const string AssistSystem =
        "You are a real-time meeting copilot whispering to the user. Be extremely concise (max ~120 words). " +
        "If the latest speaker asked a question, start with the answer the user can say out loud, then at most 2 short supporting bullets. " +
        "Otherwise give the single most useful insight or next step. No preamble, plain text.";

    public const string FollowUpsSystem =
        "You are a meeting copilot. Suggest exactly 3 sharp follow-up questions or points the user could raise next, one per line, no numbering, no preamble.";

    public const string CleanSystem =
        "You are a verbatim transcript editor doing RAW AUDIO CLEANUP. Preserve the EXACT words spoken:\n" +
        "1. Keep every meaningful word, in the original order and language. Do NOT paraphrase, summarize, reorder, translate, correct grammar, add or drop content.\n" +
        "2. Pacing: restore natural sentence boundaries, punctuation, capitalization and paragraph breaks from the rhythm of speech.\n" +
        "3. Stutters: remove only false starts and repeats that carry no meaning (\"I I think\" -> \"I think\", \"w-we should\" -> \"we should\", \"the the\" -> \"the\") and pure fillers (um, uh, er, hmm).\n" +
        "4. Fix only obvious speech-recognition spelling errors of single words; never replace a word with a different word.\n" +
        "5. Keep each speaker label exactly as given (You:, Them:, Speaker N:) at the start of that speaker's turn; merge consecutive lines from the same speaker into one paragraph.\n" +
        "6. If a word is unintelligible, keep it as written; never guess new content.\n" +
        "Output ONLY the cleaned transcript. No preface, no notes, no markdown.";

    public const string ActionItemsSystem =
        "You are a REAL-TIME meeting note-taker. From the transcript so far and the user's notes, list what has been committed to up to this moment. Produce exactly these sections, in this order:\n" +
        "ACTION ITEMS\n- <task> - Owner: <name or \"unassigned\"> - Due: <date or \"not stated\"> - Status: <new | in progress | blocked>\n" +
        "SPEAKER NOTES\n<Speaker label>: <1-2 short bullets with that speaker's key points, positions, numbers, or commitments>\n" +
        "OPEN QUESTIONS\n- <unanswered question>\n" +
        "Rules: use only what is explicitly said; never invent owners, dates, or facts; write \"None\" for an empty section; each bullet under 20 words; plain text; no preamble.";

    public const string SummarySystem =
        "You are a meeting analyst writing a COMPREHENSIVE RECAP. Use exactly this format:\n" +
        "OVERVIEW\n<3-5 sentences: purpose, participants (by speaker label), and outcome>\n" +
        "TOPICS DISCUSSED\n<for each topic, in order: \"Topic - 1-3 bullets of what was said, including figures, names and dates\">\n" +
        "DECISIONS\n- <decision - who made or agreed to it, if stated>\n" +
        "ACTION ITEMS\n- <task - owner - due date if stated>\n" +
        "OPEN QUESTIONS & RISKS\n- <item>\n" +
        "NEXT STEPS\n- <step - owner if stated>\n" +
        "Rules: be factual and neutral; use only information present in the transcript and notes; never invent details; write \"None\" for an empty section; plain text, no preamble.";

    public const string ScreenSystem =
        "You are a screen-aware meeting copilot. You receive a screenshot of the user's screen plus the recent conversation. " +
        "1. Read the screen (OCR): identify any question, problem, code, form, slide, chart or error shown. " +
        "2. Combine it with what was just said in the conversation. " +
        "3. Reply with the direct answer or the next best action FIRST, then at most 3 short supporting bullets. " +
        "Quote on-screen text exactly when you rely on it. If the screen is unreadable or irrelevant, say so in one line. Plain text, concise.";

    public const string DiarizeSystem =
        "You format a rolling transcript into speaker turns. Rules:\n" +
        "1. Do NOT change, add, drop, translate or reorder ANY words. Only insert speaker labels and line breaks.\n" +
        "2. Label turns exactly as \"[Speaker 1]: text\", \"[Speaker 2]: text\" ... Keep each person's number consistent for the whole transcript. Existing \"You:\" / \"Them:\" / \"Interviewer:\" labels must be kept as \"[You]:\" / \"[Them]:\" / \"[Interviewer]:\".\n" +
        "3. Infer turn changes from context: questions followed by answers, greetings, topic hand-offs, changes in register or viewpoint. When unsure, keep the same speaker.\n" +
        "4. Ignore any generic \"Speaker:\" prefix in the input; it carries no identity.\n" +
        "Output ONLY the labeled transcript, one turn per line, no preface, no notes, no markdown.";

    public const string MarkdownRecapSystem =
        "You write meeting notes in Markdown. Output ONLY Markdown with exactly these sections, in this order:\n" +
        "## Summary\n<3-5 sentence overview>\n" +
        "## Decisions\n- <decision (who decided, if stated)>\n" +
        "## Action Items\n- [ ] <task> - **Owner:** <name or unassigned> - **Due:** <date or not stated>\n" +
        "## Open Questions\n- <question>\n" +
        "Rules: use only what is said in the transcript and notes; never invent owners, dates or facts; write \"None\" under an empty section; no preamble, no code fences.";

    public static (string System, string User, int MaxTokens) Build(AssistMode mode, string transcript, string notes)
    {
        var (system, limit, maxTokens) = mode switch
        {
            AssistMode.FollowUps => (FollowUpsSystem, 12000, 512),
            AssistMode.Clean => (CleanSystem, 30000, 8192),
            AssistMode.ActionItems => (ActionItemsSystem, 40000, 1500),
            AssistMode.Summary => (SummarySystem, 60000, 3000),
            AssistMode.Screen => (ScreenSystem, 6000, 1024),
            AssistMode.Diarize => (DiarizeSystem, 30000, 8192),
            AssistMode.MarkdownRecap => (MarkdownRecapSystem, 60000, 2500),
            _ => (AssistSystem, 12000, 512),
        };
        string Tail(string s, int n) => s.Length <= n ? s : s[^n..];
        string user = mode is AssistMode.Clean or AssistMode.Diarize
            ? $"Transcript:\n{(string.IsNullOrWhiteSpace(transcript) ? "(empty)" : Tail(transcript, limit).Trim())}"
            : $"Notes:\n{(string.IsNullOrWhiteSpace(notes) ? "(none)" : Tail(notes, 6000).Trim())}\n\n" +
              $"Live transcript (oldest to newest):\n{(string.IsNullOrWhiteSpace(transcript) ? "(empty)" : Tail(transcript, limit).Trim())}";
        return (system, user, maxTokens);
    }
}
