using TransparentNotepad.Config;
using TransparentNotepad.Native;
using TransparentNotepad.Session;

namespace TransparentNotepad.UI;

/// <summary>Tabbed settings: Speech / AI providers / API keys. Edits a copy; nothing is applied until Save. Excluded from screen capture.</summary>
public sealed class SettingsForm : Form
{
    private readonly AppConfig _cfg, _work;
    // speech
    private readonly ComboBox _engine = Combo("Auto", "Deepgram", "Groq", "Local"), _source = Combo("Both", "Microphone", "SystemAudio");
    private readonly TextBox _lang = new(), _dg = Pw();
    private readonly NumericUpDown _eos = Num(150, 2000), _partial = Num(0, 5000);
    private readonly CheckBox _fallback = new() { Text = "Fall back to local Whisper on failure", AutoSize = true };
    private readonly CheckBox _stealth = new() { Text = "Hide from screen sharing (stealth)", AutoSize = true };
    private readonly CheckBox _noActivate = new() { Text = "Never steal focus (no-activate)", AutoSize = true };
    private readonly CheckBox _keyless = new() { Text = "Allow free keyless cloud AI", AutoSize = true };
    private readonly TextBox _sysLabel = new() { PlaceholderText = "Interviewer" };
    private readonly ComboBox _whisper = Combo("tiny", "base");
    private readonly CheckBox _hideCap = new() { Text = "Hide window while capturing screen", AutoSize = true };
    private readonly CheckBox _diarize = new() { Text = "Label speakers (single source)", AutoSize = true };
    // providers
    private readonly CheckedListBox _list = new() { IntegralHeight = false, CheckOnClick = true };
    private readonly TextBox _key = Pw(), _model = new(), _visionModel = new() { PlaceholderText = "empty = not used for screen questions" }, _baseUrl = new();
    private readonly ComboBox _kind = Combo("OpenAi", "Anthropic");
    private readonly LinkLabel _link = new() { Text = "Get a free API key", AutoSize = true };
    private LlmProviderConfig? _current; private bool _loading;
    private string _pendingKey = "";
    private readonly ToolTip _tip = new() { AutoPopDelay = 20000, InitialDelay = 400 };
    private readonly LinkLabel _dgLink = new() { Text = "Get a free Deepgram key (optional)", AutoSize = true };

    private static TextBox Pw() => new() { UseSystemPasswordChar = true };
    private static ComboBox Combo(params string[] items) { var c = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList }; c.Items.AddRange(items); return c; }
    private static NumericUpDown Num(int min, int max) => new() { Minimum = min, Maximum = max, Increment = 50 };

    public SettingsForm(AppConfig cfg)
    {
        _cfg = cfg; _work = cfg.Clone();
        AutoScaleDimensions = new SizeF(96F, 96F); AutoScaleMode = AutoScaleMode.Dpi;
        Text = "Settings"; FormBorderStyle = FormBorderStyle.Sizable; StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(480, 440); Size = new Size(640, 680); ShowInTaskbar = false; Font = new Font("Segoe UI", 9.5F);

        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(BuildSpeechTab()); tabs.TabPages.Add(BuildProvidersTab());
        var ok = new Button { Text = "Save", AutoSize = true, Padding = new Padding(10, 2, 10, 2) };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true, Padding = new Padding(10, 2, 10, 2) };
        ok.Click += (_, _) => { CommitCurrent(); Apply(); DialogResult = DialogResult.OK; };
        var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Padding = new Padding(8) };
        bar.Controls.Add(cancel); bar.Controls.Add(ok);
        Controls.Add(tabs); Controls.Add(bar);
        AcceptButton = ok; CancelButton = cancel;
        LoadValues();
    }

    private static TableLayoutPanel Grid()
    {
        var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(12), AutoScroll = true, AutoScrollMargin = new Size(0, 24) };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return t;
    }
    private static void Row(TableLayoutPanel t, string label, Control c)
    {
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        t.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 8, 12, 8) });
        c.Dock = DockStyle.Fill; c.Margin = new Padding(0, 4, 0, 4); t.Controls.Add(c);
    }

    private TabPage BuildSpeechTab()
    {
        var page = new TabPage("Speech & audio"); var t = Grid();
        Row(t, "Speech engine", _engine); Row(t, "Audio source", _source); Row(t, "Language", _lang);
        Row(t, "Pause to finalize (ms)", _eos); Row(t, "Live partials every (ms, 0=off)", _partial);
        Row(t, string.IsNullOrEmpty(_work.DeepgramKeyProtected) ? "Deepgram API key" : "Deepgram API key  ✓ saved", _dg);
        Row(t, "", _dgLink); Row(t, "Local model", _whisper); Row(t, "System audio label", _sysLabel);
        Row(t, "", _fallback); Row(t, "", _diarize); Row(t, "", _stealth); Row(t, "", _noActivate); Row(t, "", _keyless); Row(t, "", _hideCap);
        Row(t, "", new Label { AutoSize = true, ForeColor = Color.DimGray, Text = "Auto = Deepgram → Groq → Local (first available)." });
        _tip.SetToolTip(_engine, "Auto: Deepgram (if key) → Groq (if key) → Local, switching down automatically if one fails.\nLocal: free, offline, no key (first run downloads ~150 MB).\nGroq: whisper-large-v3-turbo in the cloud, uses the Groq key from the AI providers tab.\nDeepgram: true streaming WebSocket (lowest latency, trial credit).");
        _tip.SetToolTip(_stealth, "Excludes every window of this app (including menus and this dialog) from screen capture: Zoom, Teams, Meet, OBS...");
        _tip.SetToolTip(_noActivate, "Clicking the overlay or pressing hotkeys never takes focus from the app you are using (no blur events). Focus is only taken while you type in the notes box or browser.");
        _tip.SetToolTip(_keyless, "When no key or local Ollama is available, send prompts (transcript text, screenshots) anonymously to the free Pollinations service. Third party, no account, no key. Off = fully offline fallback.");
        _tip.SetToolTip(_whisper, "tiny = fastest, ~75 MB. base = more accurate, ~150 MB. Downloaded once; Windows speech recognition is used until it is ready.");
        _tip.SetToolTip(_dg, "Optional. Without a key the app uses Groq (if configured) or local Whisper.");
        _tip.SetToolTip(_eos, "Silence length that ends a sentence (Groq/Local engines).");
        _tip.SetToolTip(_partial, "How often live partial text refreshes while speaking (0 = off). Self-throttles on rate limits.");
        _tip.SetToolTip(_hideCap, "Briefly blanks this window when you press Ctrl+Shift+S. Normally unnecessary: the window is already excluded from capture.");
        page.Controls.Add(t); return page;
    }

    private TabPage BuildProvidersTab()
    {
        var page = new TabPage("AI providers & keys");
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(10) };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 38)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 62));
        root.Controls.Add(new Label { Text = "Checked providers are tried top to bottom.\nThe next one is used automatically if one fails or hits a limit.", AutoSize = true, ForeColor = Color.DimGray }, 0, 0);

        var top = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _list.Dock = DockStyle.Fill;
        var mv = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, Dock = DockStyle.Fill, WrapContents = false };
        var up = new Button { Text = "▲", Width = 34 }; var dn = new Button { Text = "▼", Width = 34 };
        up.Click += (_, _) => MoveProvider(-1); dn.Click += (_, _) => MoveProvider(1);
        mv.Controls.Add(up); mv.Controls.Add(dn);
        top.Controls.Add(_list, 0, 0); top.Controls.Add(mv, 1, 0); root.Controls.Add(top, 0, 1);

        var d = Grid(); d.Padding = new Padding(0, 6, 0, 0);
        Row(d, "API key", _key); Row(d, "Model", _model); Row(d, "Vision model", _visionModel); Row(d, "Base URL", _baseUrl); Row(d, "API type", _kind); Row(d, "", _link);
        root.Controls.Add(d, 0, 2);
        page.Controls.Add(root);

        _list.SelectedIndexChanged += (_, _) => { CommitCurrent(); ShowSelected(); };
        _list.ItemCheck += (_, e) => { if (!_loading && e.Index < _work.Providers.Count) _work.Providers[e.Index].Enabled = e.NewValue == CheckState.Checked; };
        _link.LinkClicked += (_, _) => { if (_current is { SignupUrl: { Length: > 0 } u }) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(u) { UseShellExecute = true }); };
        _key.TextChanged += (_, _) => { if (!_loading) _pendingKey = _key.Text; };
        return page;
    }

    private void MoveProvider(int dir)
    {
        int i = _list.SelectedIndex, j = i + dir;
        if (i < 0 || j < 0 || j >= _work.Providers.Count) return;
        CommitCurrent();
        (_work.Providers[i], _work.Providers[j]) = (_work.Providers[j], _work.Providers[i]);
        FillList(); _list.SelectedIndex = j;
    }

    private void FillList()
    {
        _loading = true; _list.Items.Clear();
        foreach (var p in _work.Providers) _list.Items.Add(p.Name + (string.IsNullOrEmpty(p.KeyProtected) && !p.BaseUrl.Contains("localhost") ? "   (no key)" : ""), p.Enabled);
        _loading = false;
    }

    private void CommitCurrent()
    {
        if (_current == null) return;
        _current.Model = _model.Text.Trim(); _current.VisionModel = _visionModel.Text.Trim(); _current.BaseUrl = _baseUrl.Text.Trim(); _current.Kind = (string)(_kind.SelectedItem ?? "OpenAi");
        if (_pendingKey.Length > 0) _current.KeyProtected = ConfigStore.Protect(_pendingKey.Trim());
        _pendingKey = "";
    }

    private void ShowSelected()
    {
        _current = _list.SelectedIndex >= 0 ? _work.Providers[_list.SelectedIndex] : null;
        _loading = true;
        _key.Text = ""; _pendingKey = "";
        if (_current != null)
        {
            _key.PlaceholderText = string.IsNullOrEmpty(_current.KeyProtected) ? "paste key (not needed for local Ollama)" : "saved - leave blank to keep";
            _model.Text = _current.Model; _visionModel.Text = _current.VisionModel; _baseUrl.Text = _current.BaseUrl; _kind.SelectedItem = _current.Kind;
            _link.Enabled = _current.SignupUrl.Length > 0; _link.Text = $"Get a free {_current.Name} API key";
        }
        _loading = false;
    }

    private void LoadValues()
    {
        _engine.SelectedItem = _work.AsrEngine; if (_engine.SelectedIndex < 0) _engine.SelectedIndex = 0;
        _source.SelectedItem = _work.AudioSource.ToString(); _lang.Text = _work.Language;
        _eos.Value = Math.Clamp(_work.EndOfSpeechMs, 150, 2000); _partial.Value = Math.Clamp(_work.PartialEveryMs, 0, 5000);
        _fallback.Checked = _work.FallbackToLocal; _diarize.Checked = _work.Diarize; _hideCap.Checked = _work.HideWindowWhileCapturing;
        _stealth.Checked = _work.ExcludeFromCapture; _noActivate.Checked = _work.NoActivateMode; _keyless.Checked = _work.AllowKeylessCloud;
        _sysLabel.Text = _work.SystemAudioLabel; _whisper.SelectedItem = _work.WhisperModel; if (_whisper.SelectedIndex < 0) _whisper.SelectedIndex = 0;
        _dg.PlaceholderText = string.IsNullOrEmpty(_work.DeepgramKeyProtected) ? "Optional - paste your Deepgram key here" : "Saved - type a new key to replace it";
        _dgLink.LinkClicked += (_, _) => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://console.deepgram.com") { UseShellExecute = true });
        FillList(); if (_list.Items.Count > 0) _list.SelectedIndex = 0;
    }

    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Win32.ApplyCaptureExclusion(Handle, _cfg.ExcludeFromCapture); }

    protected override void Dispose(bool disposing) { if (disposing) _tip.Dispose(); base.Dispose(disposing); }

    /// <summary>At 200% scaling the design size can exceed a laptop screen: never open larger than the working area.</summary>
    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        var wa = Screen.FromControl(this).WorkingArea;
        Size = new Size(Math.Min(Width, wa.Width), Math.Min(Height, wa.Height));
        MinimumSize = new Size(Math.Min(MinimumSize.Width, wa.Width), Math.Min(MinimumSize.Height, wa.Height));
        Location = new Point(Math.Max(wa.X, Math.Min(Left, wa.Right - Width)), Math.Max(wa.Y, Math.Min(Top, wa.Bottom - Height)));
    }

    private void Apply()
    {
        _work.AsrEngine = (string)_engine.SelectedItem!;
        _work.AudioSource = Enum.Parse<AudioSourceKind>((string)_source.SelectedItem!);
        _work.Language = string.IsNullOrWhiteSpace(_lang.Text) ? "en" : _lang.Text.Trim();
        _work.EndOfSpeechMs = (int)_eos.Value; _work.PartialEveryMs = (int)_partial.Value;
        _work.FallbackToLocal = _fallback.Checked; _work.Diarize = _diarize.Checked; _work.HideWindowWhileCapturing = _hideCap.Checked;
        _work.ExcludeFromCapture = _stealth.Checked; _work.NoActivateMode = _noActivate.Checked; _work.AllowKeylessCloud = _keyless.Checked; if (_keyless.Checked) _work.KeylessAsked = true;
        _work.SystemAudioLabel = string.IsNullOrWhiteSpace(_sysLabel.Text) ? "Interviewer" : _sysLabel.Text.Trim(); _work.WhisperModel = (string)_whisper.SelectedItem!;
        if (_dg.Text.Length > 0) _work.DeepgramKeyProtected = ConfigStore.Protect(_dg.Text.Trim());
        // copy the edited working copy back into the live config object
        _cfg.AsrEngine = _work.AsrEngine; _cfg.AudioSource = _work.AudioSource; _cfg.Language = _work.Language;
        _cfg.EndOfSpeechMs = _work.EndOfSpeechMs; _cfg.PartialEveryMs = _work.PartialEveryMs; _cfg.FallbackToLocal = _work.FallbackToLocal;
        _cfg.Diarize = _work.Diarize; _cfg.HideWindowWhileCapturing = _work.HideWindowWhileCapturing;
        _cfg.ExcludeFromCapture = _work.ExcludeFromCapture; _cfg.NoActivateMode = _work.NoActivateMode; _cfg.AllowKeylessCloud = _work.AllowKeylessCloud; _cfg.KeylessAsked = _work.KeylessAsked;
        _cfg.SystemAudioLabel = _work.SystemAudioLabel; _cfg.WhisperModel = _work.WhisperModel; _cfg.DeepgramKeyProtected = _work.DeepgramKeyProtected; _cfg.Providers = _work.Providers;
    }
}
