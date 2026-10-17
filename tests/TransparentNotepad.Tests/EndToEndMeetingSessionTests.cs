using System.Net;
using System.Text;
using TransparentNotepad.Ai;
using TransparentNotepad.Session;
using TransparentNotepad.Transcription;

namespace TransparentNotepad.Tests;

public static class EndToEndMeetingSessionTests
{
    public static void Register(Action<string, Func<Task>> T)
    {
        T("E2E: FakeAudio -> MeetingSession -> mock STT -> TranscriptBuffer -> PromptBuilder -> keyless LLM -> MarkdownExporter ([Interviewer] + [You] + AI answer)", async () =>
        {
            var audio = new FakeAudio(2); FakeClient? stt = null;
            var session = new MeetingSession(audio, ch => stt = new FakeClient(), "Interviewer");
            var commits = new List<Commit>(); session.FinalCommitted += commits.Add;
            session.Start(AudioSourceKind.Both);
            audio.Emit(new byte[] { 1, 2 }); Assert.Eq(1, stt!.Sent.Count, "PCM reached the STT client");
            stt.Raise(new TranscriptEvent("Tell me about a time you led a project?", false, false, 1, null, 0));
            Assert.Eq("Interviewer: Tell me about a time you led a project?", session.Buffer.ToText(), "interim labelled");
            stt.Raise(new TranscriptEvent("Tell me about a time you led a project?", true, true, 1, null, 1));
            stt.Raise(new TranscriptEvent("I led the migration of our billing system.", true, true, 0, null, 6));
            stt.Raise(new TranscriptEvent("How did you handle risk?", true, true, 1, null, 12));
            Assert.Eq(3, commits.Count, "three final turns");
            Assert.Eq("Interviewer: Tell me about a time you led a project?\nYou: I led the migration of our billing system.\nInterviewer: How did you handle risk?", session.Buffer.ToText(), "multi-turn transcript");

            var (system, user, max) = PromptBuilder.Build(AssistMode.Assist, session.Buffer.ToText(), "STAR format");
            Assert.Eq(true, user.Contains("Interviewer: How did you handle risk?") && user.Contains("STAR format") && max > 0, "prompt contains labelled transcript + notes");

            var h = new ScriptedHandler((r, b) => r.RequestUri!.Host == "text.pollinations.ai" ? ScriptedHandler.Sse("Mention", " risk registers", " and weekly reviews.") : throw new HttpRequestException("offline"));
            var http = new HttpClient(h);
            var ai = new AiEngine(new LlmFailoverClient(new LlmClient(http)) { RetryBase = TimeSpan.FromMilliseconds(1) }, new OllamaProbe(http), () => true);
            var sb = new StringBuilder();
            await foreach (var d in ai.StreamAsync(Array.Empty<LlmEndpoint>(), AssistMode.Assist, system, user, max, session.Buffer.ToText(), "STAR format")) sb.Append(d);
            Assert.Eq("Mention risk registers and weekly reviews.", sb.ToString(), "copilot answer streamed");
            Assert.Eq(true, h.Calls.Single(c => c.Url.Host == "text.pollinations.ai").Body.Contains("How did you handle risk?"), "transcript sent to the model");

            var t0 = new DateTime(2026, 10, 9, 10, 0, 0);
            var md = MarkdownExporter.Build(t0, t0.AddMinutes(30), session.Buffer.Snapshot(), "STAR format", "## Summary\n" + sb, "Both");
            Assert.Eq(true, md.Contains("**[Interviewer]:** Tell me about a time you led a project?") && md.Contains("**[You]:** I led the migration of our billing system.") && md.Contains("## Summary\nMention risk registers"), md);
            Assert.Eq(true, md.IndexOf("**[Interviewer]:** Tell me", StringComparison.Ordinal) < md.IndexOf("**[You]:** I led", StringComparison.Ordinal), "turn order");
            await session.StopAsync(); Assert.Eq(true, audio.Stopped && stt.Stopped, "clean shutdown");
        });

        T("MeetingSession labels: dual-stream You/<system label>, runtime label change, single stream falls back to Speaker N", async () =>
        {
            var audio = new FakeAudio(2); FakeClient? c = null; var s = new MeetingSession(audio, ch => c = new FakeClient(), "Them");
            s.Start(AudioSourceKind.Both); s.SystemLabel = "Interviewer";
            c!.Raise(new TranscriptEvent("a", true, true, 0, null, 0)); c.Raise(new TranscriptEvent("b", true, true, 1, null, 0));
            Assert.Eq("You: a\nInterviewer: b", s.Buffer.ToText(), "mic=You, loopback=system label"); await s.StopAsync();
            var mono = new FakeAudio(1); FakeClient? c2 = null; var s2 = new MeetingSession(mono, ch => c2 = new FakeClient());
            s2.Start(AudioSourceKind.SystemAudio); c2!.Raise(new TranscriptEvent("x", true, true, 0, 2, 0)); Assert.Eq("Speaker 3: x", s2.Buffer.ToText(), "diarized speaker"); await s2.StopAsync();
            Assert.Eq("Interviewer", SpeakerTurns.Parse("[Interviewer]: hi")[0].Label, "parser knows Interviewer"); await Task.CompletedTask;
        });

        T("Composite local STT: instant engine serves while the model loads, then Whisper takes over; missing instant engine waits for Whisper", async () =>
        {
            var gate = new TaskCompletionSource(); var best = new GateStt(gate.Task, "whisper"); var instant = new GateStt(Task.CompletedTask, "windows");
            var notices = new List<string>(); var c = new CompositeLocalStt(best, instant); c.Notice += notices.Add;
            await c.InitializeAsync(default);
            Assert.Eq("windows", await c.TranscribeAsync(new float[10], default), "instant engine first");
            gate.SetResult(); for (int i = 0; i < 100 && !notices.Any(n => n.Contains("ready")); i++) await Task.Delay(20);
            Assert.Eq("whisper", await c.TranscribeAsync(new float[10], default), "upgraded to whisper"); Assert.Eq(true, notices.Any(n => n.Contains("ready")), "ready notice");
            var c2 = new CompositeLocalStt(new GateStt(Task.CompletedTask, "whisper"), new GateStt(Task.FromException(new Exception("no recognizer")), "windows"));
            await c2.InitializeAsync(default); Assert.Eq("whisper", await c2.TranscribeAsync(new float[10], default), "no instant engine -> whisper"); await Task.CompletedTask;
        });
    }
}

internal sealed class GateStt : ISpeechToText
{
    private readonly Task _init; private readonly string _name;
    public GateStt(Task init, string name) { _init = init; _name = name; }
    public Task InitializeAsync(CancellationToken ct) => _init;
    public Task<string> TranscribeAsync(float[] s, CancellationToken ct) => Task.FromResult(_name);
}
