using System.Security.Cryptography;

namespace PhoneBackup.Desktop;

public sealed record AgentPreflightResult(
    bool Installed,
    bool Compatible,
    string Summary,
    InstalledAgentInfo? InstalledAgent,
    AgentBuildInfo? RunningBuild,
    string EmbeddedApkSha256,
    IReadOnlyList<string> Problems);

public sealed class AgentPreflight(AdbService adb)
{
    public async Task<AgentPreflightResult> CheckAsync(
        string serial,
        CancellationToken cancellationToken = default)
    {
        var operationId = Guid.NewGuid().ToString("D");
        var embeddedHash = RuntimeBootstrap.ComputeAgentApkSha256();
        DesktopDiagnostics.Log(
            "info",
            "preflight",
            "started",
            "Agent preflight started",
            operationId: operationId,
            fields: new Dictionary<string, object?>
            {
                ["expectedPackage"] = AppRuntimeProfile.AgentPackage,
                ["expectedChannel"] = AppRuntimeProfile.Channel,
                ["expectedPort"] = AppRuntimeProfile.AgentPort,
                ["embeddedApkSha256"] = embeddedHash
            });

        var installed = await adb.GetInstalledAgentInfoAsync(serial, cancellationToken);
        if (installed is null)
        {
            var missing = new AgentPreflightResult(
                false,
                false,
                "Agent Dev не установлен.",
                null,
                null,
                embeddedHash,
                ["Пакет Agent Dev отсутствует на телефоне."]);
            LogResult(operationId, missing);
            return missing;
        }

        AgentBuildInfo? runningBuild = null;
        var problems = new List<string>();
        try
        {
            await using var agent = await AgentClient.ConnectAsync(adb, serial, cancellationToken);
            runningBuild = await agent.GetBuildInfoAsync(cancellationToken);
        }
        catch (Exception error)
        {
            problems.Add($"Не удалось получить hello/build от Agent: {error.Message}");
            DesktopDiagnostics.Log(
                "error",
                "preflight",
                "hello_failed",
                "Agent hello/build failed",
                operationId: operationId,
                error: error);
        }

        if (!string.Equals(installed.PackageName, AppRuntimeProfile.AgentPackage, StringComparison.Ordinal))
            problems.Add(
                $"Установлен пакет {installed.PackageName}, ожидался {AppRuntimeProfile.AgentPackage}.");
        if (runningBuild is not null)
        {
            if (!string.Equals(runningBuild.Channel, AppRuntimeProfile.Channel, StringComparison.Ordinal))
                problems.Add(
                    $"Канал Agent {runningBuild.Channel}, ожидался {AppRuntimeProfile.Channel}.");
            if (!string.Equals(
                    runningBuild.PackageName,
                    AppRuntimeProfile.AgentPackage,
                    StringComparison.Ordinal))
                problems.Add(
                    $"Agent сообщил пакет {runningBuild.PackageName}, " +
                    $"ожидался {AppRuntimeProfile.AgentPackage}.");
            if (runningBuild.AgentPort != AppRuntimeProfile.AgentPort)
                problems.Add(
                    $"Порт Agent {runningBuild.AgentPort}, ожидался {AppRuntimeProfile.AgentPort}.");
            if (runningBuild.ProtocolVersion != PhoneBackup.Core.ProtocolConstants.ProtocolVersion)
                problems.Add(
                    $"Протокол Agent {runningBuild.ProtocolVersion}, ожидался " +
                    $"{PhoneBackup.Core.ProtocolConstants.ProtocolVersion}.");
            if (!runningBuild.DiagnosticsEnabled)
                problems.Add("Agent запущен без DEV-диагностики.");
            var expectedVersionPrefix = AppRuntimeProfile.IsDev
                ? AppRuntimeProfile.VersionName + "-dev"
                : AppRuntimeProfile.VersionName;
            if (!string.Equals(
                    runningBuild.VersionName,
                    expectedVersionPrefix,
                    StringComparison.OrdinalIgnoreCase))
                problems.Add(
                    $"Версия Agent {runningBuild.VersionName}, ожидалась {expectedVersionPrefix}.");
            if (!string.Equals(
                    runningBuild.BuildId,
                    AppRuntimeProfile.BuildId,
                    StringComparison.OrdinalIgnoreCase))
                problems.Add(
                    $"Build ID Agent {runningBuild.BuildId}, " +
                    $"Desktop {AppRuntimeProfile.BuildId}.");
        }
        if (!string.IsNullOrWhiteSpace(embeddedHash) &&
            !string.IsNullOrWhiteSpace(installed.Sha256) &&
            !CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(embeddedHash),
                Convert.FromHexString(installed.Sha256)))
            problems.Add("SHA-256 установленного Agent не совпадает со встроенным APK.");

        var compatible = runningBuild is not null && problems.Count == 0;
        var result = new AgentPreflightResult(
            true,
            compatible,
            compatible
                ? $"Agent Dev готов: {runningBuild!.VersionName}, UID {installed.UserId}."
                : "Agent Dev требует установки или обновления.",
            installed,
            runningBuild,
            embeddedHash,
            problems);
        LogResult(operationId, result);
        return result;
    }

    private static void LogResult(string operationId, AgentPreflightResult result)
    {
        DesktopDiagnostics.Log(
            result.Compatible ? "info" : "warn",
            "preflight",
            "completed",
            result.Summary,
            operationId: operationId,
            result: result.Compatible ? "compatible" : "incompatible",
            fields: new Dictionary<string, object?>
            {
                ["installed"] = result.Installed,
                ["installedVersion"] = result.InstalledAgent?.VersionName,
                ["installedUid"] = result.InstalledAgent?.UserId,
                ["runningChannel"] = result.RunningBuild?.Channel,
                ["runningVersion"] = result.RunningBuild?.VersionName,
                ["runningBuildId"] = result.RunningBuild?.BuildId,
                ["problems"] = string.Join(" | ", result.Problems)
            });
    }
}
