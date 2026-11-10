using System.Collections.Concurrent;
using System.Diagnostics;
using FolderMirror.Core.Models;
using Serilog;

namespace FolderMirror.Core.Engine;

/// <summary>Remembers which profiles have synchronized a destination (used for the "unrelated files" warning).</summary>
public static class MarkerStore
{
    static string FilePath(string root) => PathTools.ReservedPath(root, "mirror.id");

    public static HashSet<Guid>? Read(string root)
    {
        try
        {
            var f = FilePath(root);
            if (!File.Exists(f)) return null;
            return File.ReadAllLines(f).Select(l => Guid.TryParse(l.Trim(), out var g) ? g : Guid.Empty).Where(g => g != Guid.Empty).ToHashSet();
        }
        catch { return null; }
    }

    public static void Add(string root, Guid id)
    {
        try
        {
            var set = Read(root) ?? new HashSet<Guid>();
            if (!set.Add(id)) return;
            Directory.CreateDirectory(PathTools.ReservedPath(root));
            File.WriteAllLines(FilePath(root), set.Select(g => g.ToString()));
        }
        catch { /* marker is advisory only */ }
    }
}

public sealed class SyncEngine
{
    readonly IStateStore _state;
    readonly ILogger _log;
    readonly IDiskSpaceProvider _space;

    public SyncEngine(IStateStore state, ILogger? log = null, IDiskSpaceProvider? space = null)
    {
        _state = state;
        _log = log ?? Serilog.Core.Logger.None;
        _space = space ?? new SystemDiskSpace();
    }

    public Task<SyncPlan> PlanAsync(SyncProfile profile, int maxParallel, Action<string>? status, CancellationToken ct) =>
        Task.Run(() => new Planner(_state, _space, _log).Build(profile, ct, maxParallel, status), ct);

    sealed class Tracker
    {
        readonly Action<SyncProgress>? _report;
        readonly Stopwatch _sw = Stopwatch.StartNew();
        public long BytesTotal; public int FilesTotal;
        long _bytes; int _files;
        long _lastReportMs; long _lastSampleMs; long _lastSampleBytes; double _ema;
        volatile string _current = "";
        public string Phase = "Synchronizing";

        public Tracker(Action<SyncProgress>? report) => _report = report;
        public long Bytes => Interlocked.Read(ref _bytes);

        public void AddBytes(long n) { Interlocked.Add(ref _bytes, n); Tick(); }
        public void FileDone() { Interlocked.Increment(ref _files); Tick(true); }
        public void Current(string f) { _current = f; Tick(true); }

        void Tick(bool force = false)
        {
            if (_report == null) return;
            var now = _sw.ElapsedMilliseconds;
            var last = Interlocked.Read(ref _lastReportMs);
            if (!force && now - last < 120) return;
            if (Interlocked.CompareExchange(ref _lastReportMs, now, last) != last && !force) return;
            lock (this)
            {
                var dt = now - _lastSampleMs;
                if (dt >= 400)
                {
                    var b = Bytes;
                    var inst = (b - _lastSampleBytes) * 1000.0 / dt;
                    _ema = _ema <= 0 ? inst : 0.7 * _ema + 0.3 * inst;
                    _lastSampleMs = now; _lastSampleBytes = b;
                }
                var done = Bytes;
                TimeSpan? rem = _ema > 1 && BytesTotal > done ? TimeSpan.FromSeconds((BytesTotal - done) / _ema) : null;
                _report(new SyncProgress
                {
                    Phase = Phase, CurrentFile = _current, FilesDone = _files, FilesTotal = FilesTotal,
                    BytesDone = done, BytesTotal = BytesTotal, BytesPerSecond = _ema, Remaining = rem
                });
            }
        }
    }

    sealed record Op(PlanItem Item, string Kind);

    public async Task<SyncResult> ExecuteAsync(SyncPlan plan, ExecuteOptions options, IProgress<SyncProgress>? progress, CancellationToken ct)
    {
        var p = plan.Profile;
        var res = new SyncResult { StartUtc = DateTime.UtcNow, Status = SyncStatus.Running };
        var srcRoot = PathTools.Normalize(p.SourcePath);
        var dstRoot = PathTools.Normalize(p.DestinationPath);
        bool twoWay = p.Mode == SyncMode.TwoWay;

        // Re-validate against the live system: drives may have disappeared since the preview.
        var live = SafetyChecker.CheckPaths(p, _space).Where(i => i.Severity == IssueSeverity.Error).ToList();
        live.AddRange(plan.Issues.Where(i => i.Severity == IssueSeverity.Error && live.All(l => l.Code != i.Code)));
        if (live.Count > 0) throw new SyncBlockedException(string.Join(" ", live.Select(i => i.Message)), live);

        // Deletions need explicit confirmation (or an explicit unattended opt-in).
        bool deletesAllowed = true;
        var delItems = plan.Items.Where(i => i.Action == PlanAction.Delete).ToList();
        int delFiles = delItems.Count(i => !i.IsDirectory);
        if (delItems.Count > 0)
        {
            if (options.Unattended)
            {
                bool breaker = delFiles > 10 && delFiles > Math.Max(plan.DestinationFileCount, plan.SourceFileCount) / 2;
                bool sourceEmpty = plan.Issues.Any(i => i.Code == "SourceEmpty");
                deletesAllowed = p.AllowUnattendedDeletions && !breaker && !sourceEmpty;
                if (!deletesAllowed)
                    res.Messages.Add(breaker || sourceEmpty
                        ? $"Safety stop: {delFiles} deletion(s) skipped because they look unusually large or the source is empty."
                        : $"{delFiles} deletion(s) skipped: unattended deletions are not enabled for this profile.");
            }
            else if (!options.ConfirmedDeletions)
                throw new InvalidOperationException("This synchronization deletes files. Explicit confirmation is required (ExecuteOptions.ConfirmedDeletions).");
        }

        // Build operation list.
        var ops = new List<Op>();
        var dirCreates = new List<PlanItem>();
        foreach (var it in plan.Items)
        {
            switch (it.Action)
            {
                case PlanAction.New or PlanAction.Modified when it.IsDirectory: dirCreates.Add(it); break;
                case PlanAction.New or PlanAction.Modified: ops.Add(new Op(it, "copy")); break;
                case PlanAction.Conflict when it.Resolution != ConflictResolution.Skip: ops.Add(new Op(it, "conflict")); break;
                case PlanAction.Conflict: res.ConflictsLeft++; break;
                case PlanAction.Unchanged: res.FilesUnchanged++; break;
            }
        }
        var deleteOps = deletesAllowed ? delItems.Where(i => !i.IsDirectory).Select(i => new Op(i, "delete")).ToList() : new List<Op>();

        // Space check (live)
        long needDst = 0, needSrc = 0;
        foreach (var o in ops.Where(o => o.Kind == "copy"))
            if (o.Item.Direction == CopyDirection.SourceToDestination) needDst += o.Item.Size; else needSrc += o.Item.Size;
        if (_space.TryGetSpace(dstRoot, out var free, out _) && needDst > free)
            throw new SyncBlockedException($"Not enough free space on the destination: {Format.Bytes(needDst)} needed, {Format.Bytes(free)} available.");
        if (twoWay && _space.TryGetSpace(srcRoot, out var sfree, out _) && needSrc > sfree)
            throw new SyncBlockedException($"Not enough free space on the source drive: {Format.Bytes(needSrc)} needed, {Format.Bytes(sfree)} available.");

        var stamp = res.StartUtc.ToLocalTime().ToString("yyyy-MM-dd_HHmmss");
        var tracker = new Tracker(progress == null ? null : progress.Report)
        {
            BytesTotal = ops.Where(o => o.Kind != "delete").Sum(o => o.Item.Size),
            FilesTotal = ops.Count + deleteOps.Count
        };
        var copyOpts = new CopyOptions
        {
            PreserveTimestamps = p.PreserveTimestamps || twoWay,
            PreserveAttributes = p.PreserveAttributes,
            Verify = p.VerifyAfterCopy,
            LockedFileRetries = options.LockedFileRetries,
            LockedFileRetryDelayMs = options.LockedFileRetryDelayMs
        };

        var upserts = new ConcurrentDictionary<string, FileState>(StringComparer.OrdinalIgnoreCase);
        var removals = new ConcurrentBag<string>();
        using var abort = CancellationTokenSource.CreateLinkedTokenSource(ct);
        string? criticalMessage = null;

        void AddError(string path, Exception ex)
        {
            var msg = ErrorTranslator.Describe(ex);
            lock (res) { res.Errors.Add(new SyncError(path, msg, ex.GetType().Name + ": " + ex.Message)); }
            _log.Warning(ex, "Error on {Path}: {Message}", path, msg);
        }
        void AddOp(string action, string rel, long size, string status, string msg = "")
        { lock (res) res.Operations.Add(new OpRecord(action, rel, size, status, msg)); }

        FileState Snapshot(string rel)
        {
            var s = new FileInfo(Path.Combine(srcRoot, rel)); var d = new FileInfo(Path.Combine(dstRoot, rel));
            return new FileState(s.Length, s.LastWriteTimeUtc.Ticks, d.Length, d.LastWriteTimeUtc.Ticks);
        }

        async Task CopyFile(string rel, CopyDirection dir, bool backup)
        {
            var (fromRoot, toRoot) = dir == CopyDirection.SourceToDestination ? (srcRoot, dstRoot) : (dstRoot, srcRoot);
            var from = Path.Combine(fromRoot, rel); var to = Path.Combine(toRoot, rel);
            tracker.Current(rel);
            Action<string>? before = backup ? existing =>
            {
                var target = PathTools.ReservedPath(toRoot, "Versions", stamp, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                target = PathTools.Unique(target);
                File.SetAttributes(existing, FileAttributes.Normal);
                File.Move(existing, target);
            } : null;
            await FileCopier.CopyAsync(from, to, copyOpts, tracker.AddBytes, before, abort.Token).ConfigureAwait(false);
            if (twoWay) upserts[rel] = Snapshot(rel);
        }

        void DeleteFile(string rel, CopyDirection side)
        {
            var root = side == CopyDirection.SourceToDestination ? dstRoot : srcRoot;
            var path = Path.Combine(root, rel);
            tracker.Current(rel);
            if (!File.Exists(path)) return;
            File.SetAttributes(path, FileAttributes.Normal);
            if (p.DeletionBehavior == DeletionBehavior.MoveToArchive)
            {
                var target = PathTools.ReservedPath(root, "Archive", stamp, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Move(path, PathTools.Unique(target));
            }
            else File.Delete(path);
            if (twoWay) removals.Add(rel);
        }

        async Task RunOp(Op op)
        {
            var it = op.Item;
            abort.Token.ThrowIfCancellationRequested();
            try
            {
                switch (op.Kind)
                {
                    case "copy":
                    {
                        bool backup = p.BackupOverwrittenFiles || p.Mode == SyncMode.Incremental;
                        await CopyFile(it.RelPath, it.Direction, backup && it.Action == PlanAction.Modified).ConfigureAwait(false);
                        lock (res) { if (it.Action == PlanAction.New) res.FilesCopied++; else res.FilesModified++; res.BytesTransferred += it.Size; }
                        AddOp(it.Action == PlanAction.New ? "New" : "Modified", it.RelPath, it.Size, "OK");
                        break;
                    }
                    case "delete":
                        DeleteFile(it.RelPath, it.Direction);
                        lock (res) res.FilesDeleted++;
                        AddOp("Deleted", it.RelPath, it.Size, "OK");
                        break;
                    case "conflict":
                        await ResolveConflict(it).ConfigureAwait(false);
                        break;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                AddError(it.RelPath, ex);
                AddOp(it.ActionText, it.RelPath, it.Size, "Error", ErrorTranslator.Describe(ex));
                if (ErrorTranslator.IsDiskFull(ex))
                {
                    criticalMessage = "The destination drive is full. Synchronization was stopped.";
                    abort.Cancel();
                }
                else if (ErrorTranslator.IsNetworkLost(ex) || ex is DirectoryNotFoundException)
                {
                    if (!PathTools.IsRootAvailable(dstRoot) || !Directory.Exists(dstRoot) || !Directory.Exists(srcRoot))
                    {
                        criticalMessage = "The source or destination became unavailable (disconnected drive or lost network connection). Synchronization was stopped.";
                        abort.Cancel();
                    }
                }
            }
            finally { tracker.FileDone(); }
        }

        async Task ResolveConflict(PlanItem it)
        {
            if (it.SourceEntry?.IsDirectory != it.DestinationEntry?.IsDirectory && it.SourceEntry != null && it.DestinationEntry != null)
            {
                AddOp("Conflict", it.RelPath, 0, "Skipped", "A file and a folder share the same name; resolve this manually.");
                lock (res) res.ConflictsLeft++;
                return;
            }
            bool sExists = it.SourceEntry != null, dExists = it.DestinationEntry != null;
            switch (it.Resolution)
            {
                case ConflictResolution.UseSource:
                    if (sExists) { await CopyFile(it.RelPath, CopyDirection.SourceToDestination, dExists).ConfigureAwait(false); Count(it, true); }
                    else { DeleteFile(it.RelPath, CopyDirection.SourceToDestination); lock (res) res.FilesDeleted++; AddOp("Deleted", it.RelPath, it.Size, "OK", "Conflict: source deletion kept"); }
                    break;
                case ConflictResolution.UseDestination:
                    if (dExists) { await CopyFile(it.RelPath, CopyDirection.DestinationToSource, sExists).ConfigureAwait(false); Count(it, true); }
                    else { DeleteFile(it.RelPath, CopyDirection.DestinationToSource); lock (res) res.FilesDeleted++; AddOp("Deleted", it.RelPath, it.Size, "OK", "Conflict: destination deletion kept"); }
                    break;
                case ConflictResolution.KeepBoth:
                    if (sExists && dExists)
                    {
                        var conflictRel = PathTools.AddSuffixBeforeExtension(it.RelPath,
                            $" (conflict copy {DateTime.Now:yyyyMMdd-HHmmss})");
                        // 1. keep the destination version: copy to source side, move aside on destination
                        await FileCopier.CopyAsync(Path.Combine(dstRoot, it.RelPath), Path.Combine(srcRoot, conflictRel), copyOpts, tracker.AddBytes, null, abort.Token).ConfigureAwait(false);
                        var movedAside = Path.Combine(dstRoot, conflictRel);
                        Directory.CreateDirectory(Path.GetDirectoryName(movedAside)!);
                        File.SetAttributes(Path.Combine(dstRoot, it.RelPath), FileAttributes.Normal);
                        File.Move(Path.Combine(dstRoot, it.RelPath), movedAside, overwrite: true);
                        // 2. source version becomes the shared name
                        await CopyFile(it.RelPath, CopyDirection.SourceToDestination, false).ConfigureAwait(false);
                        Count(it, true, "Both versions kept (conflict copy: " + conflictRel + ")");
                    }
                    else
                    {   // modified-vs-deleted: restore the surviving version
                        var dir = sExists ? CopyDirection.SourceToDestination : CopyDirection.DestinationToSource;
                        await CopyFile(it.RelPath, dir, false).ConfigureAwait(false);
                        Count(it, true, "Restored");
                    }
                    break;
            }
        }

        void Count(PlanItem it, bool copied, string msg = "")
        {
            lock (res) { res.FilesModified++; res.BytesTransferred += it.Size; }
            AddOp("Conflict", it.RelPath, it.Size, "Resolved", msg.Length > 0 ? msg : it.Resolution.ToString());
        }

        try
        {
            _log.Information("Starting {Mode} '{Profile}' {Src} → {Dst}: {Ops} operation(s)", p.Mode, p.Name, srcRoot, dstRoot, ops.Count + deleteOps.Count);
            Directory.CreateDirectory(dstRoot);

            foreach (var f in plan.StaleTempFiles) { try { File.SetAttributes(f, FileAttributes.Normal); File.Delete(f); } catch { } }

            // Folders first
            foreach (var d in dirCreates.OrderBy(d => d.RelPath.Count(c => c == Path.DirectorySeparatorChar)))
            {
                try
                {
                    var target = d.Direction == CopyDirection.SourceToDestination ? d.DestinationPath : d.SourcePath;
                    Directory.CreateDirectory(target);
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { AddError(d.RelPath, ex); }
            }

            progress?.Report(new SyncProgress { Phase = "Copying", FilesTotal = tracker.FilesTotal, BytesTotal = tracker.BytesTotal });
            tracker.Phase = "Copying";
            await Parallel.ForEachAsync(ops, new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(1, options.MaxParallel),
                CancellationToken = abort.Token
            }, async (op, _) => await RunOp(op).ConfigureAwait(false)).ConfigureAwait(false);

            tracker.Phase = "Cleaning up";
            foreach (var op in deleteOps)
            {
                abort.Token.ThrowIfCancellationRequested();
                await RunOp(op).ConfigureAwait(false);
            }

            if (deletesAllowed)
                foreach (var d in delItems.Where(i => i.IsDirectory).OrderByDescending(i => i.RelPath.Count(c => c == Path.DirectorySeparatorChar)))
                {
                    var path = d.Direction == CopyDirection.SourceToDestination ? d.DestinationPath : d.SourcePath;
                    try { if (Directory.Exists(path)) Directory.Delete(path, false); }
                    catch (IOException) { res.Messages.Add($"Folder not removed because it still contains ignored items: {d.RelPath}"); }
                    catch (UnauthorizedAccessException ex) { AddError(d.RelPath, ex); }
                }

            res.Status = res.Errors.Count > 0 ? SyncStatus.CompletedWithErrors : SyncStatus.Completed;
        }
        catch (OperationCanceledException)
        {
            res.Status = criticalMessage != null ? SyncStatus.Failed : SyncStatus.Cancelled;
            if (criticalMessage != null) res.Messages.Add(criticalMessage);
            else res.Messages.Add("The synchronization was cancelled. Files already copied are complete; unfinished files were discarded.");
        }
        catch (Exception ex)
        {
            AddError("(synchronization)", ex);
            res.Status = SyncStatus.Failed;
            res.Messages.Add(ErrorTranslator.Describe(ex));
            _log.Error(ex, "Synchronization failed");
        }
        finally
        {
            if (twoWay)
            {
                try
                {
                    // Files already identical are recorded as in sync (baseline).
                    foreach (var it in plan.Items.Where(i => i.Action == PlanAction.Unchanged && !i.IsDirectory && i.SourceEntry != null && i.DestinationEntry != null))
                        upserts.TryAdd(it.RelPath, new FileState(it.SourceEntry!.Size, it.SourceEntry.LastWriteUtc.Ticks, it.DestinationEntry!.Size, it.DestinationEntry.LastWriteUtc.Ticks));
                    _state.Save(p.Id, upserts, removals);
                }
                catch (Exception ex) { _log.Error(ex, "Could not save synchronization state"); }
            }
            if (p.Mode == SyncMode.Mirror && res.Status is SyncStatus.Completed or SyncStatus.CompletedWithErrors) MarkerStore.Add(dstRoot, p.Id);
            res.EndUtc = DateTime.UtcNow;
            tracker.Phase = "Done";
            progress?.Report(new SyncProgress { Phase = "Done", FilesDone = tracker.FilesTotal, FilesTotal = tracker.FilesTotal, BytesDone = tracker.Bytes, BytesTotal = tracker.BytesTotal });
            _log.Information("Finished '{Profile}': {Status} — {Summary} in {Duration}", p.Name, res.Status, res.Summary, Format.Duration(res.Duration));
        }
        return res;
    }
}
