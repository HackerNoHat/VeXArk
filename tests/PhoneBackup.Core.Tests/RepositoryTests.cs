using System.Security.Cryptography;
using PhoneBackup.Core;

namespace PhoneBackup.Core.Tests;

public sealed class RepositoryTests
{
    [Fact]
    public async Task RepositoryRoundTripAndDeduplicates()
    {
        var root = Path.Combine(Path.GetTempPath(), $"phonebackup-test-{Guid.NewGuid():N}");
        try
        {
            var created = await EncryptedRepository.CreateAsync(root, "correct horse battery staple");
            Assert.Equal(24, created.RecoveryCode.Split(' ').Length);
            var repository = await EncryptedRepository.OpenWithPasswordAsync(
                root,
                "correct horse battery staple");
            var payload = RandomNumberGenerator.GetBytes(FastCdcChunker.AverageSize * 3);

            var first = await repository.PutStreamAsync(new MemoryStream(payload));
            var objectCount = Directory.EnumerateFiles(Path.Combine(root, "objects"), "*", SearchOption.AllDirectories).Count();
            var second = await repository.PutStreamAsync(new MemoryStream(payload));
            var objectCountAfter = Directory.EnumerateFiles(Path.Combine(root, "objects"), "*", SearchOption.AllDirectories).Count();

            Assert.Equal(first, second);
            Assert.Equal(objectCount, objectCountAfter);

            await using var restored = new MemoryStream();
            await repository.RestoreStreamAsync(first, restored);
            Assert.Equal(payload, restored.ToArray());

            var recovered = await EncryptedRepository.OpenWithRecoveryCodeAsync(root, created.RecoveryCode);
            await using var restoredByRecovery = new MemoryStream();
            await recovered.RestoreStreamAsync(first, restoredByRecovery);
            Assert.Equal(payload, restoredByRecovery.ToArray());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void RecoveryPhraseRejectsUnknownWord()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var phrase = RecoveryPhrase.Encode(key);
        Assert.Equal(key, RecoveryPhrase.Decode(phrase));
        var words = phrase.Split(' ');
        words[^1] = "not-a-phonebackup-word";
        Assert.Throws<CryptographicException>(() => RecoveryPhrase.Decode(string.Join(' ', words)));
    }

    [Fact]
    public async Task ManifestIsAuthenticatedAndEnumerable()
    {
        var root = Path.Combine(Path.GetTempPath(), $"phonebackup-test-{Guid.NewGuid():N}");
        try
        {
            await EncryptedRepository.CreateAsync(root, "correct horse battery staple");
            var repository = await EncryptedRepository.OpenWithPasswordAsync(
                root, "correct horse battery staple");
            var manifest = new SnapshotManifest(
                1,
                Guid.NewGuid().ToString("N"),
                DateTimeOffset.UtcNow,
                BackupMode.Portable,
                new(
                    "device", "Model", "device", "16", 36, "fingerprint", "arm64-v8a",
                    "Enforcing", RootState.Unavailable, []),
                [],
                new(0, 0, []));
            await repository.CommitSnapshotAsync(manifest);
            var listed = await repository.ListSnapshotsAsync();
            Assert.Equal(manifest.SnapshotId, Assert.Single(listed).SnapshotId);

            var path = Assert.Single(Directory.EnumerateFiles(Path.Combine(root, "snapshots")));
            var bytes = await File.ReadAllBytesAsync(path);
            bytes[^1] ^= 0x40;
            await File.WriteAllBytesAsync(path, bytes);
            await Assert.ThrowsAnyAsync<CryptographicException>(
                () => repository.ListSnapshotsAsync());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WrongPasswordFailsAuthentication()
    {
        var root = Path.Combine(Path.GetTempPath(), $"phonebackup-test-{Guid.NewGuid():N}");
        try
        {
            await EncryptedRepository.CreateAsync(root, "correct horse battery staple");
            await Assert.ThrowsAnyAsync<CryptographicException>(
                () => EncryptedRepository.OpenWithPasswordAsync(root, "incorrect password"));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PasswordChangeOnlyRewrapsMasterKey()
    {
        var root = Path.Combine(Path.GetTempPath(), $"phonebackup-test-{Guid.NewGuid():N}");
        try
        {
            await EncryptedRepository.CreateAsync(root, "old password is long enough");
            var repository = await EncryptedRepository.OpenWithPasswordAsync(
                root, "old password is long enough");
            var chunks = await repository.PutStreamAsync(new MemoryStream("persistent"u8.ToArray()));
            await repository.ChangePasswordAsync("new password is also long");
            await Assert.ThrowsAnyAsync<CryptographicException>(
                () => EncryptedRepository.OpenWithPasswordAsync(root, "old password is long enough"));
            var reopened = await EncryptedRepository.OpenWithPasswordAsync(
                root, "new password is also long");
            await using var restored = new MemoryStream();
            await reopened.RestoreStreamAsync(chunks, restored);
            Assert.Equal("persistent"u8.ToArray(), restored.ToArray());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DeletedSnapshotBecomesGarbageCollectable()
    {
        var root = Path.Combine(Path.GetTempPath(), $"phonebackup-test-{Guid.NewGuid():N}");
        try
        {
            await EncryptedRepository.CreateAsync(root, "correct horse battery staple");
            var repository = await EncryptedRepository.OpenWithPasswordAsync(
                root, "correct horse battery staple");
            var chunks = await repository.PutStreamAsync(new MemoryStream("orphaned payload"u8.ToArray()));
            var manifest = new SnapshotManifest(
                1,
                Guid.NewGuid().ToString("N"),
                DateTimeOffset.UtcNow,
                BackupMode.Portable,
                new(
                    "device", "Model", "device", "16", 36, "fingerprint", "arm64-v8a",
                    "Enforcing", RootState.Unavailable, []),
                [
                    new(
                        "test", "test",
                        [new("value.bin", "file", 16, 0, 0, 0, 0, null, null, null, chunks)],
                        16,
                        chunks.Sum(x => (long)x.StoredLength))
                ],
                new(0, 0, []));
            await repository.CommitSnapshotAsync(manifest);

            Assert.True(await repository.DeleteSnapshotAsync(manifest.SnapshotId));
            Assert.Empty(await repository.ListSnapshotsAsync());
            var gc = await repository.GarbageCollectAsync();
            Assert.True(gc.DeletedObjects > 0);
            Assert.False(await repository.DeleteSnapshotAsync(manifest.SnapshotId));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ExistingRepositoryCannotBeOverwritten()
    {
        var root = Path.Combine(Path.GetTempPath(), $"phonebackup-test-{Guid.NewGuid():N}");
        try
        {
            await EncryptedRepository.CreateAsync(root, "correct horse battery staple");
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => EncryptedRepository.CreateAsync(root, "another long password"));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task EncryptedSnapshotBundleCanBeExportedAndImported()
    {
        var root = Path.Combine(Path.GetTempPath(), $"phonebackup-test-{Guid.NewGuid():N}");
        var importedRoot = Path.Combine(Path.GetTempPath(), $"phonebackup-import-{Guid.NewGuid():N}");
        var bundle = Path.Combine(Path.GetTempPath(), $"vexark-{Guid.NewGuid():N}.vexark");
        try
        {
            await EncryptedRepository.CreateAsync(root, "correct horse battery staple");
            var repository = await EncryptedRepository.OpenWithPasswordAsync(
                root,
                "correct horse battery staple");
            var payload = RandomNumberGenerator.GetBytes(1024 * 1024 + 17);
            var chunks = await repository.PutStreamAsync(new MemoryStream(payload));
            var manifest = new SnapshotManifest(
                1,
                Guid.NewGuid().ToString("N"),
                DateTimeOffset.UtcNow,
                BackupMode.Portable,
                new(
                    "device", "Model", "device", "16", 36, "fingerprint", "arm64-v8a",
                    "Enforcing", RootState.Unavailable, []),
                [
                    new(
                        "test",
                        "test",
                        [
                            new(
                                "payload.bin", "file", payload.Length, 0,
                                0, 0, 0, null, null, null, chunks)
                        ],
                        payload.Length,
                        chunks.Sum(x => (long)x.StoredLength))
                ],
                new(0, 0, []));
            await repository.CommitSnapshotAsync(manifest);

            var exported = await repository.ExportSnapshotBundleAsync(manifest, bundle);
            Assert.True(exported.ObjectCount > 0);
            Assert.True(new FileInfo(bundle).Length > 0);

            var imported = await EncryptedRepository.ImportSnapshotBundleAsync(
                bundle,
                importedRoot);
            Assert.Equal(exported.ObjectCount, imported.ObjectCount);
            var reopened = await EncryptedRepository.OpenWithPasswordAsync(
                importedRoot,
                "correct horse battery staple");
            var listed = Assert.Single(await reopened.ListSnapshotsAsync());
            await using var restored = new MemoryStream();
            await reopened.RestoreStreamAsync(
                Assert.Single(listed.Components).Files.Single().Chunks!,
                restored);
            Assert.Equal(payload, restored.ToArray());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            if (Directory.Exists(importedRoot)) Directory.Delete(importedRoot, recursive: true);
            if (File.Exists(bundle)) File.Delete(bundle);
        }
    }

    [Fact]
    public async Task SnapshotBundleImportRejectsPathTraversal()
    {
        var destination = Path.Combine(Path.GetTempPath(), $"phonebackup-import-{Guid.NewGuid():N}");
        var bundle = Path.Combine(Path.GetTempPath(), $"vexark-evil-{Guid.NewGuid():N}.vexark");
        try
        {
            using (var archive = System.IO.Compression.ZipFile.Open(
                       bundle,
                       System.IO.Compression.ZipArchiveMode.Create))
            {
                archive.CreateEntry("../escape");
            }
            await Assert.ThrowsAsync<InvalidDataException>(
                () => EncryptedRepository.ImportSnapshotBundleAsync(bundle, destination));
        }
        finally
        {
            if (Directory.Exists(destination)) Directory.Delete(destination, recursive: true);
            if (File.Exists(bundle)) File.Delete(bundle);
        }
    }
}
