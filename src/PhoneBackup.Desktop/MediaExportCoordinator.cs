using PhoneBackup.Core;

namespace PhoneBackup.Desktop;

public sealed record MediaExportReport(
    int CopiedFiles,
    int SkippedFiles,
    int FailedFiles,
    long CopiedBytes,
    long TotalBytes,
    IReadOnlyList<string> Errors);

public sealed class MediaExportCoordinator
{
    public async Task<MediaExportReport> ExportAsync(
        AgentClient agent,
        string destination,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(destination))
            throw new ArgumentException("Выберите папку для фото и видео.", nameof(destination));

        var destinationRoot = Path.GetFullPath(destination);
        Directory.CreateDirectory(destinationRoot);

        var capability = await agent.GetMediaCapabilityAsync(cancellationToken);
        if (!capability.Images && !capability.Videos)
            throw new UnauthorizedAccessException(
                "На телефоне не разрешён доступ к фото и видео. " +
                "Откройте VeXArk Agent и нажмите «Разрешить фото и видео».");

        progress?.Report(new("media-scan", "Чтение каталога MediaStore", 0, 0));
        var entries = new List<FileEntry>();
        await foreach (var entry in agent.ScanMediaAsync(cancellationToken))
        {
            if (entry.Kind != "file" ||
                string.IsNullOrWhiteSpace(entry.LinkTarget) ||
                !RestorePathPolicy.IsSafeRelativePath(entry.RelativePath))
                continue;
            entries.Add(entry);
        }

        var totalBytes = entries.Sum(x => x.Size);
        var usedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var copied = 0;
        var skipped = 0;
        var copiedBytes = 0L;
        var errors = new List<string>();

        for (var index = 0; index < entries.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = entries[index];
            var relative = MakeUniqueRelativePath(
                MakeWindowsSafeRelativePath(entry.RelativePath),
                entry.LinkTarget!,
                usedPaths);
            var target = RestorePathPolicy.ResolveUnderRoot(destinationRoot, relative);
            progress?.Report(new(
                "media-copy",
                relative,
                index,
                entries.Count));

            try
            {
                if (IsUnchanged(target, entry))
                {
                    skipped++;
                    continue;
                }

                var parent = Path.GetDirectoryName(target)
                    ?? throw new InvalidDataException("Некорректный путь назначения.");
                Directory.CreateDirectory(parent);
                var temporary = Path.Combine(
                    parent,
                    $".{Path.GetFileName(target)}.phonebackup-{Guid.NewGuid():N}.part");
                try
                {
                    await using (var source = await agent.OpenMediaFileAsync(
                                     entry.LinkTarget!,
                                     cancellationToken))
                    await using (var output = new FileStream(
                                     temporary,
                                     FileMode.CreateNew,
                                     FileAccess.Write,
                                     FileShare.None,
                                     256 * 1024,
                                     FileOptions.Asynchronous | FileOptions.SequentialScan))
                    {
                        await source.CopyToAsync(output, 256 * 1024, cancellationToken);
                        await output.FlushAsync(cancellationToken);
                        output.Flush(flushToDisk: true);
                    }

                    if (new FileInfo(temporary).Length != entry.Size)
                        throw new InvalidDataException(
                            $"Размер полученного файла не совпал: {relative}");
                    File.Move(temporary, target, overwrite: true);
                    ApplyTimestamp(target, entry.ModifiedUnixNanos);
                    copied++;
                    copiedBytes += entry.Size;
                }
                finally
                {
                    if (File.Exists(temporary))
                        File.Delete(temporary);
                }
            }
            catch (Exception error) when (
                error is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                if (errors.Count < 50)
                    errors.Add($"{relative}: {error.Message}");
            }
        }

        progress?.Report(new("media-complete", "Копирование завершено", entries.Count, entries.Count));
        return new(
            copied,
            skipped,
            entries.Count - copied - skipped,
            copiedBytes,
            totalBytes,
            errors);
    }

    private static bool IsUnchanged(string target, FileEntry entry)
    {
        if (!File.Exists(target) || entry.ModifiedUnixNanos <= 0)
            return false;
        var info = new FileInfo(target);
        if (info.Length != entry.Size)
            return false;
        var sourceSeconds = entry.ModifiedUnixNanos / 1_000_000_000L;
        var targetSeconds = new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeSeconds();
        return Math.Abs(sourceSeconds - targetSeconds) <= 2;
    }

    private static void ApplyTimestamp(string target, long unixNanos)
    {
        if (unixNanos <= 0)
            return;
        try
        {
            File.SetLastWriteTimeUtc(
                target,
                DateTimeOffset.FromUnixTimeSeconds(unixNanos / 1_000_000_000L).UtcDateTime);
        }
        catch (ArgumentOutOfRangeException)
        {
            // A broken MediaStore timestamp must not invalidate an otherwise complete copy.
        }
    }

    private static string MakeWindowsSafeRelativePath(string source)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var segments = source.Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(segment =>
            {
                var clean = new string(segment
                    .Select(character => invalid.Contains(character) ? '_' : character)
                    .ToArray())
                    .Trim()
                    .TrimEnd('.');
                if (clean.Length == 0)
                    clean = "_";
                var stem = Path.GetFileNameWithoutExtension(clean);
                if (ReservedWindowsNames.Contains(stem))
                    clean = "_" + clean;
                return clean;
            });
        var result = Path.Combine(segments.ToArray());
        if (!RestorePathPolicy.IsSafeRelativePath(result))
            throw new InvalidDataException($"Небезопасный путь MediaStore: {source}");
        return result;
    }

    private static string MakeUniqueRelativePath(
        string relative,
        string contentUri,
        ISet<string> used)
    {
        if (used.Add(relative))
            return relative;
        var directory = Path.GetDirectoryName(relative);
        var extension = Path.GetExtension(relative);
        var name = Path.GetFileNameWithoutExtension(relative);
        var mediaId = contentUri.TrimEnd('/').Split('/').LastOrDefault() ?? "copy";
        var candidateName = $"{name} ({mediaId}){extension}";
        var candidate = string.IsNullOrEmpty(directory)
            ? candidateName
            : Path.Combine(directory, candidateName);
        var suffix = 2;
        while (!used.Add(candidate))
        {
            candidateName = $"{name} ({mediaId}-{suffix++}){extension}";
            candidate = string.IsNullOrEmpty(directory)
                ? candidateName
                : Path.Combine(directory, candidateName);
        }
        return candidate;
    }

    private static readonly HashSet<string> ReservedWindowsNames =
        new(
            [
                "CON", "PRN", "AUX", "NUL",
                "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
                "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
            ],
            StringComparer.OrdinalIgnoreCase);
}
