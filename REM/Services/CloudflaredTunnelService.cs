using System.Diagnostics;
using System.Net.Http;
using System.Text.RegularExpressions;

namespace REM.Services;

/// <summary>
/// Manages lazy download and silent child-process lifecycle of cloudflared.exe
/// for CGNAT-friendly Quick Tunnels (zero router port-forwarding required).
/// </summary>
public sealed partial class CloudflaredTunnelService : IDisposable
{
    private const string LatestCloudflaredUrl =
        "https://github.com/cloudflare/cloudflared/releases/latest/download/cloudflared-windows-amd64.exe";

    private static readonly SemaphoreSlim DownloadGate = new(1, 1);

    [GeneratedRegex(@"https://[a-z0-9-]+\.trycloudflare\.com", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TryCloudflareRegex();

    private readonly ActionLogService _actionLog;
    private Process? _process;
    private bool _disposed;

    public CloudflaredTunnelService(ActionLogService actionLog)
    {
        _actionLog = actionLog;
    }

    public bool IsRunning => _process is { HasExited: false };

    public static string ManagedToolsDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "REM",
        "Tools");

    public static string? ResolveExecutablePath()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Tools", "cloudflared.exe"),
            Path.Combine(AppContext.BaseDirectory, "cloudflared.exe"),
            Path.Combine(ManagedToolsDirectory, "cloudflared.exe")
        };

        foreach (var path in candidates)
        {
            if (File.Exists(path))
            {
                return path;
            }
        }

        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(pathEnv))
        {
            foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    var full = Path.Combine(dir.Trim(), "cloudflared.exe");
                    if (File.Exists(full))
                    {
                        return full;
                    }
                }
                catch
                {
                }
            }
        }

        return null;
    }

    public async Task<string?> EnsureExecutableAsync(
        Action<string>? onStatus,
        CancellationToken cancellationToken)
    {
        var existing = ResolveExecutablePath();
        if (!string.IsNullOrWhiteSpace(existing))
        {
            return existing;
        }

        await DownloadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            existing = ResolveExecutablePath();
            if (!string.IsNullOrWhiteSpace(existing))
            {
                return existing;
            }

            onStatus?.Invoke("Downloading cloudflared.exe (~35 MB, one-time)...");
            Directory.CreateDirectory(ManagedToolsDirectory);
            var targetPath = Path.Combine(ManagedToolsDirectory, "cloudflared.exe");
            var tempPath = targetPath + ".download";

            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }

                using var http = new HttpClient(new BandwidthCountingHandler())
                {
                    Timeout = TimeSpan.FromMinutes(5)
                };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("REM/1.0");

                using var resp = await http.GetAsync(
                    LatestCloudflaredUrl,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken).ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();

                await using (var remoteStream = await resp.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
                await using (var localStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                {
                    await remoteStream.CopyToAsync(localStream, cancellationToken).ConfigureAwait(false);
                }

                var (valid, sha, err) = ToolIntegrityHelper.ValidateExecutableAndHash(tempPath, minBytes: 1_000_000);
                if (!valid)
                {
                    try { File.Delete(tempPath); } catch { }
                    _actionLog.Log("Warning", $"Downloaded cloudflared.exe failed integrity check: {err}");
                    return null;
                }

                File.Move(tempPath, targetPath, overwrite: true);
                _actionLog.Log("WatchParty", $"Downloaded cloudflared.exe to '{targetPath}' (SHA-256: {sha[..16]}...).");
                return targetPath;
            }
            catch (OperationCanceledException)
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
                throw;
            }
            catch (Exception ex)
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
                _actionLog.Log("Warning", $"cloudflared download failed: {ex.Message}");
                return null;
            }
        }
        finally
        {
            DownloadGate.Release();
        }
    }

    public async Task<string> StartTunnelAsync(
        int localPort,
        Action<string>? onStatus,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        Stop();

        var exePath = await EnsureExecutableAsync(onStatus, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(exePath))
        {
            throw new InvalidOperationException("Failed to locate or download cloudflared.exe.");
        }

        onStatus?.Invoke("Opening Cloudflare Quick Tunnel...");

        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        psi.ArgumentList.Add("tunnel");
        psi.ArgumentList.Add("--url");
        psi.ArgumentList.Add($"http://127.0.0.1:{localPort}");
        psi.ArgumentList.Add("--no-autoupdate");

        var process = new Process { StartInfo = psi };
        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException("Failed to launch cloudflared.exe.");
        }

        _process = process;

        var urlTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));

        using var cancelReg = timeoutCts.Token.Register(() =>
            urlTcs.TrySetException(new TimeoutException("Timed out waiting for Cloudflare Quick Tunnel URL.")));

        void InspectLine(string? line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return;
            }

            var match = TryCloudflareRegex().Match(line);
            if (match.Success)
            {
                var httpsUrl = match.Value.TrimEnd('/');
                var wssUrl = "wss://" + httpsUrl["https://".Length..] + "/ws";
                urlTcs.TrySetResult(wssUrl);
            }
        }

        _ = Task.Run(async () =>
        {
            try
            {
                while (!process.StandardError.EndOfStream)
                {
                    InspectLine(await process.StandardError.ReadLineAsync().ConfigureAwait(false));
                }
            }
            catch
            {
            }
        }, CancellationToken.None);

        _ = Task.Run(async () =>
        {
            try
            {
                while (!process.StandardOutput.EndOfStream)
                {
                    InspectLine(await process.StandardOutput.ReadLineAsync().ConfigureAwait(false));
                }
            }
            catch
            {
            }
        }, CancellationToken.None);

        try
        {
            var wssUrl = await urlTcs.Task.ConfigureAwait(false);
            _actionLog.Log("WatchParty", $"Cloudflare tunnel ready: {wssUrl}");
            return wssUrl;
        }
        catch
        {
            Stop();
            throw;
        }
    }

    public void Stop()
    {
        var process = Interlocked.Exchange(ref _process, null);
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }
        finally
        {
            process.Dispose();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
        GC.SuppressFinalize(this);
    }
}
