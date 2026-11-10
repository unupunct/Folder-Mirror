using System.Security.Cryptography;
using FolderMirror.Core.Models;
using Serilog;

namespace FolderMirror.Core.Engine;

/// <summary>Last known synced state of one file (identical on both sides when recorded).</summary>
public readonly record struct FileState(long SourceSize, long SourceMtimeTicks, long DestSize, long DestMtimeTicks);

public interface IStateStore
{
    Dictionary<string, FileState> Load(Guid profileId);
    void Save(Guid profileId, IEnumerable<KeyValuePair<string, FileState>> upserts, IEnumerable<string> deletes);
    void Clear(Guid profileId);
}

public sealed class MemoryStateStore : IStateStore
{
    readonly Dictionary<Guid, Dictionary<string, FileState>> _data = new();
    public Dictionary<string, FileState> Load(Guid id) =>
        _data.TryGetValue(id, out var d) ? new(d, PathTools.KeyComparer) : new(PathTools.KeyComparer);
    public void Save(Guid id, IEnumerable<KeyValuePair<string, FileState>> upserts, IEnumerable<string> deletes)
    {
        if (!_data.TryGetValue(id, out var d)) _data[id] = d = new(PathTools.KeyComparer);
        foreach (var kv in upserts) d[kv.Key] = kv.Value;
        foreach (var k in deletes) d.Remove(k);
    }
    public void Clear(Guid id) => _data.Remove(id);
}

public static class FileComparer
{
    public static readonly TimeSpan Tolerance = TimeSpan.FromSeconds(2);

    public static bool TimesEqual(DateTime a, DateTime b) => (a - b).Duration() <= Tolerance;

    public static string Sha256(string path, CancellationToken ct)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan);
        using var sha = SHA256.Create();
        var buf = new byte[1 << 20];
        int n;
        while ((n = fs.Read(buf, 0, buf.Length)) > 0) { ct.ThrowIfCancellationRequested(); sha.TransformBlock(buf, 0, n, null, 0); }
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Convert.ToHexString(sha.Hash!);
    }

    /// <summary>True when the two files are considered identical under the profile's rules.</summary>
    public static bool AreSame(SyncProfile p, FileEntry s, string sFull, FileEntry d, string dFull, CancellationToken ct, bool twoWay)
    {
        bool useSize = p.CompareSize || p.CompareHash;
        bool useDate = p.CompareModifiedDate;
        if (!useSize && !useDate && !p.CompareHash) { useSize = true; useDate = true; }

        if (useSize && s.Size != d.Size) return false;
        if (p.CompareHash)
        {
            if (s.Size != d.Size) return false;
            try { return Sha256(sFull, ct) == Sha256(dFull, ct); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
        }
        if (useDate)
        {
            if (twoWay || p.PreserveTimestamps) return TimesEqual(s.LastWriteUtc, d.LastWriteUtc);
            // Without preserved timestamps the destination is always "newer"; only a newer source counts as a change.
            return s.LastWriteUtc <= d.LastWriteUtc + Tolerance;
        }
        return true;
    }
}

public sealed class Planner
{
    readonly IStateStore _state;
    readonly IDiskSpaceProvider _space;
    readonly ILogger _log;

    public Planner(IStateStore state, IDiskSpaceProvider space, ILogger log)
    { _state = state; _space = space; _log = log; }

    public SyncPlan Build(SyncProfile profile, CancellationToken ct, int maxParallel = 4, Action<string>? status = null)
    {
        var p = profile.Clone();
        p.NormalizeLists();
        var plan = new SyncPlan { Profile = p };
        plan.Issues.AddRange(SafetyChecker.CheckPaths(p, _space));
        if (plan.HasBlockingIssues) return plan;

        var srcRoot = PathTools.Normalize(p.SourcePath);
        var dstRoot = PathTools.Normalize(p.DestinationPath);
        bool twoWay = p.Mode == SyncMode.TwoWay;

        status?.Invoke("Scanning source…");
        var src = FileScanner.Scan(srcRoot, p, ct, n => status?.Invoke($"Scanning source… {n:N0} files"));
        status?.Invoke("Scanning destination…");
        ScanResult dst;
        if (Directory.Exists(dstRoot)) dst = FileScanner.Scan(dstRoot, p, ct, n => status?.Invoke($"Scanning destination… {n:N0} files"));
        else dst = new ScanResult();

        plan.ScanWarnings.AddRange(src.Warnings);
        plan.ScanWarnings.AddRange(dst.Warnings);
        plan.StaleTempFiles.AddRange(dst.StaleTempFiles);
        if (twoWay) plan.StaleTempFiles.AddRange(src.StaleTempFiles);
        plan.SourceFileCount = src.FileCount;
        plan.DestinationFileCount = dst.FileCount;

        status?.Invoke("Comparing…");
        var prev = twoWay ? _state.Load(p.Id) : new Dictionary<string, FileState>(PathTools.KeyComparer);

        // Pairs present on both sides need a comparison (may involve hashing).
        var pairs = src.Entries.Values.Where(e => !e.IsDirectory && dst.Entries.TryGetValue(e.RelPath, out var d) && !d.IsDirectory).ToList();
        var same = new System.Collections.Concurrent.ConcurrentDictionary<string, bool>(PathTools.KeyComparer);
        Parallel.ForEach(pairs, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, maxParallel), CancellationToken = ct }, s =>
        {
            var d = dst.Entries[s.RelPath];
            same[s.RelPath] = FileComparer.AreSame(p, s, Path.Combine(srcRoot, s.RelPath), d, Path.Combine(dstRoot, s.RelPath), ct, twoWay);
        });

        PlanItem Make(string rel, PlanAction a, CopyDirection dir, FileEntry? s, FileEntry? d, long size, string reason, bool isDir = false) => new()
        {
            RelPath = rel, Action = a, Direction = dir, IsDirectory = isDir,
            SourcePath = Path.Combine(srcRoot, rel), DestinationPath = Path.Combine(dstRoot, rel),
            Size = size, Reason = reason, SourceEntry = s, DestinationEntry = d
        };

        var items = new List<PlanItem>();
        var allKeys = new HashSet<string>(src.Entries.Keys, PathTools.KeyComparer);
        allKeys.UnionWith(dst.Entries.Keys);

        foreach (var rel in allKeys.OrderBy(k => k, PathTools.KeyComparer))
        {
            ct.ThrowIfCancellationRequested();
            src.Entries.TryGetValue(rel, out var s);
            dst.Entries.TryGetValue(rel, out var d);

            // Directories
            if ((s?.IsDirectory ?? false) || (d?.IsDirectory ?? false))
            {
                if (s != null && s.IsDirectory && d != null && !d.IsDirectory)
                { items.Add(Make(rel, PlanAction.Conflict, CopyDirection.SourceToDestination, s, d, 0, "Source is a folder but destination is a file with the same name")); continue; }
                if (s != null && !s.IsDirectory && d != null && d.IsDirectory)
                { items.Add(Make(rel, PlanAction.Conflict, CopyDirection.SourceToDestination, s, d, s.Size, "Source is a file but destination is a folder with the same name")); continue; }
                if (s != null && s.IsDirectory && d == null)
                    items.Add(Make(rel, PlanAction.New, CopyDirection.SourceToDestination, s, null, 0, "Folder missing in destination", true));
                else if (s == null && d != null && d.IsDirectory)
                {
                    if (p.Mode == SyncMode.Mirror) items.Add(Make(rel, PlanAction.Delete, CopyDirection.SourceToDestination, null, d, 0, "Folder not present in source", true));
                    else if (twoWay) items.Add(Make(rel, PlanAction.New, CopyDirection.DestinationToSource, null, d, 0, "Folder missing in source", true));
                }
                continue;
            }

            // Files
            if (s != null && d == null)
            {
                if (twoWay && prev.TryGetValue(rel, out var ps))
                {
                    bool sChanged = !StampMatches(s, ps.SourceSize, ps.SourceMtimeTicks);
                    if (!sChanged)
                        items.Add(Make(rel, PlanAction.Delete, CopyDirection.DestinationToSource, s, null, s.Size, "Deleted in destination since last sync"));
                    else
                        items.Add(Conflict(Make(rel, PlanAction.Conflict, CopyDirection.SourceToDestination, s, null, s.Size, "Modified in source but deleted in destination"), p, true));
                }
                else items.Add(Make(rel, PlanAction.New, CopyDirection.SourceToDestination, s, null, s.Size, "Not present in destination"));
            }
            else if (s == null && d != null)
            {
                if (twoWay)
                {
                    if (prev.TryGetValue(rel, out var ps))
                    {
                        bool dChanged = !StampMatches(d, ps.DestSize, ps.DestMtimeTicks);
                        if (!dChanged)
                            items.Add(Make(rel, PlanAction.Delete, CopyDirection.SourceToDestination, null, d, d.Size, "Deleted in source since last sync"));
                        else
                            items.Add(Conflict(Make(rel, PlanAction.Conflict, CopyDirection.DestinationToSource, null, d, d.Size, "Modified in destination but deleted in source"), p, true));
                    }
                    else items.Add(Make(rel, PlanAction.New, CopyDirection.DestinationToSource, null, d, d.Size, "Not present in source"));
                }
                else if (p.Mode == SyncMode.Mirror)
                    items.Add(Make(rel, PlanAction.Delete, CopyDirection.SourceToDestination, null, d, d.Size, "Not present in source"));
                // OneWay / Incremental: destination-only files are left alone
            }
            else if (s != null && d != null)
            {
                if (same[rel])
                {
                    items.Add(Make(rel, PlanAction.Unchanged, CopyDirection.SourceToDestination, s, d, s.Size, "Identical"));
                    continue;
                }
                if (!twoWay)
                {
                    items.Add(Make(rel, PlanAction.Modified, CopyDirection.SourceToDestination, s, d, s.Size,
                        p.Mode == SyncMode.Incremental ? "Changed in source; previous version will be kept" : "Changed in source"));
                    continue;
                }
                // Two-way
                if (!prev.TryGetValue(rel, out var st))
                {
                    items.Add(Conflict(Make(rel, PlanAction.Conflict, CopyDirection.SourceToDestination, s, d, s.Size, "File exists on both sides with different content and was never synchronized"), p, false));
                    continue;
                }
                bool sc = !StampMatches(s, st.SourceSize, st.SourceMtimeTicks);
                bool dc = !StampMatches(d, st.DestSize, st.DestMtimeTicks);
                if (sc && !dc)
                    items.Add(Make(rel, PlanAction.Modified, CopyDirection.SourceToDestination, s, d, s.Size, "Changed in source since last sync"));
                else if (dc && !sc)
                    items.Add(Make(rel, PlanAction.Modified, CopyDirection.DestinationToSource, s, d, d.Size, "Changed in destination since last sync"));
                else
                    items.Add(Conflict(Make(rel, PlanAction.Conflict, CopyDirection.SourceToDestination, s, d, Math.Max(s.Size, d.Size), "Modified on both sides since last sync"), p, false));
            }
        }

        plan.Items = items;

        // Mirror safety facts
        if (p.Mode == SyncMode.Mirror)
        {
            int deletes = items.Count(i => i.Action == PlanAction.Delete && !i.IsDirectory);
            if (deletes > 0)
            {
                var marker = MarkerStore.Read(dstRoot);
                if (marker == null || !marker.Contains(p.Id))
                {
                    plan.DestinationLooksUnrelated = true;
                    plan.Issues.Add(new SafetyIssue(IssueSeverity.Warning, "DestinationUnrelated",
                        $"The destination already contains {deletes:N0} file(s) that are not in the source, and it has never been synchronized by this profile. Mirror will remove them{(p.DeletionBehavior == DeletionBehavior.MoveToArchive ? " (moved to the archive folder)" : " PERMANENTLY")}. Make sure this is the right destination."));
                }
                if (src.FileCount == 0 && dst.FileCount > 0)
                    plan.Issues.Add(new SafetyIssue(IssueSeverity.Warning, "SourceEmpty",
                        "The source folder is empty but the destination contains files. Mirror would remove all of them. Is the source drive connected and correct?"));
            }
        }

        if (twoWay && items.Any(i => i.Action == PlanAction.Conflict))
            plan.Issues.Add(new SafetyIssue(IssueSeverity.Warning, "Conflicts",
                "Some files changed on both sides. Choose how to resolve each conflict in the preview; unresolved conflicts are left untouched."));

        plan.RecomputeSummary();
        if (_space.TryGetSpace(dstRoot, out var free, out _)) plan.Summary.DestinationFreeBytes = free;
        if (_space.TryGetSpace(srcRoot, out var sfree, out _)) plan.Summary.SourceFreeBytes = sfree;

        long needDst = 0, needSrc = 0;
        foreach (var i in items.Where(i => !i.IsDirectory && i.Action is PlanAction.New or PlanAction.Modified))
            if (i.Direction == CopyDirection.SourceToDestination) needDst += i.Size; else needSrc += i.Size;
        if (plan.Summary.DestinationFreeBytes is long df && needDst > df)
            plan.Issues.Add(new SafetyIssue(IssueSeverity.Error, "InsufficientSpace",
                $"Not enough free space on the destination: {Format.Bytes(needDst)} needed, {Format.Bytes(df)} available."));
        if (plan.Summary.SourceFreeBytes is long sf && needSrc > sf)
            plan.Issues.Add(new SafetyIssue(IssueSeverity.Error, "InsufficientSpaceSource",
                $"Not enough free space on the source drive for incoming changes: {Format.Bytes(needSrc)} needed, {Format.Bytes(sf)} available."));

        _log.Information("Plan for '{Profile}' ({Mode}): {New} new, {Mod} modified, {Del} delete, {Same} unchanged, {Conf} conflicts",
            p.Name, p.Mode, plan.Summary.FilesToCopy, plan.Summary.FilesToUpdate, plan.Summary.FilesToDelete, plan.Summary.FilesUnchanged, plan.Summary.Conflicts);
        return plan;
    }

    static bool StampMatches(FileEntry e, long size, long mtimeTicks) =>
        e.Size == size && FileComparer.TimesEqual(e.LastWriteUtc, new DateTime(mtimeTicks, DateTimeKind.Utc));

    static PlanItem Conflict(PlanItem item, SyncProfile p, bool isDeletion)
    {
        item.ConflictIsDeletion = isDeletion;
        item.Resolution = p.ConflictPolicy switch
        {
            ConflictPolicy.KeepBoth => isDeletion ? ConflictResolution.Skip : ConflictResolution.KeepBoth,
            ConflictPolicy.PreferSource => ConflictResolution.UseSource,
            ConflictPolicy.PreferDestination => ConflictResolution.UseDestination,
            _ => ConflictResolution.Skip
        };
        return item;
    }
}

public static class Format
{
    public static string Bytes(long b)
    {
        string[] u = { "B", "KB", "MB", "GB", "TB" };
        double v = b; int i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return i == 0 ? $"{b} B" : $"{v:0.##} {u[i]}";
    }

    public static string Duration(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes}m {t.Seconds}s" :
        t.TotalMinutes >= 1 ? $"{t.Minutes}m {t.Seconds}s" : $"{Math.Max(0, t.TotalSeconds):0.#}s";
}
