using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace PhotoOrganiser;

// A preview row records both the proposed action and the hash used to validate it later.
public sealed record Entry(string Source, string Destination, string Action,
    string Date, string DateSource, string Note, string Hash = "");
public sealed record Preview(string Source, string Destination, IReadOnlyList<Entry> Entries);
public sealed record CopyResult(int Copied, int Skipped, int Errors, bool Cancelled, string LogPath, int Moved = 0);

// A null total marks a phase where the number of files is still unknown.
public sealed record OperationProgress(string Phase, string? Path = null, int Completed = 0, int? Total = null);

public sealed class Organiser(string? logDirectory = null)
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".jpe", ".jfif", ".png", ".tif", ".tiff", ".heic", ".heif",
        ".webp", ".avif", ".bmp", ".gif", ".dng", ".cr2", ".cr3", ".nef", ".arw",
        ".raf", ".orf", ".rw2", ".pef", ".srw"
    };
    private static readonly StringComparer Paths = StringComparer.OrdinalIgnoreCase;

    public Preview Scan(string source, string destination, CancellationToken token,
        IProgress<OperationProgress>? progress = null, bool includeSubfolders = true)
    {
        // Resolve absolute paths before checking for overlapping trees and filesystem links.
        source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source.Trim()));
        destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination.Trim()));
        if (!Directory.Exists(source))
            throw new DirectoryNotFoundException("Choose an existing source folder.");
        if (Contains(source, destination) || Contains(destination, source))
            throw new IOException("Choose separate folders outside each other's folder trees.");

        CheckPath(source);
        CheckPath(destination);
        var rows = new List<Entry>();
        // Seed the duplicate lookup from existing photos and reserve names as the preview grows.
        var hashes = Index(destination, rows, token, progress);
        var reserved = new HashSet<string>(Paths);
        var files = FindPhotos(source, rows, token, progress, "Finding source photos", includeSubfolders)
            .OrderBy(path => path, Paths).ToArray();
        var count = 0;

        foreach (var path in files)
        {
            token.ThrowIfCancellationRequested();
            progress?.Report(new("Reading source photos", path, count++, files.Length));

            try
            {
                // Hash and read metadata from the same open handle, with the stream rewound for WIC.
                using var stream = OpenRead(path);
                var hash = Hash(stream, token);
                stream.Position = 0;
                var date = PhotoDates.Read(stream, new FileInfo(path));
                var displayedDate = date.Value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                if (hashes.TryGetValue(hash, out var match))
                {
                    rows.Add(new(path, match, "Duplicate", displayedDate, date.Origin,
                        "Same SHA-256 as the destination or an earlier planned photo.", hash));
                    continue;
                }

                var folder = Path.Combine(destination, date.Value.ToString("yyyy", CultureInfo.InvariantCulture),
                    date.Value.ToString("MM", CultureInfo.InvariantCulture));
                CheckPath(folder);
                var original = Path.Combine(folder, Path.GetFileName(path));
                var target = FreeName(original, reserved, token);
                reserved.Add(target);
                hashes.Add(hash, target);
                rows.Add(new(path, target, "Copy", displayedDate, date.Origin,
                    Paths.Equals(original, target) ? "" : "Renamed to avoid a filename collision.", hash));
            }
            catch (Exception error) when (IsFileError(error))
            {
                rows.Add(Issue(path, error.Message));
            }
        }

        token.ThrowIfCancellationRequested();
        return new(source, destination, rows.AsReadOnly());
    }

    public CopyResult Copy(Preview preview, CancellationToken token, IProgress<OperationProgress>? progress = null)
    {
        return Execute(preview, token, progress, move: false);
    }

    public CopyResult Move(Preview preview, CancellationToken token, IProgress<OperationProgress>? progress = null)
    {
        return Execute(preview, token, progress, move: true);
    }

    private CopyResult Execute(Preview preview, CancellationToken token, IProgress<OperationProgress>? progress,
        bool move)
    {
        // Store the log outside the photo tree so preview and destination indexing ignore it.
        var logDir = logDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PhotoOrganiser", "Logs");
        Directory.CreateDirectory(logDir);
        var logPath = Path.Combine(logDir, $"{(move ? "move" : "copy")}-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.jsonl");
        using var log = new StreamWriter(new FileStream(logPath, FileMode.CreateNew, FileAccess.Write))
            { AutoFlush = true };
        void Write(string status, string? source = null, string? destination = null, string? note = null) =>
            log.WriteLine(JsonSerializer.Serialize(new { utc = DateTimeOffset.UtcNow, status, source, destination, note }));

        Write("Started", preview.Source, preview.Destination, move ? "Move" : "Copy");
        int copied = 0, moved = 0, skipped = 0, errors = 0;
        bool cancelled = false;
        try
        {
            CheckPath(preview.Destination);
            // Rebuild the index because destination contents can change while the user reviews the plan.
            var issues = new List<Entry>();
            var hashes = Index(preview.Destination, issues, token, progress);
            foreach (var issue in issues)
            {
                Write(issue.Action, issue.Source, note: issue.Note);
                if (issue.Action == "Error") errors++;
            }

            var processed = 0;
            foreach (var row in preview.Entries)
            {
                token.ThrowIfCancellationRequested();
                progress?.Report(new(move ? "Processing move plan" : "Processing copy plan", row.Source, processed++, preview.Entries.Count));
                if (row.Action is "Error" or "Skipped")
                {
                    Write(row.Action, row.Source, note: row.Note);
                    if (row.Action == "Error") errors++;
                    else skipped++;
                    continue;
                }

                string? temporary = null;
                string? target = null;
                string status = "Error";
                string? note = null;
                try
                {
                    // Confirm both files still match before treating this row as a duplicate.
                    if (hashes.TryGetValue(row.Hash, out var match) && Matches(match, row.Hash, token))
                    {
                        if (!Matches(row.Source, row.Hash, token))
                            throw new IOException("The source changed or is unavailable. Preview again.");
                        status = "Duplicate";
                        if (move)
                            note = "Original kept because an identical photo already exists.";
                        target = match;
                        skipped++;
                    }
                    // A duplicate row has no reviewed copy target if its matching file disappears.
                    else if (row.Action == "Duplicate")
                    {
                        errors++;
                        note = "The matching copy is unavailable. Preview again to include this photo.";
                    }
                    else
                    {
                        CheckPath(row.Source);
                        var folder = Path.GetDirectoryName(row.Destination)!;
                        CheckPath(folder);
                        Directory.CreateDirectory(folder);
                        // Stage in the destination folder so publishing is a same-directory rename.
                        temporary = Path.Combine(folder, $".photo-organiser-{Guid.NewGuid():N}.tmp");
                        using var input = move ? MoveSource.Open(row.Source) : OpenRead(row.Source);
                        Stage(input, temporary, row.Hash, token);
                        target = Publish(temporary, row.Destination, token);
                        temporary = null;
                        hashes[row.Hash] = target;
                        note = Paths.Equals(target, row.Destination) ? row.Note : "Name changed after preview to avoid a collision.";
                        if (move)
                        {
                            // Keep both handles open during verification and removal. A changed or
                            // inaccessible destination leaves the original in place.
                            using var published = OpenRead(target);
                            if (Hash(published, token) != row.Hash)
                                throw new IOException("The published photo failed verification.");
                            token.ThrowIfCancellationRequested();
                            MoveSource.Remove(input.SafeFileHandle);
                            moved++;
                            status = "Moved";
                        }
                        else
                        {
                            copied++;
                            status = "Copied";
                        }
                    }
                }
                catch (Exception error) when (IsFileError(error))
                {
                    errors++;
                    if (move && target is not null)
                    {
                        status = "Move incomplete";
                        note = $"Original kept. A destination copy was published. {error.Message}";
                    }
                    else
                    {
                        note = error.Message;
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested && move && target is not null)
                {
                    Write("Move incomplete", row.Source, target, "Cancelled after publishing. Original kept.");
                    throw;
                }
                // Remove incomplete staging files on errors or cooperative cancellation.
                finally
                {
                    if (temporary is not null)
                    {
                        try { File.Delete(temporary); }
                        catch (Exception error) when (IsFileError(error))
                        {
                            errors++;
                            Write("Cleanup error", row.Source, temporary, error.Message);
                        }
                    }
                }

                Write(status, row.Source, target ?? row.Destination, note);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            cancelled = true;
        }
        catch (Exception error) when (IsFileError(error))
        {
            errors++;
            Write("Run error", note: error.Message);
        }

        Write(cancelled ? "Cancelled" : "Finished", note: $"Copied {copied}, moved {moved}, skipped {skipped}, errors {errors}.");
        return new(copied, skipped, errors, cancelled, logPath, moved);
    }

    // Keep one matching destination path per hash for duplicate checks.
    private static Dictionary<string, string> Index(string destination, List<Entry> issues,
        CancellationToken token, IProgress<OperationProgress>? progress)
    {
        progress?.Report(new("Checking destination photos", destination));
        var index = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!Directory.Exists(destination))
            return index;

        foreach (var path in FindPhotos(destination, issues, token, progress, "Checking destination photos"))
        {
            progress?.Report(new("Checking destination photos", path));

            try
            {
                using var stream = OpenRead(path);
                index.TryAdd(Hash(stream, token), path);
            }
            catch (Exception error) when (IsFileError(error))
            {
                issues.Add(Issue(path, error.Message));
            }
        }

        return index;
    }

    private static IEnumerable<string> FindPhotos(string root, List<Entry> issues, CancellationToken token,
        IProgress<OperationProgress>? progress, string phase, bool includeSubfolders = true)
    {
        // Walk one directory at a time so an unreadable directory does not abort its siblings.
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var folder))
        {
            token.ThrowIfCancellationRequested();
            progress?.Report(new(phase, folder));
            string[] children;
            try { children = Directory.GetFileSystemEntries(folder); }
            catch (Exception error) when (IsFileError(error))
            {
                issues.Add(Issue(folder, error.Message));
                continue;
            }

            foreach (var path in children)
            {
                token.ThrowIfCancellationRequested();
                FileAttributes attributes;
                try { attributes = File.GetAttributes(path); }
                catch (Exception error) when (IsFileError(error))
                {
                    issues.Add(Issue(path, error.Message));
                    continue;
                }
                // A top-level scan ignores directory entries, including links to directories.
                if (!includeSubfolders && attributes.HasFlag(FileAttributes.Directory))
                    continue;

                // Avoid recursing through links or opening cloud placeholders marked as reparse points.
                if (attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    issues.Add(new(path, "", "Skipped", "", "", "Linked or cloud-placeholder entry."));
                    continue;
                }
                if (attributes.HasFlag(FileAttributes.Directory))
                    pending.Push(path);
                else if (Extensions.Contains(Path.GetExtension(path)))
                    yield return path;
            }
        }
    }

    // Include the separator in the prefix so similarly named sibling folders stay distinct.
    private static bool Contains(string folder, string path)
    {
        var prefix = Path.EndsInDirectorySeparator(folder) ? folder : folder + Path.DirectorySeparatorChar;
        return Paths.Equals(folder, path) || path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    // Check existing ancestors too. The destination and its month folders may not exist yet.
    private static void CheckPath(string path)
    {
        for (string? part = path; part is not null; part = Path.GetDirectoryName(part))
        {
            try
            {
                if (File.GetAttributes(part).HasFlag(FileAttributes.ReparsePoint))
                    throw new IOException($"Linked or cloud-placeholder path: {part}");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    private static string FreeName(string path, HashSet<string> reserved, CancellationToken token)
    {
        var candidate = path;
        for (var suffix = 2; Exists(candidate) || reserved.Contains(candidate); suffix++)
        {
            token.ThrowIfCancellationRequested();
            // Fit the suffix inside the Windows filename limit without splitting a surrogate pair.
            var ending = $" ({suffix.ToString(CultureInfo.InvariantCulture)}){Path.GetExtension(path)}";
            var stem = Path.GetFileNameWithoutExtension(path);
            var length = Math.Min(stem.Length, 255 - ending.Length);
            if (length < stem.Length && length > 0 && char.IsHighSurrogate(stem[length - 1]))
                length--;
            candidate = Path.Combine(Path.GetDirectoryName(path)!, stem[..length] + ending);
        }
        return candidate;
    }

    // The move itself forbids overwrites, even if a filename is claimed after the availability check.
    private static string Publish(string temporary, string desired, CancellationToken token)
    {
        var occupied = new HashSet<string>(Paths);
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var candidate = FreeName(desired, occupied, token);
            try
            {
                File.Move(temporary, candidate, overwrite: false);
                return candidate;
            }
            catch (IOException) when (Exists(candidate)) { occupied.Add(candidate); }
        }
    }

    // Hash bytes as they are copied and only publish when they match the reviewed source.
    private static void Stage(FileStream input, string temporary, string expectedHash, CancellationToken token)
    {
        using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            Transfer(input, hash, token, output);
            if (Convert.ToHexString(hash.GetHashAndReset()) != expectedHash)
                throw new IOException("The source changed after preview. Preview again.");
            output.Flush(flushToDisk: true);
        }
        File.SetLastWriteTimeUtc(temporary, File.GetLastWriteTimeUtc(input.SafeFileHandle));
    }

    private static bool Matches(string path, string expectedHash, CancellationToken token)
    {
        try
        {
            CheckPath(path);
            using var stream = OpenRead(path);
            return Hash(stream, token) == expectedHash;
        }
        catch (Exception error) when (IsFileError(error)) { return false; }
    }

    private static string Hash(Stream stream, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Transfer(stream, hash, token);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    // Use bounded chunks for both hashing and copying, checking cancellation between reads.
    private static void Transfer(Stream input, IncrementalHash hash, CancellationToken token, Stream? output = null)
    {
        var buffer = new byte[64 * 1024];
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var read = input.Read(buffer);
            if (read == 0)
                return;
            hash.AppendData(buffer, 0, read);
            output?.Write(buffer, 0, read);
        }
    }

    private static FileStream OpenRead(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);

    private static Entry Issue(string path, string message) => new(path, "", "Error", "", "", message);

    private static bool IsFileError(Exception error) =>
        error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException;
}
