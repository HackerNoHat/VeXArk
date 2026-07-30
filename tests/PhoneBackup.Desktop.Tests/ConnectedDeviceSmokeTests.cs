using System.IO.Compression;
using System.Security.Cryptography;
using PhoneBackup.Core;
using PhoneBackup.Desktop;
using Xunit.Abstractions;

namespace PhoneBackup.Desktop.Tests;

public sealed class ConnectedDeviceSmokeTests(ITestOutputHelper output)
{
    [Fact]
    [Trait("Category", "RepositoryIntegration")]
    public async Task EncryptedRepositoryFullVerificationWhenRequested()
    {
        var repositoryRoot =
            Environment.GetEnvironmentVariable("VEXARK_REPOSITORY_VERIFY_ROOT");
        var password =
            Environment.GetEnvironmentVariable("VEXARK_REPOSITORY_VERIFY_PASSWORD");
        if (string.IsNullOrWhiteSpace(repositoryRoot) ||
            string.IsNullOrWhiteSpace(password))
        {
            return;
        }

        var repository = await EncryptedRepository.OpenWithPasswordAsync(
            repositoryRoot,
            password);
        var snapshots = await repository.ListSnapshotsAsync();
        Assert.NotEmpty(snapshots);

        var integrity = await repository.VerifyAsync(sampleFraction: 1);
        Assert.Empty(integrity.Errors);
        Assert.Equal(integrity.ReferencedObjectCount, integrity.VerifiedObjectCount);

        var latest = snapshots.OrderByDescending(x => x.CreatedAtUtc).First();
        var failureReport = latest.Components.SingleOrDefault(
            x => string.Equals(x.Id, "backup.failures", StringComparison.Ordinal));
        var failureCount = 0;
        var failureStages = string.Empty;
        var failureSources = string.Empty;
        var failureComponents = string.Empty;
        if (failureReport is not null)
        {
            var reportFile = Assert.Single(failureReport.Files);
            Assert.NotNull(reportFile.Chunks);
            await using var restored = new MemoryStream();
            await repository.RestoreStreamAsync(reportFile.Chunks, restored);
            using var report = System.Text.Json.JsonDocument.Parse(restored.ToArray());
            failureCount = report.RootElement.GetProperty("count").GetInt32();
            Assert.True(failureCount > 0);
            var failures = report.RootElement.GetProperty("failures")
                .EnumerateArray()
                .Select(item => new
                {
                    Component = item.GetProperty("componentId").GetString() ?? "unknown",
                    Stage = item.GetProperty("stage").GetString() ?? "unknown",
                    Source = item.GetProperty("source").GetString() ?? "unknown"
                })
                .ToList();
            failureStages = string.Join(
                ", ",
                failures
                    .GroupBy(x => x.Stage, StringComparer.Ordinal)
                    .OrderBy(x => x.Key, StringComparer.Ordinal)
                    .Select(x => $"{x.Key}:{x.Count()}"));
            failureSources = string.Join(
                ", ",
                failures
                    .GroupBy(x => x.Source, StringComparer.Ordinal)
                    .OrderByDescending(x => x.Count())
                    .ThenBy(x => x.Key, StringComparer.Ordinal)
                    .Select(x => $"{x.Key}:{x.Count()}"));
            failureComponents = string.Join(
                ", ",
                failures
                    .GroupBy(x => x.Component, StringComparer.Ordinal)
                    .OrderByDescending(x => x.Count())
                    .ThenBy(x => x.Key, StringComparer.Ordinal)
                    .Select(x => $"{x.Key}:{x.Count()}"));
        }

        output.WriteLine(
            "Repository verification passed: snapshots={0}, mode={1}, components={2}, " +
            "objects={3}, verified={4}, failureReportEntries={5}, excludedMediaFiles={6}",
            snapshots.Count,
            latest.Mode,
            latest.Components.Count,
            integrity.ReferencedObjectCount,
            integrity.VerifiedObjectCount,
            failureCount,
            latest.ExcludedMedia.FileCount);
        if (failureCount > 0)
        {
            output.WriteLine("Failure report stages: {0}", failureStages);
            output.WriteLine("Failure report sources: {0}", failureSources);
            output.WriteLine("Failure report components: {0}", failureComponents);
        }
    }

    [Fact]
    [Trait("Category", "ConnectedDevice")]
    public async Task DevAgentLaunchAndHelloMatchRuntimeProfile()
    {
        var serial = Environment.GetEnvironmentVariable("VEXARK_DEVICE_SERIAL");
        if (string.IsNullOrWhiteSpace(serial)) return;

        var adb = new AdbService();
        await using var agent = await AgentClient.ConnectAsync(adb, serial);
        var build = await agent.GetBuildInfoAsync();

        Assert.Equal("dev", build.Channel);
        Assert.Equal("com.vex.phonebackup.agent.dev", build.PackageName);
        Assert.Equal(49322, build.AgentPort);
        Assert.True(build.DiagnosticsEnabled);

        var rootSmoke = string.Equals(
            Environment.GetEnvironmentVariable("VEXARK_DEVICE_ROOT_SMOKE"),
            "1",
            StringComparison.Ordinal);
        var bundleSmoke = string.Equals(
            Environment.GetEnvironmentVariable("VEXARK_DEVICE_BUNDLE_SMOKE"),
            "1",
            StringComparison.Ordinal);
        var rootReadSmoke = string.Equals(
            Environment.GetEnvironmentVariable("VEXARK_DEVICE_ROOT_READ_SMOKE"),
            "1",
            StringComparison.Ordinal);
        var backupRoot = Environment.GetEnvironmentVariable("VEXARK_DEVICE_BACKUP_ROOT");
        var backupSmoke = !string.IsNullOrWhiteSpace(backupRoot);
        var mediaRoot = Environment.GetEnvironmentVariable("VEXARK_DEVICE_MEDIA_BACKUP_ROOT");
        var mediaSmoke = !string.IsNullOrWhiteSpace(mediaRoot);
        var mediaCancelSmoke = string.Equals(
            Environment.GetEnvironmentVariable("VEXARK_DEVICE_MEDIA_CANCEL_SMOKE"),
            "1",
            StringComparison.Ordinal);
        if (!rootSmoke && !bundleSmoke && !rootReadSmoke && !backupSmoke &&
            !mediaSmoke && !mediaCancelSmoke)
            return;

        Assert.True(
            await agent.PairWithApprovalAsync(TimeSpan.FromMinutes(2)),
            "Desktop pairing was not approved on the Dev Agent.");

        if (bundleSmoke)
        {
            var snapshot = await agent.GetDiagnosticsSnapshotAsync();
            var device = (await adb.DiscoverAsync())
                .Single(x => x.Transports.Any(transport =>
                    string.Equals(transport.AdbSerial, serial, StringComparison.Ordinal)));
            var bundlePath = Path.Combine(
                Path.GetTempPath(),
                $"vexark-device-support-{Guid.NewGuid():N}.zip");
            try
            {
                await new SupportBundleService(adb)
                    .CreateAsync(bundlePath, device, snapshot);
                using var archive = ZipFile.OpenRead(bundlePath);
                Assert.Contains(archive.Entries, x => x.FullName == "manifest.json");
                Assert.Contains(archive.Entries, x => x.FullName == "agent/agent.jsonl");
                Assert.Contains(
                    archive.Entries,
                    x => x.FullName.StartsWith("desktop/desktop.jsonl", StringComparison.Ordinal));
                foreach (var entry in archive.Entries.Where(x => x.Length > 0))
                {
                    using var reader = new StreamReader(entry.Open());
                    var contents = await reader.ReadToEndAsync();
                    Assert.DoesNotContain(serial, contents, StringComparison.Ordinal);
                    Assert.DoesNotContain(device.StableId, contents, StringComparison.Ordinal);
                }
            }
            finally
            {
                if (File.Exists(bundlePath)) File.Delete(bundlePath);
            }
        }

        RootRequestResult? rootResult = null;
        if (rootSmoke || rootReadSmoke || backupSmoke)
        {
            rootResult = await agent.RequestRootDetailsAsync();
            Assert.True(
                rootResult.RootDataReady,
                $"Root/helper failed: granted={rootResult.Granted}, provider={rootResult.Provider}, " +
                $"detail={rootResult.Detail}, helperOk={rootResult.Helper?.Ok}, " +
                $"helperUid={rootResult.Helper?.Uid}, helperStderr={rootResult.Helper?.Stderr}");
            output.WriteLine(
                "Root test passed: provider={0}, appUid={1}, shellId={2}, " +
                "helperUid={3}, selinux={4}, durationMs={5}",
                rootResult.Provider,
                rootResult.Diagnostics?.AppUid,
                rootResult.Diagnostics?.Stdout,
                rootResult.Helper?.Uid,
                rootResult.Diagnostics?.Selinux,
                rootResult.Diagnostics?.DurationMs);
        }

        if (backupSmoke)
        {
            var selectedBackupRoot = backupRoot!;
            var password = Environment.GetEnvironmentVariable("VEXARK_DEVICE_BACKUP_PASSWORD");
            var credentialsPath =
                Environment.GetEnvironmentVariable("VEXARK_DEVICE_BACKUP_CREDENTIALS_PATH");
            Assert.False(string.IsNullOrWhiteSpace(password));
            Assert.False(string.IsNullOrWhiteSpace(credentialsPath));

            Directory.CreateDirectory(selectedBackupRoot);
            if (!File.Exists(Path.Combine(selectedBackupRoot, "repository.json")))
            {
                var creation = await EncryptedRepository.CreateAsync(selectedBackupRoot, password);
                var credentialsDirectory = Path.GetDirectoryName(
                    Path.GetFullPath(credentialsPath));
                if (!string.IsNullOrWhiteSpace(credentialsDirectory))
                    Directory.CreateDirectory(credentialsDirectory);
                await File.WriteAllTextAsync(
                    credentialsPath,
                    "VeXArk pre-flash backup credentials\r\n" +
                    $"Repository: {Path.GetFullPath(selectedBackupRoot)}\r\n" +
                    $"Password: {password}\r\n" +
                    $"Recovery key: {creation.RecoveryCode}\r\n");
            }

            var repository = await EncryptedRepository.OpenWithPasswordAsync(
                selectedBackupRoot,
                password);
            var device = (await adb.DiscoverAsync())
                .Single(x => x.Transports.Any(transport =>
                    string.Equals(transport.AdbSerial, serial, StringComparison.Ordinal)))
                with
                {
                    Root = RootState.Granted
                };
            var backupPackages = await agent.GetPackagesAsync();
            var backupProgress = new InlineProgress<TransferProgress>(value =>
            {
                if (value.Total == 0 ||
                    value.Completed == value.Total ||
                    value.Completed % 10 == 0)
                {
                    output.WriteLine(
                        "Backup progress: stage={0}, completed={1}, total={2}",
                        value.Stage,
                        value.Completed,
                        value.Total);
                }
            });
            if ((await repository.ListSnapshotsAsync()).Count == 0)
            {
                var apkCheckpoint = await new BackupCoordinator(adb).BackupApksAsync(
                    serial,
                    device,
                    backupPackages,
                    repository,
                    includeSystemApps: false,
                    progress: backupProgress,
                    purpose: "manual");
                output.WriteLine(
                    "APK checkpoint committed: snapshot={0}, components={1}",
                    apkCheckpoint.SnapshotId,
                    apkCheckpoint.Components.Count);
            }

            var manifest = await new BackupCoordinator(adb).BackupPortableAsync(
                serial,
                device,
                backupPackages,
                repository,
                agent,
                includeSystemApps: false,
                includeApks: true,
                includeAppData: true,
                includeSharedStorage: true,
                includePersonalData: true,
                includeSystemState: true,
                includeFullComponents: true,
                includeCaches: false,
                fullHash: false,
                progress: backupProgress);

            Assert.Contains(
                manifest.Components,
                component =>
                    component.Kind == "app-data" &&
                    component.Files.Any(file => file.Kind == "file" && file.Chunks?.Count > 0));
            var integrity = await repository.VerifyAsync(sampleFraction: 1);
            Assert.Empty(integrity.Errors);
            Assert.Equal(integrity.ReferencedObjectCount, integrity.VerifiedObjectCount);
            output.WriteLine(
                "Encrypted backup passed: snapshot={0}, components={1}, objects={2}, verified={3}",
                manifest.SnapshotId,
                manifest.Components.Count,
                integrity.ReferencedObjectCount,
                integrity.VerifiedObjectCount);
        }

        if (mediaSmoke)
        {
            var mediaProgress = new InlineProgress<TransferProgress>(value =>
            {
                if (value.Total == 0 || value.Completed % 100 == 0)
                {
                    output.WriteLine(
                        "Media progress: stage={0}, completed={1}, total={2}",
                        value.Stage,
                        value.Completed,
                        value.Total);
                }
            });
            var report = await new MediaExportCoordinator().ExportAsync(
                agent,
                mediaRoot!,
                new(MediaTransportMode.Auto),
                mediaProgress);
            Assert.Equal(0, report.FailedFiles);
            output.WriteLine(
                "Media backup passed: copied={0}, skipped={1}, bytes={2}, transport={3}",
                report.CopiedFiles,
                report.SkippedFiles,
                report.CopiedBytes,
                report.Transport);
        }

        if (mediaCancelSmoke)
        {
            var cancellationRoot = Path.Combine(
                Path.GetTempPath(),
                $"vexark-media-cancel-{Guid.NewGuid():N}");
            Directory.CreateDirectory(cancellationRoot);
            try
            {
                using var cancellation = new CancellationTokenSource(
                    TimeSpan.FromMinutes(2));
                var sawTransferredBytes = false;
                var metrics = new InlineProgress<MediaTransferMetrics>(value =>
                {
                    if (value.CompletedBytes <= 0 || cancellation.IsCancellationRequested)
                        return;
                    sawTransferredBytes = true;
                    cancellation.Cancel();
                });

                await Assert.ThrowsAnyAsync<OperationCanceledException>(
                    () => new MediaExportCoordinator().ExportAsync(
                        agent,
                        cancellationRoot,
                        new(MediaTransportMode.Adb, AdbWorkers: 1),
                        metrics: metrics,
                        cancellationToken: cancellation.Token));

                Assert.True(sawTransferredBytes);
                var completedFiles = Directory.EnumerateFiles(
                        cancellationRoot,
                        "*",
                        SearchOption.AllDirectories)
                    .Count(path =>
                        !Path.GetFileName(path).StartsWith(".", StringComparison.Ordinal));
                var resumableParts = Directory.EnumerateFiles(
                        cancellationRoot,
                        ".*.vexark.part.json",
                        SearchOption.AllDirectories)
                    .Count();
                Assert.True(completedFiles > 0 || resumableParts > 0);
                output.WriteLine(
                    "Media cancellation passed: completedFiles={0}, resumableParts={1}",
                    completedFiles,
                    resumableParts);
            }
            finally
            {
                var fullCancellationRoot = Path.GetFullPath(cancellationRoot);
                var fullTempRoot = Path.GetFullPath(Path.GetTempPath());
                Assert.StartsWith(
                    fullTempRoot,
                    fullCancellationRoot,
                    StringComparison.OrdinalIgnoreCase);
                if (Directory.Exists(fullCancellationRoot))
                    Directory.Delete(fullCancellationRoot, recursive: true);
            }
        }

        if (!rootReadSmoke) return;

        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var packages = await agent.GetPackagesAsync(cancellationToken: timeout.Token);
        var package = packages.FirstOrDefault(x =>
                          string.Equals(x.PackageName, "ru.yandex.disk", StringComparison.Ordinal) &&
                          x.DataPaths.Count > 0)
                      ?? packages.First(x =>
                          !x.IsSystem &&
                          !x.PackageName.StartsWith("com.vex.phonebackup.agent", StringComparison.Ordinal) &&
                          x.DataPaths.Count > 0);

        FileEntry? candidate = null;
        string? candidateRoot = null;
        foreach (var dataRoot in package.DataPaths)
        {
            await foreach (var entry in agent.ScanRootAsync(
                               dataRoot,
                               includeCaches: false,
                               fullHash: false,
                               timeout.Token))
            {
                if (candidate is null &&
                    string.Equals(entry.Kind, "file", StringComparison.Ordinal) &&
                    entry.Size is > 0 and <= 4 * 1024 * 1024)
                {
                    candidate = entry;
                    candidateRoot = dataRoot;
                }
            }

            if (candidate is not null)
                break;
        }

        Assert.NotNull(candidate);
        Assert.NotNull(candidateRoot);
        var selectedFile = candidate;
        var selectedRoot = candidateRoot;

        await using var input = await agent.OpenRootFileAsync(
            selectedRoot,
            selectedFile.RelativePath,
            timeout.Token);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        long copied = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer, timeout.Token);
            if (read == 0) break;
            copied += read;
            hash.AppendData(buffer, 0, read);
        }

        var sha256 = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        Assert.Equal(selectedFile.Size, copied);
        output.WriteLine(
            "Root read smoke passed: package={0}, bytes={1}, sha256={2}",
            package.PackageName,
            copied,
            sha256);
    }

    private sealed class InlineProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }
}
