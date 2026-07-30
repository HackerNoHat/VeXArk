using PhoneBackup.Core;

namespace PhoneBackup.Desktop;

public sealed class BackupCoordinator(AdbService adb)
{
    public async Task<SnapshotManifest> BackupApksAsync(
        string serial,
        DeviceInventory device,
        IReadOnlyList<PackageSnapshot> packages,
        EncryptedRepository repository,
        bool includeSystemApps,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default,
        string purpose = "manual")
    {
        var snapshots = await repository.ListSnapshotsAsync(cancellationToken);
        var previous = LatestForDevice(snapshots, device);
        var selected = SelectPackages(packages, includeSystemApps);
        var components = await BuildApkComponentsAsync(
            serial, selected, repository, previous, progress, cancellationToken);
        return await CommitAsync(
            device, components, repository, purpose, progress, selected.Count, cancellationToken);
    }

    public async Task<SnapshotManifest> BackupPortableAsync(
        string serial,
        DeviceInventory device,
        IReadOnlyList<PackageSnapshot> packages,
        EncryptedRepository repository,
        AgentClient agent,
        bool includeSystemApps,
        bool includeApks,
        bool includeAppData,
        bool includeSharedStorage,
        bool includePersonalData,
        bool includeSystemState,
        bool includeFullComponents,
        bool includeCaches,
        bool fullHash,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default,
        string purpose = "manual")
    {
        var snapshots = await repository.ListSnapshotsAsync(cancellationToken);
        var previous = LatestForDevice(snapshots, device);
        var selected = SelectPackages(packages, includeSystemApps);
        var failures = new List<BackupFailure>();
        var components = includeApks
            ? await BuildApkComponentsAsync(
                serial, selected, repository, previous, progress, cancellationToken)
            : new List<BackupComponentManifest>();
        if (includeAppData)
        {
            var appData = await BuildAppDataComponentsAsync(
                selected,
                repository,
                agent,
                previous,
                includeCaches,
                fullHash,
                progress,
                failures,
                cancellationToken);
            components.AddRange(appData);
        }
        var excludedMedia = new ExcludedMediaReport(0, 0, []);
        if (includeSharedStorage)
        {
            var shared = await BuildSharedStorageComponentsAsync(
                repository, agent, previous, progress, failures, cancellationToken);
            components.AddRange(shared.Components);
            excludedMedia = shared.Excluded;
        }
        if (includePersonalData)
        {
            var personal = await BuildPersonalDataComponentsAsync(
                repository, agent, progress, cancellationToken);
            components.AddRange(personal);
            components.Add(await BuildAccountInventoryComponentAsync(
                repository, agent, progress, cancellationToken));
        }
        if (includeSystemState)
            components.Add(await BuildSystemStateComponentAsync(
                repository, agent, progress, cancellationToken));
        if (includeFullComponents)
        {
            var full = await BuildFullComponentsAsync(
                repository, agent, previous, progress, failures, cancellationToken);
            components.AddRange(full);
        }
        if (failures.Count > 0)
            components.Add(await BuildFailureReportComponentAsync(
                repository, failures, cancellationToken));
        return await CommitAsync(
            device, components, repository, purpose, progress, selected.Count,
            cancellationToken, excludedMedia,
            includeFullComponents ? BackupMode.Full : BackupMode.Portable);
    }

    private async Task<List<BackupComponentManifest>> BuildApkComponentsAsync(
        string serial,
        IReadOnlyList<PackageSnapshot> selected,
        EncryptedRepository repository,
        SnapshotManifest? previous,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        var reusable = previous?.Components
            .Where(x => x.Kind == "apk")
            .SelectMany(x => x.Files)
            .Where(x => !string.IsNullOrWhiteSpace(x.ContentHash) && x.Chunks is not null)
            .ToDictionary(x => (x.RelativePath, x.ContentHash!), StringTupleComparer.Ordinal)
            ?? new Dictionary<(string, string), FileEntry>(StringTupleComparer.Ordinal);

        var components = new List<BackupComponentManifest>(selected.Count);
        var itemIndex = 0L;
        foreach (var package in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new("apk", package.Label, itemIndex, selected.Count));
            var files = new List<FileEntry>();
            foreach (var artifact in package.ApkArtifacts)
            {
                var relativePath = $"{package.PackageName}/{SafeFileName(artifact.FileName)}";
                if (reusable.TryGetValue((relativePath, artifact.Sha256), out var old) &&
                    old.Size == artifact.Size)
                {
                    files.Add(old);
                    continue;
                }

                await using var input = await adb.OpenRemoteFileAsync(
                    serial, artifact.Path, cancellationToken);
                var chunks = await repository.PutStreamAsync(input, cancellationToken);
                files.Add(new(
                    relativePath,
                    "file",
                    artifact.Size,
                    artifact.ModifiedUnixNanos,
                    0,
                    package.Uid,
                    package.Uid,
                    null,
                    null,
                    artifact.Sha256,
                    chunks));
            }
            components.Add(Component(package, "apk", package.PackageName, files));
            itemIndex++;
        }
        return components;
    }

    private static async Task<List<BackupComponentManifest>> BuildAppDataComponentsAsync(
        IReadOnlyList<PackageSnapshot> selected,
        EncryptedRepository repository,
        AgentClient agent,
        SnapshotManifest? previous,
        bool includeCaches,
        bool fullHash,
        IProgress<TransferProgress>? progress,
        List<BackupFailure> failures,
        CancellationToken cancellationToken)
    {
        var components = new List<BackupComponentManifest>();
        await using var scanner = await agent.ConnectSiblingAsync(cancellationToken);
        await using var reader = await agent.ConnectSiblingAsync(cancellationToken);
        var packageIndex = 0L;
        foreach (var package in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new("app-data", package.Label, packageIndex, selected.Count));
            await agent.BeginPackageSnapshotAsync(package.PackageName, cancellationToken);
            try
            {
                for (var rootIndex = 0; rootIndex < package.DataPaths.Count; rootIndex++)
                {
                    var root = package.DataPaths[rootIndex];
                    var componentId = $"{package.PackageName}:app-data:{rootIndex}";
                    var reusable = previous?.Components
                        .FirstOrDefault(x => x.Id == componentId && x.Kind == "app-data")
                        ?.Files.ToDictionary(x => x.RelativePath, StringComparer.Ordinal)
                        ?? new Dictionary<string, FileEntry>(StringComparer.Ordinal);
                    var files = new List<FileEntry>();
                    try
                    {
                        await foreach (var entry in scanner.ScanRootAsync(
                                           root, includeCaches, fullHash, cancellationToken))
                        {
                            if (!RestorePathPolicy.IsSafeRelativePath(entry.RelativePath))
                                throw new InvalidDataException(
                                    $"Agent returned unsafe path: {entry.RelativePath}");
                            if (reusable.TryGetValue(entry.RelativePath, out var old) &&
                                CanReuse(old, entry))
                            {
                                files.Add(old);
                                continue;
                            }
                            if (entry.Kind != "file")
                            {
                                files.Add(entry with { Chunks = null });
                                continue;
                            }
                            var chunks = await TryPutFileAsync(
                                () => reader.OpenRootFileAsync(
                                    root, entry.RelativePath, cancellationToken),
                                repository,
                                entry,
                                new(
                                    componentId,
                                    package.PackageName,
                                    entry.RelativePath,
                                    "root_read",
                                    "",
                                    ""),
                                failures,
                                cancellationToken);
                            if (chunks is null) continue;
                            files.Add(entry with { Chunks = chunks });
                        }
                    }
                    catch (Exception error) when (
                        IsRecoverableScanFailure(error, cancellationToken))
                    {
                        failures.Add(new(
                            componentId,
                            package.PackageName,
                            "",
                            "root_scan",
                            error.GetType().FullName ?? error.GetType().Name,
                            error.Message));
                    }
                    components.Add(Component(package, "app-data", componentId, files));
                }
            }
            finally
            {
                await EndPackageSnapshotAsync(
                    agent,
                    package.PackageName,
                    cancellationToken);
            }
            packageIndex++;
        }
        return components;
    }

    private static async Task EndPackageSnapshotAsync(
        AgentClient agent,
        string packageName,
        CancellationToken operationToken)
    {
        if (!operationToken.IsCancellationRequested)
        {
            await agent.EndPackageSnapshotAsync(packageName, operationToken);
            return;
        }

        using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await agent.EndPackageSnapshotAsync(packageName, cleanupTimeout.Token);
        }
        catch (Exception error)
        {
            DesktopDiagnostics.Log(
                "warn",
                "backup",
                "package_cleanup_failed",
                "Could not release package snapshot after cancellation",
                result: "failed",
                error: error,
                fields: new Dictionary<string, object?>
                {
                    ["packageName"] = packageName
                });
        }
    }

    private static bool CanReuse(FileEntry old, FileEntry current)
    {
        if (old.Kind != current.Kind || old.Size != current.Size ||
            old.ModifiedUnixNanos != current.ModifiedUnixNanos)
            return false;
        if (!string.IsNullOrWhiteSpace(current.ContentHash) &&
            !string.Equals(old.ContentHash, current.ContentHash, StringComparison.OrdinalIgnoreCase))
            return false;
        return current.Kind != "file" || old.Chunks is not null;
    }

    private static async Task<SharedBuildResult> BuildSharedStorageComponentsAsync(
        EncryptedRepository repository,
        AgentClient agent,
        SnapshotManifest? previous,
        IProgress<TransferProgress>? progress,
        List<BackupFailure> failures,
        CancellationToken cancellationToken)
    {
        var capability = await agent.GetSharedStorageAsync(cancellationToken);
        if (!capability.AccessGranted)
            throw new UnauthorizedAccessException(
                "На Agent не выдан «Доступ ко всем файлам». Откройте Agent и нажмите «Доступ к файлам».");

        var components = new List<BackupComponentManifest>();
        await using var scanner = await agent.ConnectSiblingAsync(cancellationToken);
        await using var reader = await agent.ConnectSiblingAsync(cancellationToken);
        long excludedCount = 0;
        long excludedBytes = 0;
        var samples = new List<string>();
        var index = 0L;
        foreach (var root in capability.Roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new("shared-storage", root, index, capability.Roots.Count));
            var componentId = $"shared:{root.Replace('\\', '/').Trim('/')}";
            var reusable = previous?.Components
                .FirstOrDefault(x => x.Id == componentId && x.Kind == "shared-storage")
                ?.Files.ToDictionary(x => x.RelativePath, StringComparer.Ordinal)
                ?? new Dictionary<string, FileEntry>(StringComparer.Ordinal);
            var files = new List<FileEntry>();
            try
            {
                await foreach (var entry in scanner.ScanSharedAsync(root, cancellationToken))
                {
                    if (!RestorePathPolicy.IsSafeRelativePath(entry.RelativePath))
                        throw new InvalidDataException(
                            $"Agent returned unsafe shared path: {entry.RelativePath}");
                    if (entry.Kind == "excluded")
                    {
                        excludedCount++;
                        excludedBytes += entry.Size;
                        if (samples.Count < 20) samples.Add($"{root}/{entry.RelativePath}");
                        continue;
                    }
                    if (reusable.TryGetValue(entry.RelativePath, out var old) &&
                        CanReuse(old, entry))
                    {
                        files.Add(old);
                        continue;
                    }
                    if (entry.Kind != "file")
                    {
                        files.Add(entry with { Chunks = null });
                        continue;
                    }
                    var chunks = await TryPutFileAsync(
                        () => reader.OpenSharedFileAsync(
                            root, entry.RelativePath, cancellationToken),
                        repository,
                        entry,
                        new(
                            componentId,
                            "shared-storage",
                            entry.RelativePath,
                            "shared_read",
                            "",
                            ""),
                        failures,
                        cancellationToken);
                    if (chunks is null) continue;
                    files.Add(entry with { Chunks = chunks });
                }
            }
            catch (Exception error) when (
                IsRecoverableScanFailure(error, cancellationToken))
            {
                failures.Add(new(
                    componentId,
                    "shared-storage",
                    "",
                    "shared_scan",
                    error.GetType().FullName ?? error.GetType().Name,
                    error.Message));
            }
            components.Add(new(
                componentId,
                "shared-storage",
                files,
                files.Where(x => x.Kind == "file").Sum(x => x.Size),
                files.SelectMany(x => x.Chunks ?? []).Sum(x => (long)x.StoredLength)));
            index++;
        }
        return new(
            components,
            new(excludedCount, excludedBytes, samples));
    }

    private static async Task<IReadOnlyList<BackupComponentManifest>> BuildPersonalDataComponentsAsync(
        EncryptedRepository repository,
        AgentClient agent,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        var capability = await agent.GetPersonalDataAsync(cancellationToken);
        var exports = new[]
        {
            (Kind: "contacts", FileName: "contacts.vcf", Granted: capability.Contacts),
            (Kind: "messages", FileName: "messages.jsonl", Granted: capability.Messages),
            (Kind: "calls", FileName: "calls.jsonl", Granted: capability.Calls)
        };
        var components = new List<BackupComponentManifest>();
        for (var index = 0; index < exports.Length; index++)
        {
            var item = exports[index];
            progress?.Report(new("personal-data", item.Kind, index, exports.Length));
            if (!item.Granted)
                continue;
            await using var input = await agent.OpenPersonalDataAsync(item.Kind, cancellationToken);
            var chunks = await repository.PutStreamAsync(input, cancellationToken);
            var plainBytes = chunks.Sum(x => (long)x.PlainLength);
            components.Add(new(
                $"personal:{item.Kind}",
                "personal-data",
                [
                    new(
                        item.FileName,
                        "file",
                        plainBytes,
                        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000,
                        0,
                        0,
                        0,
                        null,
                        null,
                        null,
                        chunks)
                ],
                plainBytes,
                chunks.Sum(x => (long)x.StoredLength)));
        }

        var permissionReport = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new
        {
            contacts = capability.Contacts,
            messages = capability.Messages,
            calls = capability.Calls
        });
        await using var reportStream = new MemoryStream(permissionReport, writable: false);
        var reportChunks = await repository.PutStreamAsync(reportStream, cancellationToken);
        components.Add(new(
            "personal:permissions",
            "personal-data-report",
            [
                new(
                    "permissions.json",
                    "file",
                    permissionReport.LongLength,
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000,
                    0,
                    0,
                    0,
                    null,
                    null,
                    null,
                    reportChunks)
            ],
            permissionReport.LongLength,
            reportChunks.Sum(x => (long)x.StoredLength)));
        return components;
    }

    private static async Task<BackupComponentManifest> BuildSystemStateComponentAsync(
        EncryptedRepository repository,
        AgentClient agent,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report(new("system-state", "Безопасные настройки и роли", 0, 1));
        var state = await agent.ExportSystemStateAsync(cancellationToken);
        await using var input = new MemoryStream(state, writable: false);
        var chunks = await repository.PutStreamAsync(input, cancellationToken);
        return new(
            "portable.system_state",
            "system-state",
            [
                new(
                    "system-state.json",
                    "file",
                    state.LongLength,
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000,
                    0, 0, 0, null, null, null, chunks)
            ],
            state.LongLength,
            chunks.Sum(x => (long)x.StoredLength));
    }

    private static async Task<BackupComponentManifest> BuildAccountInventoryComponentAsync(
        EncryptedRepository repository,
        AgentClient agent,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report(new(
            "account-inventory",
            "Список аккаунтов без паролей и токенов",
            0,
            1));
        var inventory = await agent.ExportAccountInventoryAsync(cancellationToken);
        await using var input = new MemoryStream(inventory, writable: false);
        var chunks = await repository.PutStreamAsync(input, cancellationToken);
        return new(
            "portable.account_inventory",
            "account-inventory",
            [
                new(
                    "accounts.json",
                    "file",
                    inventory.LongLength,
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000,
                    0, 0, 0, null, null, null, chunks)
            ],
            inventory.LongLength,
            chunks.Sum(x => (long)x.StoredLength));
    }

    private static async Task<IReadOnlyList<BackupComponentManifest>> BuildFullComponentsAsync(
        EncryptedRepository repository,
        AgentClient agent,
        SnapshotManifest? previous,
        IProgress<TransferProgress>? progress,
        List<BackupFailure> failures,
        CancellationToken cancellationToken)
    {
        var roots = new[]
        {
            (Id: "full.wifi", Root: "/data/misc/apexdata/com.android.wifi/"),
            (Id: "full.root_modules", Root: "/data/adb/modules/")
        };
        var components = new List<BackupComponentManifest>();
        await using var scanner = await agent.ConnectSiblingAsync(cancellationToken);
        await using var reader = await agent.ConnectSiblingAsync(cancellationToken);
        for (var index = 0; index < roots.Length; index++)
        {
            var source = roots[index];
            progress?.Report(new("full", source.Id, index, roots.Length));
            var reusable = previous?.Components
                .FirstOrDefault(x => x.Id == source.Id && x.Kind == "full")
                ?.Files.ToDictionary(x => x.RelativePath, StringComparer.Ordinal)
                ?? new Dictionary<string, FileEntry>(StringComparer.Ordinal);
            var files = new List<FileEntry>();
            try
            {
                await foreach (var entry in scanner.ScanRootAsync(
                                   source.Root,
                                   includeCaches: false,
                                   fullHash: true,
                                   cancellationToken))
                {
                    if (!RestorePathPolicy.IsSafeRelativePath(entry.RelativePath))
                        throw new InvalidDataException(
                            $"Agent returned unsafe Full path: {entry.RelativePath}");
                    if (reusable.TryGetValue(entry.RelativePath, out var old) &&
                        CanReuse(old, entry))
                    {
                        files.Add(old);
                        continue;
                    }
                    if (entry.Kind != "file")
                    {
                        files.Add(entry with { Chunks = null });
                        continue;
                    }
                    var chunks = await TryPutFileAsync(
                        () => reader.OpenRootFileAsync(
                            source.Root, entry.RelativePath, cancellationToken),
                        repository,
                        entry,
                        new(
                            source.Id,
                            "full",
                            entry.RelativePath,
                            "root_read",
                            "",
                            ""),
                        failures,
                        cancellationToken);
                    if (chunks is null) continue;
                    files.Add(entry with { Chunks = chunks });
                }
            }
            catch (Exception error) when (
                IsRecoverableScanFailure(error, cancellationToken))
            {
                failures.Add(new(
                    source.Id,
                    "full",
                    "",
                    "root_scan",
                    error.GetType().FullName ?? error.GetType().Name,
                    error.Message));
            }
            components.Add(new(
                source.Id,
                "full",
                files,
                files.Where(x => x.Kind == "file").Sum(x => x.Size),
                files.SelectMany(x => x.Chunks ?? []).Sum(x => (long)x.StoredLength)));
        }
        return components;
    }

    private static async Task<IReadOnlyList<ChunkReference>?> TryPutFileAsync(
        Func<Task<Stream>> open,
        EncryptedRepository repository,
        FileEntry entry,
        BackupFailure failure,
        List<BackupFailure> failures,
        CancellationToken cancellationToken)
    {
        Exception? lastError = null;
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                await using var input = await open();
                var chunks = await repository.PutStreamAsync(input, cancellationToken);
                var copiedBytes = chunks.Sum(x => (long)x.PlainLength);
                if (copiedBytes != entry.Size)
                    throw new IOException(
                        $"File changed during backup: scanned={entry.Size}, read={copiedBytes}.");
                return chunks;
            }
            catch (IOException error) when (!cancellationToken.IsCancellationRequested)
            {
                lastError = error;
                if (attempt == 1)
                    await Task.Delay(100, cancellationToken);
            }
        }

        failures.Add(failure with
        {
            ErrorType = lastError?.GetType().FullName ?? "System.IO.IOException",
            Message = lastError?.Message ?? "File could not be read after two attempts."
        });
        return null;
    }

    private static bool IsRecoverableScanFailure(
        Exception error,
        CancellationToken cancellationToken) =>
        !cancellationToken.IsCancellationRequested &&
        (error is InvalidOperationException ||
         error is IOException);

    private static async Task<BackupComponentManifest> BuildFailureReportComponentAsync(
        EncryptedRepository repository,
        IReadOnlyList<BackupFailure> failures,
        CancellationToken cancellationToken)
    {
        var report = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
            new
            {
                generatedAtUtc = DateTimeOffset.UtcNow,
                count = failures.Count,
                failures
            },
            new System.Text.Json.JsonSerializerOptions(
                System.Text.Json.JsonSerializerDefaults.Web));
        await using var input = new MemoryStream(report, writable: false);
        var chunks = await repository.PutStreamAsync(input, cancellationToken);
        return new(
            "backup.failures",
            "backup-report",
            [
                new(
                    "backup-failures.json",
                    "file",
                    report.LongLength,
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000,
                    0,
                    0,
                    0,
                    null,
                    null,
                    null,
                    chunks)
            ],
            report.LongLength,
            chunks.Sum(x => (long)x.StoredLength));
    }

    private static BackupComponentManifest Component(
        PackageSnapshot package,
        string kind,
        string id,
        IReadOnlyList<FileEntry> files) =>
        new(
            id,
            kind,
            files,
            files.Where(x => x.Kind == "file").Sum(x => x.Size),
            files.SelectMany(x => x.Chunks ?? []).Sum(x => (long)x.StoredLength),
            new(
                package.PackageName,
                package.VersionCode,
                package.VersionName,
                package.SigningCertificateSha256,
                package.IsEnabled,
                package.RuntimePermissions,
                package.BatteryOptimizationExempt));

    private static async Task<SnapshotManifest> CommitAsync(
        DeviceInventory device,
        IReadOnlyList<BackupComponentManifest> components,
        EncryptedRepository repository,
        string purpose,
        IProgress<TransferProgress>? progress,
        int packageCount,
        CancellationToken cancellationToken,
        ExcludedMediaReport? excludedMedia = null,
        BackupMode mode = BackupMode.Portable)
    {
        var manifest = new SnapshotManifest(
            1,
            Guid.NewGuid().ToString("N"),
            DateTimeOffset.UtcNow,
            mode,
            device,
            components,
            excludedMedia ?? new(0, 0, []),
            purpose);
        await repository.CommitSnapshotAsync(manifest, cancellationToken);
        progress?.Report(new("complete", manifest.SnapshotId, packageCount, packageCount));
        return manifest;
    }

    private static SnapshotManifest? LatestForDevice(
        IReadOnlyList<SnapshotManifest> snapshots,
        DeviceInventory device) =>
        snapshots.FirstOrDefault(x =>
            x.Device.StableId == device.StableId && x.Purpose == "manual");

    private static List<PackageSnapshot> SelectPackages(
        IReadOnlyList<PackageSnapshot> packages,
        bool includeSystemApps) =>
        packages
            .Where(x => includeSystemApps || !x.IsSystem)
            .Where(x => x.PackageName != AdbService.AgentPackage)
            .OrderBy(x => x.PackageName)
            .ToList();

    private static string SafeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var filtered = new string(value.Select(x => invalid.Contains(x) ? '_' : x).ToArray());
        return string.IsNullOrWhiteSpace(filtered) ? "base.apk" : filtered;
    }

    private sealed class StringTupleComparer : IEqualityComparer<(string Path, string Hash)>
    {
        public static readonly StringTupleComparer Ordinal = new();
        public bool Equals((string Path, string Hash) x, (string Path, string Hash) y) =>
            string.Equals(x.Path, y.Path, StringComparison.Ordinal) &&
            string.Equals(x.Hash, y.Hash, StringComparison.OrdinalIgnoreCase);
        public int GetHashCode((string Path, string Hash) obj) =>
            HashCode.Combine(
                StringComparer.Ordinal.GetHashCode(obj.Path),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Hash));
    }

    private sealed record SharedBuildResult(
        IReadOnlyList<BackupComponentManifest> Components,
        ExcludedMediaReport Excluded);

    private sealed record BackupFailure(
        string ComponentId,
        string Source,
        string RelativePath,
        string Stage,
        string ErrorType,
        string Message);
}
