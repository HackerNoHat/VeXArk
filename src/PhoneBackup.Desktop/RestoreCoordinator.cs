using System.Security.Cryptography;
using PhoneBackup.Core;

namespace PhoneBackup.Desktop;

public sealed class RestoreCoordinator(AdbService adb)
{
    public static void CleanupStaleStaging()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PhoneBackup",
            "staging");
        if (!Directory.Exists(root)) return;
        foreach (var directory in Directory.EnumerateDirectories(root))
            DeleteStaging(directory);
    }

    public CompatibilityReport PlanApkRestore(
        SnapshotManifest snapshot,
        DeviceInventory target,
        IReadOnlyList<PackageSnapshot> installed)
    {
        var items = CompatibilityEngine.Compare(snapshot.Device, target).Items
            .Where(x => x.Component == "portable")
            .ToList();
        var current = installed.ToDictionary(x => x.PackageName, StringComparer.Ordinal);
        foreach (var component in snapshot.Components.Where(x => x.Kind == "apk"))
        {
            var metadata = component.Package;
            if (metadata is null)
            {
                items.Add(new(component.Id, CompatibilityLevel.Blocked, "В snapshot нет metadata подписи."));
                continue;
            }
            if (!current.TryGetValue(metadata.PackageName, out var present))
            {
                items.Add(new(metadata.PackageName, CompatibilityLevel.Safe, "Приложение будет установлено из backup."));
                continue;
            }
            if (!string.Equals(
                    present.SigningCertificateSha256,
                    metadata.SigningCertificateSha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                items.Add(new(metadata.PackageName, CompatibilityLevel.Blocked, "Подпись установленного APK не совпадает."));
            }
            else if (present.VersionCode > metadata.VersionCode)
            {
                items.Add(new(metadata.PackageName, CompatibilityLevel.Conditional, "Установлена более новая версия; downgrade выключен."));
            }
            else
            {
                items.Add(new(metadata.PackageName, CompatibilityLevel.Safe, "Подпись приложения совпадает."));
            }
        }
        var overall = items.Any(x => x.Level == CompatibilityLevel.Blocked)
            ? CompatibilityLevel.Blocked
            : items.Any(x => x.Level == CompatibilityLevel.Conditional)
                ? CompatibilityLevel.Conditional
                : CompatibilityLevel.Safe;
        return new(overall, items);
    }

    public async Task RestoreApksAsync(
        string serial,
        SnapshotManifest snapshot,
        DeviceInventory targetDevice,
        IReadOnlyList<PackageSnapshot> installed,
        EncryptedRepository repository,
        AgentClient agent,
        IReadOnlyCollection<string>? selectedPackages = null,
        bool allowDowngrade = false,
        bool createSafetySnapshot = true,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var components = snapshot.Components
            .Where(x => x.Kind == "apk" && x.Package is not null)
            .Where(x => selectedPackages is null || selectedPackages.Contains(x.Id))
            .ToList();
        foreach (var entry in components.SelectMany(x => x.Files))
        {
            if (entry.Chunks is null)
                throw new InvalidDataException($"У {entry.RelativePath} нет chunks.");
            if (string.IsNullOrWhiteSpace(entry.ContentHash))
                throw new InvalidDataException($"У {entry.RelativePath} нет SHA-256.");
        }
        progress?.Report(new("verify", "Проверка APK chunks", 0, components.Count));
        await repository.VerifyReferencesAsync(
            components.SelectMany(x => x.Files).SelectMany(x => x.Chunks!),
            cancellationToken);
        foreach (var entry in components.SelectMany(x => x.Files))
        {
            await using var hashing = new HashingWriteStream(Stream.Null);
            await repository.RestoreStreamAsync(entry.Chunks!, hashing, cancellationToken);
            var hash = hashing.FinalizeHash();
            if (!string.Equals(hash, entry.ContentHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"SHA-256 не совпал: {entry.RelativePath}");
        }
        if (createSafetySnapshot)
        {
            var selectedIds = components.Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
            var currentForSafety = installed.Where(x => selectedIds.Contains(x.PackageName)).ToList();
            if (currentForSafety.Count > 0)
            {
                progress?.Report(new("safety-snapshot", "Текущее состояние приложений", 0, currentForSafety.Count));
                var includeAppData = await agent.RequestRootAsync(cancellationToken);
                await new BackupCoordinator(adb).BackupPortableAsync(
                    serial,
                    targetDevice,
                    currentForSafety,
                    repository,
                    agent,
                    includeSystemApps: true,
                    includeApks: true,
                    includeAppData: includeAppData,
                    includeSharedStorage: false,
                    includePersonalData: false,
                    includeSystemState: false,
                    includeFullComponents: false,
                    includeCaches: false,
                    fullHash: true,
                    progress: progress,
                    cancellationToken: cancellationToken,
                    purpose: "safety");
            }
        }
        if (!await agent.RequestRestoreApprovalAsync(
                snapshot.SnapshotId, components.Count, TimeSpan.FromMinutes(2), progress, cancellationToken))
            throw new UnauthorizedAccessException("Restore не подтверждён на телефоне.");

        var installedByPackage = installed.ToDictionary(x => x.PackageName, StringComparer.Ordinal);
        var index = 0L;
        foreach (var component in components)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var metadata = component.Package!;
            if (installedByPackage.TryGetValue(metadata.PackageName, out var present))
            {
                if (!string.Equals(
                        present.SigningCertificateSha256,
                        metadata.SigningCertificateSha256,
                        StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"Подпись {metadata.PackageName} не совпадает.");
                if (present.VersionCode > metadata.VersionCode && !allowDowngrade)
                    throw new InvalidOperationException(
                        $"{metadata.PackageName}: downgrade требует отдельного подтверждения.");
            }

            progress?.Report(new("restore-apk", metadata.PackageName, index, components.Count));
            int? sessionId = null;
            try
            {
                sessionId = await adb.CreateInstallSessionAsync(
                    serial,
                    component.Files.Sum(x => x.Size),
                    allowDowngrade,
                    cancellationToken);
                foreach (var entry in component.Files)
                {
                    var name = Path.GetFileName(entry.RelativePath);
                    await adb.WriteInstallSessionAsync(
                        serial,
                        sessionId.Value,
                        name,
                        entry.Size,
                        (output, token) =>
                            repository.RestoreStreamAsync(entry.Chunks!, output, token),
                        cancellationToken);
                }
                await adb.CommitInstallSessionAsync(serial, sessionId.Value, cancellationToken);
                sessionId = null;
            }
            finally
            {
                if (sessionId is not null)
                    await adb.AbandonInstallSessionAsync(serial, sessionId.Value, cancellationToken);
            }
            index++;
        }

        var packageNames = components.Select(x => x.Id).ToList();
        var verified = (await agent.GetPackagesAsync(
                includeSystemApps: true,
                packageNames,
                cancellationToken))
            .ToDictionary(x => x.PackageName, StringComparer.Ordinal);
        foreach (var component in components)
        {
            if (!verified.TryGetValue(component.Id, out var restored))
                throw new InvalidDataException($"{component.Id} не найден после установки.");
            if (!string.Equals(
                    restored.SigningCertificateSha256,
                    component.Package!.SigningCertificateSha256,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Подпись {component.Id} изменилась после установки.");
        }
    }

    public async Task RestoreAppDataAsync(
        SnapshotManifest snapshot,
        IReadOnlyList<PackageSnapshot> installed,
        EncryptedRepository repository,
        AgentClient agent,
        IReadOnlyCollection<string> selectedPackages,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var selected = selectedPackages.ToHashSet(StringComparer.Ordinal);
        var components = snapshot.Components
            .Where(x => x.Kind == "app-data" && x.Package is not null)
            .Where(x => selected.Contains(x.Package!.PackageName))
            .ToList();
        if (components.Count == 0) return;
        if (!await agent.RequestRootAsync(cancellationToken))
            throw new UnauthorizedAccessException("Для восстановления app data нужен root.");

        foreach (var entry in components.SelectMany(x => x.Files))
        {
            if (!RestorePathPolicy.IsSafeRelativePath(entry.RelativePath))
                throw new InvalidDataException($"Небезопасный путь Restore: {entry.RelativePath}");
            if (entry.Kind is not ("file" or "directory"))
                throw new NotSupportedException(
                    $"Restore остановлен до очистки данных: тип {entry.Kind} пока не поддерживается ({entry.RelativePath}).");
            if (entry.Kind == "file" && entry.Chunks is null)
                throw new InvalidDataException($"У {entry.RelativePath} нет chunks.");
        }
        progress?.Report(new("verify", "Проверка зашифрованных app data", 0, components.Count));
        await repository.VerifyReferencesAsync(
            components.SelectMany(x => x.Files).SelectMany(x => x.Chunks ?? []),
            cancellationToken);

        var installedByPackage = installed.ToDictionary(x => x.PackageName, StringComparer.Ordinal);
        var groups = components.GroupBy(x => x.Package!.PackageName, StringComparer.Ordinal).ToList();
        var packageIndex = 0L;
        foreach (var group in groups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!installedByPackage.TryGetValue(group.Key, out var current))
                throw new InvalidOperationException($"{group.Key} не установлен после APK Restore.");
            var metadata = group.First().Package!;
            if (!string.Equals(
                    current.SigningCertificateSha256,
                    metadata.SigningCertificateSha256,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Подпись {group.Key} не совпадает перед app data Restore.");

            progress?.Report(new("restore-app-data", group.Key, packageIndex, groups.Count));
            await agent.PreparePackageRestoreAsync(group.Key, cancellationToken);
            try
            {
                foreach (var component in group)
                {
                    var rootIndex = ParseRootIndex(component.Id, group.Key);
                    if (rootIndex < 0 || rootIndex >= current.DataPaths.Count)
                        throw new InvalidDataException(
                            $"{group.Key}: целевой data root #{rootIndex} отсутствует.");
                    var root = current.DataPaths[rootIndex];
                    foreach (var entry in component.Files
                                 .OrderBy(x => x.Kind == "file" ? 0 : 1)
                                 .ThenByDescending(x => PathDepth(x.RelativePath)))
                    {
                        await agent.RestoreRootEntryAsync(
                            root,
                            entry,
                            current.Uid,
                            entry.Kind == "file"
                                ? (output, token) => repository.RestoreStreamAsync(
                                    entry.Chunks!, output, token)
                                : null,
                            cancellationToken);
                    }
                }
            }
            finally
            {
                await agent.FinishPackageRestoreAsync(group.Key, cancellationToken);
            }
            packageIndex++;
        }
    }

    public async Task RestoreSharedStorageAsync(
        SnapshotManifest snapshot,
        EncryptedRepository repository,
        AgentClient agent,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var components = snapshot.Components
            .Where(x => x.Kind == "shared-storage")
            .ToList();
        if (components.Count == 0) return;
        var capability = await agent.GetSharedStorageAsync(cancellationToken);
        if (!capability.AccessGranted)
            throw new UnauthorizedAccessException(
                "Для восстановления общей памяти включите «Доступ ко всем файлам» в Agent.");
        var rootsById = capability.Roots.ToDictionary(
            SharedComponentId,
            StringComparer.Ordinal);
        foreach (var component in components)
        {
            if (!rootsById.ContainsKey(component.Id))
                throw new InvalidDataException(
                    $"На телефоне отсутствует разрешённый shared root: {component.Id}");
            foreach (var entry in component.Files)
            {
                if (!RestorePathPolicy.IsSafeRelativePath(entry.RelativePath))
                    throw new InvalidDataException($"Небезопасный shared path: {entry.RelativePath}");
                if (entry.Kind is not ("file" or "directory"))
                    throw new NotSupportedException(
                        $"Shared restore не поддерживает {entry.Kind}: {entry.RelativePath}");
                if (entry.Kind == "file" && entry.Chunks is null)
                    throw new InvalidDataException($"У {entry.RelativePath} нет chunks.");
            }
        }
        progress?.Report(new("verify", "Проверка shared storage", 0, components.Count));
        await repository.VerifyReferencesAsync(
            components.SelectMany(x => x.Files).SelectMany(x => x.Chunks ?? []),
            cancellationToken);

        var index = 0L;
        foreach (var component in components)
        {
            var root = rootsById[component.Id];
            progress?.Report(new("restore-shared", component.Id, index, components.Count));
            foreach (var entry in component.Files
                         .OrderBy(x => x.Kind == "directory" ? 0 : 1)
                         .ThenBy(x => PathDepth(x.RelativePath)))
            {
                await agent.RestoreSharedEntryAsync(
                    root,
                    entry,
                    entry.Kind == "file"
                        ? (output, token) => repository.RestoreStreamAsync(
                            entry.Chunks!, output, token)
                        : null,
                    cancellationToken);
            }
            index++;
        }
    }

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> RestorePackagePoliciesAsync(
        SnapshotManifest snapshot,
        AgentClient agent,
        IReadOnlyCollection<string> selectedPackages,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!await agent.RequestRootAsync(cancellationToken))
            return new Dictionary<string, IReadOnlyList<string>>();
        var selected = selectedPackages.ToHashSet(StringComparer.Ordinal);
        var metadata = snapshot.Components
            .Where(x => x.Kind == "apk" && x.Package is not null)
            .Select(x => x.Package!)
            .Where(x => selected.Contains(x.PackageName))
            .DistinctBy(x => x.PackageName, StringComparer.Ordinal)
            .ToList();
        var failures = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        for (var index = 0; index < metadata.Count; index++)
        {
            var item = metadata[index];
            progress?.Report(new("restore-policy", item.PackageName, index, metadata.Count));
            var itemFailures = await agent.ApplyPackagePolicyAsync(item, cancellationToken);
            if (itemFailures.Count > 0) failures[item.PackageName] = itemFailures;
        }
        return failures;
    }

    public async Task<IReadOnlyList<string>> RestoreSystemStateAsync(
        SnapshotManifest snapshot,
        EncryptedRepository repository,
        AgentClient agent,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var component = snapshot.Components.FirstOrDefault(x => x.Kind == "system-state");
        if (component is null) return [];
        var file = component.Files.SingleOrDefault()
            ?? throw new InvalidDataException("System state component is invalid.");
        if (file.Chunks is null)
            throw new InvalidDataException("System state component has no chunks.");
        if (!await agent.RequestRootAsync(cancellationToken))
            return ["root-required"];
        progress?.Report(new("restore-system-state", "Безопасные настройки и роли", 0, 1));
        await repository.VerifyReferencesAsync(file.Chunks, cancellationToken);
        await using var output = new MemoryStream();
        await repository.RestoreStreamAsync(file.Chunks, output, cancellationToken);
        return await agent.RestoreSystemStateAsync(output.ToArray(), cancellationToken);
    }

    private static string SharedComponentId(string root) =>
        $"shared:{root.Replace('\\', '/').Trim('/')}";

    private static int ParseRootIndex(string componentId, string packageName)
    {
        var prefix = $"{packageName}:app-data:";
        return componentId.StartsWith(prefix, StringComparison.Ordinal) &&
               int.TryParse(componentId[prefix.Length..], out var index)
            ? index
            : -1;
    }

    private static int PathDepth(string path) =>
        path.Count(x => x is '/' or '\\');

    private static void DeleteStaging(string path)
    {
        var allowedRoot = Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PhoneBackup",
            "staging")) + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(path);
        if (target.StartsWith(allowedRoot, StringComparison.OrdinalIgnoreCase) && Directory.Exists(target))
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    Directory.Delete(target, recursive: true);
                    return;
                }
                catch (IOException) when (attempt < 2)
                {
                    Thread.Sleep(100);
                }
            }
        }
    }

    private sealed class HashingWriteStream(Stream inner) : Stream
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private bool _finalized;
        public string FinalizeHash()
        {
            if (_finalized) throw new InvalidOperationException("Hash is already finalized.");
            _finalized = true;
            return Convert.ToHexString(_hash.GetHashAndReset()).ToLowerInvariant();
        }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) =>
            inner.FlushAsync(cancellationToken);
        public override void Write(byte[] buffer, int offset, int count)
        {
            _hash.AppendData(buffer, offset, count);
            inner.Write(buffer, offset, count);
        }
        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            _hash.AppendData(buffer.Span);
            await inner.WriteAsync(buffer, cancellationToken);
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) _hash.Dispose();
            base.Dispose(disposing);
        }
        public override ValueTask DisposeAsync()
        {
            _hash.Dispose();
            GC.SuppressFinalize(this);
            return ValueTask.CompletedTask;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
