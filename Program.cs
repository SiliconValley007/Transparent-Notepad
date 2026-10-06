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
            LogException("UI Thread Exception", e.Exception);
            ShowErrorMessage("An unexpected interface error occurred.");
        };

        // Non-UI / Background Thread Exception Handler
        AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
        {
            if (e.ExceptionObject is Exception ex)
            {
                LogException("AppDomain Unhandled Exception", ex);
            }
            ShowErrorMessage("A critical background error occurred.");
        };

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }

    private static void LogException(string source, Exception ex)
    {
        try
        {
            string logMessage = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{source}] {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex.StackTrace}{Environment.NewLine}{new string('-', 50)}{Environment.NewLine}";
            File.AppendAllText(LogFilePath, logMessage);
        }
        catch
        {
            // Fallback if logging fails
        }
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