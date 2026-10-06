using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Reflection;

namespace TransparentNotepad
{
    public class AppConfig
    {
        public double Opacity { get; set; } = 1.0;
    }

    public partial class MainForm : Form
    {
        private static readonly string ConfigDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), 
            "TransparentNotepad"
        );
        private static readonly string ConfigPath = Path.Combine(ConfigDir, "config.json");

        private AppConfig appConfig = new AppConfig();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);

        private const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        private const int HOTKEY_ID = 9000;
        private const uint MOD_CONTROL = 0x0002;
        private const uint MOD_SHIFT = 0x0004;
        private const uint VK_H = 0x48;
        private const int WM_HOTKEY = 0x0312;

        private MenuStrip mainMenuStrip = null!;
        private ToolStripMenuItem fileMenu = null!;
        private ToolStripMenuItem opacityMenu = null!;
        private RichTextBox notepadTextBox = null!;
        private ToastLabel toastOverlay = null!;
        private System.Windows.Forms.Timer toastTimer = null!;

        private string? currentFilePath = null;
        private bool isModified = false;

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x80; // WS_EX_TOOLWINDOW: Hides completely from Alt+Tab
                return cp;
            }
        }

        public MainForm()
        {
            LoadConfig();
            InitializeComponent();
            this.ShowInTaskbar = false;
            this.FormBorderStyle = FormBorderStyle.SizableToolWindow;
            this.KeyPreview = true;

            var assembly = Assembly.GetExecutingAssembly();
            using var stream = assembly.GetManifestResourceStream("TransparentNotepad.notepad.ico");
            if (stream != null)
            {
                this.Icon = new Icon(stream);
            }
        }

        private void LoadConfig()
        {
            try
            {
                if (File.Exists(ConfigPath))
                {
                    string json = File.ReadAllText(ConfigPath);
                    var config = JsonSerializer.Deserialize<AppConfig>(json);
                    if (config != null)
                    {
                        appConfig = config;
                    }
                }
            }
            catch
            {
                appConfig = new AppConfig();
            }
        }

        private void SaveConfig()
        {
            try
            {
                Directory.CreateDirectory(ConfigDir);
                string json = JsonSerializer.Serialize(appConfig);
                File.WriteAllText(ConfigPath, json);
            }
            catch { }
        }

        private void InitializeComponent()
        {
            this.notepadTextBox = new RichTextBox();
            this.mainMenuStrip = new MenuStrip();
            this.fileMenu = new ToolStripMenuItem("&File");
            this.opacityMenu = new ToolStripMenuItem("&Opacity");
            this.toastOverlay = new ToastLabel();
            this.toastTimer = new System.Windows.Forms.Timer();

            this.SuspendLayout();

            BuildMenus();

            this.notepadTextBox.Dock = DockStyle.Fill;
            this.notepadTextBox.BorderStyle = BorderStyle.None;
            this.notepadTextBox.BackColor = Color.White;
            this.notepadTextBox.ForeColor = Color.Black;
            this.notepadTextBox.Font = new Font("Consolas", 11.5F, FontStyle.Regular, GraphicsUnit.Point);
            this.notepadTextBox.AcceptsTab = true;
            this.notepadTextBox.Margin = new Padding(0);
            this.notepadTextBox.TextChanged += NotepadTextBox_TextChanged;

            this.notepadTextBox.AllowDrop = true;
            this.notepadTextBox.DragEnter += NotepadTextBox_DragEnter;
            this.notepadTextBox.DragDrop += NotepadTextBox_DragDrop;

            this.toastOverlay.AutoSize = true;
            this.toastOverlay.BackColor = Color.FromArgb(220, 230, 230, 230);
            this.toastOverlay.ForeColor = Color.Black;
            this.toastOverlay.Font = new Font("Segoe UI", 9.5F, FontStyle.Bold, GraphicsUnit.Point);
            this.toastOverlay.Padding = new Padding(12, 6, 12, 6);
            this.toastOverlay.Visible = false;

            this.toastTimer.Interval = 2200;
            this.toastTimer.Tick += (s, e) =>
            {
                this.toastOverlay.Visible = false;
                this.toastTimer.Stop();
            };

            this.Controls.Add(this.toastOverlay);
            this.Controls.Add(this.notepadTextBox);
            this.Controls.Add(this.mainMenuStrip);
            this.MainMenuStrip = this.mainMenuStrip;

            this.notepadTextBox.Resize += (s, e) => PositionToastHUD();

            this.Opacity = Math.Clamp(appConfig.Opacity, 0.15, 1.0);
            this.ClientSize = new Size(820, 520);
            this.Text = "Transparent Notepad - Untitled";
            this.BackColor = Color.White;
            
            this.ResumeLayout(false);
            this.PerformLayout();
        }

        private void BuildMenus()
        {
            this.mainMenuStrip.Renderer = new CustomLightMenuRenderer();
            this.mainMenuStrip.BackColor = Color.FromArgb(245, 245, 245);
            this.mainMenuStrip.ForeColor = Color.Black;

            var newMenuItem = new ToolStripMenuItem("&New", null, (s, e) => CreateNewFile()) { ShortcutKeys = Keys.Control | Keys.N };
            var openMenuItem = new ToolStripMenuItem("&Open...", null, (s, e) => OpenFile()) { ShortcutKeys = Keys.Control | Keys.O };
            var saveMenuItem = new ToolStripMenuItem("&Save", null, (s, e) => SaveFile(false)) { ShortcutKeys = Keys.Control | Keys.S };
            var saveAsMenuItem = new ToolStripMenuItem("Save &As...", null, (s, e) => SaveFile(true)) { ShortcutKeys = Keys.Control | Keys.Shift | Keys.S };
            var exitMenuItem = new ToolStripMenuItem("E&xit", null, (s, e) => Close());

            fileMenu.DropDownItems.AddRange(new ToolStripItem[] {
                newMenuItem, openMenuItem, saveMenuItem, saveAsMenuItem, new ToolStripSeparator(), exitMenuItem
            });

            for (int opacityVal = 100; opacityVal >= 20; opacityVal -= 20)
            {
                double targetOpacity = opacityVal / 100.0;
                var opacityItem = new ToolStripMenuItem($"{opacityVal}%", null, (s, e) => SetOpacity(targetOpacity));
                opacityMenu.DropDownItems.Add(opacityItem);
            }

            mainMenuStrip.Items.Add(fileMenu);
            mainMenuStrip.Items.Add(opacityMenu);
        }

        private void ShowToast(string text)
        {
            this.toastOverlay.Text = text;
            PositionToastHUD();
            this.toastOverlay.BringToFront();
            this.toastOverlay.Visible = true;
            this.toastTimer.Stop();
            this.toastTimer.Start();
        }

        private void PositionToastHUD()
        {
            this.toastOverlay.Location = new Point(
                this.ClientSize.Width - this.toastOverlay.Width - 18,
                this.ClientSize.Height - this.toastOverlay.Height - 18
            );
        }

        private void SetOpacity(double level)
        {
            this.Opacity = Math.Clamp(level, 0.15, 1.0);
            appConfig.Opacity = this.Opacity;
            SaveConfig();
            ShowToast($"Opacity: {Math.Round(this.Opacity * 100)}%");
        }

        private void NotepadTextBox_TextChanged(object? sender, EventArgs e)
        {
            if (!isModified)
            {
                isModified = true;
                UpdateTitleBar();
            }
        }

        private void UpdateTitleBar()
        {
            string fileName = string.IsNullOrEmpty(currentFilePath) ? "Untitled" : Path.GetFileName(currentFilePath);
            string dirtyMarker = isModified ? "*" : "";
            this.Text = $"Transparent Notepad - {fileName}{dirtyMarker}";
        }

        private bool PromptSaveIfModified()
        {
            if (!isModified) return true;

            DialogResult result = MessageBox.Show(
                "Do you want to save changes to this file?",
                "Transparent Notepad",
                MessageBoxButtons.YesNoCancel,
                MessageBoxIcon.Question
            );

            if (result == DialogResult.Yes)
            {
                return SaveFile(false);
            }
            return result != DialogResult.Cancel;
        }

        private void CreateNewFile()
        {
            if (!PromptSaveIfModified()) return;

            notepadTextBox.Clear();
            currentFilePath = null;
            isModified = false;
            UpdateTitleBar();
            ShowToast("New document created");
        }

        private void OpenFile(string? filePath = null)
        {
            if (!PromptSaveIfModified()) return;

            if (filePath == null)
            {
                using OpenFileDialog openDialog = new OpenFileDialog
                {
                    Filter = "Text Files (*.txt)|*.txt|All Files (*.*)|*.*",
                    Title = "Open File"
                };

                if (openDialog.ShowDialog() == DialogResult.OK)
                {
                    filePath = openDialog.FileName;
                }
                else
                {
                    return;
                }
            }

            try
            {
                notepadTextBox.Text = File.ReadAllText(filePath);
                currentFilePath = filePath;
                isModified = false;
                UpdateTitleBar();
                ShowToast($"Opened: {Path.GetFileName(currentFilePath)}");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error opening file:\n{ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private bool SaveFile(bool saveAs)
        {
            if (saveAs || string.IsNullOrEmpty(currentFilePath))
            {
                using SaveFileDialog saveDialog = new SaveFileDialog
                {
                    Filter = "Text Files (*.txt)|*.txt|All Files (*.*)|*.*",
                    DefaultExt = "txt",
                    Title = "Save File"
                };

                if (saveDialog.ShowDialog() != DialogResult.OK)
                {
                    return false;
                }
                currentFilePath = saveDialog.FileName;
            }

            try
            {
                File.WriteAllText(currentFilePath, notepadTextBox.Text);
                isModified = false;
                UpdateTitleBar();
                ShowToast($"Saved: {Path.GetFileName(currentFilePath)}");
                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error saving file:\n{ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
        }

        private void NotepadTextBox_DragEnter(object? sender, DragEventArgs e)
        {
            if (e.Data != null && e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effect = DragDropEffects.Copy;
            }
        }

        private void NotepadTextBox_DragDrop(object? sender, DragEventArgs e)
        {
            if (e.Data != null && e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                string[] files = (string[])e.Data.GetData(DataFormats.FileDrop)!;
                if (files.Length > 0)
                {
                    OpenFile(files[0]);
                }
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            SetWindowDisplayAffinity(this.Handle, WDA_EXCLUDEFROMCAPTURE);

            bool success = RegisterHotKey(this.Handle, HOTKEY_ID, MOD_CONTROL | MOD_SHIFT, VK_H);
            if (!success)
            {
                MessageBox.Show(
                    "Failed to register global hotkey (Ctrl + Shift + H). Another application might be using it.",
                    "Hotkey Warning",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning
                );
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!PromptSaveIfModified())
            {
                e.Cancel = true;
                return;
            }

            UnregisterHotKey(this.Handle, HOTKEY_ID);
            base.OnFormClosing(e);
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            EnsureWindowIsOnValidScreen();
        }

        private void EnsureWindowIsOnValidScreen()
        {
            Rectangle windowRect = this.Bounds;
            bool isVisibleOnAnyScreen = false;

            foreach (Screen screen in Screen.AllScreens)
            {
                if (screen.WorkingArea.IntersectsWith(windowRect))
                {
                    isVisibleOnAnyScreen = true;
                    break;
                }
            }

            if (!isVisibleOnAnyScreen)
            {
                Screen primaryScreen = Screen.PrimaryScreen ?? Screen.AllScreens[0];
                this.StartPosition = FormStartPosition.Manual;
                this.Location = new Point(
                    primaryScreen.WorkingArea.X + (primaryScreen.WorkingArea.Width - this.Width) / 2,
                    primaryScreen.WorkingArea.Y + (primaryScreen.WorkingArea.Height - this.Height) / 2
                );
            }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);

            if (e.KeyCode == Keys.Escape)
            {
                ToggleWindowVisibility();
                e.Handled = true;
            }
            else if (e.Control && e.KeyCode == Keys.Up)
            {
                SetOpacity(this.Opacity + 0.05);
                e.Handled = true;
            }
            else if (e.Control && e.KeyCode == Keys.Down)
            {
                SetOpacity(this.Opacity - 0.05);
                e.Handled = true;
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY && m.WParam.ToInt32() == HOTKEY_ID)
            {
                ToggleWindowVisibility();
            }

            base.WndProc(ref m);
        }

        private void ToggleWindowVisibility()
        {
            if (!this.Visible)
            {
                this.Show();
                this.WindowState = FormWindowState.Normal;
                this.Activate();
            }
            else
            {
                this.Hide();
            }
        }
    }

    internal class CustomLightMenuRenderer : ToolStripProfessionalRenderer
    {
        public CustomLightMenuRenderer() : base(new LightColors()) { }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            Rectangle rc = new Rectangle(Point.Empty, e.Item.Size);
            Color bkColor = e.Item.Selected ? Color.FromArgb(225, 225, 230) : Color.FromArgb(245, 245, 245);
            using SolidBrush brush = new SolidBrush(bkColor);
            e.Graphics.FillRectangle(brush, rc);
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = Color.Black;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            if (e.ToolStrip is ToolStripDropDown)
            {
                using Pen borderPen = new Pen(Color.FromArgb(200, 200, 205));
                e.Graphics.DrawRectangle(borderPen, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
            }
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            using Pen p = new Pen(Color.FromArgb(210, 210, 215));
            int y = e.Item.ContentRectangle.Height / 2;
            e.Graphics.DrawLine(p, 4, y, e.Item.Width - 4, y);
        }
    }

    internal class LightColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Color.FromArgb(245, 245, 245);
        public override Color ImageMarginGradientBegin => Color.FromArgb(245, 245, 245);
        public override Color ImageMarginGradientMiddle => Color.FromArgb(245, 245, 245);
        public override Color ImageMarginGradientEnd => Color.FromArgb(245, 245, 245);
        public override Color MenuBorder => Color.FromArgb(200, 200, 205);
        public override Color MenuItemPressedGradientBegin => Color.FromArgb(230, 230, 235);
        public override Color MenuItemPressedGradientEnd => Color.FromArgb(230, 230, 235);
        public override Color MenuItemSelected => Color.FromArgb(225, 225, 230);
        public override Color MenuItemSelectedGradientBegin => Color.FromArgb(225, 225, 230);
        public override Color MenuItemSelectedGradientEnd => Color.FromArgb(225, 225, 230);
    }

    internal class ToastLabel : Label
    {
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using GraphicsPath path = GetRoundedPath(this.ClientRectangle, 8);
            using SolidBrush brush = new SolidBrush(this.BackColor);
            e.Graphics.FillPath(brush, path);

            TextRenderer.DrawText(
                e.Graphics, 
                this.Text, 
                this.Font, 
                this.ClientRectangle, 
                this.ForeColor, 
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
            );
        }

        private static GraphicsPath GetRoundedPath(Rectangle rect, int radius)
        {
            GraphicsPath path = new GraphicsPath();
            int d = radius * 2;
            path.AddArc(rect.X, rect.Y, d, d, 180, 90);
            path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
            path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
            path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
}