using FolderMirror.Core.Models;

namespace FolderMirror.Core.Engine;

public sealed class ScanResult
{
    public Dictionary<string, FileEntry> Entries { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Warnings { get; } = new();
    public List<string> StaleTempFiles { get; } = new();
    public int FileCount => Entries.Values.Count(e => !e.IsDirectory);
}

public static class FileScanner
{
    static readonly string[] TempExtensions = { ".tmp", ".temp", ".crdownload", ".part", ".partial", ".swp" };

    public static bool IsTemporary(string name)
    {
        if (name.StartsWith("~$", StringComparison.Ordinal)) return true;
        if (name.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase)) return true;
        var ext = Path.GetExtension(name);
        return TempExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase);
    }

    static bool FolderExcluded(string rel, string name, SyncProfile p)
    {
        foreach (var f in p.ExcludedFolders)
        {
            if (f.Contains('\\') || f.Contains('/'))
            {
                var sep = Path.DirectorySeparatorChar;
                var norm = f.Replace('/', sep).Replace('\\', sep).Trim(sep);
                if (rel.Equals(norm, StringComparison.OrdinalIgnoreCase)) return true;
            }
            else if (name.Equals(f, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    public static bool FileExcluded(string name, FileAttributes attrs, SyncProfile p)
    {
        if (name.EndsWith(PathTools.TempSuffix, StringComparison.OrdinalIgnoreCase)) return true;
        if (p.IgnoreHidden && (attrs & FileAttributes.Hidden) != 0) return true;
        if (p.IgnoreSystem && (attrs & FileAttributes.System) != 0) return true;
        if (p.IgnoreTemporary && IsTemporary(name)) return true;
        if (p.ExcludedExtensions.Count > 0)
        {
            var ext = Path.GetExtension(name);
            if (p.ExcludedExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    public static ScanResult Scan(string root, SyncProfile p, CancellationToken ct, Action<int>? onCount = null)
    {
        var result = new ScanResult();
        var stack = new Stack<(string Full, string Rel)>();
        stack.Push((root, ""));
        int count = 0;

        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (dirFull, dirRel) = stack.Pop();
            IEnumerable<FileSystemInfo> children;
            try
            {
                children = new DirectoryInfo(dirFull).EnumerateFileSystemInfos().ToList();
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                if (dirRel.Length == 0) throw;
                result.Warnings.Add($"Skipped inaccessible folder: {dirFull} — {ErrorTranslator.Describe(ex)}");
                continue;
            }

            foreach (var child in children)
            {
                var rel = dirRel.Length == 0 ? child.Name : dirRel + Path.DirectorySeparatorChar + child.Name;
                try
                {
                    var attrs = child.Attributes;
                    if (child is DirectoryInfo)
                    {
                        if (dirRel.Length == 0 && child.Name.Equals(PathTools.ReservedFolder, StringComparison.OrdinalIgnoreCase)) continue;
                        if (!p.IncludeSubdirectories) continue;
                        if ((attrs & FileAttributes.ReparsePoint) != 0)
                        {
                            result.Warnings.Add($"Skipped link/junction folder: {child.FullName}");
                            continue;
                        }
                        if (p.IgnoreHidden && (attrs & FileAttributes.Hidden) != 0) continue;
                        if (p.IgnoreSystem && (attrs & FileAttributes.System) != 0) continue;
                        if (FolderExcluded(rel, child.Name, p)) continue;
                        result.Entries[rel] = new FileEntry(rel, true, 0, child.LastWriteTimeUtc, attrs);
                        stack.Push((child.FullName, rel));
                    }
                    else if (child is FileInfo fi)
                    {
                        if (fi.Name.EndsWith(PathTools.TempSuffix, StringComparison.OrdinalIgnoreCase))
                        {
                            result.StaleTempFiles.Add(fi.FullName);
                            continue;
                        }
                        if (FileExcluded(fi.Name, attrs, p)) continue;
                        result.Entries[rel] = new FileEntry(rel, false, fi.Length, fi.LastWriteTimeUtc, attrs);
                        if (++count % 500 == 0) onCount?.Invoke(count);
                    }
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                {
                    result.Warnings.Add($"Skipped {child.FullName}: {ErrorTranslator.Describe(ex)}");
                }
            }
        }

        if (p.IgnoreEmptyFolders)
        {
            var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in result.Entries.Values.Where(e => !e.IsDirectory))
            {
                var d = Path.GetDirectoryName(e.RelPath);
                while (!string.IsNullOrEmpty(d)) { keep.Add(d); d = Path.GetDirectoryName(d); }
            }
            foreach (var dir in result.Entries.Values.Where(e => e.IsDirectory && !keep.Contains(e.RelPath)).Select(e => e.RelPath).ToList())
                result.Entries.Remove(dir);
        }
        onCount?.Invoke(count);
        return result;
    }
}
