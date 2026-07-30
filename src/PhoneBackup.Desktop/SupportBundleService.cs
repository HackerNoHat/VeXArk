using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PhoneBackup.Core;

namespace PhoneBackup.Desktop;

public sealed record SupportBundleManifest(
    DateTimeOffset CreatedAtUtc,
    string DesktopChannel,
    string DesktopVersion,
    string DesktopBuildId,
    string AgentPackage,
    int AgentPort,
    string DeviceIdHash,
    string DeviceModel,
    string DeviceFingerprint,
    AgentBuildInfo? AgentBuild);

public sealed class SupportBundleService(AdbService adb)
{
    public async Task CreateAsync(
        string destination,
        DeviceInventory device,
        AgentDiagnosticsSnapshot? agentSnapshot,
        CancellationToken cancellationToken = default)
    {
        var deviceIdHash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(device.StableId)))
            .ToLowerInvariant()[..12];
        var manifest = new SupportBundleManifest(
            DateTimeOffset.UtcNow,
            AppRuntimeProfile.Channel,
            AppRuntimeProfile.VersionName,
            AppRuntimeProfile.BuildId,
            AppRuntimeProfile.AgentPackage,
            AppRuntimeProfile.AgentPort,
            deviceIdHash,
            device.Model,
            device.Fingerprint,
            agentSnapshot?.Build);
        var temporary = destination + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var file = new FileStream(
                             temporary,
                             FileMode.CreateNew,
                             FileAccess.ReadWrite,
                             FileShare.None,
                             64 * 1024,
                             useAsync: true))
            using (var archive = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: false))
            {
                await WriteTextAsync(
                    archive,
                    "manifest.json",
                    JsonSerializer.Serialize(manifest, new JsonSerializerOptions
                    {
                        WriteIndented = true,
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                    }),
                    cancellationToken);
                if (Directory.Exists(DesktopDiagnostics.LogDirectory))
                {
                    foreach (var path in Directory.EnumerateFiles(
                                 DesktopDiagnostics.LogDirectory,
                                 "desktop.jsonl*"))
                    {
                        var contents = await File.ReadAllTextAsync(path, cancellationToken);
                        var sanitized = SanitizeDeviceIdentifiers(contents, device);
                        await WriteTextAsync(
                            archive,
                            "desktop/" + Path.GetFileName(path),
                            DesktopDiagnostics.RedactText(sanitized),
                            cancellationToken);
                    }
                }
                if (agentSnapshot is not null)
                {
                    await WriteTextAsync(
                        archive,
                        "agent/snapshot.json",
                        DesktopDiagnostics.RedactText(agentSnapshot.RawJson),
                        cancellationToken);
                    var agentJsonl = string.Join(
                        '\n',
                        agentSnapshot.Events.Select(item =>
                            JsonSerializer.Serialize(item, new JsonSerializerOptions
                            {
                                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                            })));
                    if (agentJsonl.Length > 0) agentJsonl += "\n";
                    await WriteTextAsync(
                        archive,
                        "agent/agent.jsonl",
                        DesktopDiagnostics.RedactText(agentJsonl),
                        cancellationToken);
                }
                var pid = (await adb.RunAsync(
                    [
                        "-s", device.Transports.First(x => x.IsPreferred).AdbSerial,
                        "shell", "pidof", AppRuntimeProfile.AgentPackage
                    ],
                    cancellationToken,
                    false)).Trim();
                if (int.TryParse(pid.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(),
                        out var processId))
                {
                    var logcat = await adb.RunAsync(
                        [
                            "-s", device.Transports.First(x => x.IsPreferred).AdbSerial,
                            "logcat", "-d", "-v", "threadtime", "--pid", processId.ToString()
                        ],
                        cancellationToken,
                        false);
                    await WriteTextAsync(
                        archive,
                        "agent/logcat.txt",
                        DesktopDiagnostics.RedactText(
                            SanitizeDeviceIdentifiers(logcat, device)),
                        cancellationToken);
                }
            }
            File.Move(temporary, destination, overwrite: true);
            DesktopDiagnostics.Log(
                "info",
                "diagnostics",
                "bundle_created",
                "Support bundle created",
                result: "ok",
                fields: new Dictionary<string, object?>
                {
                    ["destination"] = destination
                });
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static async Task WriteTextAsync(
        ZipArchive archive,
        string path,
        string value,
        CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
        await using var stream = entry.Open();
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        await writer.WriteAsync(value.AsMemory(), cancellationToken);
    }

    private static string SanitizeDeviceIdentifiers(
        string value,
        DeviceInventory device)
    {
        var sanitized = value.Replace(
            device.StableId,
            "[device]",
            StringComparison.Ordinal);
        foreach (var serial in device.Transports.Select(x => x.AdbSerial).Distinct())
            sanitized = sanitized.Replace(serial, "[device]", StringComparison.Ordinal);
        return sanitized;
    }
}
