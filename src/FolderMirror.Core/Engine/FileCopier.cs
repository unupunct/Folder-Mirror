using System.Security.Cryptography;

namespace FolderMirror.Core.Engine;

public sealed class CopyOptions
{
    public bool PreserveTimestamps { get; init; } = true;
    public bool PreserveAttributes { get; init; } = true;
    public bool Verify { get; init; }
    public int LockedFileRetries { get; init; } = 3;
    public int LockedFileRetryDelayMs { get; init; } = 400;
}

/// <summary>Safe streaming copy: temp file in the destination folder → flush → verify → atomic rename.</summary>
public static class FileCopier
{
    const FileAttributes Preserved = FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System | FileAttributes.Archive;

    public static async Task CopyAsync(string src, string dst, CopyOptions o, Action<long> onBytes,
        Action<string>? beforeReplace, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
        var temp = dst + "." + Guid.NewGuid().ToString("N")[..8] + PathTools.TempSuffix;
        var before = new FileInfo(src);
        long len0 = before.Length;
        var m0 = before.LastWriteTimeUtc;
        var attrs0 = before.Attributes;
        var created0 = before.CreationTimeUtc;

        try
        {
            string? hash = null;
            for (int attempt = 0; ; attempt++)
            {
                try { hash = await StreamCopyAsync(src, temp, len0, o.Verify, onBytes, ct).ConfigureAwait(false); break; }
                catch (IOException ex) when (ErrorTranslator.IsLocked(ex) && attempt < o.LockedFileRetries)
                {
                    TryDelete(temp);
                    await Task.Delay(o.LockedFileRetryDelayMs, ct).ConfigureAwait(false);
                }
            }

            // Was the source modified while we were copying?
            var after = new FileInfo(src);
            if (!after.Exists || after.Length != len0 || after.LastWriteTimeUtc != m0)
                throw new SourceChangedException(src);

            if (o.Verify)
            {
                var actual = await Task.Run(() => FileComparer.Sha256(temp, ct), ct).ConfigureAwait(false);
                if (!string.Equals(actual, hash, StringComparison.Ordinal))
                    throw new IOException("Verification failed: the copied file does not match the source.");
            }

            if (o.PreserveTimestamps)
            {
                File.SetCreationTimeUtc(temp, created0);
                File.SetLastWriteTimeUtc(temp, m0);
            }
            var wanted = o.PreserveAttributes ? attrs0 & Preserved : FileAttributes.Normal;
            File.SetAttributes(temp, wanted == 0 ? FileAttributes.Normal : wanted);

            if (File.Exists(dst))
            {
                beforeReplace?.Invoke(dst);
                if (File.Exists(dst))
                {
                    File.SetAttributes(dst, FileAttributes.Normal);
                }
            }
            File.Move(temp, dst, overwrite: true);
        }
        finally
        {
            TryDelete(temp);
        }
    }

    static async Task<string?> StreamCopyAsync(string src, string temp, long length, bool hashIt, Action<long> onBytes, CancellationToken ct)
    {
        await using var input = new FileStream(src, new FileStreamOptions
        {
            Mode = FileMode.Open, Access = FileAccess.Read, Share = FileShare.ReadWrite | FileShare.Delete,
            BufferSize = 0, Options = FileOptions.Asynchronous | FileOptions.SequentialScan
        });
        await using var output = new FileStream(temp, new FileStreamOptions
        {
            Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None,
            BufferSize = 0, Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
            PreallocationSize = length
        });
        using var sha = hashIt ? IncrementalHash.CreateHash(HashAlgorithmName.SHA256) : null;
        var buffer = new byte[length > (8 << 20) ? 1 << 20 : 1 << 18];
        int n;
        while ((n = await input.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
            sha?.AppendData(buffer, 0, n);
            onBytes(n);
        }
        output.Flush(true);
        return sha == null ? null : Convert.ToHexString(sha.GetHashAndReset());
    }

    static void TryDelete(string path)
    {
        try { if (File.Exists(path)) { File.SetAttributes(path, FileAttributes.Normal); File.Delete(path); } } catch { }
    }
}
