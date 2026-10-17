using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace TransparentNotepad
{
    static class Program
    {
        private static readonly string LogDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TransparentNotepad"
        );

        private static readonly string LogFilePath = Path.Combine(LogDirectory, "error_log.txt");

        [STAThread]
        static void Main()
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // 1. Set exception mode for WinForms controls
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

            // 2. UI Thread Exception Handler
            Application.ThreadException += (sender, e) =>
            {
                LogAndShowException(e.Exception, "An unexpected interface error occurred.");
            };

            // 3. Non-UI / Background Thread Exception Handler
            AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
            {
                if (e.ExceptionObject is Exception ex)
                {
                    LogAndShowException(ex, "A fatal background error occurred.");
                }
            };

            TaskScheduler.UnobservedTaskException += (sender, e) => { try { Directory.CreateDirectory(LogDirectory); File.AppendAllText(LogFilePath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] (unobserved task) {e.Exception}\n--------------------------------------------------\n"); } catch { } e.SetObserved(); };

            TransparentNotepad.Logging.Log.Info("app.start", ("version", typeof(MainForm).Assembly.GetName().Version), ("os", Environment.OSVersion.VersionString));
            Application.Run(new MainForm());
            TransparentNotepad.Logging.Log.Info("app.exit");
        }

        private static void LogAndShowException(Exception ex, string userMessage)
        {
            TransparentNotepad.Logging.Log.Error("app.unhandled_exception", ex, ("message", userMessage));
            try
            {
                // Ensure directory exists
                if (!Directory.Exists(LogDirectory))
                {
                    Directory.CreateDirectory(LogDirectory);
                }

                // Append error details to log file
                string logContent = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\n" +
                                   $"--------------------------------------------------\n";
                File.AppendAllText(LogFilePath, logContent);
            }
            catch
            {
                // Fail silently if writing to log fails
            }

            // Show friendly message to user instead of crashing silently
            MessageBox.Show(
                $"{userMessage}\n\nError Details: {ex.Message}\n\nLog saved to:\n{LogFilePath}",
                "Transparent Notepad Error",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error
            );
        }
    }
}