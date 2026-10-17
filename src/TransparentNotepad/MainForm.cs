using System.Text;
using TransparentNotepad.Ai;
using TransparentNotepad.Audio;
using TransparentNotepad.Config;
using TransparentNotepad.Logging;
using TransparentNotepad.Native;
using TransparentNotepad.Session;
using TransparentNotepad.Transcription;
using TransparentNotepad.UI;

namespace TransparentNotepad;

/// <summary>
/// Borderless, capture-invisible overlay. Fluid layout: header (drag) / tabs / status; the Assistant tab reflows
/// 3 columns -> 2 columns -> stacked as the window narrows. Heavy lifting lives in Audio/, Transcription/, Ai/, Native/.
/// </summary>
public partial class MainForm : Form
{
    private const int HOTKEY_TOGGLE = 9000, HOTKEY_ASSIST = 9001, HOTKEY_SCREEN = 9002;

    private AppConfig _cfg = ConfigStore.Load();
    private readonly MeetingSession _session;
    private readonly NAudioCaptureSource _capture;
    private readonly LlmFailoverClient _llm = new();
    private readonly AiEngine _ai;
    private string? _asrOverride;                 // set when a cloud engine failed and we dropped to local
    private CancellationTokenSource? _assistCts;
    private bool _listening;

    // UI
    private readonly HitTransparentPanel _root = new();
    private readonly Label _title = new(), _status = new();
    private readonly Button _btnListen = new(), _btnAssist = new(), _btnAi = new(), _btnCopyMd = new();
    private readonly SessionLog _sessionLog = new(ConfigStore.SessionsDir);
    private readonly System.Windows.Forms.Timer _logTimer = new() { Interval = 10000 };
    private bool _logDirty, _capturing, _exporting, _logErrorShown;
    private string? _lastSummaryMd;
    private readonly ContextMenuStrip _aiMenu = new();
    private readonly System.Windows.Forms.Timer _enforceTimer = new() { Interval = 3000 };
    private readonly TabControl _tabs = new();
    private readonly TabPage _tabAssistant = new("Assistant"), _tabBrowser = new("Browser");
    private readonly TableLayoutPanel _grid = new();
    private readonly TranscriptView _transcript = new();
    private readonly TextBox _notes = new();
    private readonly RichTextBox _assistBox = new();
    private readonly Label _assistTitle = new();
    private TableLayoutPanel _paneTranscript = null!, _paneNotes = null!, _paneAssist = null!;
    private readonly BrowserHost _browser = new();
    private readonly ToastLabel _toast = new();
    private readonly System.Windows.Forms.Timer _toastTimer = new() { Interval = 2200 };
    private readonly System.Windows.Forms.Timer _autoSaveTimer = new() { Interval = 1000 };
    private readonly ContextMenuStrip _menu = new();
    private ToolStripMenuItem _topMostItem = null!;
    private int _layoutMode;
    private string _captureMode = "";

    // Notepad state
    private string? _filePath;
    private bool _dirty, _loadingText;

    public MainForm()
    {
        _capture = new NAudioCaptureSource();
        _ai = new AiEngine(_llm, allowKeylessCloud: () => _cfg.AllowKeylessCloud);
        _ai.Notice += m => OnUi(() => ShowToast(m));
        _session = new MeetingSession(_capture, CreateAsrClient, _cfg.SystemAudioLabel);
        _session.FinalCommitted += c => OnUi(() => { _transcript.AppendFinal(c); _sessionLog.Begin(); _logDirty = true; });
        _session.InterimChanged += () => OnUi(() => _transcript.SetInterim(_session.Buffer.Interim()));
        _session.StateChanged += s => { Log.Info("asr.state", ("state", s)); OnUi(() => OnConnectionState(s)); };
        _session.Error += e => { Log.Warn("asr.error", ("message", e)); OnUi(() => OnAsrError(e)); };
        _session.Warning += w => { Log.Warn("audio.warning", ("message", w)); OnUi(() => ShowToast(w)); };

        _llm.ProviderUsed += n => OnUi(() => _assistTitle.Text = _assistTitle.Text.Replace("  …", "") + $"  ·  via {n}");
        _llm.ProviderFailed += m => OnUi(() => ShowToast(m + " - trying next provider"));
        InitializeShell();
        BuildHeader();
        BuildBody();
        BuildMenu();
        WireNotes();
        LoadAutoSaveDraft();
        UpdateTitle();
    }

    private void OnUi(Action a) { if (IsHandleCreated && !IsDisposed) BeginInvoke(a); }

    // ---------------- shell / layout ----------------
    private void InitializeShell()
    {
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 9F);
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        KeyPreview = true;
        DoubleBuffered = true;
        BackColor = Color.FromArgb(190, 190, 196);            // 1-px-ish border colour (form padding shows through)
        Padding = new Padding(2);
        MinimumSize = new Size(680, 360);
        ClientSize = new Size(Math.Max(_cfg.Width, 680), Math.Max(_cfg.Height, 360));
        Opacity = Math.Clamp(_cfg.Opacity, 0.15, 1.0);
        TopMost = _cfg.AlwaysOnTop;
        StartPosition = FormStartPosition.CenterScreen;
        if (_cfg.X != int.MinValue) { StartPosition = FormStartPosition.Manual; Location = new Point(_cfg.X, _cfg.Y); }

        _root.Dock = DockStyle.Fill;
        _root.BackColor = Color.White;
        _root.Padding = new Padding(8);                          // outer band belongs to the form -> resize grips
        Controls.Add(_root);

        _toast.AutoSize = true; _toast.BackColor = Color.FromArgb(220, 230, 230, 230); _toast.ForeColor = Color.Black;
        _toast.Font = new Font("Segoe UI", 9.5F, FontStyle.Bold); _toast.Padding = new Padding(12, 6, 12, 6); _toast.Visible = false;
        _toastTimer.Tick += (_, _) => { _toast.Visible = false; _toastTimer.Stop(); };
        Controls.Add(_toast);
        _autoSaveTimer.Tick += (_, _) => { _autoSaveTimer.Stop(); PerformAutoSave(); };
        _logTimer.Tick += (_, _) => { if (_logDirty) { _logDirty = false; SaveSessionLog(); } };
        _logTimer.Start();
        ResumeLayout(false);
    }

    private void BuildHeader()
    {
        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 3,
            RowCount = 1,
            BackColor = Color.FromArgb(244, 244, 247),
            Margin = Padding.Empty,
            Padding = new Padding(2)
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var btnMenu = Flat("☰"); btnMenu.Click += (_, _) => _menu.Show(btnMenu, new Point(0, btnMenu.Height));
        _title.Dock = DockStyle.Fill; _title.TextAlign = ContentAlignment.MiddleLeft; _title.AutoEllipsis = true;
        _title.Font = new Font("Segoe UI Semibold", 9.5F); _title.Padding = new Padding(6, 0, 6, 0);

        var right = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Dock = DockStyle.Fill, Margin = Padding.Empty };
        Configure(_btnListen, "● Listen", (_, _) => ToggleListen());
        Configure(_btnAssist, "Assist", (_, _) => TriggerAssist(AssistMode.Assist));
        Configure(_btnCopyMd, "Copy MD", (_, _) => _ = CopyMarkdownAsync());
        Configure(_btnAi, "AI ▾", (_, _) => _aiMenu.Show(_btnAi, new Point(0, _btnAi.Height)));
        _aiMenu.Renderer = new CustomLightMenuRenderer();
        _aiMenu.Items.Add(new ToolStripMenuItem("Ask about my screen", null, (_, _) => TriggerScreenAssist()) { ShortcutKeyDisplayString = "Ctrl+Shift+S" });
        _aiMenu.Items.Add("Label speakers (AI)", null, (_, _) => TriggerAssist(AssistMode.Diarize));
        _aiMenu.Items.Add(new ToolStripSeparator());
        _aiMenu.Items.Add("Follow-up questions", null, (_, _) => TriggerAssist(AssistMode.FollowUps));
        _aiMenu.Items.Add("Action items && speaker notes", null, (_, _) => TriggerAssist(AssistMode.ActionItems));
        _aiMenu.Items.Add("Meeting summary", null, (_, _) => TriggerAssist(AssistMode.Summary));
        _aiMenu.Items.Add("Clean transcript (word for word)", null, (_, _) => TriggerAssist(AssistMode.Clean));
        var btnSettings = Flat("⚙"); btnSettings.Click += (_, _) => OpenSettings();
        var btnMin = Flat("–"); btnMin.Click += (_, _) => Hide();   // Ctrl+Shift+H brings it back
        var btnClose = Flat("✕"); btnClose.Click += (_, _) => Close();
        right.Controls.AddRange(new Control[] { _btnListen, _btnAssist, _btnAi, _btnCopyMd, btnSettings, btnMin, btnClose });

        header.Controls.Add(btnMenu, 0, 0); header.Controls.Add(_title, 1, 0); header.Controls.Add(right, 2, 0);
        // Borderless drag: ReleaseCapture + WM_NCLBUTTONDOWN(HTCAPTION)
        foreach (Control c in new Control[] { header, _title }) { 
            c.MouseDown += (_, e) => { 
                if (e.Button != MouseButtons.Left) return;
                if (e.Clicks == 2)
                {
                    ToggleFullscreen();
                }
                else if (e.Clicks == 1)
                {
                    Win32.BeginDrag(Handle);
                }
            };
        }

        _status.Dock = DockStyle.Bottom; _status.AutoSize = true; _status.Padding = new Padding(4, 3, 4, 3); _status.ForeColor = Color.DimGray; _status.TextAlign = ContentAlignment.MiddleLeft;
        _status.Font = new Font("Segoe UI", 8.5F);

        _root.Controls.Add(_status);   // add order: Fill first (below), then Top/Bottom docks
        _root.Tag = header;
    }

    private void BuildBody()
    {
        _tabs.Dock = DockStyle.Fill;
        _tabs.TabPages.Add(_tabAssistant); _tabs.TabPages.Add(_tabBrowser);
        _tabs.SelectedIndexChanged += (_, _) => { if (_tabs.SelectedTab == _tabBrowser) _browser.EnsureStarted(); AllowTyping(_tabs.SelectedTab == _tabBrowser); };
        _tabBrowser.Controls.Add(_browser);

        _grid.Dock = DockStyle.Fill; _grid.Margin = Padding.Empty; _grid.Padding = Padding.Empty;
        _tabAssistant.Controls.Add(_grid);

        _notes.Multiline = true; _notes.ScrollBars = ScrollBars.Vertical; _notes.WordWrap = true; _notes.AcceptsTab = true;
        _notes.BorderStyle = BorderStyle.None; _notes.Dock = DockStyle.Fill; _notes.Font = new Font("Consolas", 11F); _notes.AllowDrop = true;
        _notes.DragEnter += (_, e) => { if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true) e.Effect = DragDropEffects.Copy; };
        _notes.MouseDown += (_, _) => AllowTyping(true);
        _notes.Leave += (_, _) => AllowTyping(false);
        _notes.DragDrop += (_, e) => { if (e.Data?.GetData(DataFormats.FileDrop) is string[] f && f.Length > 0) OpenFile(f[0]); };

        _assistBox.ReadOnly = true; _assistBox.BorderStyle = BorderStyle.None; _assistBox.BackColor = Color.White; _assistBox.Dock = DockStyle.Fill;
        _assistBox.Font = new Font("Segoe UI", 10.5F); _assistBox.DetectUrls = false; _assistBox.WordWrap = true; _assistBox.ScrollBars = RichTextBoxScrollBars.Vertical;
        _assistBox.Text = "Press Ctrl+Enter anywhere for an instant answer based on the live transcript and your notes.";

        _assistTitle.Text = "AI ASSIST";
        _paneTranscript = Pane("LIVE TRANSCRIPT", _transcript);
        _paneNotes = Pane("NOTES", _notes);
        _paneAssist = Pane(null, _assistBox, _assistTitle);

        var header = (Control)_root.Tag!; _root.Tag = null;
        _root.Controls.Add(_tabs);
        _root.Controls.Add(header);
        _root.Controls.SetChildIndex(_tabs, 0);   // Fill must be at index 0 so Top/Bottom docks reserve space first
        _grid.SizeChanged += (_, _) => ApplyLayout();
        ApplyLayout();
    }

    /// <summary>3 columns when wide, 2 columns (assist spans) when medium, one stacked column when narrow.</summary>
    private void ApplyLayout()
    {
        float s = DeviceDpi / 96f;
        int w = _grid.ClientSize.Width;
        int mode = w >= 900 * s ? 3 : w >= 620 * s ? 2 : 1;
        if (mode == _layoutMode || w <= 0) return;
        _layoutMode = mode;
        _grid.SuspendLayout();
        _grid.Controls.Clear(); _grid.ColumnStyles.Clear(); _grid.RowStyles.Clear();
        _grid.SetRowSpan(_paneAssist, 1);
        void Cols(int n) { _grid.ColumnCount = n; for (int i = 0; i < n; i++) _grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / n)); }
        void Rows(int n) { _grid.RowCount = n; for (int i = 0; i < n; i++) _grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100f / n)); }
        switch (mode)
        {
            case 3: Cols(3); Rows(1); _grid.Controls.Add(_paneTranscript, 0, 0); _grid.Controls.Add(_paneNotes, 1, 0); _grid.Controls.Add(_paneAssist, 2, 0); break;
            case 2: Cols(2); Rows(2); _grid.Controls.Add(_paneTranscript, 0, 0); _grid.Controls.Add(_paneNotes, 0, 1); _grid.Controls.Add(_paneAssist, 1, 0); _grid.SetRowSpan(_paneAssist, 2); break;
            default: Cols(1); Rows(3); _grid.Controls.Add(_paneTranscript, 0, 0); _grid.Controls.Add(_paneNotes, 0, 1); _grid.Controls.Add(_paneAssist, 0, 2); break;
        }
        _grid.ResumeLayout(true);
    }

    private static TableLayoutPanel Pane(string? title, Control body, Label? titleLabel = null)
    {
        var p = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = new Padding(3), BackColor = Color.White };
        p.RowStyles.Add(new RowStyle(SizeType.AutoSize)); p.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var l = titleLabel ?? new Label();
        if (title != null) l.Text = title;
        l.AutoSize = true; l.Dock = DockStyle.Fill; l.ForeColor = Color.DimGray; l.Font = new Font("Segoe UI Semibold", 8.5F); l.Padding = new Padding(2, 2, 2, 4);
        body.Margin = Padding.Empty;
        p.Controls.Add(l, 0, 0); p.Controls.Add(body, 0, 1);
        return p;
    }

    private static Button Flat(string text)
    {
        var b = new Button { Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlatStyle = FlatStyle.Flat, MinimumSize = new Size(34, 30), Margin = new Padding(2, 5, 2, 5), Cursor = Cursors.Hand };
        b.FlatAppearance.BorderSize = 0; b.FlatAppearance.MouseOverBackColor = Color.FromArgb(225, 225, 230);
        return b;
    }
    private static void Configure(Button b, string text, EventHandler click)
    {
        b.Text = text; b.AutoSize = true; b.AutoSizeMode = AutoSizeMode.GrowAndShrink; b.FlatStyle = FlatStyle.Flat; b.MinimumSize = new Size(34, 30);
        b.Margin = new Padding(2, 5, 2, 5); b.Cursor = Cursors.Hand; b.FlatAppearance.BorderSize = 0; b.FlatAppearance.MouseOverBackColor = Color.FromArgb(225, 225, 230);
        b.Click += click;
    }

    // ---------------- menu ----------------
    private void BuildMenu()
    {
        _menu.Renderer = new CustomLightMenuRenderer();
        var file = new ToolStripMenuItem("&File");
        file.DropDownItems.AddRange(new ToolStripItem[]
        {
            new ToolStripMenuItem("&New", null, (_, _) => CreateNewFile()) { ShortcutKeyDisplayString = "Ctrl+N" },
            new ToolStripMenuItem("&Open...", null, (_, _) => OpenFile()) { ShortcutKeyDisplayString = "Ctrl+O" },
            new ToolStripMenuItem("&Save", null, (_, _) => SaveFile(false)) { ShortcutKeyDisplayString = "Ctrl+S" },
            new ToolStripMenuItem("Save &As...", null, (_, _) => SaveFile(true)) { ShortcutKeyDisplayString = "Ctrl+Alt+S" },
        });
        var opacity = new ToolStripMenuItem("&Opacity");
        for (int p = 20; p <= 100; p += 10) { double v = p / 100.0; opacity.DropDownItems.Add(new ToolStripMenuItem($"{p}%", null, (_, _) => SetOpacity(v))); }
        _topMostItem = new ToolStripMenuItem("Always-On-Top", null, (_, _) => ToggleAlwaysOnTop()) { Checked = TopMost };
        var view = new ToolStripMenuItem("&View"); view.DropDownItems.AddRange(new ToolStripItem[] { opacity, _topMostItem });
        view.DropDownItems.Add(new ToolStripMenuItem("Full screen", null, (_, _) => ToggleFullscreen()) { ShortcutKeyDisplayString = "F11" });
        _menu.Items.AddRange(new ToolStripItem[]
        {
            file, view,
            new ToolStripMenuItem("&Clear transcript", null, (_, _) => ClearTranscript()),
            new ToolStripMenuItem("Open &sessions folder", null, (_, _) => OpenSessionsFolder()),
            new ToolStripSeparator(),
            new ToolStripMenuItem("E&xit", null, (_, _) => Close())
        });
        foreach (ToolStripItem i in _menu.Items) i.BackColor = Color.FromArgb(245, 245, 245);
        StealthMenu(_menu); StealthMenu(_aiMenu);   // popup menus are separate windows: keep them out of screen shares too
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        switch (keyData)
        {
            case Keys.F11: ToggleFullscreen(); return true;
            case Keys.Control | Keys.N: CreateNewFile(); return true;
            case Keys.Control | Keys.O: OpenFile(); return true;
            case Keys.Control | Keys.S: SaveFile(false); return true;
            case Keys.Control | Keys.Alt | Keys.S: SaveFile(true); return true;   // Ctrl+Shift+S is the global screen-question hotkey
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private void ToggleFullscreen() => WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized;

    private void ToggleAlwaysOnTop() { TopMost = !TopMost; _topMostItem.Checked = TopMost; _cfg.AlwaysOnTop = TopMost; ConfigStore.Save(_cfg); ShowToast(TopMost ? "Always on top: on" : "Always on top: off"); }
    private void SetOpacity(double level) { Opacity = Math.Clamp(level, 0.15, 1.0); _cfg.Opacity = Opacity; ConfigStore.Save(_cfg); ShowToast($"Opacity: {Math.Round(Opacity * 100)}%"); }

    // ---------------- live transcription ----------------
    private void ToggleListen()
    {
        if (_listening) { StopListening(); return; }
        var engine = ResolveEngine();
        if (engine == "Deepgram" && string.IsNullOrEmpty(ConfigStore.Unprotect(_cfg.DeepgramKeyProtected))) { ShowToast("Add your Deepgram API key in Settings"); OpenSettings(); return; }
        if (engine == "Groq" && _cfg.GroqKey().Length == 0) { ShowToast("Add your Groq API key (AI providers tab), or pick Local"); OpenSettings(); return; }
        try
        {
            _asrOverride = null;
            Log.Info("session.start", ("engine", engine), ("source", _cfg.AudioSource), ("language", _cfg.Language));
            _session.Start(_cfg.AudioSource);
            _listening = true; _btnListen.Text = "■ Stop"; _btnListen.BackColor = Color.FromArgb(255, 225, 225);
            _status.Text = "Connecting…";
        }
        catch (Exception ex) { Log.Error("session.start_failed", ex); ShowToast(ex.Message); }
    }

    private async void StopListening()
    {
        if (!_listening) return;
        _listening = false; _btnListen.Text = "● Listen"; _btnListen.BackColor = Color.Transparent;
        try { await _session.StopAsync(); } catch { }
        UpdateStatus("Stopped");
    }

    private void OnConnectionState(ConnectionState s)
    {
        UpdateStatus(s switch { ConnectionState.Open => "Listening", ConnectionState.Connecting => "Connecting…", ConnectionState.Reconnecting => "Reconnecting…", _ => "Stopped" });
        if (s == ConnectionState.Closed && _listening && _session.Running && !(_cfg.FallbackToLocal && ResolveEngine() == "Deepgram")) StopListening(); // gave up (bad key / offline)
    }

    private void UpdateStatus(string state) => _status.Text = $"{state}  •  {_captureMode}";

    // ---------------- AI assist ----------------
    private List<LlmEndpoint> BuildEndpoints(bool vision)
    {
        IEnumerable<LlmProviderConfig> src = _cfg.Providers;
        if (vision)   // primary Gemini, then OpenRouter, then Groq, then the rest (Ollama/Anthropic)
            src = src.OrderBy(p => p.Name switch { "Gemini" => 0, "OpenRouter" => 1, "Groq" => 2, _ => 3 });
        return src.Where(p => !vision || p.VisionModel.Length > 0)
            .Select(p => new LlmEndpoint(p.Name, p.Kind == "Anthropic" ? LlmProvider.Anthropic : LlmProvider.OpenAi, p.BaseUrl,
                vision ? p.VisionModel : p.Model, ConfigStore.Unprotect(p.KeyProtected), p.Enabled)).ToList();
    }

    private async void TriggerAssist(AssistMode mode, byte[]? image = null)
    {
        _assistCts?.Cancel();
        var cts = _assistCts = new CancellationTokenSource();
        _tabs.SelectedTab = _tabAssistant;
        _assistTitle.Text = mode switch
        {
            AssistMode.FollowUps => "AI FOLLOW-UPS",
            AssistMode.ActionItems => "ACTION ITEMS & NOTES",
            AssistMode.Summary => "MEETING SUMMARY",
            AssistMode.Clean => "CLEAN TRANSCRIPT",
            AssistMode.Screen => "SCREEN ASSIST",
            AssistMode.Diarize => "LABEL SPEAKERS",
            _ => "AI ASSIST"
        } + "  …";
        _assistBox.Clear();
        var (system, user, maxTokens) = PromptBuilder.Build(mode, _session.Buffer.ToText(), _notes.Text);
        EnsureKeylessConsent();
        var endpoints = BuildEndpoints(image != null);
        var text = new StringBuilder(); bool ok = false;
        try
        {
            await foreach (var delta in _ai.StreamAsync(endpoints, mode, system, user, maxTokens, _session.Buffer.ToText(), _notes.Text, image, cts.Token)) { text.Append(delta); _assistBox.AppendText(delta); _assistBox.ScrollToCaret(); }
            ok = true;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Error("assist.failed", ex, ("mode", mode)); _assistBox.AppendText($"\n[Error] {ex.Message}"); }
        finally { if (_assistCts == cts) _assistTitle.Text = _assistTitle.Text.Replace("  …", ""); }
        if (ok && mode == AssistMode.Diarize && !cts.IsCancellationRequested) ApplyDiarizedTranscript(text.ToString());
    }

    // ---------------- screen vision (Ctrl+Shift+S) ----------------
    /// <summary>Captures the monitor holding the foreground window as an in-memory JPEG - without our own window in it - and asks a vision model.</summary>
    private async void TriggerScreenAssist()
    {
        if (_capturing) return;
        _capturing = true; byte[]? jpeg = null;
        try
        {
            var fg = Win32.GetForegroundWindow();
            var screen = Screen.FromHandle(fg == IntPtr.Zero ? Handle : fg);
            // Normally WDA_EXCLUDEFROMCAPTURE already removes this window from every capture. Blank it anyway when the
            // exclusion isn't active (older Windows -> black box) or the user asked for the safest mode.
            bool excluded = Win32.GetAffinity(Handle) == Win32.WDA_EXCLUDEFROMCAPTURE;
            bool hide = Visible && (_cfg.HideWindowWhileCapturing || !excluded);
            double prev = Opacity;
            if (hide) { Opacity = 0; await Task.Delay(160); }          // let DWM drop the window from the composed frame
            try { jpeg = ScreenCapture.CaptureJpeg(screen.Bounds); }
            finally { if (hide) Opacity = prev; }
        }
        catch (Exception ex) { Log.Error("screen.capture_failed", ex); ShowToast($"Screen capture failed: {ex.Message}"); return; }
        finally { _capturing = false; }
        TriggerAssist(AssistMode.Screen, jpeg);
    }

    // ---------------- speaker labelling fallback (Groq / local STT) ----------------
    private static int WordCount(string s) => s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    private void ApplyDiarizedTranscript(string llmOutput)
    {
        var turns = SpeakerTurns.Parse(llmOutput);
        var before = _session.Buffer.Snapshot().Sum(l => WordCount(l.Text));
        var after = turns.Sum(t => WordCount(t.Text));
        if (turns.Count == 0 || before == 0) { ShowToast("Could not read the speaker labels from the AI answer"); return; }
        if (after < before * 0.9 || after > before * 1.1) { ShowToast("AI changed the wording - transcript left unchanged"); return; }   // diarization must not alter words
        _session.Buffer.ReplaceAll(turns);
        _transcript.ResetView();
        foreach (var t in turns) _transcript.AppendFinal(new Commit(true, t.Label, t.Text));
        _logDirty = true; ShowToast("Speakers labelled");
    }

    // ---------------- Markdown export + session logs ----------------
    private string BuildSessionMarkdown(string? summary) =>
        MarkdownExporter.Build(_sessionLog.Started ?? DateTime.Now, DateTime.Now, _session.Buffer.Snapshot(), _notes.Text, summary ?? _lastSummaryMd, _cfg.AudioSource.ToString());

    /// <summary>Writes %AppData%\TransparentNotepad\Sessions\yyyy-MM-dd_HH-mm-ss.md (skips empty sessions). Never throws.</summary>
    private string? SaveSessionLog(string? markdown = null)
    {
        try
        {
            if (_session.Buffer.Snapshot().Count == 0 && markdown == null) return null;
            return _sessionLog.Save(markdown ?? BuildSessionMarkdown(null));
        }
        catch (Exception ex) { if (!_logErrorShown) { _logErrorShown = true; ShowToast($"Could not save session log: {ex.Message}"); } return null; }
    }

    private async Task CopyMarkdownAsync()
    {
        if (_exporting) return;
        if (_session.Buffer.Snapshot().Count == 0 && string.IsNullOrWhiteSpace(_notes.Text)) { ShowToast("Nothing to export yet"); return; }
        _exporting = true; _btnCopyMd.Enabled = false; ShowToast("Building Markdown summary…");
        try
        {
            string? summary = null;
            try
            {
                var (system, user, maxTokens) = PromptBuilder.Build(AssistMode.MarkdownRecap, _session.Buffer.ToText(), _notes.Text);
                var sb = new StringBuilder();
                EnsureKeylessConsent();
                await foreach (var d in _ai.StreamAsync(BuildEndpoints(false), AssistMode.MarkdownRecap, system, user, maxTokens, _session.Buffer.ToText(), _notes.Text)) sb.Append(d);
                summary = StripFences(sb.ToString());
                _lastSummaryMd = summary;
            }
            catch (Exception) { ShowToast("AI summary unavailable - exporting the transcript only"); }
            var md = BuildSessionMarkdown(summary);
            try { Clipboard.SetDataObject(md, true, 5, 100); ShowToast("Markdown copied to clipboard"); }
            catch (Exception ex) { ShowToast($"Clipboard busy: {ex.Message}"); }
            var path = SaveSessionLog(md);
            if (path != null) _logDirty = false;
        }
        finally { _exporting = false; _btnCopyMd.Enabled = true; }
    }

    private static string StripFences(string t)
    {
        t = t.Trim();
        if (t.StartsWith("```")) { int nl = t.IndexOf('\n'); if (nl > 0) t = t[(nl + 1)..]; if (t.EndsWith("```")) t = t[..^3]; }
        return t.Trim();
    }

    private void ClearTranscript()
    {
        SaveSessionLog();                       // keep what was captured before starting a fresh session
        _session.Buffer.Clear(); _transcript.ResetView();
        _sessionLog.Reset(); _lastSummaryMd = null; _logDirty = false;
    }

    private void OpenSessionsFolder()
    {
        try { Directory.CreateDirectory(ConfigStore.SessionsDir); System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(ConfigStore.SessionsDir) { UseShellExecute = true }); }
        catch (Exception ex) { ShowToast($"Cannot open folder: {ex.Message}"); }
    }

    /// <summary>Primary Deepgram -> secondary Groq -> tertiary Local. "Auto" picks the first tier that has a key; explicit choices are honoured.</summary>
    private string ResolveEngine()
    {
        var e = _asrOverride ?? _cfg.AsrEngine;
        if (e != "Auto") return e;
        return _cfg.DeepgramKey().Length > 0 ? "Deepgram" : _cfg.GroqKey().Length > 0 ? "Groq" : "Local";
    }

    private ITranscriptionClient CreateAsrClient(int channels)
    {
        var engine = ResolveEngine();
        _capture.NoiseGateEnabled = engine == "Deepgram";   // gate silence only for the streaming socket; VAD engines need the pauses
        var opts = new LocalAsrOptions { SilenceMs = _cfg.EndOfSpeechMs, PartialEveryMs = _cfg.PartialEveryMs };
        if (engine == "Deepgram")
            return new DeepgramClient(new DeepgramOptions { ApiKey = ConfigStore.Unprotect(_cfg.DeepgramKeyProtected), Language = _cfg.Language, Diarize = _cfg.Diarize, Channels = channels });
        var whisper = new WhisperNetStt(_cfg.Language, _cfg.WhisperModel);
        whisper.Progress += m => OnUi(() => ShowToast(m));
        var local = new CompositeLocalStt(whisper, new WindowsSpeechStt());   // starts instantly, upgrades to Whisper when loaded
        local.Notice += m => OnUi(() => ShowToast(m));
        if (engine != "Groq") return new WhisperLocalClient(local, channels, opts);
        var groq = new GroqWhisperStt(_cfg.GroqKey(), _cfg.Language);
        if (!_cfg.FallbackToLocal) return new WhisperLocalClient(groq, channels, opts);
        var fb = new FallbackSpeechToText(groq, local);
        fb.Notice += m => OnUi(() => ShowToast(m));
        return new WhisperLocalClient(fb, channels, opts);
    }

    /// <summary>Cloud streaming engine gave up -> restart on local Whisper so the meeting keeps being transcribed.</summary>
    private async void OnAsrError(string message)
    {
        ShowToast(message);
        if (!_listening || !_cfg.FallbackToLocal || ResolveEngine() != "Deepgram" || !message.StartsWith("Cannot reach")) return;
        try
        {
            await _session.StopAsync();
            _asrOverride = _cfg.GroqKey().Length > 0 ? "Groq" : "Local";   // Groq itself falls back to Local on failure
            _session.Start(_cfg.AudioSource);
            ShowToast($"Deepgram unreachable - continuing with {(_asrOverride == "Groq" ? "Groq Whisper" : "local Whisper")}");
        }
        catch (Exception ex) { _listening = false; _btnListen.Text = "● Listen"; ShowToast(ex.Message); }
    }

    /// <summary>One-time, explicit consent before anything is sent to the anonymous keyless cloud service.</summary>
    private void EnsureKeylessConsent()
    {
        if (_cfg.AllowKeylessCloud || _cfg.KeylessAsked) return;
        if (_cfg.Providers.Any(p => p.Enabled && p.KeyProtected.Length > 0)) return;      // user has their own key: nothing to ask
        _cfg.KeylessAsked = true;
        var r = MessageBox.Show(this,
            "No AI key is configured.\n\nUse the free keyless cloud AI (Pollinations)? Your prompts - including transcript text and screenshots - are sent anonymously to that third-party service. No account or key is needed.\n\nChoose No to stay offline (you can add a free key or install Ollama in Settings at any time).",
            "Free AI without a key", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        _cfg.AllowKeylessCloud = r == DialogResult.Yes; ConfigStore.Save(_cfg);
        Log.Info("ai.keyless_consent", ("allowed", _cfg.AllowKeylessCloud));
    }

    private void ApplyWindowModes()
    {
        Win32.SetNoActivate(Handle, _cfg.NoActivateMode); EnforceCaptureExclusion(); _session.SystemLabel = _cfg.SystemAudioLabel;
    }

    /// <summary>Stealth typing: clear WS_EX_NOACTIVATE only while the user is typing in an input, restore it on leave.</summary>
    private void AllowTyping(bool typing)
    {
        if (!_cfg.NoActivateMode || !IsHandleCreated) return;
        Win32.SetNoActivate(Handle, !typing);
        if (typing) Win32.SetForegroundWindow(Handle);
    }

    private void StealthMenu(ToolStripDropDown dd)
    {
        dd.HandleCreated += (_, _) => Win32.ApplyCaptureExclusion(dd.Handle, _cfg.ExcludeFromCapture);
        foreach (ToolStripItem i in dd.Items) if (i is ToolStripDropDownItem di && di.HasDropDownItems) StealthMenu(di.DropDown);
    }

    private void OpenSettings()
    {
        using var dlg = new SettingsForm(_cfg);
        if (dlg.ShowDialog(this) == DialogResult.OK) { ConfigStore.Save(_cfg); ApplyWindowModes(); ShowToast("Settings saved"); }
    }

    // ---------------- notes / files ----------------
    private void WireNotes()
    {
        _notes.TextChanged += (_, _) => { if (_loadingText) return; _dirty = true; UpdateTitle(); _autoSaveTimer.Stop(); _autoSaveTimer.Start(); };
    }

    private void SetNotes(string text) { _loadingText = true; _notes.Text = text; _loadingText = false; }

    private void UpdateTitle()
    {
        var name = _filePath == null ? "Untitled" : Path.GetFileName(_filePath);
        Text = $"Transparent Notepad - {name}{(_dirty ? "*" : "")}";
        _title.Text = Text;
    }

    private void PerformAutoSave() { try { Directory.CreateDirectory(ConfigStore.Dir); File.WriteAllText(ConfigStore.AutoSavePath, _notes.Text); } catch { } }
    private void LoadAutoSaveDraft()
    {
        try { if (File.Exists(ConfigStore.AutoSavePath)) { var t = File.ReadAllText(ConfigStore.AutoSavePath); if (t.Length > 0) SetNotes(t); } } catch { }
    }

    private bool PromptSaveIfModified()
    {
        if (!_dirty) return true;
        var r = MessageBox.Show(this, "Save changes to your notes?", "Transparent Notepad", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
        return r switch { DialogResult.Yes => SaveFile(false), DialogResult.No => true, _ => false };
    }

    private void CreateNewFile() { if (!PromptSaveIfModified()) return; SetNotes(""); _filePath = null; _dirty = false; UpdateTitle(); }

    private void OpenFile(string? path = null)
    {
        if (!PromptSaveIfModified()) return;
        if (path == null)
        {
            using var dlg = new OpenFileDialog { Filter = "Text files (*.txt;*.md)|*.txt;*.md|All files (*.*)|*.*" };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            path = dlg.FileName;
        }
        try { SetNotes(File.ReadAllText(path)); _filePath = path; _dirty = false; UpdateTitle(); ShowToast($"Opened {Path.GetFileName(path)}"); }
        catch (Exception ex) { ShowToast($"Open failed: {ex.Message}"); }
    }

    private bool SaveFile(bool saveAs)
    {
        var path = saveAs ? null : _filePath;
        if (path == null)
        {
            using var dlg = new SaveFileDialog { Filter = "Text files (*.txt)|*.txt|Markdown (*.md)|*.md|All files (*.*)|*.*", FileName = "notes.txt" };
            if (dlg.ShowDialog(this) != DialogResult.OK) return false;
            path = dlg.FileName;
        }
        try { File.WriteAllText(path, _notes.Text); _filePath = path; _dirty = false; UpdateTitle(); ShowToast($"Saved {Path.GetFileName(path)}"); return true; }
        catch (Exception ex) { ShowToast($"Save failed: {ex.Message}"); return false; }
    }

    // ---------------- toast ----------------
    private void ShowToast(string text)
    {
        _toast.Text = text; PositionToast(); _toast.BringToFront(); _toast.Visible = true; _toastTimer.Stop(); _toastTimer.Start();
    }
    private void PositionToast() => _toast.Location = new Point(Math.Max(0, ClientSize.Width - _toast.Width - 18), Math.Max(0, ClientSize.Height - _toast.Height - 34));
    protected override void OnSizeChanged(EventArgs e) { base.OnSizeChanged(e); if (_toast.Visible) PositionToast(); }

    // ---------------- window / Win32 ----------------
    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams; cp.ExStyle |= Win32.WS_EX_TOOLWINDOW;                    // keep out of Alt+Tab
            if (_cfg.NoActivateMode) cp.ExStyle |= Win32.WS_EX_NOACTIVATE;                      // never steal focus / fire blur in other apps
            return cp;
        }
    }

    protected override bool ShowWithoutActivation => _cfg.NoActivateMode;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        EnforceCaptureExclusion();
        UpdateStatus("Idle");
        _enforceTimer.Tick -= EnforceTick; _enforceTimer.Tick += EnforceTick; _enforceTimer.Start();

        if (!Win32.RegisterHotKey(Handle, HOTKEY_TOGGLE, Win32.MOD_CONTROL | Win32.MOD_SHIFT, Win32.VK_H))
        {
            Log.Warn("hotkey.register_failed", ("hotkey", "Ctrl+Shift+H"));
            BeginInvoke(() => ShowToast("Ctrl+Shift+H is taken by another app; use Esc or the – button to hide"));   // non-modal: never blocks startup
        }
        if (!Win32.RegisterHotKey(Handle, HOTKEY_SCREEN, Win32.MOD_CONTROL | Win32.MOD_SHIFT | Win32.MOD_NOREPEAT, Win32.VK_S))
            BeginInvoke(() => ShowToast("Ctrl+Shift+S is taken by another app; use AI ▾ → Ask about my screen"));
        if (!Win32.RegisterHotKey(Handle, HOTKEY_ASSIST, Win32.MOD_CONTROL | Win32.MOD_NOREPEAT, Win32.VK_RETURN))
            BeginInvoke(() => ShowToast("Ctrl+Enter is taken by another app; use the Assist button"));
    }

    private void EnforceTick(object? s, EventArgs e) { if (Visible && _cfg.ExcludeFromCapture && Win32.GetAffinity(Handle) is not (Win32.WDA_EXCLUDEFROMCAPTURE or Win32.WDA_MONITOR)) EnforceCaptureExclusion(); }

    /// <summary>Applies WDA_EXCLUDEFROMCAPTURE (0x11) and re-applies it if anything resets it; the status bar always shows the real state.</summary>
    private void EnforceCaptureExclusion()
    {
        var (ok, mode) = Win32.ApplyCaptureExclusion(Handle, _cfg.ExcludeFromCapture);
        var text = !_cfg.ExcludeFromCapture ? "stealth off (visible to screen capture)" : ok ? mode : "WARNING: not hidden from screen capture";
        if (text == _captureMode) return;
        _captureMode = text; Log.Info("capture.exclusion", ("ok", ok), ("mode", mode)); if (!ok) ShowToast(text);
        UpdateStatus(_listening ? "Listening" : "Idle");
    }

    protected override void OnShown(EventArgs e) { base.OnShown(e); EnforceCaptureExclusion(); }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        _enforceTimer.Stop();
        Win32.UnregisterHotKey(Handle, HOTKEY_TOGGLE); Win32.UnregisterHotKey(Handle, HOTKEY_ASSIST); Win32.UnregisterHotKey(Handle, HOTKEY_SCREEN);
        base.OnHandleDestroyed(e);
    }

    protected override void OnLoad(EventArgs e) { base.OnLoad(e); EnsureWindowIsOnValidScreen(); }

    private void EnsureWindowIsOnValidScreen()
    {
        var cur = Screen.FromControl(this).WorkingArea;
        if (Width > cur.Width || Height > cur.Height) Size = new Size(Math.Min(Width, cur.Width), Math.Min(Height, cur.Height));
        if (Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(Bounds))) return;
        var wa = (Screen.PrimaryScreen ?? Screen.AllScreens[0]).WorkingArea;
        StartPosition = FormStartPosition.Manual;
        Location = new Point(wa.X + (wa.Width - Width) / 2, wa.Y + (wa.Height - Height) / 2);
    }

    protected override void OnResizeEnd(EventArgs e) { base.OnResizeEnd(e); SaveBounds(); }
    private void SaveBounds()
    {
        var b = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        float sc = DeviceDpi / 96f;
        _cfg.X = b.X; _cfg.Y = b.Y; _cfg.Width = (int)(b.Width / sc); _cfg.Height = (int)(b.Height / sc); ConfigStore.Save(_cfg);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!PromptSaveIfModified()) { e.Cancel = true; return; }
        PerformAutoSave(); SaveBounds(); SaveSessionLog();
        _assistCts?.Cancel();
        try { _session.DisposeAsync().AsTask().Wait(2000); } catch { }
        base.OnFormClosing(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape) { ToggleWindowVisibility(); e.Handled = true; }
        else if (e.Control && e.KeyCode == Keys.Up) { SetOpacity(Opacity + 0.05); e.Handled = true; }
        else if (e.Control && e.KeyCode == Keys.Down) { SetOpacity(Opacity - 0.05); e.Handled = true; }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Win32.WM_HOTKEY)
        {
            int id = m.WParam.ToInt32();
            if (id == HOTKEY_TOGGLE) ToggleWindowVisibility();
            else if (id == HOTKEY_ASSIST) TriggerAssist(AssistMode.Assist);
            else if (id == HOTKEY_SCREEN) TriggerScreenAssist();
        }
        else if (m.Msg == Win32.WM_NCHITTEST)
        {
            base.WndProc(ref m);
            if ((int)m.Result == Win32.HTCLIENT && WindowState == FormWindowState.Normal)
            {
                long l = m.LParam.ToInt64();
                var p = PointToClient(new Point((short)(l & 0xFFFF), (short)((l >> 16) & 0xFFFF)));
                int g = (int)(8 * DeviceDpi / 96f);
                bool left = p.X < g, right = p.X >= ClientSize.Width - g, top = p.Y < g, bottom = p.Y >= ClientSize.Height - g;
                int hit = top ? (left ? Win32.HTTOPLEFT : right ? Win32.HTTOPRIGHT : Win32.HTTOP)
                        : bottom ? (left ? Win32.HTBOTTOMLEFT : right ? Win32.HTBOTTOMRIGHT : Win32.HTBOTTOM)
                        : left ? Win32.HTLEFT : right ? Win32.HTRIGHT : 0;
                if (hit != 0) m.Result = (IntPtr)hit;
            }
            return;
        }
        base.WndProc(ref m);
    }

    private void ToggleWindowVisibility()
    {
        if (!Visible) { Show(); EnforceCaptureExclusion(); WindowState = FormWindowState.Normal; Activate(); }
        else Hide();
    }
}
