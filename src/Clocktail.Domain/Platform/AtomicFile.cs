namespace Clocktail.Domain.Platform;

/// <summary>
/// Crash-safe file replacement: write a temporary file next to the target,
/// flush it to disk, then swap it in while keeping one generation as <c>.bak</c>. A reader never sees a
/// half-written file, and a crash between any two steps leaves either the old or the new content, never a mix.
/// </summary>
public static class AtomicFile
{
    public static string BackupPath(string path) => path + ".bak";

    /// <summary>
    /// Writes of one path from this process take turns. The temporary name carries only the process id, so two writes at
    /// once shared it, and one failed on the other's open file (two crash traces in the same second, 2026-10-19 21:48:29Z);
    /// its cleanup could also have deleted the other's half-written file. A small fixed set of locks keyed by the path.
    /// </summary>
    private static readonly object[] Turns = [.. Enumerable.Range(0, 16).Select(_ => new object())];

    private static object TurnFor(string fullPath) => Turns[(StringComparer.OrdinalIgnoreCase.GetHashCode(fullPath) & int.MaxValue) % Turns.Length];

    public static void Write(string path, ReadOnlySpan<byte> content)
    {
        lock (TurnFor(Path.GetFullPath(path))) WriteAlone(path, content);
    }

    private static void WriteAlone(string path, ReadOnlySpan<byte> content)
    {
        string dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(dir);
        string tmp = $"{path}.tmp.{Environment.ProcessId}";
        try
        {
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                fs.Write(content);
                fs.Flush(flushToDisk: true);
            }
            if (File.Exists(path))
            {
                string bak = BackupPath(path);
                try { File.Replace(tmp, path, bak, ignoreMetadataErrors: true); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException && !File.Exists(path) && File.Exists(bak))
                {
                    // ReplaceFile can fail after it has renamed the target to .bak (ERROR_UNABLE_TO_MOVE_REPLACEMENT_2),
                    // leaving nothing under the name. Move the old generation back.
                    File.Move(bak, path);
                    throw;
                }
            }
            else
            {
                File.Move(tmp, path);
            }
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    public static void WriteText(string path, string text) => Write(path, System.Text.Encoding.UTF8.GetBytes(text));

    /// <summary>
    /// Reads the file, refusing anything above <paramref name="maxBytes"/> (16 MiB by default). When only the
    /// <c>.bak</c> is there (a replacement cut off between its two renames) it reads that: a missing file must not read
    /// as "nothing", or the ownership journal would forget the originals it still owes.
    /// </summary>
    public static byte[]? Read(string path, long maxBytes = 16 * 1024 * 1024)
    {
        if (!File.Exists(path)) path = BackupPath(path);
        if (!File.Exists(path)) return null;
        var info = new FileInfo(path);
        if (info.Length > maxBytes) throw new InvalidDataException($"{path} is {info.Length} bytes, above the {maxBytes} byte limit.");
        return File.ReadAllBytes(path);
    }
}
