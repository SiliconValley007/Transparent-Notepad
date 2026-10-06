using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace TransparentNotepad
{
    public partial class MainForm : Form
    {
        // --- Win32 Display Affinity API ---
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);

        private const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;

        // --- Win32 Global Hotkey API ---
        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        private const int HOTKEY_ID = 9000;
        private const uint MOD_CONTROL = 0x0002;
        private const uint MOD_SHIFT = 0x0004;
        private const uint VK_H = 0x48; // 'H' key
        private const int WM_HOTKEY = 0x0312;

        public MainForm()
        {
            InitializeComponent();
            this.KeyPreview = true; // Enables Form-level KeyDown events (e.g. Esc)
        }

        private void InitializeComponent()
        {
            this.ClientSize = new System.Drawing.Size(800, 450);
            this.Text = "Transparent Notepad";
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            
            // Apply screen share protection whenever native handle is created/recreated
            SetWindowDisplayAffinity(this.Handle, WDA_EXCLUDEFROMCAPTURE);

            // Register global hotkey: Ctrl + Shift + H
            RegisterHotKey(this.Handle, HOTKEY_ID, MOD_CONTROL | MOD_SHIFT, VK_H);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // Unregister hotkey on exit
            UnregisterHotKey(this.Handle, HOTKEY_ID);
            base.OnFormClosing(e);
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);

            // Validate window location across all multi-monitor setups
            EnsureWindowIsOnValidScreen();
        }

        /// <summary>
        /// Ensures saved or startup coordinates are visible on current active monitor bounds.
        /// </summary>
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

            // Fallback: If off-screen, center on primary monitor
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

        /// <summary>
        /// Local Esc Key Press Handler to minimize window quickly.
        /// </summary>
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);

            if (e.KeyCode == Keys.Escape)
            {
                ToggleWindowVisibility();
                e.Handled = true;
            }
        }

        /// <summary>
        /// Intercepts Global Hotkey Messages (Ctrl + Shift + H).
        /// </summary>
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
            if (this.WindowState == FormWindowState.Minimized || !this.Visible)
            {
                this.Show();
                this.WindowState = FormWindowState.Normal;
                this.Activate();
            }
            else
            {
                this.WindowState = FormWindowState.Minimized;
            }
        }
    }
}