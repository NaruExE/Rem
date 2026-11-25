using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;

namespace REM.Services;

/// <summary>
/// Compacts managed &amp; native heaps and reads accurate Private Working Set (Task Manager) RAM.
/// </summary>
public static class MemoryTrimService
{
    private const uint Th32csSnapProcess = 0x00000002;
    private static readonly IntPtr InvalidHandleValue = new(-1);
    private static readonly ConcurrentDictionary<uint, byte> TrackedExternalRootPids = new();
    private static CancellationTokenSource? _pendingCts;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32W
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public UIntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

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
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32FirstW(IntPtr hSnapshot, ref ProcessEntry32W lppe);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32NextW(IntPtr hSnapshot, ref ProcessEntry32W lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

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

    private static readonly Dictionary<string, string> LabelByExeName =
        ChildProcessDescriptors.ToDictionary(d => d.ProcessName, d => d.Label, StringComparer.OrdinalIgnoreCase);

    public static void RegisterTrackedRootPid(uint pid)
    {
        if (pid > 0)
        {
            TrackedExternalRootPids[pid] = 0;
        }
    }

    public static void UnregisterTrackedRootPid(uint pid)
    {
        if (pid > 0)
        {
            TrackedExternalRootPids.TryRemove(pid, out _);
        }
    }

    public static (double ActiveRamMb, double CommitMb, double ManagedMb) ReadMemoryMetrics(Process process)
    {
        var managedMb = GC.GetTotalMemory(forceFullCollection: false) / (1024.0 * 1024.0);
        var (activeMb, commitMb) = ReadProcessMemoryPair(process, allowRefreshFallback: true);
        return (activeMb, commitMb, managedMb);
    }

    public static (double TotalChildActiveMb, double TotalChildCommitMb, string Breakdown) ReadChildProcessMetrics()
    {
        if (!OperatingSystem.IsWindows())
        {
            return (0, 0, string.Empty);
        }

        var snapshot = CreateToolhelp32Snapshot(Th32csSnapProcess, 0);
        if (snapshot == IntPtr.Zero || snapshot == InvalidHandleValue)
        {
            return (0, 0, string.Empty);
        }

        var childrenByParent = new Dictionary<uint, List<(uint Pid, string ExeStem)>>();
        var exeStemByPid = new Dictionary<uint, string>();

        try
        {
            var entry = new ProcessEntry32W { dwSize = (uint)Marshal.SizeOf<ProcessEntry32W>() };
            if (Process32FirstW(snapshot, ref entry))
            {
                do
                {
                    var stem = Path.GetFileNameWithoutExtension(entry.szExeFile ?? string.Empty);
                    exeStemByPid[entry.th32ProcessID] = stem;

                    if (!childrenByParent.TryGetValue(entry.th32ParentProcessID, out var list))
                    {
                        list = [];
                        childrenByParent[entry.th32ParentProcessID] = list;
                    }

                    list.Add((entry.th32ProcessID, stem));
                }
                while (Process32NextW(snapshot, ref entry));
            }
        }
        finally
        {
            CloseHandle(snapshot);
        }

        var selfPid = (uint)Environment.ProcessId;
        var visited = new HashSet<uint> { selfPid };
        var queue = new Queue<uint>();
        var ownedHelpers = new List<(uint Pid, string Label)>();

        if (childrenByParent.TryGetValue(selfPid, out var directChildren))
        {
            foreach (var (childPid, _) in directChildren)
            {
                if (visited.Add(childPid))
                {
                    queue.Enqueue(childPid);
                }
            }
        }

        foreach (var rootPid in TrackedExternalRootPids.Keys)
        {
            if (exeStemByPid.TryGetValue(rootPid, out var rootStem) &&
                LabelByExeName.ContainsKey(rootStem) &&
                visited.Add(rootPid))
            {
                queue.Enqueue(rootPid);
            }
        }

        while (queue.Count > 0)
        {
            var currentPid = queue.Dequeue();
            if (exeStemByPid.TryGetValue(currentPid, out var stem) &&
                LabelByExeName.TryGetValue(stem, out var label))
            {
                ownedHelpers.Add((currentPid, label));
            }

            if (childrenByParent.TryGetValue(currentPid, out var subChildren))
            {
                foreach (var (subPid, _) in subChildren)
                {
                    if (visited.Add(subPid))
                    {
                        queue.Enqueue(subPid);
                    }
                }
            }
        }

        if (ownedHelpers.Count == 0)
        {
            return (0, 0, string.Empty);
        }

        double totalActive = 0;
        double totalCommit = 0;
        var activeByLabel = new Dictionary<string, double>(StringComparer.Ordinal);

        foreach (var (pid, label) in ownedHelpers)
        {
            Process? proc = null;
            try
            {
                proc = Process.GetProcessById((int)pid);
                if (proc.HasExited)
                {
                    continue;
                }

                var (active, commit) = ReadProcessMemoryPair(proc, allowRefreshFallback: false);
                totalActive += active;
                totalCommit += commit;
                activeByLabel[label] = activeByLabel.TryGetValue(label, out var prev) ? prev + active : active;
            }
            catch
            {
            }
            finally
            {
                proc?.Dispose();
            }
        }

        List<string>? parts = null;
        foreach (var (_, label) in ChildProcessDescriptors)
        {
            if (activeByLabel.TryGetValue(label, out var groupActive) && groupActive > 0.05)
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
