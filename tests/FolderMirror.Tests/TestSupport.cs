using FolderMirror.Core.Engine;
using FolderMirror.Core.Models;

namespace FolderMirror.Tests;

/// <summary>Temporary sandbox: every test works only inside a fresh directory under the system temp folder.</summary>
public sealed class Sandbox : IDisposable
{
    public string Root { get; }
    public string Src { get; }
    public string Dst { get; }
    public SyncProfile Profile { get; }
    public IStateStore State { get; set; } = new MemoryStateStore();
    public IDiskSpaceProvider Space { get; set; } = new SystemDiskSpace();

    static int _n;
    public Sandbox(SyncMode mode = SyncMode.OneWay)
    {
        Root = Path.Combine(Path.GetTempPath(), "FolderMirrorTests", $"{Environment.ProcessId}_{Interlocked.Increment(ref _n)}_{Guid.NewGuid():N}"[..36]);
        Src = Path.Combine(Root, "src");
        Dst = Path.Combine(Root, "dst");
        Directory.CreateDirectory(Src); Directory.CreateDirectory(Dst);
        Profile = new SyncProfile { Name = "test", SourcePath = Src, DestinationPath = Dst, Mode = mode };
    }

    public SyncEngine Engine() => new(State, Serilog.Core.Logger.None, Space);

    public string Write(string side, string rel, string content, DateTime? mtimeUtc = null)
    {
        var path = Path.Combine(side, Rel(rel));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        File.SetLastWriteTimeUtc(path, mtimeUtc ?? DateTime.UtcNow.AddMinutes(-30));
        return path;
    }

    /// <summary>Tests are written with Windows-style relative paths; make them portable.</summary>
    public static string Rel(string rel) => rel.Replace('\\', Path.DirectorySeparatorChar);
    public static string Read(string side, string rel) => File.ReadAllText(Path.Combine(side, Rel(rel)));
    public static bool Exists(string side, string rel) => File.Exists(Path.Combine(side, Rel(rel)));

    public async Task<SyncPlan> Plan(SyncProfile? p = null) =>
        await Engine().PlanAsync(p ?? Profile, 2, null, CancellationToken.None);

    public async Task<SyncResult> Sync(SyncProfile? p = null, ExecuteOptions? o = null, Action<SyncPlan>? tweak = null, CancellationToken ct = default, IProgress<SyncProgress>? progress = null)
    {
        var e = Engine();
        var plan = await e.PlanAsync(p ?? Profile, 2, null, ct);
        tweak?.Invoke(plan);
        o ??= new ExecuteOptions { ConfirmedDeletions = true, MaxParallel = 2, LockedFileRetries = 1, LockedFileRetryDelayMs = 30 };
        return await e.ExecuteAsync(plan, o, progress, ct);
    }

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
                try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
            Directory.Delete(Root, true);
        }
        catch { }
    }
}

public sealed class FakeSpace : IDiskSpaceProvider
{
    readonly long _free;
    public FakeSpace(long free) => _free = free;
    public bool TryGetSpace(string path, out long freeBytes, out long totalBytes) { freeBytes = _free; totalBytes = _free * 2; return true; }
}

public sealed class SyncProgressSink : IProgress<SyncProgress>
{
    readonly Action<SyncProgress> _a;
    public SyncProgressSink(Action<SyncProgress> a) => _a = a;
    public void Report(SyncProgress v) => _a(v);
}
