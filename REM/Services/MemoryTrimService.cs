using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;

namespace REM.Services;

/// <summary>
/// Compacts managed &amp; native heaps and reads accurate Private Working Set (Task Manager) RAM.
/// </summary>
public static class MemoryTrimService
{
    private static CancellationTokenSource? _pendingCts;

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessMemoryCountersEx2
    {
        public uint cb;
        public uint PageFaultCount;
        public UIntPtr PeakWorkingSetSize;
        public UIntPtr WorkingSetSize;
        public UIntPtr QuotaPeakPagedPoolUsage;
        public UIntPtr QuotaPagedPoolUsage;
        public UIntPtr QuotaPeakNonPagedPoolUsage;
        public UIntPtr QuotaNonPagedPoolUsage;
        public UIntPtr PagefileUsage;
        public UIntPtr PeakPagefileUsage;
        public UIntPtr PrivateUsage;
        public UIntPtr PrivateWorkingSetSize;
        public UIntPtr SharedCommitUsage;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessWorkingSetSizeEx(
        IntPtr hProcess,
        IntPtr dwMinimumWorkingSetSize,
        IntPtr dwMaximumWorkingSetSize,
        uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetProcessHeap();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern UIntPtr HeapCompact(IntPtr hHeap, uint dwFlags);

    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessMemoryInfo(
        IntPtr Process,
        out ProcessMemoryCountersEx2 ppsmemCounters,
        uint cb);

    private static readonly (string ProcessName, string Label)[] ChildProcessDescriptors =
    [
        ("soloist", "Soloist"),
        ("msedgewebview2", "WebView2"),
        ("aria2c", "aria2c"),
        ("ffmpeg", "FFmpeg"),
        ("ffprobe", "FFprobe"),
        ("cloudflared", "Cloudflared")
    ];

    public static (double ActiveRamMb, double CommitMb, double ManagedMb) ReadMemoryMetrics(Process process)
    {
        var managedMb = GC.GetTotalMemory(forceFullCollection: false) / (1024.0 * 1024.0);
        var (activeMb, commitMb) = ReadProcessMemoryPair(process, allowRefreshFallback: true);
        return (activeMb, commitMb, managedMb);
    }

    public static (double TotalChildActiveMb, double TotalChildCommitMb, string Breakdown) ReadChildProcessMetrics()
    {
        double totalActive = 0;
        double totalCommit = 0;
        List<string>? parts = null;

        foreach (var (procName, label) in ChildProcessDescriptors)
        {
            Process[] procs;
            try
            {
                procs = Process.GetProcessesByName(procName);
            }
            catch
            {
                continue;
            }

            if (procs.Length == 0)
            {
                continue;
            }

            double groupActive = 0;
            foreach (var proc in procs)
            {
                try
                {
                    if (proc.HasExited)
                    {
                        continue;
                    }

                    var (active, commit) = ReadProcessMemoryPair(proc, allowRefreshFallback: false);
                    groupActive += active;
                    totalActive += active;
                    totalCommit += commit;
                }
                catch
                {
                }
                finally
                {
                    proc.Dispose();
                }
            }

            if (groupActive > 0.05)
            {
                parts ??= new List<string>(4);
                parts.Add($"{label}: {groupActive:F1} MB");
            }
        }

        return (totalActive, totalCommit, parts is { Count: > 0 } ? string.Join(" · ", parts) : string.Empty);
    }

    private static (double ActiveMb, double CommitMb) ReadProcessMemoryPair(Process process, bool allowRefreshFallback)
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                var cb = (uint)Marshal.SizeOf<ProcessMemoryCountersEx2>();
                if (GetProcessMemoryInfo(process.Handle, out var counters, cb))
                {
                    var activeBytes = counters.PrivateWorkingSetSize != UIntPtr.Zero
                        ? counters.PrivateWorkingSetSize.ToUInt64()
                        : counters.WorkingSetSize.ToUInt64();
                    var commitBytes = counters.PrivateUsage != UIntPtr.Zero
                        ? counters.PrivateUsage.ToUInt64()
                        : counters.PagefileUsage.ToUInt64();
                    return (activeBytes / (1024.0 * 1024.0), commitBytes / (1024.0 * 1024.0));
                }
            }
            catch
            {
            }
        }

        if (!allowRefreshFallback)
        {
            return (0, 0);
        }

        process.Refresh();
        return (process.WorkingSet64 / (1024.0 * 1024.0), process.PrivateMemorySize64 / (1024.0 * 1024.0));
    }

    public static void ScheduleTrim(Action? onCompleted = null, int delayMs = 300, bool trimOsWorkingSet = true)
    {
        var cts = new CancellationTokenSource();
        var prev = Interlocked.Exchange(ref _pendingCts, cts);
        if (prev is not null)
        {
            try { prev.Cancel(); } catch (ObjectDisposedException) { }
            finally { prev.Dispose(); }
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delayMs, cts.Token).ConfigureAwait(false);
                if (cts.IsCancellationRequested)
                {
                    return;
                }

                TrimNow(trimOsWorkingSet);
                onCompleted?.Invoke();

                // Second pass after Avalonia/Skia finishes detaching visual trees & GPU staging surfaces.
                await Task.Delay(1200, cts.Token).ConfigureAwait(false);
                if (!cts.IsCancellationRequested)
                {
                    TrimNow(trimOsWorkingSet);
                    onCompleted?.Invoke();
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch
            {
                // Best-effort memory compaction.
            }
            finally
            {
                if (ReferenceEquals(Interlocked.CompareExchange(ref _pendingCts, null, cts), cts))
                {
                    cts.Dispose();
                }
            }
        });
    }

    public static void TrimNow(bool trimOsWorkingSet = true)
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);

        if (OperatingSystem.IsWindows())
        {
            try
            {
                var heap = GetProcessHeap();
                if (heap != IntPtr.Zero)
                {
                    HeapCompact(heap, 0);
                }

                if (trimOsWorkingSet)
                {
                    using var current = Process.GetCurrentProcess();
                    SetProcessWorkingSetSizeEx(current.Handle, new IntPtr(-1), new IntPtr(-1), 0);
                }
            }
            catch
            {
                // Ignore working-set trim refusal.
            }
        }
    }
}
