using System.Diagnostics;
using PhoneBackup.Core;
using Windows.Foundation;
using Windows.Media.Import;
using Windows.Storage;

namespace PhoneBackup.Desktop;

public sealed class IPhoneMediaSourceViewModel
{
    internal IPhoneMediaSourceViewModel(PhotoImportSource source)
    {
        Source = source;
        Id = source.Id.ToString();
        DisplayName = BuildDisplayName(source);
        Details = BuildDetails(source);
        IsLocked = source.IsLocked == true;
    }

    internal PhotoImportSource Source { get; }
    public string Id { get; }
    public string DisplayName { get; }
    public string Details { get; }
    public bool IsLocked { get; }

    private static string BuildDisplayName(PhotoImportSource source)
    {
        if (!string.IsNullOrWhiteSpace(source.DisplayName))
            return source.DisplayName;
        if (!string.IsNullOrWhiteSpace(source.Model))
            return source.Model;
        return "iPhone";
    }

    private static string BuildDetails(PhotoImportSource source)
    {
        var parts = new[]
        {
            source.Model,
            source.ConnectionTransport.ToString(),
            source.IsLocked == true ? "заблокирован" : "готов"
        };
        return string.Join(" • ", parts.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct());
    }
}

public sealed record IPhoneMediaTransferMetrics(
    long ImportedItems,
    long TotalItems,
    long ImportedBytes,
    long TotalBytes,
    double BytesPerSecond);

public sealed record IPhoneMediaImportReport(
    string SourceName,
    long ImportedItems,
    long SkippedItems,
    long FailedItems,
    long ImportedBytes,
    long TotalBytes,
    long Photos,
    long Videos,
    double AverageBytesPerSecond,
    IReadOnlyList<string> Errors);

public sealed class IPhoneMediaImportCoordinator
{
    public async Task<IReadOnlyList<IPhoneMediaSourceViewModel>> DiscoverAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!await PhotoImportManager.IsSupportedAsync())
            return [];

        var sources = await PhotoImportManager.FindAllSourcesAsync();
        cancellationToken.ThrowIfCancellationRequested();
        return sources
            .Where(IsAppleMobileDevice)
            .Select(source => new IPhoneMediaSourceViewModel(source))
            .OrderBy(source => source.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    public async Task<IPhoneMediaImportReport> ImportNewAsync(
        IPhoneMediaSourceViewModel source,
        string destination,
        IProgress<TransferProgress>? progress = null,
        IProgress<IPhoneMediaTransferMetrics>? metrics = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (string.IsNullOrWhiteSpace(destination))
            throw new ArgumentException("Выберите папку для фото и видео.", nameof(destination));
        if (source.IsLocked)
            throw new UnauthorizedAccessException(
                "iPhone заблокирован. Разблокируйте его и нажмите «Доверять» на телефоне.");

        var destinationRoot = Path.GetFullPath(destination);
        Directory.CreateDirectory(destinationRoot);
        var destinationFolder = await StorageFolder.GetFolderFromPathAsync(destinationRoot);
        cancellationToken.ThrowIfCancellationRequested();

        using var session = source.Source.CreateImportSession();
        session.DestinationFolder = destinationFolder;
        session.AppendSessionDateToDestinationFolder = false;
        session.SubfolderCreationMode =
            PhotoImportSubfolderCreationMode.KeepOriginalFolderStructure;

        progress?.Report(new("iphone-scan", source.DisplayName, 0, 100));
        var findOperation = session.FindItemsAsync(
            PhotoImportContentTypeFilter.ImagesAndVideos,
            PhotoImportItemSelectionMode.SelectNew);
        var found = await AwaitWithProgressAsync(
            findOperation,
            cancellationToken,
            percent => progress?.Report(new(
                "iphone-scan",
                source.DisplayName,
                Math.Min(percent, 100u),
                100)));

        if (!found.HasSucceeded)
            throw new IOException(
                "Windows не смогла прочитать медиатеку iPhone. " +
                "Оставьте телефон разблокированным и переподключите кабель.");

        found.SetImportMode(PhotoImportImportMode.ImportEverything);
        var totalItems = (long)found.SelectedTotalCount;
        var totalBytes = ToInt64(found.SelectedTotalSizeInBytes);
        var skippedItems = Math.Max(0L, (long)found.TotalCount - totalItems);
        if (totalItems == 0)
        {
            progress?.Report(new("iphone-complete", source.DisplayName, 0, 0));
            return new(
                source.DisplayName,
                0,
                skippedItems,
                0,
                0,
                totalBytes,
                0,
                0,
                0,
                []);
        }

        var stopwatch = Stopwatch.StartNew();
        progress?.Report(new("iphone-copy", source.DisplayName, 0, totalItems));
        var importOperation = found.ImportItemsAsync();
        var imported = await AwaitWithProgressAsync(
            importOperation,
            cancellationToken,
            value =>
            {
                var elapsedSeconds = Math.Max(stopwatch.Elapsed.TotalSeconds, 0.001);
                var importedBytes = ToInt64(value.BytesImported);
                var currentTotalBytes = ToInt64(value.TotalBytesToImport);
                progress?.Report(new(
                    "iphone-copy",
                    source.DisplayName,
                    value.ItemsImported,
                    value.TotalItemsToImport));
                metrics?.Report(new(
                    value.ItemsImported,
                    value.TotalItemsToImport,
                    importedBytes,
                    currentTotalBytes,
                    importedBytes / elapsedSeconds));
            });
        stopwatch.Stop();

        var importedItems = (long)imported.TotalCount;
        var importedBytesFinal = ToInt64(imported.TotalSizeInBytes);
        var failedItems = Math.Max(0, totalItems - importedItems);
        var errors = imported.HasSucceeded
            ? Array.Empty<string>()
            :
            [
                "Windows завершила импорт не полностью. " +
                "Не отключайте iPhone и повторите копирование: уже импортированные файлы будут пропущены."
            ];
        progress?.Report(new(
            "iphone-complete",
            source.DisplayName,
            importedItems,
            totalItems));

        return new(
            source.DisplayName,
            importedItems,
            skippedItems,
            failedItems,
            importedBytesFinal,
            totalBytes,
            imported.PhotosCount,
            imported.VideosCount,
            stopwatch.Elapsed.TotalSeconds > 0
                ? importedBytesFinal / stopwatch.Elapsed.TotalSeconds
                : 0,
            errors);
    }

    private static async Task<TResult> AwaitWithProgressAsync<TResult, TProgress>(
        IAsyncOperationWithProgress<TResult, TProgress> operation,
        CancellationToken cancellationToken,
        Action<TProgress> onProgress)
    {
        operation.Progress = (_, value) => onProgress(value);
        using var registration = cancellationToken.Register(operation.Cancel);
        return await operation;
    }

    private static bool IsAppleMobileDevice(PhotoImportSource source)
    {
        var identity = string.Join(
            "\n",
            source.Manufacturer,
            source.Model,
            source.DisplayName,
            source.Description);
        return identity.Contains("iphone", StringComparison.OrdinalIgnoreCase) ||
               identity.Contains("ipad", StringComparison.OrdinalIgnoreCase) ||
               identity.Contains("apple", StringComparison.OrdinalIgnoreCase);
    }

    private static long ToInt64(ulong value) =>
        value > long.MaxValue ? long.MaxValue : (long)value;
}
