using System.Diagnostics;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using TransparentNotepad.Config;

namespace TransparentNotepad.UI;

/// <summary>The original multi-tab WebView2 browser, isolated from the main form. Initialised lazily on first show.</summary>
public sealed class BrowserHost : UserControl
{
    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill };
    private readonly TabPage _addTab = new("+");
    private CoreWebView2Environment? _env;
    private bool _started;
    private const string Home = "https://www.google.com";

    public BrowserHost()
    {
        AutoScaleDimensions = new SizeF(96F, 96F); AutoScaleMode = AutoScaleMode.Dpi;
        Dock = DockStyle.Fill;
        _tabs.TabPages.Add(_addTab);
        _tabs.SelectedIndexChanged += (_, _) => { if (_tabs.SelectedTab == _addTab && _env != null) AddTab(Home); };
        Controls.Add(_tabs);
    }

    public async void EnsureStarted()
    {
        if (_started) return;
        _started = true;
        try
        {
            _env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(ConfigStore.Dir, "WebView2Data"));
            AddTab(Home);
        }
        catch (Exception ex)      // WebView2RuntimeNotFoundException and anything else: degrade to an inline message, never crash
        {
            ShowUnavailable(ex is Microsoft.Web.WebView2.Core.WebView2RuntimeNotFoundException
                ? "The Microsoft WebView2 Runtime is not installed, so the built-in browser is unavailable."
                : $"The built-in browser could not start: {ex.Message}");
        }
    }

    private void ShowUnavailable(string message)
    {
        _tabs.Visible = false;
        var link = new LinkLabel { Text = "Download the WebView2 Runtime", AutoSize = true, Margin = new Padding(0, 8, 0, 0) };
        link.LinkClicked += (_, _) => Process.Start(new ProcessStartInfo("https://go.microsoft.com/fwlink/p/?LinkId=2124703") { UseShellExecute = true });
        var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(16), AutoScroll = true };
        panel.Controls.Add(new Label { Text = message, AutoSize = true, MaximumSize = new Size(480, 0) });
        panel.Controls.Add(link);
        Controls.Add(panel);
    }

    private void AddTab(string url)
    {
        var page = new TabPage("New Tab");
        var nav = new Panel { Dock = DockStyle.Top, Height = 38, Padding = new Padding(4) };
        var back = new Button { Text = "◄", Width = 35, Dock = DockStyle.Left };
        var fwd = new Button { Text = "►", Width = 35, Dock = DockStyle.Left };
        var refresh = new Button { Text = "↻", Width = 35, Dock = DockStyle.Left };
        var go = new Button { Text = "Go", Width = 50, Dock = DockStyle.Right };
        var close = new Button { Text = "✕", Width = 35, Dock = DockStyle.Right };
        var txt = new TextBox { Dock = DockStyle.Fill, Font = new Font("Segoe UI", 10F) };
        nav.Controls.AddRange(new Control[] { txt, go, close, refresh, fwd, back });
        var web = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.Transparent };
        page.Controls.Add(web); page.Controls.Add(nav);

        _tabs.TabPages.Insert(Math.Max(0, _tabs.TabPages.Count - 1), page);
        _tabs.SelectedTab = page;
        InitWeb(web, txt, page, url);

        back.Click += (_, _) => { if (web.CanGoBack) web.GoBack(); };
        fwd.Click += (_, _) => { if (web.CanGoForward) web.GoForward(); };
        refresh.Click += (_, _) => web.Reload();
        go.Click += (_, _) => Navigate(web, txt.Text);
        txt.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Navigate(web, txt.Text); } };
        close.Click += (_, _) =>
        {
            web.CoreWebView2?.Stop();
            _tabs.TabPages.Remove(page); web.Dispose();
            if (_tabs.TabPages.Count == 1) AddTab(Home);
        };
    }

    private async void InitWeb(WebView2 web, TextBox txt, TabPage page, string url)
    {
        try
        {
            await web.EnsureCoreWebView2Async(_env);
            web.CoreWebView2.Navigate(url);
            web.SourceChanged += (_, _) => txt.Text = web.Source?.ToString() ?? "";
            web.CoreWebView2.DocumentTitleChanged += (_, _) =>
            {
                var t = web.CoreWebView2.DocumentTitle;
                page.Text = string.IsNullOrWhiteSpace(t) ? "New Tab" : t.Length > 15 ? t[..15] + "..." : t;
            };
        }
        catch (Exception ex) { page.Text = "Unavailable"; page.Controls.Add(new Label { Text = $"Failed to load tab: {ex.Message}", AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(12) }); }
    }

    private static void Navigate(WebView2 web, string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        if (!url.StartsWith("http://") && !url.StartsWith("https://")) url = "https://" + url;
        web.CoreWebView2?.Navigate(url);
    }
}
