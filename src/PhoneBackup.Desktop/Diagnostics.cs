using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace PhoneBackup.Desktop;

public sealed record DiagnosticEvent(
    DateTimeOffset TimestampUtc,
    string Level,
    string Component,
    string EventCode,
    string? OperationId,
    string? RequestId,
    long? DurationMs,
    string? Result,
    string Message,
    string? ErrorType,
    string? StackTrace,
    IReadOnlyDictionary<string, string> Fields)
{
    [JsonIgnore]
    public string DisplayLine
    {
        get
        {
            var result = string.IsNullOrWhiteSpace(Result) ? string.Empty : $" [{Result}]";
            return $"{TimestampUtc.ToLocalTime():HH:mm:ss.fff} {Level.ToUpperInvariant()} " +
                   $"{Component}/{EventCode}{result} — {Message}";
        }
    }
}

public static class DesktopDiagnostics
{
    private const long MaxFileBytes = 5L * 1024 * 1024;
    private const int MaxFiles = 10;
    private const int MaxMemoryEvents = 500;
    private static readonly object Gate = new();
    private static readonly ConcurrentQueue<DiagnosticEvent> Events = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
    private static readonly Regex SecretAssignmentRegex = new(
        "(?i)(?<prefix>[\"']?(?:password|recovery(?:code)?|privatekey|publickey|" +
        "desktopkey|signature|nonce|pairingcode|sessionkey|approvaltoken)[\"']?" +
        "\\s*(?:=|:)\\s*[\"']?)(?<secret>[^\"'\\s,}\\]]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex PairingCodeRegex = new(
        "(?i)(?<prefix>\\bpairing\\s*code\\b\\s*(?:=|:)?\\s*)\\d{6,12}\\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static event Action<DiagnosticEvent>? EventWritten;

    public static string LogDirectory =>
        Path.Combine(AppRuntimeProfile.LocalRoot, "Logs");

    public static string CurrentLogPath =>
        Path.Combine(LogDirectory, "desktop.jsonl");

    public static IReadOnlyList<DiagnosticEvent> Snapshot() => Events.ToArray();

    public static void ClearView()
    {
        while (Events.TryDequeue(out _)) { }
        Log("info", "diagnostics", "view_cleared", "Diagnostic view cleared");
    }

    public static void Log(
        string level,
        string component,
        string eventCode,
        string message,
        string? operationId = null,
        string? requestId = null,
        long? durationMs = null,
        string? result = null,
        Exception? error = null,
        IReadOnlyDictionary<string, object?>? fields = null)
    {
        var safeFields = fields?.ToDictionary(
            x => x.Key,
            x => Redact(x.Key, x.Value?.ToString() ?? string.Empty),
            StringComparer.Ordinal) ?? new Dictionary<string, string>(StringComparer.Ordinal);
        var diagnosticEvent = new DiagnosticEvent(
            DateTimeOffset.UtcNow,
            level.ToLowerInvariant(),
            component,
            eventCode,
            operationId,
            requestId,
            durationMs,
            result,
            RedactText(message),
            error?.GetType().FullName,
            error is null ? null : RedactText(error.ToString()),
            safeFields);

        lock (Gate)
        {
            Events.Enqueue(diagnosticEvent);
            while (Events.Count > MaxMemoryEvents) Events.TryDequeue(out _);
            try
            {
                Directory.CreateDirectory(LogDirectory);
                var line = JsonSerializer.Serialize(diagnosticEvent, JsonOptions) +
                           Environment.NewLine;
                if (File.Exists(CurrentLogPath) &&
                    new FileInfo(CurrentLogPath).Length +
                    System.Text.Encoding.UTF8.GetByteCount(line) > MaxFileBytes)
                    RotateFiles(CurrentLogPath, MaxFiles);
                File.AppendAllText(CurrentLogPath, line, System.Text.Encoding.UTF8);
            }
            catch (Exception persistenceError)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"VeXArk diagnostics persistence failed: {persistenceError}");
            }
        }

        EventWritten?.Invoke(diagnosticEvent);
    }

    public static string Redact(string key, string value)
    {
        var normalized = key.ToLowerInvariant();
        string[] secretKeys =
        [
            "password", "recovery", "privatekey", "publickey", "desktopkey",
            "signature", "nonce", "pairingcode", "sessionkey", "approvaltoken",
            "serial", "stableid", "deviceid"
        ];
        return secretKeys.Any(normalized.Contains)
            ? "[redacted]"
            : RedactText(value.Length > 16 * 1024 ? value[..(16 * 1024)] : value);
    }

    public static string RedactText(string value)
    {
        var redacted = SecretAssignmentRegex.Replace(
            value,
            match => match.Groups["prefix"].Value + "[redacted]");
        return PairingCodeRegex.Replace(
            redacted,
            match => match.Groups["prefix"].Value + "[redacted]");
    }

    public static IReadOnlyList<string> SanitizeAdbArguments(IReadOnlyList<string> arguments)
    {
        var result = arguments.ToArray();
        var serialIndex = Array.FindIndex(
            result,
            x => string.Equals(x, "-s", StringComparison.OrdinalIgnoreCase));
        if (serialIndex >= 0 && serialIndex + 1 < result.Length)
            result[serialIndex + 1] = "[device]";
        var pairIndex = Array.FindIndex(
            result,
            x => string.Equals(x, "pair", StringComparison.OrdinalIgnoreCase));
        if (pairIndex >= 0 && pairIndex + 2 < result.Length)
            result[pairIndex + 2] = "[redacted]";
        return result;
    }

    public static string SanitizeAdbOutput(
        IReadOnlyList<string> arguments,
        string output)
    {
        var result = output;
        var argumentArray = arguments.ToArray();
        var serialIndex = Array.FindIndex(
            argumentArray,
            value => string.Equals(value, "-s", StringComparison.OrdinalIgnoreCase));
        if (serialIndex >= 0 && serialIndex + 1 < argumentArray.Length)
            result = result.Replace(
                argumentArray[serialIndex + 1],
                "[device]",
                StringComparison.Ordinal);

        if (argumentArray.Length > 0 &&
            string.Equals(argumentArray[0], "devices", StringComparison.OrdinalIgnoreCase))
            result = Regex.Replace(
                result,
                @"(?m)^(?!List of devices)(?<serial>\S+)(?<rest>\s+device\b)",
                "[device]${rest}");

        var pairIndex = Array.FindIndex(
            argumentArray,
            value => string.Equals(value, "pair", StringComparison.OrdinalIgnoreCase));
        if (pairIndex >= 0 && pairIndex + 2 < argumentArray.Length)
            result = result.Replace(
                argumentArray[pairIndex + 2],
                "[redacted]",
                StringComparison.Ordinal);
        return RedactText(result);
    }

    internal static void RotateFiles(string currentPath, int maxFiles)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxFiles, 2);
        for (var index = maxFiles - 1; index >= 1; index--)
        {
            var destination = currentPath + $".{index}";
            var source = index == 1 ? currentPath : currentPath + $".{index - 1}";
            if (!File.Exists(source)) continue;
            File.Move(source, destination, overwrite: true);
        }
    }
}
