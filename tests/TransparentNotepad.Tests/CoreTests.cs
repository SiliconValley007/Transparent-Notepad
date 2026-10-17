using System.Net;
using System.Net.WebSockets;
using System.Text;
using TransparentNotepad.Ai;
using TransparentNotepad.Logging;
using TransparentNotepad.Session;
using TransparentNotepad.Transcription;

namespace TransparentNotepad.Tests;

/// <summary>Platform-neutral core test cases, shared by the xUnit suite (CoreSuite) and any console harness.</summary>
public static class CoreTests
{
    public static IReadOnlyList<(string Name, Func<Task> Body)> All()
    {
        var tests = new List<(string Name, Func<Task> Body)>();
        void T(string name, Func<Task> body) => tests.Add((name, body));

        void Eq<X>(X exp, X act, string what = "") { if (!EqualityComparer<X>.Default.Equals(exp, act)) throw new Exception($"{what} expected [{exp}] got [{act}]"); }
        async Task Until(Func<bool> c, int ms = 5000) { var t = Environment.TickCount64; while (!c()) { if (Environment.TickCount64 - t > ms) throw new TimeoutException("until"); await Task.Delay(10); } }

        static int FreePort() { var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0); l.Start(); var p = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return p; }

        T("Deepgram: auth header, query, audio, results parse", async () =>
        {
            using var s = new FakeDg();
            s.Reply = "{\"type\":\"Results\",\"is_final\":true,\"speech_final\":true,\"start\":1.5,\"channel_index\":[1,2],\"channel\":{\"alternatives\":[{\"transcript\":\" hello there \",\"words\":[{\"speaker\":1},{\"speaker\":1},{\"speaker\":0}]}]}}";
            var c = new DeepgramClient(new DeepgramOptions { ApiKey = "KEY", BaseUri = s.Uri, Channels = 2 });
            var got = new List<TranscriptEvent>(); c.Transcript += got.Add; c.Start();
            await Until(() => c.State == ConnectionState.Open);
            c.Send(new byte[1280]);
            await Until(() => got.Count == 1);
            Eq("Token KEY", s.Auth[0], "auth"); Eq(true, s.Urls[0].Contains("encoding=linear16") && s.Urls[0].Contains("multichannel=true") && s.Urls[0].Contains("channels=2"), "query");
            Eq(1280, s.BinLens[0], "len"); Eq("hello there", got[0].Text); Eq(1, got[0].Channel); Eq(1, got[0].Speaker); Eq(true, got[0].IsFinal);
            await c.StopAsync();
        });

        T("Deepgram: reconnect replays queued audio in order", async () =>
        {
            using var s = new FakeDg();
            var c = new DeepgramClient(new DeepgramOptions { ApiKey = "K", BaseUri = s.Uri, MinBackoff = TimeSpan.FromMilliseconds(20), MaxBackoff = TimeSpan.FromMilliseconds(40) });
            var states = new List<ConnectionState>(); c.StateChanged += states.Add; c.Start();
            await Until(() => c.State == ConnectionState.Open && s.Conns.Count == 1);
            s.L.Stop(); // drop: abort server side socket
            s.Conns[0].Abort();
            await Until(() => c.State == ConnectionState.Reconnecting);
            foreach (byte n in new byte[] { 1, 2, 3 }) c.Send(new[] { n, (byte)0 });
            s.L = new HttpListener(); s.L.Prefixes.Add($"http://127.0.0.1:{s.Port}/"); s.L.Start(); _ = typeof(FakeDg).GetMethod("Loop", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(s, null);
            await Until(() => { lock (s.BinLens) return s.FirstBytes.Count >= 3; });
            Eq("1,2,3", string.Join(",", s.FirstBytes.Take(3)), "order"); Eq(true, states.Contains(ConnectionState.Reconnecting), "state");
            await c.StopAsync();
        });

        T("Deepgram: gives up with error after repeated failures", async () =>
        {
            var c = new DeepgramClient(new DeepgramOptions { ApiKey = "K", BaseUri = new Uri($"ws://127.0.0.1:{FreePort()}/x"), MinBackoff = TimeSpan.FromMilliseconds(5), MaxBackoff = TimeSpan.FromMilliseconds(10), MaxFailedAttempts = 2 });
            string? err = null; c.Error += e => err = e; c.Start();
            await Until(() => err != null && c.State == ConnectionState.Closed, 30000);
            Eq(true, err!.Contains("Cannot reach"), "msg"); await c.StopAsync();
        });

        T("Deepgram: offline queue is bounded", async () =>
        {
            var c = new DeepgramClient(new DeepgramOptions { ApiKey = "K", MaxBufferedChunks = 3 });
            for (int i = 0; i < 10; i++) c.Send(new byte[10]); // never started: must not block or throw
        });

        // ---- fake LLM server ----
        async Task<(HttpListener, int)> Llm(Func<HttpListenerContext, string, Task> h)
        {
            var l = new HttpListener(); int p = FreePort(); l.Prefixes.Add($"http://127.0.0.1:{p}/"); l.Start();
            _ = Task.Run(async () => { while (l.IsListening) { HttpListenerContext c; try { c = await l.GetContextAsync(); } catch { return; } var body = await new StreamReader(c.Request.InputStream).ReadToEndAsync(); _ = h(c, body); } });
            return (l, p);
        }
        async Task<string> Collect(IAsyncEnumerable<string> e) { var sb = new StringBuilder(); await foreach (var d in e) sb.Append(d); return sb.ToString(); }

        T("LLM: OpenAI-compatible SSE (split chunks, CRLF, [DONE])", async () =>
        {
            string? auth = null, url = null, body0 = null;
            var (l, p) = await Llm(async (c, b) =>
            {
                auth = c.Request.Headers["Authorization"]; url = c.Request.Url!.AbsolutePath; body0 = b;
                c.Response.ContentType = "text/event-stream"; c.Response.SendChunked = true;
                var o = c.Response.OutputStream;
                async Task W(string s) { var x = Encoding.UTF8.GetBytes(s); await o.WriteAsync(x); await o.FlushAsync(); await Task.Delay(15); }
                var e1 = "data: {\"choices\":[{\"delta\":{\"content\":\"Hel\"}}]}\n\n"; var e2 = "data: {\"choices\":[{\"delta\":{\"content\":\"lo\"}}]}\r\n\r\n";
                await W(e1[..10]); await W(e1[10..] + e2[..7]); await W(e2[7..] + "data: [DONE]\n\n"); c.Response.Close();
            });
            var o = await Collect(new LlmClient().StreamAsync(new LlmRequest(LlmProvider.OpenAi, "sk", "m", $"http://127.0.0.1:{p}/v1", "S", "U")));
            Eq("Hello", o); Eq("Bearer sk", auth); Eq("/v1/chat/completions", url); Eq(true, body0!.Contains("\"stream\":true") && body0.Contains("\"role\":\"system\""), "body"); l.Stop();
        });

        T("LLM: Anthropic SSE (text deltas only, headers)", async () =>
        {
            string? key = null, ver = null, url = null, body0 = null;
            var (l, p) = await Llm(async (c, b) =>
            {
                key = c.Request.Headers["x-api-key"]; ver = c.Request.Headers["anthropic-version"]; url = c.Request.Url!.AbsolutePath; body0 = b;
                c.Response.ContentType = "text/event-stream";
                string Ev(string e, string j) => $"event: {e}\ndata: {j}\n\n";
                var s = Ev("message_start", "{\"type\":\"message_start\"}") + Ev("content_block_delta", "{\"type\":\"content_block_delta\",\"delta\":{\"type\":\"text_delta\",\"text\":\"Hi \"}}") +
                        Ev("content_block_delta", "{\"type\":\"content_block_delta\",\"delta\":{\"type\":\"text_delta\",\"text\":\"there\"}}") + Ev("ping", "{\"type\":\"ping\"}") + Ev("message_stop", "{\"type\":\"message_stop\"}");
                var x = Encoding.UTF8.GetBytes(s); await c.Response.OutputStream.WriteAsync(x); c.Response.Close();
            });
            var o = await Collect(new LlmClient().StreamAsync(new LlmRequest(LlmProvider.Anthropic, "ak", "claude-haiku-5-5", $"http://127.0.0.1:{p}", "SYS", "U")));
            Eq("Hi there", o); Eq("ak", key); Eq("2023-06-01", ver); Eq("/v1/messages", url); Eq(true, body0!.Contains("\"system\":\"SYS\""), "system"); l.Stop();
        });

        T("LLM: HTTP error, missing key, cancel", async () =>
        {
            var (l, p) = await Llm(async (c, b) => { c.Response.StatusCode = 401; var x = Encoding.UTF8.GetBytes("{\"error\":\"bad key\"}"); await c.Response.OutputStream.WriteAsync(x); c.Response.Close(); });
            try { await Collect(new LlmClient().StreamAsync(new LlmRequest(LlmProvider.OpenAi, "x", "m", $"http://127.0.0.1:{p}", "", ""))); throw new Exception("no throw"); }
            catch (HttpRequestException e) { Eq(true, e.Message.Contains("401") && e.Message.Contains("bad key"), "msg"); }
            try { await Collect(new LlmClient().StreamAsync(new LlmRequest(LlmProvider.Anthropic, "", "m", "https://api.anthropic.com", "", ""))); throw new Exception("no throw"); }
            catch (InvalidOperationException e) { Eq(true, e.Message.Contains("API key")); }
            l.Stop();
            var (l2, p2) = await Llm(async (c, b) => { c.Response.ContentType = "text/event-stream"; c.Response.SendChunked = true; var x = Encoding.UTF8.GetBytes(": hold\n\n"); await c.Response.OutputStream.WriteAsync(x); await c.Response.OutputStream.FlushAsync(); await Task.Delay(5000); });
            using var cts = new CancellationTokenSource(100);
            try { await Collect(new LlmClient().StreamAsync(new LlmRequest(LlmProvider.OpenAi, "x", "m", $"http://127.0.0.1:{p2}", "", ""), cts.Token)); throw new Exception("no throw"); }
            catch (OperationCanceledException) { }
            l2.Abort();
        });

        T("PromptBuilder trims to most recent transcript", async () =>
        {
            var (_, u, _) = PromptBuilder.Build(AssistMode.Assist, new string('a', 20000) + "END", "n");
            Eq(true, u.EndsWith("END") && u.Length < 13000, "trim"); await Task.CompletedTask;
        });

        T("TranscriptBuffer merges same speaker within window, splits otherwise", async () =>
        {
            var now = DateTime.UtcNow; var b = new TranscriptBuffer(TimeSpan.FromSeconds(6), () => now);
            Eq(true, b.CommitFinal("You", "Hi").NewParagraph, "first");
            now = now.AddSeconds(2); var c2 = b.CommitFinal("You", "there"); Eq(false, c2.NewParagraph, "merge"); Eq(" there", c2.Text);
            Eq(true, b.CommitFinal("Them", "Hello").NewParagraph, "speaker change");
            now = now.AddSeconds(10); Eq(true, b.CommitFinal("Them", "Later").NewParagraph, "window");
            b.SetInterim("You", "par"); Eq("You: Hi there\nThem: Hello\nThem: Later\nYou: par", b.ToText()); Eq("You: Hi there\nThem: Hello\nThem: Later", b.ToText(false));
            await Task.CompletedTask;
        });

        T("MeetingSession: labels (You/Them multichannel), wiring, stop", async () =>
        {
            var audio = new FakeAudio(2); FakeClient? cl = null;
            var s = new MeetingSession(audio, ch => cl = new FakeClient());
            var commits = new List<Commit>(); s.FinalCommitted += commits.Add;
            s.Start(AudioSourceKind.Both);
            audio.Emit(new byte[] { 9 }); Eq(1, cl!.Sent.Count, "audio forwarded");
            cl.Raise(new TranscriptEvent("question?", false, false, 1, null, 0)); Eq("Interviewer: question?", s.Buffer.ToText(), "interim");
            cl.Raise(new TranscriptEvent("question?", true, true, 1, null, 0)); cl.Raise(new TranscriptEvent("ok", true, true, 0, null, 0));
            Eq("Interviewer: question?\nYou: ok", s.Buffer.ToText(), "finals"); Eq(2, commits.Count);
            await s.StopAsync(); Eq(true, audio.Stopped && cl.Stopped, "stopped");
        });

        T("Local ASR: VAD splits utterances, keeps You/Them channels, emits partial then final", async () =>
        {
            var stt = new FakeStt(); var c = new WhisperLocalClient(stt, 2, new LocalAsrOptions { PartialEveryMs = 200 });
            var ev = new List<TranscriptEvent>(); c.Transcript += e => { lock (ev) ev.Add(e); }; c.Start();
            await Until(() => c.State == ConnectionState.Open);
            byte[] Frame(float l, float r) { var b = new byte[640 * 2 * 2]; for (int i = 0; i < 640; i++) { BitConverter.GetBytes((short)(l * 32767)).CopyTo(b, i * 4); BitConverter.GetBytes((short)(r * 32767)).CopyTo(b, i * 4 + 2); } return b; }
            for (int i = 0; i < 25; i++) { c.Send(Frame(0, 0.5f)); await Task.Delay(40); }   // 1 s of speech on channel 1 (system)
            for (int i = 0; i < 20; i++) { c.Send(Frame(0, 0)); await Task.Delay(5); }          // pause -> final
            await Until(() => { lock (ev) return ev.Any(e => e.IsFinal); });
            TranscriptEvent fin; lock (ev) fin = ev.First(e => e.IsFinal);
            Eq(1, fin.Channel, "channel"); Eq(true, fin.Text.StartsWith("len="), "text"); Eq(true, fin.SpeechFinal);
            Eq(true, stt.Calls.Count >= 1 && stt.Calls.Last() >= 16000 * 0.9, "audio len");
            // silence only: nothing emitted
            int before; lock (ev) before = ev.Count; for (int i = 0; i < 30; i++) c.Send(Frame(0, 0)); await Task.Delay(200); lock (ev) Eq(before, ev.Count, "silence");
            await c.StopAsync(); Eq(ConnectionState.Closed, c.State);
        });

        T("Local ASR: model load failure surfaces as error and closes", async () =>
        {
            var c = new WhisperLocalClient(new FakeStt { FailInit = true }, 1); string? err = null; c.Error += e => err = e; c.Start();
            await Until(() => err != null && c.State == ConnectionState.Closed); Eq(true, err!.Contains("Speech model"), "msg");
        });

        T("Segmenter: max-length cut and short blips ignored", async () =>
        {
            var o = new LocalAsrOptions { MaxUtteranceMs = 1000 }; var s = new UtteranceSegmenter(o); float[]? u = null; int n = 0;
            for (int i = 0; i < 40; i++) { u = s.Feed(Enumerable.Repeat(0.5f, 640).ToArray()); if (u != null) { n++; break; } }
            Eq(1, n, "cut"); Eq(true, u!.Length >= 16000, "len");
            var s2 = new UtteranceSegmenter(new LocalAsrOptions()); s2.Feed(Enumerable.Repeat(0.5f, 640).ToArray()); float[]? r = null;
            for (int i = 0; i < 30 && r == null; i++) r = s2.Feed(new float[640]);
            Eq(true, r == null, "40ms blip < MinUtterance ignored"); await Task.CompletedTask;
        });

        T("Prompts: every mode has a distinct template; Clean is verbatim-only; token caps", async () =>
        {
            var seen = new HashSet<string>();
            foreach (AssistMode m in Enum.GetValues<AssistMode>()) { var (sys, user, max) = PromptBuilder.Build(m, "You: hi", "note"); Eq(true, seen.Add(sys), m + " distinct"); Eq(true, max >= 512, "max"); Eq(true, user.Contains("You: hi"), "transcript included"); }
            Eq(true, PromptBuilder.CleanSystem.Contains("EXACT words") && PromptBuilder.CleanSystem.Contains("Do NOT paraphrase") && PromptBuilder.CleanSystem.Contains("Stutters"), "clean");
            Eq(true, PromptBuilder.ActionItemsSystem.Contains("ACTION ITEMS") && PromptBuilder.ActionItemsSystem.Contains("SPEAKER NOTES"), "actions");
            Eq(true, PromptBuilder.SummarySystem.Contains("TOPICS DISCUSSED") && PromptBuilder.SummarySystem.Contains("DECISIONS") && PromptBuilder.SummarySystem.Contains("COMPREHENSIVE"), "recap");
            Eq(false, PromptBuilder.Build(AssistMode.Clean, "x", "SECRETNOTE").User.Contains("SECRETNOTE"), "clean ignores notes"); await Task.CompletedTask;
        });

        T("Groq Whisper STT: multipart request, WAV, auth, parsing, hallucination filter, 429", async () =>
        {
            string? auth = null, body0 = null, url = null; int status = 200; string reply = "{\"text\":\" Hello world \"}";
            var (l, p) = await Llm(async (c, b) => { auth = c.Request.Headers["Authorization"]; url = c.Request.Url!.AbsolutePath; body0 = b; c.Response.StatusCode = status; var x = Encoding.UTF8.GetBytes(reply); await c.Response.OutputStream.WriteAsync(x); c.Response.Close(); });
            var stt = new GroqWhisperStt("gk", "en", baseUrl: $"http://127.0.0.1:{p}/openai/v1");
            var o = await stt.TranscribeAsync(new float[16000], default);
            Eq("Hello world", o); Eq("Bearer gk", auth); Eq("/openai/v1/audio/transcriptions", url);
            Eq(true, body0!.Contains("whisper-large-v3-turbo") && body0.Contains("name=language") && body0.Contains("RIFF") && body0.Contains("audio/wav"), "multipart");
            var wav = GroqWhisperStt.ToWav(new float[16000]); Eq(44 + 32000, wav.Length, "wav len"); Eq(32000, BitConverter.ToInt32(wav, 28), "byte rate");
            reply = "{\"text\":\"Thank you.\"}"; Eq("", await stt.TranscribeAsync(new float[100], default), "hallucination");
            status = 429; reply = "{\"error\":\"rate\"}";
            try { await stt.TranscribeAsync(new float[100], default); throw new Exception("no throw"); } catch (AsrHttpException e) { Eq(429, e.Status); }
            l.Stop();
        });

        T("Fallback STT: cloud failure switches to local, skips cloud during cooldown", async () =>
        {
            var cloud = new CountingStt { Fail = true }; var local = new CountingStt(); var notices = new List<string>();
            var f = new FallbackSpeechToText(cloud, local); f.Notice += notices.Add;
            await f.InitializeAsync(default);
            Eq("local", await f.TranscribeAsync(new float[10], default)); Eq("local", await f.TranscribeAsync(new float[10], default));
            Eq(1, cloud.Calls, "cloud hit once then cooldown"); Eq(2, local.Calls, "local"); Eq(true, notices.Count >= 1, "notice");
            var bad = new FallbackSpeechToText(new CountingStt { FailInit = true }, new CountingStt()); await bad.InitializeAsync(default);
            Eq("local", await bad.TranscribeAsync(new float[10], default), "init failure -> local");
        });

        // ---- failover LLM ----
        LlmEndpoint Ep(string n, int port, string key = "k", bool en = true) => new(n, LlmProvider.OpenAi, $"http://127.0.0.1:{port}/v1", "m", key, en);
        async Task<(HttpListener, int)> Srv(int status, string text, Action? hit = null, bool dieMidStream = false) => await Llm(async (c, b) =>
        {
            hit?.Invoke(); c.Response.StatusCode = status;
            if (status != 200) { var e = Encoding.UTF8.GetBytes("err"); await c.Response.OutputStream.WriteAsync(e); c.Response.Close(); return; }
            c.Response.ContentType = "text/event-stream"; c.Response.SendChunked = true;
            var d = Encoding.UTF8.GetBytes("data: {\"choices\":[{\"delta\":{\"content\":\"" + text + "\"}}]}\n\n"); await c.Response.OutputStream.WriteAsync(d); await c.Response.OutputStream.FlushAsync();
            if (dieMidStream) { c.Response.Abort(); return; }
            var e2 = Encoding.UTF8.GetBytes("data: [DONE]\n\n"); await c.Response.OutputStream.WriteAsync(e2); c.Response.Close();
        });

        T("LLM failover: 429 -> next provider, cooldown skips failed one, no-key skipped, local needs no key", async () =>
        {
            int hitsA = 0; var (a, pa) = await Srv(429, "", () => hitsA++); var (b, pb) = await Srv(200, "from-B"); var (c, pc) = await Srv(200, "from-C");
            var fo = new LlmFailoverClient { RetryBase = TimeSpan.FromMilliseconds(1) }; var used = new List<string>(); var failed = new List<string>(); fo.ProviderUsed += used.Add; fo.ProviderFailed += failed.Add;
            var eps = new[] { Ep("A", pa), Ep("NoKey", pc, key: "") with { BaseUrl = "http://nokey.invalid/v1" }, Ep("B", pb), Ep("C", pc) };
            Eq("from-B", await Collect(fo.StreamAsync(eps, "s", "u", 100)), "first"); Eq("B", used.Last(), "used"); Eq(1, failed.Count, "failed A");
            Eq("from-B", await Collect(fo.StreamAsync(eps, "s", "u", 100)), "second"); Eq(3, hitsA, "A tried 1+2 retries on the first call only; on cooldown afterwards");
            Eq("from-C", await Collect(fo.StreamAsync(new[] { Ep("Ollama", pc, key: "") }, "s", "u", 100)), "local no key");
            a.Stop(); b.Stop(); c.Stop();
        });

        T("LLM failover: mid-stream failure is not spliced; all-fail and none-configured raise clear errors", async () =>
        {
            var (b, pb) = await Srv(200, "from-B");
            var broken = new LlmClient(new HttpClient(new FailMidStreamHandler()));
            var got = new StringBuilder(); bool threw = false;
            try { await foreach (var d in new LlmFailoverClient(broken).StreamAsync(new[] { new LlmEndpoint("A", LlmProvider.OpenAi, "http://a.invalid/v1", "m", "k"), Ep("B", pb) }, "s", "u", 100)) got.Append(d); } catch { threw = true; }
            Eq(true, threw, "mid-stream error propagates"); Eq("partial", got.ToString(), "no splice"); b.Stop();
            var (x, px) = await Srv(500, ""); var (y, py) = await Srv(401, "");
            try { await Collect(new LlmFailoverClient { RetryBase = TimeSpan.FromMilliseconds(1) }.StreamAsync(new[] { Ep("X", px), Ep("Y", py) }, "s", "u", 100)); throw new Exception("no throw"); }
            catch (InvalidOperationException e) { Eq(true, e.Message.Contains("All AI providers failed"), "all fail"); }
            try { await Collect(new LlmFailoverClient { RetryBase = TimeSpan.FromMilliseconds(1) }.StreamAsync(new[] { Ep("Z", px, key: "") with { BaseUrl = "http://nokey.invalid/v1" } }, "s", "u", 100)); throw new Exception("no throw"); }
            catch (InvalidOperationException e) { Eq(true, e.Message.Contains("No AI provider"), "none"); }
            x.Stop(); y.Stop();
        });

        T("LLM failover: first-token timeout moves on to the next provider", async () =>
        {
            var (slow, ps) = await Llm(async (c, b) => { c.Response.ContentType = "text/event-stream"; c.Response.SendChunked = true; await c.Response.OutputStream.FlushAsync(); await Task.Delay(4000); });
            var (ok, po) = await Srv(200, "fast");
            var fo = new LlmFailoverClient { FirstTokenTimeout = TimeSpan.FromMilliseconds(300) };
            Eq("fast", await Collect(fo.StreamAsync(new[] { Ep("Slow", ps), Ep("Fast", po) }, "s", "u", 100)));
            slow.Abort(); ok.Stop();
        });

        T("Sliding window: interim snapshot is capped to the last N ms", async () =>
        {
            var s = new UtteranceSegmenter(new LocalAsrOptions()); for (int i = 0; i < 100; i++) s.Feed(Enumerable.Repeat(0.5f, 640).ToArray()); // 4 s (hits max? MaxUtterance=10 s -> no)
            Eq(true, s.Snapshot().Length >= 60000, "full"); Eq(8000, s.Snapshot(500).Length, "500 ms window"); await Task.CompletedTask;
        });

        T("Failover: 429 once is retried on the SAME provider with backoff (no failover)", async () =>
        {
            int n = 0; var (l, p) = await Llm(async (c, b) =>
            {
                if (Interlocked.Increment(ref n) == 1) { c.Response.StatusCode = 429; c.Response.Close(); return; }
                c.Response.ContentType = "text/event-stream"; c.Response.SendChunked = true;
                var d = Encoding.UTF8.GetBytes("data: {\"choices\":[{\"delta\":{\"content\":\"ok\"}}]}\n\ndata: [DONE]\n\n"); await c.Response.OutputStream.WriteAsync(d); c.Response.Close();
            });
            var failed = new List<string>(); var fo = new LlmFailoverClient { RetryBase = TimeSpan.FromMilliseconds(5) }; fo.ProviderFailed += failed.Add;
            Eq("ok", await Collect(fo.StreamAsync(new[] { Ep("G", p) }, "s", "u", 10))); Eq(2, n, "two requests"); Eq(0, failed.Count, "not failed over"); l.Stop();
        });

        T("Ring buffer: SPSC integrity under real producer/consumer threads, wrap-around, overflow accounting, skip", async () =>
        {
            var r = new SpscFloatRing(1024); const int total = 2_000_000; long bad = 0, got = 0;
            var prod = Task.Run(() => { var buf = new float[137]; long next = 0; while (next < total) { int n = (int)Math.Min(buf.Length, total - next); for (int i = 0; i < n; i++) buf[i] = next + i; int w = r.Write(buf.AsSpan(0, n)); next += w; if (w < n) Thread.Yield(); } });
            var cons = Task.Run(() => { var buf = new float[89]; long expect = 0; while (expect < total) { int n = r.Read(buf); for (int i = 0; i < n; i++) { if (buf[i] != expect + i) bad++; } expect += n; got = expect; if (n == 0) Thread.Yield(); } });
            await Task.WhenAll(prod, cons); Eq(0L, bad, "corrupt samples"); Eq((long)total, got, "all samples");
            var s = new SpscFloatRing(8); Eq(8, s.Write(new float[12]), "capacity cap"); Eq(4L, s.Dropped, "dropped counted"); Eq(5, s.Skip(5)); Eq(3, s.Available);
            try { new SpscFloatRing(1000); throw new Exception("no throw"); } catch (ArgumentException) { }
        });

        T("Noise gate: closes below -45 dBFS after hangover, opens on speech, floor adapts and stays bounded", async () =>
        {
            var g = new NoiseGate(-45, 15); int open = 0;
            for (int i = 0; i < 100; i++) if (g.Process(1e-4f)) open++;           // -80 dBFS silence
            Eq(0, open, "silence blocked");
            Eq(true, g.Process(0.1f), "speech (-20 dBFS) opens");
            open = 0; for (int i = 0; i < 40; i++) if (g.Process(1e-4f)) open++; Eq(15, open, "hangover frames exactly");
            Eq(false, new NoiseGate().Process(0.004f), "-48 dBFS below -45 gate");
            Eq(true, new NoiseGate().Process(0.01f), "-40 dBFS above gate");
            var n = new NoiseGate(); for (int i = 0; i < 50000; i++) n.Process(0.003f); Eq(true, n.ThresholdDb <= -35 && n.FloorDb <= -50, "floor bounded"); await Task.CompletedTask;
        });

        T("Deepgram: empty key fails fast with a clear error (no crash, no retries); URL uses nova-3 + endpointing=200", async () =>
        {
            var c = new DeepgramClient(new DeepgramOptions { ApiKey = "  " }); string? err = null; c.Error += e => err = e; c.Start();
            Eq(true, err != null && err.Contains("no API key"), "error"); Eq(ConnectionState.Closed, c.State);
            var u = new DeepgramOptions { ApiKey = "k" }.BuildUri().Query; Eq(true, u.Contains("model=nova-3") && u.Contains("endpointing=200") && u.Contains("interim_results=true"), u);
            await c.StopAsync();
        });

        T("Device recovery backoff: immediate -> quick retry -> monotone -> capped at 5 s", async () =>
        {
            Eq(TimeSpan.Zero, RecoveryBackoff.Delay(0)); Eq(400.0, RecoveryBackoff.Delay(1).TotalMilliseconds);
            var prev = TimeSpan.Zero; for (int i = 1; i < 30; i++) { var d = RecoveryBackoff.Delay(i); Eq(true, d >= prev, "monotone"); Eq(true, d <= TimeSpan.FromSeconds(5), "cap"); prev = d; }
            Eq(5000.0, RecoveryBackoff.Delay(99).TotalMilliseconds); await Task.CompletedTask;
        });

        T("Vision: image sent as OpenAI image_url data-URI and as Anthropic image block; failover passes image through", async () =>
        {
            string? b1 = null, b2 = null, b3 = null; var img = new byte[] { 0xFF, 0xD8, 1, 2, 3 }; string b64 = Convert.ToBase64String(img);
            var (l1, p1) = await Llm(async (c, b) => { b1 = b; c.Response.ContentType = "text/event-stream"; var d = Encoding.UTF8.GetBytes("data: [DONE]\n\n"); await c.Response.OutputStream.WriteAsync(d); c.Response.Close(); });
            await Collect(new LlmClient().StreamAsync(new LlmRequest(LlmProvider.OpenAi, "k", "gemini-2.5-flash", $"http://127.0.0.1:{p1}/v1", "SYS", "what is on screen", 100, img)));
            using (var doc = System.Text.Json.JsonDocument.Parse(b1!))
            {
                var content = doc.RootElement.GetProperty("messages")[1].GetProperty("content");
                Eq(System.Text.Json.JsonValueKind.Array, content.ValueKind, "content array"); Eq("text", content[0].GetProperty("type").GetString()); Eq("image_url", content[1].GetProperty("type").GetString());
                Eq("data:image/jpeg;base64," + b64, content[1].GetProperty("image_url").GetProperty("url").GetString(), "data uri");
            }
            var (l2, p2) = await Llm(async (c, b) => { b2 = b; c.Response.ContentType = "text/event-stream"; var d = Encoding.UTF8.GetBytes("event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n"); await c.Response.OutputStream.WriteAsync(d); c.Response.Close(); });
            await Collect(new LlmClient().StreamAsync(new LlmRequest(LlmProvider.Anthropic, "k", "m", $"http://127.0.0.1:{p2}", "SYS", "q", 100, img)));
            using (var doc = System.Text.Json.JsonDocument.Parse(b2!))
            {
                var c0 = doc.RootElement.GetProperty("messages")[0].GetProperty("content");
                Eq("image", c0[0].GetProperty("type").GetString()); Eq("image/jpeg", c0[0].GetProperty("source").GetProperty("media_type").GetString()); Eq(b64, c0[0].GetProperty("source").GetProperty("data").GetString());
            }
            var (l3, p3) = await Llm(async (c, b) => { b3 = b; c.Response.ContentType = "text/event-stream"; var d = Encoding.UTF8.GetBytes("data: {\"choices\":[{\"delta\":{\"content\":\"x\"}}]}\n\ndata: [DONE]\n\n"); await c.Response.OutputStream.WriteAsync(d); c.Response.Close(); });
            Eq("x", await Collect(new LlmFailoverClient().StreamAsync(new[] { Ep("V", p3) }, "s", "u", 50, default, img)), "failover");
            Eq(true, b3!.Contains("image_url"), "image forwarded by failover");
            string? b4 = null; var (l4, p4) = await Llm(async (c, b) => { b4 = b; c.Response.ContentType = "text/event-stream"; var d = Encoding.UTF8.GetBytes("data: [DONE]\n\n"); await c.Response.OutputStream.WriteAsync(d); c.Response.Close(); });
            await Collect(new LlmClient().StreamAsync(new LlmRequest(LlmProvider.OpenAi, "k", "m", $"http://127.0.0.1:{p4}/v1", "S", "plain", 10)));
            Eq(false, b4!.Contains("image_url"), "text-only requests unchanged"); l1.Stop(); l2.Stop(); l3.Stop(); l4.Stop();
        });

        T("Deepgram diarization: final result splits into one event per speaker turn; interim stays one event; diarize_model opt-in", async () =>
        {
            var c = new DeepgramClient(new DeepgramOptions { ApiKey = "k" }); var ev = new List<TranscriptEvent>(); c.Transcript += ev.Add;
            string Payload(bool fin) => "{\"type\":\"Results\",\"is_final\":" + (fin ? "true" : "false") + ",\"speech_final\":" + (fin ? "true" : "false") + ",\"start\":2.0,\"channel\":{\"alternatives\":[{\"transcript\":\"hi there yes\",\"words\":[" +
                "{\"word\":\"hi\",\"punctuated_word\":\"Hi\",\"speaker\":0,\"start\":2.0},{\"word\":\"there\",\"punctuated_word\":\"there.\",\"speaker\":0,\"start\":2.2},{\"word\":\"yes\",\"punctuated_word\":\"Yes.\",\"speaker\":1,\"start\":2.6}]}]}}";
            c.HandleMessage(Encoding.UTF8.GetBytes(Payload(true)));
            Eq(2, ev.Count, "two turns"); Eq("Hi there.", ev[0].Text); Eq(0, ev[0].Speaker); Eq("Yes.", ev[1].Text); Eq(1, ev[1].Speaker);
            Eq(false, ev[0].SpeechFinal, "only last run is speech_final"); Eq(true, ev[1].SpeechFinal); Eq(true, ev[0].IsFinal && ev[1].IsFinal);
            ev.Clear(); c.HandleMessage(Encoding.UTF8.GetBytes(Payload(false))); Eq(1, ev.Count, "interim single"); Eq(0, ev[0].Speaker, "dominant");
            Eq(false, new DeepgramOptions { ApiKey = "k" }.BuildUri().Query.Contains("diarize_model"), "off by default");
            Eq(true, new DeepgramOptions { ApiKey = "k", DiarizeModel = "latest" }.BuildUri().Query.Contains("diarize_model=latest"), "opt-in");
            Eq(false, new DeepgramOptions { ApiKey = "k", Channels = 2, DiarizeModel = "latest" }.BuildUri().Query.Contains("diarize"), "no diarize with multichannel"); await Task.CompletedTask;
        });

        T("SpeakerTurns.Parse: labels, [You]/[Them], wrapped lines, junk tolerated", async () =>
        {
            var t = SpeakerTurns.Parse("[Speaker 1]: Hello everyone.\n[speaker 2]: Hi!\nHow are you?\n\nYou: fine\n[Them]:   ok  \nno label at start");
            Eq(4, t.Count, "turns"); Eq("Speaker 1", t[0].Label); Eq("Speaker 2", t[1].Label); Eq("Hi! How are you?", t[1].Text, "continuation"); Eq("You", t[2].Label);
            Eq("Them", t[3].Label); Eq("ok no label at start", t[3].Text); Eq(0, SpeakerTurns.Parse("").Count); Eq(0, SpeakerTurns.Parse("just text").Count, "leading unlabeled ignored"); await Task.CompletedTask;
        });

        T("Transcript buffer: Snapshot + ReplaceAll (AI-labelled turns replace the transcript)", async () =>
        {
            var b = new TranscriptBuffer(); b.CommitFinal("Speaker", "a b c"); Eq(1, b.Snapshot().Count);
            b.ReplaceAll(new[] { ("Speaker 1", "a"), ("Speaker 2", "b c") }); var sn = b.Snapshot();
            Eq(2, sn.Count); Eq("Speaker 2", sn[1].Label); Eq("Speaker 1: a\nSpeaker 2: b c", b.ToText()); await Task.CompletedTask;
        });

        T("Markdown export + session log: clean format, notes quoted, unique atomic file in Sessions dir", async () =>
        {
            var dir = Path.Combine(Path.GetTempPath(), "tn_sessions_" + Guid.NewGuid().ToString("N")); var t0 = new DateTime(2026, 10, 9, 14, 3, 7);
            var lines = new List<(string, string, DateTime)> { ("Speaker 1", "Let's ship Friday.", t0), ("You", "I will own the release.", t0.AddSeconds(5)) };
            var md = MarkdownExporter.Build(t0, t0.AddMinutes(12), lines, "remember\nbudget", "## Summary\nShip Friday.\n\n## Action Items\n- [ ] Release - **Owner:** You", "Both");
            Eq(true, md.StartsWith("# Meeting session - 2026-10-09 14:03\n"), "title"); Eq(true, md.Contains("Duration: 12 min"), "duration");
            Eq(true, md.Contains("## Transcript\n\n**[Speaker 1]:** Let's ship Friday.\n\n**[You]:** I will own the release."), "transcript"); Eq(true, md.Contains("> remember\n> budget"), "notes"); Eq(true, md.Contains("- [ ] Release"), "actions");
            Eq(true, MarkdownExporter.Build(t0, t0, new List<(string, string, DateTime)>(), "", null, "Mic").Contains("_No AI summary generated"), "no summary note");
            var log = new SessionLog(dir, () => t0); var p = log.Save(md); Eq("2026-10-09_14-03-07.md", Path.GetFileName(p)); Eq(md, File.ReadAllText(p), "content");
            log.Save(md + "more"); Eq(p, log.Path, "same file across saves"); Eq(1, Directory.GetFiles(dir).Length, "no temp leftovers");
            var other = new SessionLog(dir, () => t0); Eq("2026-10-09_14-03-07_1.md", Path.GetFileName(other.Save("x")), "collision-safe");
            log.Reset(); Eq(null, log.Path); Directory.Delete(dir, true); await Task.CompletedTask;
        });

        T("AppLogger: structured line, secret redaction, quoting, never throws, size cap, 7-day retention", async () =>
        {
            var dir = Path.Combine(Path.GetTempPath(), "tn_log_" + Guid.NewGuid().ToString("N")); var t = new DateTime(2026, 10, 9, 14, 3, 7, 123);
            var log = new AppLogger(dir, LogLevel.Info, () => t);
            log.Debug("hidden.event"); log.Info("llm.provider used", ("provider", "Groq"), ("apiKey", "gsk_ABCDEFGHIJKLMNOP"), ("note", "has space"), ("error", "bad Bearer abcdefghijklmnop1234 here"));
            log.Error("boom", new InvalidOperationException("kaput\nline2"));
            var file = Path.Combine(dir, "app-20261009.log"); var lines = File.ReadAllLines(file);
            Eq(2, lines.Length, "debug filtered out"); Eq(true, lines[0].Contains("INFO  llm.provider_used provider=Groq"), lines[0]);
            Eq(true, lines[0].Contains("apiKey=***") && !lines[0].Contains("gsk_") && !lines[0].Contains("abcdefghijklmnop1234"), "secrets redacted");
            Eq(true, lines[0].Contains("note=\"has space\""), "quoting"); Eq(true, lines[1].Contains("ERROR boom") && lines[1].Contains("InvalidOperationException: kaput line2"), lines[1]);
            File.WriteAllText(Path.Combine(dir, "app-20250101.log"), "old"); File.SetLastWriteTime(Path.Combine(dir, "app-20250101.log"), t.AddDays(-30));
            var t2 = t.AddDays(1); var log2 = new AppLogger(dir, LogLevel.Info, () => t2); log2.Info("next.day");
            Eq(false, File.Exists(Path.Combine(dir, "app-20250101.log")), "old log deleted"); Eq(true, File.Exists(Path.Combine(dir, "app-20261010.log")), "new day file");
            File.WriteAllBytes(Path.Combine(dir, "app-20261010.log"), new byte[AppLogger.MaxFileBytes + 10]); log2.Info("dropped"); log2.Info("dropped2");
            Eq(true, new FileInfo(Path.Combine(dir, "app-20261010.log")).Length < AppLogger.MaxFileBytes + 200, "size cap holds");
            var bad = new AppLogger(Path.Combine(dir, "app-20261009.log", "x"), LogLevel.Info); bad.Info("cannot write here");   // dir path is a file: must not throw
            Directory.Delete(dir, true); await Task.CompletedTask;
        });

        KeylessLlmFailoverTests.Register(T);
        AudioLoopbackSilenceFlushTests.Register(T);
        EndToEndMeetingSessionTests.Register(T);
        return tests;

    }

    public static async Task RunAsync(string name)
    {
        var t = All().Single(x => x.Name == name);
        await t.Body();
    }
}

sealed class FakeAudio : IAudioSource
{
    public FakeAudio(int ch) => Channels = ch; public int Channels { get; } public bool NoiseGateEnabled { get; set; }
    public event Action<byte[]>? PcmChunk; public event Action<float>? Level; public event Action<string>? Warning;
    public bool Stopped; public void Start(AudioSourceKind k) { } public void Stop() => Stopped = true; public void Dispose() { }
    public void Emit(byte[] b) => PcmChunk?.Invoke(b);
}
sealed class FakeClient : ITranscriptionClient
{
    public List<byte[]> Sent = new(); public bool Stopped;
    public event Action<TranscriptEvent>? Transcript; public event Action<ConnectionState>? StateChanged; public event Action<string>? Error;
    public void Start() { } public void Send(byte[] p) => Sent.Add(p); public Task StopAsync() { Stopped = true; return Task.CompletedTask; }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask; public void Raise(TranscriptEvent e) => Transcript?.Invoke(e);
}

// ---- fake Deepgram server ----
sealed class FakeDg : IDisposable
{
    public HttpListener L = new(); public int Port = FreePort2();
    public List<WebSocket> Conns = new(); public List<string> Auth = new(); public List<string> Urls = new();
    public List<int> BinLens = new(); public List<byte> FirstBytes = new(); public string Reply = "";
    static int FreePort2() { var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0); l.Start(); var p = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return p; }
    public FakeDg() { L.Prefixes.Add($"http://127.0.0.1:{Port}/"); L.Start(); _ = Loop(); }
    public Uri Uri => new($"ws://127.0.0.1:{Port}/v1/listen");
    async Task Loop()
    {
        while (L.IsListening)
        {
            HttpListenerContext c; try { c = await L.GetContextAsync(); } catch { return; }
            var ctx = await c.AcceptWebSocketAsync(null); var ws = ctx.WebSocket;
            lock (Conns) { Conns.Add(ws); Auth.Add(c.Request.Headers["Authorization"] ?? ""); Urls.Add(c.Request.Url!.Query); }
            _ = Task.Run(async () =>
            {
                var b = new byte[65536];
                try { while (ws.State == WebSocketState.Open)
                {
                    var r = await ws.ReceiveAsync(b, default);
                    if (r.MessageType == WebSocketMessageType.Close) break;
                    if (r.MessageType == WebSocketMessageType.Binary)
                    { lock (BinLens) { BinLens.Add(r.Count); FirstBytes.Add(b[0]); } if (Reply != "") await ws.SendAsync(Encoding.UTF8.GetBytes(Reply), WebSocketMessageType.Text, true, default); }
                } } catch { }
            });
        }
    }
    public void Dispose() { try { L.Stop(); } catch { } }
}


sealed class FakeStt : ISpeechToText
{
    public bool FailInit; public List<int> Calls = new();
    public Task InitializeAsync(CancellationToken ct) => FailInit ? Task.FromException(new Exception("no model")) : Task.CompletedTask;
    public Task<string> TranscribeAsync(float[] s, CancellationToken ct) { lock (Calls) Calls.Add(s.Length); return Task.FromResult($"len={s.Length}"); }
}

sealed class CountingStt : ISpeechToText
{
    public bool Fail, FailInit; public int Calls;
    public Task InitializeAsync(CancellationToken ct) => FailInit ? Task.FromException(new Exception("no key")) : Task.CompletedTask;
    public Task<string> TranscribeAsync(float[] s, CancellationToken ct) { Calls++; return Fail ? Task.FromException<string>(new AsrHttpException(429, "rate")) : Task.FromResult("local"); }
}

sealed class FailMidStreamHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) =>
        Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StreamContent(new FailAfterStream()) });
    sealed class FailAfterStream : Stream
    {
        bool sent; readonly byte[] d = Encoding.UTF8.GetBytes("data: {\"choices\":[{\"delta\":{\"content\":\"partial\"}}]}\n\n");
        public override int Read(byte[] b, int o, int c) { if (sent) throw new IOException("connection lost"); sent = true; d.CopyTo(b, o); return d.Length; }
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false; public override long Length => 0;
        public override long Position { get => 0; set { } } public override void Flush() { } public override long Seek(long o, SeekOrigin s) => 0; public override void SetLength(long v) { } public override void Write(byte[] b, int o, int c) { }
    }
}
