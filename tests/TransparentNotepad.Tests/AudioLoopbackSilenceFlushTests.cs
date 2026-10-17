using TransparentNotepad.Session;
using TransparentNotepad.Transcription;

namespace TransparentNotepad.Tests;

public static class AudioLoopbackSilenceFlushTests
{
    private const int Frame = 640;           // 40 ms @ 16 kHz

    private static float[] Channel(short[] pcm, int ch, int channels) { var f = new float[Frame]; for (int i = 0; i < Frame; i++) f[i] = pcm[i * channels + ch] / 32768f; return f; }

    public static void Register(Action<string, Func<Task>> T)
    {
        T("Loopback starvation: after burst speech the idle ring still yields silence frames and the utterance flushes within 500 ms", async () =>
        {
            var mic = new SpscFloatRing(1 << 14); var sys = new SpscFloatRing(1 << 14);
            var speech = new float[Frame]; Array.Fill(speech, 0.5f);                                // 1 s of system speech arrives in real time, then the device goes silent (no more DataAvailable)
            var asm = new FrameAssembler(2, Frame); var pcm = new short[Frame * 2]; var seg = new UtteranceSegmenter(new LocalAsrOptions { SilenceMs = 450 });
            int framesAfterSpeech = 0; float[]? utt = null;
            for (int i = 0; i < 80 && utt == null; i++)
            {
                if (i < 25) sys.Write(speech);                                                       // capture callback delivers 40 ms
                asm.Assemble(new[] { mic, sys }, pcm);                                               // pump tick (wall-clock cadence)
                utt = seg.Feed(Channel(pcm, 1, 2));
                if (i >= 25) framesAfterSpeech++;
            }
            Assert.Eq(true, utt != null, "utterance flushed by synthetic silence");
            Assert.Eq(true, framesAfterSpeech * 40 <= 500, $"flushed {framesAfterSpeech * 40} ms after speech ended");
            Assert.Eq(true, utt!.Length >= 25 * Frame, "speech kept"); await Task.CompletedTask;
        });

        T("Frame assembler: stereo layout [mic, system], idle channel is zero-filled, backlog capped to live audio", async () =>
        {
            var mic = new SpscFloatRing(1 << 14); var sys = new SpscFloatRing(1 << 14);
            var loud = new float[Frame]; Array.Fill(loud, 0.25f); mic.Write(loud);
            var asm = new FrameAssembler(2, Frame); var pcm = new short[Frame * 2];
            var rms = asm.Assemble(new[] { mic, sys }, pcm);
            Assert.Eq(true, pcm[0] > 0 && pcm[1] == 0 && pcm[2] > 0 && pcm[3] == 0, "L=mic speech, R=system silence"); Assert.Eq(true, rms > 0.1f && rms < 0.25f, "rms");
            var big = new float[16 * Frame]; Array.Fill(big, 0.1f); sys.Write(big);                  // 640 ms backlog (> 500 ms cap)
            asm.Assemble(new[] { mic, sys }, pcm); Assert.Eq(0, sys.Available, "jumped to live audio, backlog dropped");
            var silent = asm.Assemble(new[] { mic, sys }, pcm); Assert.Eq(0f, silent, "fully idle -> exact silence"); await Task.CompletedTask;
        });
    }
}
