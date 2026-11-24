using System.Diagnostics;
using System.Globalization;

namespace REM.Services;

public sealed class ActionLogService
{
    private const long MaxFileSizeBytes = 2 * 1024 * 1024;
    private readonly object _sync = new();
    private readonly string _archiveFilePath;

    public ActionLogService()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var logsDir = Path.Combine(appData, "REM", "Logs");
        Directory.CreateDirectory(logsDir);

        LogFilePath = Path.Combine(logsDir, "actions.log");
        _archiveFilePath = Path.Combine(logsDir, "actions.old.log");

        if (!File.Exists(LogFilePath))
        {
            File.WriteAllText(LogFilePath, string.Empty);
        }
    }

    public string LogFilePath { get; }

    public void Log(string category, string message)
    {
        var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        var sanitized = message.Replace('\r', ' ').Replace('\n', ' ');
        var line = $"[{timestamp}] [{category}] {sanitized}{Environment.NewLine}";

        lock (_sync)
        {
            try
            {
                if (File.Exists(LogFilePath) && new FileInfo(LogFilePath).Length > MaxFileSizeBytes)
                {
                    if (File.Exists(_archiveFilePath))
                    {
                        File.Delete(_archiveFilePath);
                    }

                    File.Move(LogFilePath, _archiveFilePath);
                }

                File.AppendAllText(LogFilePath, line);
            }
            catch
            {
                // Logging must never crash the application.
            }
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            try
            {
                File.WriteAllText(LogFilePath, string.Empty);
            }
            catch
            {
                // Ignore IO failures on clear.
            }
        }

        Log("System", "Action log cleared.");
    }

    public bool TryOpenLogFile(out string? errorMessage)
    {
        errorMessage = null;
        try
        {
            if (!File.Exists(LogFilePath))
            {
                File.WriteAllText(LogFilePath, string.Empty);
            }

            Process.Start(new ProcessStartInfo(LogFilePath)
            {
                UseShellExecute = true
            });
            return true;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            Log("Error", $"Failed to open log file: {ex.Message}");
            return false;
        }
    }
}

