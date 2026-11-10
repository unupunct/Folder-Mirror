namespace FolderMirror.Core.Models;

public enum PlanAction { New, Modified, Delete, Unchanged, Conflict }
public enum CopyDirection { SourceToDestination, DestinationToSource }
public enum ConflictResolution { Skip, UseSource, UseDestination, KeepBoth }
public enum IssueSeverity { Info, Warning, Error }

public sealed record SafetyIssue(IssueSeverity Severity, string Code, string Message);

public sealed record FileEntry(string RelPath, bool IsDirectory, long Size, DateTime LastWriteUtc, FileAttributes Attributes);

public sealed class PlanItem
{
    public string RelPath { get; set; } = "";
    public bool IsDirectory { get; set; }
    public PlanAction Action { get; set; }
    /// <summary>Direction of the copy; for deletes, the side that is deleted (S→D = destination).</summary>
    public CopyDirection Direction { get; set; } = CopyDirection.SourceToDestination;
    public string SourcePath { get; set; } = "";
    public string DestinationPath { get; set; } = "";
    public long Size { get; set; }
    public string Reason { get; set; } = "";
    public ConflictResolution Resolution { get; set; } = ConflictResolution.Skip;
    /// <summary>Conflict where one side modified and the other deleted the file.</summary>
    public bool ConflictIsDeletion { get; set; }
    public FileEntry? SourceEntry { get; set; }
    public FileEntry? DestinationEntry { get; set; }

    public string SizeText => IsDirectory ? "" : FolderMirror.Core.Engine.Format.Bytes(Size);
    public string FileName => System.IO.Path.GetFileName(RelPath);

    public string ActionText => Action switch
    {
        PlanAction.New => "New",
        PlanAction.Modified => "Modified",
        PlanAction.Delete => "Deleted",
        PlanAction.Unchanged => "Unchanged",
        _ => "Conflict"
    };

    public string DirectionText => Action == PlanAction.Unchanged ? "" :
        Direction == CopyDirection.SourceToDestination ? "→" : "←";
}

public sealed class PlanSummary
{
    public int FilesToCopy { get; set; }
    public int FilesToUpdate { get; set; }
    public int FilesToDelete { get; set; }
    public int FilesUnchanged { get; set; }
    public int Conflicts { get; set; }
    public long BytesToTransfer { get; set; }
    public long BytesToDelete { get; set; }
    public long? DestinationFreeBytes { get; set; }
    public long? SourceFreeBytes { get; set; }
}

public sealed class SyncPlan
{
    public SyncProfile Profile { get; set; } = new();
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public List<PlanItem> Items { get; set; } = new();
    public List<SafetyIssue> Issues { get; set; } = new();
    public List<string> ScanWarnings { get; set; } = new();
    public List<string> StaleTempFiles { get; set; } = new();
    public PlanSummary Summary { get; set; } = new();
    public int DestinationFileCount { get; set; }
    public int SourceFileCount { get; set; }
    public bool DestinationLooksUnrelated { get; set; }

    public bool HasBlockingIssues => Issues.Any(i => i.Severity == IssueSeverity.Error);
    public bool HasDeletions => Items.Any(i => i.Action == PlanAction.Delete);
    public int ConflictCount => Items.Count(i => i.Action == PlanAction.Conflict && i.Resolution == ConflictResolution.Skip);

    public void RecomputeSummary()
    {
        var s = new PlanSummary { DestinationFreeBytes = Summary.DestinationFreeBytes, SourceFreeBytes = Summary.SourceFreeBytes };
        foreach (var i in Items)
        {
            if (i.IsDirectory) continue;
            switch (i.Action)
            {
                case PlanAction.New: s.FilesToCopy++; s.BytesToTransfer += i.Size; break;
                case PlanAction.Modified: s.FilesToUpdate++; s.BytesToTransfer += i.Size; break;
                case PlanAction.Delete: s.FilesToDelete++; s.BytesToDelete += i.Size; break;
                case PlanAction.Unchanged: s.FilesUnchanged++; break;
                case PlanAction.Conflict: s.Conflicts++; break;
            }
        }
        Summary = s;
    }
}

public enum SyncStatus { Completed, CompletedWithErrors, Cancelled, Failed, Skipped, Interrupted, Running }

public sealed record SyncError(string Path, string Message, string Technical);

public sealed record OpRecord(string Action, string RelPath, long Size, string Status, string Message);

public sealed class SyncResult
{
    public SyncStatus Status { get; set; }
    public DateTime StartUtc { get; set; }
    public DateTime EndUtc { get; set; }
    public int FilesCopied { get; set; }
    public int FilesModified { get; set; }
    public int FilesDeleted { get; set; }
    public int FilesUnchanged { get; set; }
    public int ConflictsLeft { get; set; }
    public long BytesTransferred { get; set; }
    public List<SyncError> Errors { get; } = new();
    public List<OpRecord> Operations { get; } = new();
    public List<string> Messages { get; } = new();
    public TimeSpan Duration => EndUtc - StartUtc;
    public string Summary =>
        $"{FilesCopied} new, {FilesModified} updated, {FilesDeleted} deleted, {Errors.Count} error(s)";
}

public sealed class SyncProgress
{
    public string Phase { get; init; } = "";
    public string CurrentFile { get; init; } = "";
    public int FilesDone { get; init; }
    public int FilesTotal { get; init; }
    public long BytesDone { get; init; }
    public long BytesTotal { get; init; }
    public double BytesPerSecond { get; init; }
    public TimeSpan? Remaining { get; init; }
    public double Fraction => BytesTotal > 0 ? Math.Min(1.0, (double)BytesDone / BytesTotal)
        : FilesTotal > 0 ? Math.Min(1.0, (double)FilesDone / FilesTotal) : 0;
}

public sealed class ExecuteOptions
{
    /// <summary>User explicitly confirmed that files will be deleted/archived.</summary>
    public bool ConfirmedDeletions { get; set; }
    /// <summary>Background / scheduled run (no one to ask).</summary>
    public bool Unattended { get; set; }
    public int MaxParallel { get; set; } = 4;
    public string Trigger { get; set; } = "Manual";
    public int LockedFileRetries { get; set; } = 3;
    public int LockedFileRetryDelayMs { get; set; } = 400;
}

public sealed class SyncBlockedException : Exception
{
    public IReadOnlyList<SafetyIssue> Issues { get; }
    public SyncBlockedException(string message, IReadOnlyList<SafetyIssue>? issues = null) : base(message)
        => Issues = issues ?? Array.Empty<SafetyIssue>();
}
