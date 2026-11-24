using System.Diagnostics;

namespace REM.Services;

public sealed class StartupService
{
    private const string RunKeyPath = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "REM";

    public bool IsEnabled()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        return RunReg(["query", RunKeyPath, "/v", ValueName], out _);
    }

    public bool SetEnabled(bool enable, out string? errorMessage)
    {
        errorMessage = null;
        if (!OperatingSystem.IsWindows())
        {
            errorMessage = "Startup registration is only supported on Windows.";
            return false;
        }

        if (enable)
        {
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exePath))
            {
                errorMessage = "Could not determine REM executable path.";
                return false;
            }

            return RunReg(
                ["add", RunKeyPath, "/v", ValueName, "/t", "REG_SZ", "/d", $"\"{exePath}\"", "/f"],
                out errorMessage);
        }

        if (!IsEnabled())
        {
            return true;
        }

        return RunReg(["delete", RunKeyPath, "/v", ValueName, "/f"], out errorMessage);
    }

    private static bool RunReg(IReadOnlyList<string> args, out string? errorMessage)
    {
        errorMessage = null;
        try
        {
            var psi = new ProcessStartInfo("reg.exe")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };

            foreach (var arg in args)
            {
                psi.ArgumentList.Add(arg);
            }

            using var proc = Process.Start(psi);
            if (proc is null)
            {
                errorMessage = "Could not start reg.exe.";
                return false;
            }

            proc.WaitForExit(3000);
            if (proc.ExitCode == 0)
            {
                return true;
            }

            var err = proc.StandardError.ReadToEnd().Trim();
            errorMessage = string.IsNullOrWhiteSpace(err) ? $"reg.exe exited with code {proc.ExitCode}." : err;
            return false;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            return false;
        }
    }
}

