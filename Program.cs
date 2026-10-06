namespace TransparentNotepad;

static class Program
{
    private static readonly string LogFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TransparentNotepad",
        "error_log.txt"
    );

    [STAThread]
    static void Main()
    {
        // Set unhandled exception mode for WinForms controls
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

        // UI Thread Exception Handler
        Application.ThreadException += (sender, e) =>
        {
            LogAndShowException(e.Exception);
            ShowErrorMessage("An unexpected interface error occurred.");
        };

        // Non-UI / Background Thread Exception Handler
        AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
        {
            if (e.ExceptionObject is Exception ex)
            {
                LogAndShowException(ex);
            }
            ShowErrorMessage("A critical background error occurred.");
        };

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }

    private static void LogAndShowException(Exception ex)
    {
        try
        {
            string logDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TransparentNotepad"
            );
            Directory.CreateDirectory(logDir);
            string logFile = Path.Combine(logDir, "error_log.txt");
            File.AppendAllText(logFile, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\n\n");
        }
        catch { }

        MessageBox.Show(
            $"An unexpected error occurred:\n{ex.Message}\n\nDetails logged to local application data.",
            "Transparent Notepad Error",
            MessageBoxButtons.OK,
            MessageBoxIcon.Error
        );
    }

    private static void ShowErrorMessage(string message)
    {
        MessageBox.Show(
            $"{message}\nDetails have been logged to error_log.txt.",
            "Transparent Notepad Error",
            MessageBoxButtons.OK,
            MessageBoxIcon.Error
        );
    }
}