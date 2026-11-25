using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using REM.Models;

namespace REM.Services;

/// <summary>
/// Lazy-loaded JSON-RPC 2.0 client for aria2c.exe using plain HttpClient.
/// Never starts aria2c.exe until a direct/magnet download is actually queued.
/// </summary>
public sealed class Aria2RpcService : IDisposable
{
    private const int RpcPort = 6800;
    private const string Aria2WindowsZipUrl =
        "https://github.com/aria2/aria2/releases/download/release-1.37.0/aria2-1.37.0-win-64bit-build1.zip";
    private static readonly Uri RpcEndpoint = new($"http://127.0.0.1:{RpcPort}/jsonrpc");
    private static readonly SemaphoreSlim Aria2DownloadGate = new(1, 1);

    private readonly ActionLogService _actionLog;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);

    private Process? _process;
    private string _rpcSecret = string.Empty;
    private string? _activeDir;
    private bool _disposed;

    public Aria2RpcService(ActionLogService actionLog)
    {
        _actionLog = actionLog;
        _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(10)
        };
    }

    public bool IsRunning => _process is { HasExited: false };

    public static string ManagedToolsDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "REM",
        "Tools");

    public static string? ResolveExecutablePath(string? configuredPath = null)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            var custom = configuredPath.Trim().Trim('"');
            if (File.Exists(custom))
            {
                return custom;
            }
        }

        var localTools = Path.Combine(ManagedToolsDirectory, "aria2c.exe");

        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Tools", "aria2c.exe"),
            Path.Combine(AppContext.BaseDirectory, "aria2c.exe"),
            localTools
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
                    var full = Path.Combine(dir.Trim(), "aria2c.exe");
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

    public async Task<string?> EnsureExecutableAsync(string? configuredPath, CancellationToken cancellationToken)
    {
        var existing = ResolveExecutablePath(configuredPath);
        if (!string.IsNullOrWhiteSpace(existing))
        {
            return existing;
        }

        await Aria2DownloadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            existing = ResolveExecutablePath(configuredPath);
            if (!string.IsNullOrWhiteSpace(existing))
            {
                return existing;
            }

            Directory.CreateDirectory(ManagedToolsDirectory);
            var tempZip = Path.Combine(ManagedToolsDirectory, "aria2.zip.download");
            try
            {
                if (File.Exists(tempZip))
                {
                    File.Delete(tempZip);
                }

                using var dlHttp = new HttpClient(new BandwidthCountingHandler())
                {
                    Timeout = TimeSpan.FromMinutes(3)
                };
                dlHttp.DefaultRequestHeaders.UserAgent.ParseAdd("REM/1.0");

                using var resp = await dlHttp.GetAsync(
                    Aria2WindowsZipUrl,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken).ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();

                await using (var remoteStream = await resp.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
                await using (var localStream = new FileStream(tempZip, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                {
                    await remoteStream.CopyToAsync(localStream, cancellationToken).ConfigureAwait(false);
                }

                var targetPath = Path.Combine(ManagedToolsDirectory, "aria2c.exe");
                await Task.Run(() =>
                {
                    using var archive = ZipFile.OpenRead(tempZip);
                    foreach (var entry in archive.Entries)
                    {
                        if (!string.Equals(Path.GetFileName(entry.FullName), "aria2c.exe", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        var stagePath = targetPath + ".tmp";
                        entry.ExtractToFile(stagePath, overwrite: true);
                        var (valid, sha, err) = ToolIntegrityHelper.ValidateExecutableAndHash(stagePath);
                        if (!valid)
                        {
                            try { File.Delete(stagePath); } catch { }
                            throw new InvalidOperationException($"Extracted aria2c.exe failed integrity check: {err}");
                        }

                        File.Move(stagePath, targetPath, overwrite: true);
                        _actionLog.Log("MediaGrabber", $"Verified aria2c.exe SHA-256: {sha[..16]}...");
                        break;
                    }
                }, cancellationToken).ConfigureAwait(false);

                if (File.Exists(targetPath))
                {
                    _actionLog.Log("MediaGrabber", $"Downloaded aria2c.exe to '{targetPath}'.");
                    return targetPath;
                }

                return null;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _actionLog.Log("Warning", $"Automatic aria2c download failed: {ex.Message}");
                return null;
            }
            finally
            {
                try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
            }
        }
        finally
        {
            Aria2DownloadGate.Release();
        }
    }

    public async Task EnsureStartedAsync(MediaGrabberSettings settings, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_process is { HasExited: false })
            {
                return;
            }

            var exePath = await EnsureExecutableAsync(settings.Aria2cPath, cancellationToken).ConfigureAwait(false)
                ?? throw new FileNotFoundException(
                    "aria2c.exe could not be found or downloaded. Configure Aria2cPath in Media Grabber Settings.");

            var dir = string.IsNullOrWhiteSpace(settings.DefaultDownloadFolder)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "REM")
                : settings.DefaultDownloadFolder.Trim();
            Directory.CreateDirectory(dir);

            var connections = Math.Clamp(settings.Aria2Connections, 1, 16);
            _rpcSecret = Guid.NewGuid().ToString("N");
            _activeDir = dir;

            var psi = new ProcessStartInfo(exePath)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = false,
                RedirectStandardError = false
            };
            psi.ArgumentList.Add("--enable-rpc");
            psi.ArgumentList.Add($"--rpc-listen-port={RpcPort}");
            psi.ArgumentList.Add($"--rpc-secret={_rpcSecret}");
            psi.ArgumentList.Add($"--dir={dir}");
            psi.ArgumentList.Add("--continue=true");
            psi.ArgumentList.Add($"--max-connection-per-server={connections}");
            psi.ArgumentList.Add($"--split={connections}");
            psi.ArgumentList.Add("--min-split-size=1M");
            psi.ArgumentList.Add("--file-allocation=none");
            psi.ArgumentList.Add("--summary-interval=0");

            _process = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to launch aria2c.exe RPC daemon.");

            for (var attempt = 0; attempt < 15; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await CallRpcAsync("aria2.getVersion", [], cancellationToken).ConfigureAwait(false);
                    _actionLog.Log("MediaGrabber", $"Started aria2c RPC daemon (port {RpcPort}).");
                    return;
                }
                catch
                {
                    if (_process.HasExited)
                    {
                        throw new InvalidOperationException($"aria2c.exe exited immediately with code {_process.ExitCode}.");
                    }

                    await Task.Delay(120, cancellationToken).ConfigureAwait(false);
                }
            }

            throw new TimeoutException("Timed out waiting for aria2c JSON-RPC server to respond.");
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task<string> AddUriAsync(
        string url,
        MediaGrabberSettings settings,
        CancellationToken cancellationToken)
    {
        await EnsureStartedAsync(settings, cancellationToken).ConfigureAwait(false);

        var dir = string.IsNullOrWhiteSpace(settings.DefaultDownloadFolder)
            ? _activeDir ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "REM")
            : settings.DefaultDownloadFolder.Trim();
        Directory.CreateDirectory(dir);

        var connections = Math.Clamp(settings.Aria2Connections, 1, 16);
        var options = new Dictionary<string, string>
        {
            ["dir"] = dir,
            ["max-connection-per-server"] = connections.ToString(CultureInfo.InvariantCulture),
            ["split"] = connections.ToString(CultureInfo.InvariantCulture)
        };

        using var doc = await CallRpcAsync(
            "aria2.addUri",
            [new[] { url }, options],
            cancellationToken).ConfigureAwait(false);

        return doc.RootElement.GetProperty("result").GetString()
            ?? throw new InvalidOperationException("aria2c returned an empty GID.");
    }

    public async Task PauseAsync(string gid, CancellationToken cancellationToken)
    {
        if (!IsRunning) return;
        await CallRpcAsync("aria2.pause", [gid], cancellationToken).ConfigureAwait(false);
    }

    public async Task UnpauseAsync(string gid, CancellationToken cancellationToken)
    {
        if (!IsRunning) return;
        await CallRpcAsync("aria2.unpause", [gid], cancellationToken).ConfigureAwait(false);
    }

    public async Task RemoveAsync(string gid, CancellationToken cancellationToken)
    {
        if (!IsRunning) return;
        try { await CallRpcAsync("aria2.forceRemove", [gid], cancellationToken).ConfigureAwait(false); } catch { }
        try { await CallRpcAsync("aria2.removeDownloadResult", [gid], cancellationToken).ConfigureAwait(false); } catch { }
    }

    public async Task<List<Aria2StatusSnapshot>> TellActiveAsync(CancellationToken cancellationToken) =>
        await TellListAsync("aria2.tellActive", [], cancellationToken).ConfigureAwait(false);

    public async Task<List<Aria2StatusSnapshot>> TellWaitingAsync(int offset, int num, CancellationToken cancellationToken) =>
        await TellListAsync("aria2.tellWaiting", [offset, num], cancellationToken).ConfigureAwait(false);

    public async Task<List<Aria2StatusSnapshot>> TellStoppedAsync(int offset, int num, CancellationToken cancellationToken) =>
        await TellListAsync("aria2.tellStopped", [offset, num], cancellationToken).ConfigureAwait(false);

    public async Task ShutdownAsync()
    {
        if (_disposed) return;

        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_process is not null)
            {
                if (!_process.HasExited)
                {
                    try
                    {
                        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(900));
                        await CallRpcAsync("aria2.shutdown", [], cts.Token).ConfigureAwait(false);
                        await _process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
                    }
                    catch
                    {
                    }
                }

                try
                {
                    if (!_process.HasExited)
                    {
                        _process.Kill(entireProcessTree: true);
                    }
                }
                catch
                {
                }

                _process.Dispose();
                _process = null;
            }

            _rpcSecret = string.Empty;
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { ShutdownAsync().GetAwaiter().GetResult(); } catch { }
        _lifecycleGate.Dispose();
        _http.Dispose();
    }

    private async Task<List<Aria2StatusSnapshot>> TellListAsync(
        string method,
        object[] extraParams,
        CancellationToken cancellationToken)
    {
        if (!IsRunning)
        {
            return [];
        }

        string[] keys = ["gid", "status", "totalLength", "completedLength", "downloadSpeed", "files", "errorMessage"];
        object[] args = [.. extraParams, keys];
        using var doc = await CallRpcAsync(method, args, cancellationToken).ConfigureAwait(false);

        var list = new List<Aria2StatusSnapshot>();
        if (!doc.RootElement.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Array)
        {
            return list;
        }

        foreach (var item in result.EnumerateArray())
        {
            var gid = item.TryGetProperty("gid", out var gEl) ? gEl.GetString() ?? "" : "";
            var status = item.TryGetProperty("status", out var sEl) ? sEl.GetString() ?? "" : "";
            var totalLen = ParseLong(item, "totalLength");
            var compLen = ParseLong(item, "completedLength");
            var dlSpeed = ParseLong(item, "downloadSpeed");
            var errMsg = item.TryGetProperty("errorMessage", out var eEl) ? eEl.GetString() : null;

            string? filePath = null;
            if (item.TryGetProperty("files", out var filesEl) &&
                filesEl.ValueKind == JsonValueKind.Array &&
                filesEl.GetArrayLength() > 0 &&
                filesEl[0].TryGetProperty("path", out var pEl))
            {
                filePath = pEl.GetString();
            }

            if (!string.IsNullOrWhiteSpace(gid))
            {
                list.Add(new Aria2StatusSnapshot(gid, status, totalLen, compLen, dlSpeed, filePath, errMsg));
            }
        }

        return list;
    }

    private static long ParseLong(JsonElement el, string propName)
    {
        if (!el.TryGetProperty(propName, out var prop)) return 0;
        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt64(out var n)) return n;
        if (prop.ValueKind == JsonValueKind.String && long.TryParse(prop.GetString(), out var s)) return s;
        return 0;
    }

    private async Task<JsonDocument> CallRpcAsync(
        string method,
        object[] methodParams,
        CancellationToken cancellationToken)
    {
        var fullParams = new object[methodParams.Length + 1];
        fullParams[0] = $"token:{_rpcSecret}";
        Array.Copy(methodParams, 0, fullParams, 1, methodParams.Length);

        var payload = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = "rem",
            method,
            @params = fullParams
        });

        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var resp = await _http.PostAsync(RpcEndpoint, content, cancellationToken).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"aria2 RPC '{method}' failed ({(int)resp.StatusCode}): {body}");
        }

        var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("error", out var errEl))
        {
            var msg = errEl.TryGetProperty("message", out var mEl) ? mEl.GetString() : errEl.ToString();
            doc.Dispose();
            throw new InvalidOperationException($"aria2 RPC '{method}' error: {msg}");
        }

        return doc;
    }
}
