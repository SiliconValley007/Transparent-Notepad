using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TransparentNotepad.Session;

namespace TransparentNotepad.Config;

public sealed class LlmProviderConfig
{
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "OpenAi";          // OpenAi (any OpenAI-compatible) | Anthropic
    public string BaseUrl { get; set; } = "";
    public string Model { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public string KeyProtected { get; set; } = "";        // DPAPI blob (current Windows user)
    public string SignupUrl { get; set; } = "";
    public string VisionModel { get; set; } = "";         // vision-capable model on this endpoint (empty = not used for screen questions)
}

public sealed class AppConfig
{
    public double Opacity { get; set; } = 0.85;
    public bool AlwaysOnTop { get; set; } = false;
    public int X { get; set; } = int.MinValue;
    public int Y { get; set; } = int.MinValue;
    public int Width { get; set; } = 900;
    public int Height { get; set; } = 560;

    // Speech recognition
    public string AsrEngine { get; set; } = "Auto";       // Auto = Deepgram -> Groq -> Local (first with a key / working) | Deepgram | Groq | Local
    public string DeepgramKeyProtected { get; set; } = "";
    public AudioSourceKind AudioSource { get; set; } = AudioSourceKind.Both;
    public string Language { get; set; } = "en";
    public bool Diarize { get; set; } = true;
    public int EndOfSpeechMs { get; set; } = 450;         // pause that finalises an utterance (Local/Groq)
    public int PartialEveryMs { get; set; } = 500;        // live interim cadence; 0 = off (Groq free tier is rate-limited: adaptive back-off is applied)
    public bool FallbackToLocal { get; set; } = true;
    public bool HideWindowWhileCapturing { get; set; } = false;
    public bool ExcludeFromCapture { get; set; } = true;       // stealth: WDA_EXCLUDEFROMCAPTURE on every window we own
    public bool NoActivateMode { get; set; } = true;           // overlay never steals focus (WS_EX_NOACTIVATE) except while typing
    public bool AllowKeylessCloud { get; set; } = false;       // opt-in: anonymous free cloud AI (third party)
    public bool KeylessAsked { get; set; } = false;
    public string SystemAudioLabel { get; set; } = "Interviewer";
    public string WhisperModel { get; set; } = "tiny";         // tiny (fast, ~75 MB) | base (more accurate, ~150 MB)   // always blank the window briefly during screen capture (otherwise only if capture-exclusion isn't active)

    // LLM providers, tried in this order (auto-failover). The Groq entry's key also powers Groq speech recognition.
    public List<LlmProviderConfig> Providers { get; set; } = DefaultProviders();

    public static List<LlmProviderConfig> DefaultProviders() => new()
    {
        new() { Name = "Groq", BaseUrl = "https://api.groq.com/openai/v1", Model = "llama-3.3-70b-versatile", VisionModel = "meta-llama/llama-4-scout-17b-16e-instruct", SignupUrl = "https://console.groq.com/keys" },
        new() { Name = "Gemini", BaseUrl = "https://generativelanguage.googleapis.com/v1beta/openai", Model = "gemini-2.5-flash-lite", VisionModel = "gemini-2.5-flash", SignupUrl = "https://aistudio.google.com/apikey" },
        new() { Name = "OpenRouter", BaseUrl = "https://openrouter.ai/api/v1", Model = "meta-llama/llama-3.3-70b-instruct:free", VisionModel = "meta-llama/llama-3.2-11b-vision-instruct", SignupUrl = "https://openrouter.ai/keys" },
        new() { Name = "Ollama", BaseUrl = "http://localhost:11434/v1", Model = "llama3.2", SignupUrl = "https://ollama.com/download" },
        new() { Name = "Anthropic", Kind = "Anthropic", BaseUrl = "https://api.anthropic.com", Model = "claude-haiku-5-5", VisionModel = "claude-haiku-5-5", Enabled = false, SignupUrl = "https://console.anthropic.com/settings/keys" },
    };

    public string DeepgramKey() => ConfigStore.Unprotect(DeepgramKeyProtected);
    public string GroqKey() => ConfigStore.Unprotect(Providers.FirstOrDefault(p => p.Name == "Groq")?.KeyProtected ?? "");

    /// <summary>Upgrades configs saved by older versions: adds missing default providers, fills vision models / signup links.</summary>
    public void Normalize()
    {
        Opacity = double.IsFinite(Opacity) ? Math.Clamp(Opacity, 0.15, 1.0) : 0.85;
        Width = Math.Clamp(Width, 480, 10000); Height = Math.Clamp(Height, 300, 10000);
        EndOfSpeechMs = Math.Clamp(EndOfSpeechMs, 150, 2000); PartialEveryMs = Math.Clamp(PartialEveryMs, 0, 5000);
        if (string.IsNullOrWhiteSpace(Language)) Language = "en";
        if (string.IsNullOrWhiteSpace(SystemAudioLabel)) SystemAudioLabel = "Interviewer";
        if (WhisperModel is not ("tiny" or "base")) WhisperModel = "tiny";
        if (AsrEngine is not ("Auto" or "Deepgram" or "Groq" or "Local")) AsrEngine = "Auto";
        Providers ??= DefaultProviders();
        foreach (var d in DefaultProviders())
        {
            var p = Providers.FirstOrDefault(x => x.Name == d.Name);
            if (p == null) { Providers.Add(d); continue; }
            if (string.IsNullOrEmpty(p.VisionModel)) p.VisionModel = d.VisionModel;
            if (string.IsNullOrEmpty(p.SignupUrl)) p.SignupUrl = d.SignupUrl;
        }
    }

    public AppConfig Clone() => JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(this))!;
}

public static class ConfigStore
{
    public static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TransparentNotepad");
    public static readonly string ConfigPath = Path.Combine(Dir, "config.json");
    public static readonly string SessionsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TransparentNotepad", "Sessions");
    public static readonly string AutoSavePath = Path.Combine(Dir, "autosave_draft.txt");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static AppConfig Load()
    {
        try { if (File.Exists(ConfigPath)) { var c = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigPath)) ?? new(); c.Normalize(); return c; } }
        catch (Exception ex)
        {   // corrupt/unreadable config: keep a copy for the user and start fresh
            try { File.Copy(ConfigPath, ConfigPath + ".bad", true); } catch { }
            TransparentNotepad.Logging.Log.Error("config.load_failed", ex);
        }
        return new();
    }

    public static void Save(AppConfig c)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var tmp = ConfigPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(c, Json));
            File.Move(tmp, ConfigPath, overwrite: true);
        }
        catch { }
    }

    public static string Protect(string plain) =>
        string.IsNullOrEmpty(plain) ? "" : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.CurrentUser));

    public static string Unprotect(string blob)
    {
        if (string.IsNullOrEmpty(blob)) return "";
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(blob), null, DataProtectionScope.CurrentUser)); }
        catch { return ""; }
    }
}
