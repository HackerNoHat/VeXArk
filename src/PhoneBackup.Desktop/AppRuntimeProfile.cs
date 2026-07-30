using System.Reflection;

namespace PhoneBackup.Desktop;

public sealed record AgentRuntimeProfile(
    string Channel,
    string ProductName,
    string AgentPackage,
    int AgentPort,
    string RoamingFolder,
    string LocalFolder,
    bool DiagnosticsEnabled)
{
    public static AgentRuntimeProfile Stable { get; } = new(
        "stable",
        "VeXArk",
        "com.vex.phonebackup.agent",
        49321,
        "PhoneBackup",
        "PhoneBackup",
        false);

    public static AgentRuntimeProfile Dev { get; } = new(
        "dev",
        "VeXArk Dev",
        "com.vex.phonebackup.agent.dev",
        49322,
        "VeXArk Dev",
        "VeXArk Dev",
        true);
}

public static class AppRuntimeProfile
{
#if VEXARK_DEV
    public static AgentRuntimeProfile Current { get; } = AgentRuntimeProfile.Dev;
#else
    public static AgentRuntimeProfile Current { get; } = AgentRuntimeProfile.Stable;
#endif

    public static bool IsDev => Current.DiagnosticsEnabled;
    public static string Channel => Current.Channel;
    public static string ProductName => Current.ProductName;
    public static string AgentPackage => Current.AgentPackage;
    public static int AgentPort => Current.AgentPort;

    public static string RoamingRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        Current.RoamingFolder);

    public static string LocalRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Current.LocalFolder);

    public static string BuildId =>
        typeof(AppRuntimeProfile).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(x => x.Key == "VeXArkBuildId")?
            .Value ?? "local";

    public static string VersionName =>
        typeof(AppRuntimeProfile).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion.Split('+')[0] ??
        typeof(AppRuntimeProfile).Assembly.GetName().Version?.ToString(3) ??
        "unknown";
}
