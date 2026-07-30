using System.Reflection;
using System.Security.Cryptography;

namespace PhoneBackup.Desktop;

public static class RuntimeBootstrap
{
    private static readonly string RuntimeVersion =
        $"{AppRuntimeProfile.VersionName}-{AppRuntimeProfile.BuildId}";

    private static readonly string Root = Path.Combine(
        AppRuntimeProfile.LocalRoot,
        "runtime",
        RuntimeVersion);

    public static string AdbPath => Path.Combine(Root, "adb", "adb.exe");
    public static string AgentApkPath => Path.Combine(Root, "agent", "phonebackup-agent.apk");

    public static void EnsureExtracted()
    {
        Extract("PhoneBackup.Runtime.adb.exe", AdbPath);
        Extract("PhoneBackup.Runtime.AdbWinApi.dll", Path.Combine(Root, "adb", "AdbWinApi.dll"));
        Extract("PhoneBackup.Runtime.AdbWinUsbApi.dll", Path.Combine(Root, "adb", "AdbWinUsbApi.dll"));
        Extract("PhoneBackup.Runtime.phonebackup-agent.apk", AgentApkPath);
    }

    public static string ComputeAgentApkSha256() =>
        File.Exists(AgentApkPath)
            ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(AgentApkPath)))
                .ToLowerInvariant()
            : string.Empty;

    private static void Extract(string resourceName, string destination)
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var resource = assembly.GetManifestResourceStream(resourceName);
        if (resource is null) return;

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var expectedHash = Convert.ToHexString(SHA256.HashData(resource)).ToLowerInvariant();
        resource.Position = 0;
        if (File.Exists(destination))
        {
            var actualHash = Convert.ToHexString(
                SHA256.HashData(File.ReadAllBytes(destination))).ToLowerInvariant();
            if (string.Equals(expectedHash, actualHash, StringComparison.Ordinal))
                return;
        }

        var temporary = destination + $".{Guid.NewGuid():N}.tmp";
        try
        {
            using (var output = new FileStream(
                       temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                resource.CopyTo(output);
                output.Flush(flushToDisk: true);
            }
            File.Move(temporary, destination, overwrite: true);
            DesktopDiagnostics.Log(
                "info",
                "runtime",
                "resource_extracted",
                "Embedded runtime resource extracted",
                fields: new Dictionary<string, object?>
                {
                    ["resource"] = resourceName,
                    ["destination"] = destination,
                    ["sha256"] = expectedHash
                });
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
