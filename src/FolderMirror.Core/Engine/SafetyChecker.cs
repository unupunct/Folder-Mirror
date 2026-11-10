using FolderMirror.Core.Models;

namespace FolderMirror.Core.Engine;

public static class SafetyChecker
{
    static IEnumerable<string> ProtectedFolders()
    {
        foreach (var f in new[]
        {
            Environment.SpecialFolder.Windows, Environment.SpecialFolder.System,
            Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86,
            Environment.SpecialFolder.UserProfile
        })
        {
            var p = Environment.GetFolderPath(f);
            if (!string.IsNullOrEmpty(p)) yield return PathTools.Normalize(p);
        }
        if (!OperatingSystem.IsWindows())
            foreach (var p in new[] { "/System", "/usr", "/bin", "/sbin", "/etc", "/Library", "/Applications", "/private", "/var" })
                yield return p;
    }

    public static List<SafetyIssue> CheckPaths(SyncProfile p, IDiskSpaceProvider? space = null)
    {
        var issues = new List<SafetyIssue>();
        void Add(IssueSeverity s, string code, string msg) => issues.Add(new SafetyIssue(s, code, msg));

        if (string.IsNullOrWhiteSpace(p.SourcePath)) { Add(IssueSeverity.Error, "SourceEmpty", "No source folder selected."); }
        if (string.IsNullOrWhiteSpace(p.DestinationPath)) { Add(IssueSeverity.Error, "DestinationEmpty", "No destination folder selected."); }
        if (issues.Count > 0) return issues;

        string src, dst;
        try { src = PathTools.Normalize(p.SourcePath); dst = PathTools.Normalize(p.DestinationPath); }
        catch (Exception ex)
        {
            Add(IssueSeverity.Error, "InvalidPath", "One of the folder paths is not valid: " + ex.Message);
            return issues;
        }

        // Availability of drives
        if (!PathTools.IsRootAvailable(src))
            Add(IssueSeverity.Error, "SourceDriveUnavailable",
                $"The source drive/location '{PathTools.DriveRoot(src)}' is not available. If it is an external or network drive, connect it and try again.");
        else if (!Directory.Exists(src))
            Add(IssueSeverity.Error, "SourceMissing", $"The source folder does not exist: {src}");

        if (!PathTools.IsRootAvailable(dst))
            Add(IssueSeverity.Error, "DestinationDriveUnavailable",
                $"The destination drive/location '{PathTools.DriveRoot(dst)}' is not available. It may be a disconnected external drive or an unreachable network share.");
        else if (!Directory.Exists(dst))
            Add(IssueSeverity.Info, "DestinationWillBeCreated", $"The destination folder does not exist and will be created: {dst}");

        // Same / nested
        if (PathTools.SamePath(src, dst))
            Add(IssueSeverity.Error, "SamePath", "Source and destination are the same folder. Choose two different folders.");
        else if (PathTools.IsInside(dst, src))
            Add(IssueSeverity.Error, "DestinationInsideSource", "The destination is inside the source folder. Synchronizing a folder into itself is not allowed.");
        else if (PathTools.IsInside(src, dst))
            Add(IssueSeverity.Error, "SourceInsideDestination", "The source is inside the destination folder. This could overwrite or delete the source and is not allowed.");

        // Dangerous destinations
        var dstRoot = PathTools.DriveRoot(dst) ?? "";
        if (dst == dstRoot || dst.TrimEnd('\\', '/').Equals(dstRoot.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
        {
            if (p.Mode == SyncMode.Mirror)
                Add(IssueSeverity.Error, "MirrorToDriveRoot",
                    "Mirror mode cannot target the root of a drive, because every file there that is not in the source would be removed. Choose a dedicated folder on the drive.");
            else
                Add(IssueSeverity.Warning, "DriveRootDestination", "The destination is the root of a drive. Consider using a dedicated sub-folder.");
        }
        foreach (var prot in ProtectedFolders())
        {
            if (PathTools.SamePath(dst, prot) || PathTools.IsInside(prot, dst))
                Add(IssueSeverity.Error, "ProtectedDestination", $"The destination '{dst}' is or contains a protected system or profile folder ({prot}).");
            else if (p.Mode == SyncMode.Mirror && PathTools.IsInside(dst, prot) &&
                     (prot.EndsWith("Windows", StringComparison.OrdinalIgnoreCase) || prot.Contains("Program Files", StringComparison.OrdinalIgnoreCase)))
                Add(IssueSeverity.Error, "ProtectedDestination", $"The destination is inside a protected system folder ({prot}).");
        }
        if (p.Mode == SyncMode.TwoWay)
            foreach (var prot in ProtectedFolders())
                if (PathTools.SamePath(src, prot))
                    Add(IssueSeverity.Error, "ProtectedSource", $"Two-way sync cannot use the protected folder '{prot}' as a side.");

        var dt = PathTools.GetDriveType(dst);
        if (dt == DriveType.Removable) Add(IssueSeverity.Info, "RemovableDestination", "The destination is on a removable (USB) drive. Do not unplug it during synchronization.");
        if (dt == DriveType.Network) Add(IssueSeverity.Info, "NetworkDestination", "The destination is on a network location; speed depends on the connection.");

        if (p.Mode == SyncMode.TwoWay && !p.PreserveTimestamps)
            Add(IssueSeverity.Info, "TwoWayTimestamps", "Two-way sync always preserves timestamps so that both sides can be compared reliably.");

        return issues;
    }
}
