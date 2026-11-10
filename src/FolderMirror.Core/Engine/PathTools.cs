using System.Runtime.InteropServices;
using FolderMirror.Core.Models;

namespace FolderMirror.Core.Engine;

public static class PathTools
{
    public const string ReservedFolder = "_FolderMirror";
    public const string TempSuffix = ".fmtmp";

    public static string Normalize(string path)
    {
        var full = Path.GetFullPath(path.Trim().Trim('"'));
        var root = Path.GetPathRoot(full) ?? "";
        if (full.Length > root.Length) full = full.TrimEnd('\\', '/');
        return full;
    }

    public static bool SamePath(string a, string b) =>
        string.Equals(Normalize(a), Normalize(b), Comparison);

    /// <summary>Windows and macOS (default APFS) are case-insensitive; Linux is not.</summary>
    public static readonly StringComparison Comparison =
        OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    /// <summary>True when <paramref name="child"/> is inside <paramref name="parent"/> (not equal).</summary>
    public static bool IsInside(string child, string parent)
    {
        var c = Normalize(child);
        var p = Normalize(parent).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        return c.StartsWith(p, Comparison) && c.Length > p.Length - 1;
    }

    public static string Combine(string root, string rel) => Path.Combine(root, rel);

    public static string ReservedPath(string root, params string[] parts) =>
        Path.Combine(new[] { root, ReservedFolder }.Concat(parts).ToArray());

    /// <summary>Returns <paramref name="path"/>, or a "name (2).ext" variant if it already exists.</summary>
    public static string Unique(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path) ?? "";
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (int i = 2; ; i++)
        {
            var c = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(c) && !Directory.Exists(c)) return c;
        }
    }

    public static string AddSuffixBeforeExtension(string rel, string suffix)
    {
        var dir = Path.GetDirectoryName(rel) ?? "";
        var name = Path.GetFileNameWithoutExtension(rel);
        var ext = Path.GetExtension(rel);
        return Path.Combine(dir, name + suffix + ext);
    }

    public static string? DriveRoot(string path)
    {
        try
        {
            var full = Normalize(path);
            if (OperatingSystem.IsWindows()) return Path.GetPathRoot(full);
            // macOS: external drives and network shares are mounted under /Volumes/<name>
            var parts = full.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && parts[0] == "Volumes") return "/Volumes/" + parts[1];
            return "/";
        }
        catch { return null; }
    }

    public static bool IsRootAvailable(string path)
    {
        var root = DriveRoot(path);
        if (string.IsNullOrEmpty(root)) return false;
        try { return Directory.Exists(root); } catch { return false; }
    }

    public static bool IsNetworkPath(string path) =>
        OperatingSystem.IsWindows() && Normalize(path).StartsWith(@"\\", StringComparison.Ordinal);

    public static DriveType? GetDriveType(string path)
    {
        try
        {
            var root = DriveRoot(path);
            if (root == null) return null;
            if (OperatingSystem.IsWindows() && root.StartsWith(@"\\")) return DriveType.Network;
            return new DriveInfo(root).DriveType;
        }
        catch { return null; }
    }
}

public interface IDiskSpaceProvider
{
    bool TryGetSpace(string path, out long freeBytes, out long totalBytes);
}

public sealed class SystemDiskSpace : IDiskSpaceProvider
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool GetDiskFreeSpaceEx(string dir, out ulong freeToCaller, out ulong total, out ulong totalFree);

    public bool TryGetSpace(string path, out long freeBytes, out long totalBytes)
    {
        freeBytes = totalBytes = 0;
        try
        {
            var p = PathTools.Normalize(path);
            while (!string.IsNullOrEmpty(p) && !Directory.Exists(p))
                p = Path.GetDirectoryName(p) ?? "";
            if (string.IsNullOrEmpty(p)) return false;
            if (!OperatingSystem.IsWindows())
            {
                var di = new DriveInfo(p);
                freeBytes = di.AvailableFreeSpace; totalBytes = di.TotalSize;
                return true;
            }
            if (!p.EndsWith('\\')) p += "\\";
            if (!GetDiskFreeSpaceEx(p, out var free, out var total, out _)) return false;
            freeBytes = (long)free; totalBytes = (long)total;
            return true;
        }
        catch { return false; }
    }
}

public static class ErrorTranslator
{
    const int SharingViolation = unchecked((int)0x80070020);
    const int LockViolation = unchecked((int)0x80070021);
    const int DiskFull = unchecked((int)0x80070070);
    const int HandleDiskFull = unchecked((int)0x80070027);
    const int NetworkNameDeleted = unchecked((int)0x80070040);
    const int NetworkPathNotFound = unchecked((int)0x80070035);
    const int NetworkBusy = unchecked((int)0x80070036);
    const int NetworkUnreachable = unchecked((int)0x800704CF);
    const int DeviceNotConnected = unchecked((int)0x8007048F);
    const int PathTooLongHr = unchecked((int)0x800700CE);
    const int NotReady = unchecked((int)0x80070015);

    public static bool IsDiskFull(Exception ex) => ex is IOException io && (io.HResult is DiskFull or HandleDiskFull);
    public static bool IsLocked(Exception ex) => ex is IOException io && (io.HResult is SharingViolation or LockViolation);
    public static bool IsNetworkLost(Exception ex) => ex is IOException io &&
        (io.HResult is NetworkNameDeleted or NetworkPathNotFound or NetworkBusy or NetworkUnreachable or DeviceNotConnected or NotReady);

    public static string Describe(Exception ex, string? path = null)
    {
        var where = path == null ? "" : $" ({path})";
        return ex switch
        {
            SourceChangedException => "The file changed while it was being copied. It will be copied again on the next run." + where,
            UnauthorizedAccessException => "Access denied. Check the permissions of the file or folder, or whether it is read-only." + where,
            PathTooLongException => "The file path is too long for Windows." + where,
            DirectoryNotFoundException => "A folder could not be found. It may have been moved, deleted or disconnected." + where,
            FileNotFoundException => "The file no longer exists (it was probably removed during synchronization)." + where,
            OperationCanceledException => "The operation was cancelled.",
            IOException io when IsDiskFull(io) || io.Message.Contains("No space left", StringComparison.OrdinalIgnoreCase) => "There is not enough free space on the destination drive." + where,
            IOException io when IsLocked(io) => "The file is currently in use by another program and could not be copied." + where,
            IOException io when IsNetworkLost(io) => "The network location or drive became unavailable. Check the connection." + where,
            IOException io when io.HResult == PathTooLongHr => "The file path is too long for Windows." + where,
            IOException io => "An input/output error occurred: " + io.Message + where,
            _ => ex.Message + where
        };
    }
}

public sealed class SourceChangedException : IOException
{
    public SourceChangedException(string path) : base("Source file changed during copy: " + path) { }
}
