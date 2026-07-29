using System.Buffers;
using System.Diagnostics;
using System.Security.Cryptography;
using PhoneBackup.Core;

namespace PhoneBackup.Desktop;

public sealed record ConnectionBenchmarkProgress(
    string Stage,
    long CompletedBytes,
    long TotalBytes);

public sealed record ConnectionBenchmarkResult(
    UsbConnectionInfo Usb,
    double AdbBytesPerSecond,
    double FastLanBytesPerSecond,
    long ProbeBytesPerTransport,
    TimeSpan Elapsed,
    ConnectionBenchmarkAssessment Assessment,
    string? FastLanError = null);

public sealed class ConnectionBenchmarkCoordinator
{
    public const long DefaultProbeBytes = 256L * 1024 * 1024;
    private const long MaxProbeCommandBytes = 64L * 1024 * 1024;
    private const int IoBufferBytes = 1024 * 1024;

    public async Task<ConnectionBenchmarkResult> RunAsync(
        AgentClient agent,
        string adbSerial,
        long probeBytes = DefaultProbeBytes,
        IProgress<ConnectionBenchmarkProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (probeBytes is < 16L * 1024 * 1024 or > 2L * 1024 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(probeBytes));

        var totalStopwatch = Stopwatch.StartNew();
        progress?.Report(new("usb", 0, probeBytes));
        var usb = await Task.Run(
            () => UsbConnectionDiagnostics.Query(adbSerial),
            cancellationToken);

        progress?.Report(new("adb", 0, probeBytes));
        var adbSpeed = await ProbeAdbAsync(
            agent,
            probeBytes,
            value => progress?.Report(new("adb", value, probeBytes)),
            cancellationToken);

        var fastSpeed = 0d;
        string? fastError = null;
        var capabilities = await agent.GetCapabilitiesAsync(cancellationToken);
        if (capabilities.Contains("fast-lan-aead-v1"))
        {
            progress?.Report(new("fast-lan", 0, probeBytes));
            try
            {
                fastSpeed = await ProbeFastLanAsync(
                    agent,
                    probeBytes,
                    value => progress?.Report(new("fast-lan", value, probeBytes)),
                    cancellationToken);
            }
            catch (Exception error) when (
                error is IOException or TimeoutException or InvalidOperationException)
            {
                fastError = error.Message;
            }
        }
        else
        {
            fastError = "The installed Agent does not support Fast Wi-Fi.";
        }

        totalStopwatch.Stop();
        var assessment = ConnectionBenchmarkPolicy.Assess(
            usb.LinkSpeed,
            usb.DeviceSuperSpeedCapable,
            adbSpeed,
            fastSpeed);
        progress?.Report(new("complete", probeBytes, probeBytes));
        return new(
            usb,
            adbSpeed,
            fastSpeed,
            probeBytes,
            totalStopwatch.Elapsed,
            assessment,
            fastError);
    }

    private static async Task<double> ProbeAdbAsync(
        AgentClient control,
        long totalBytes,
        Action<long> onProgress,
        CancellationToken cancellationToken)
    {
        await using var worker = await control.ConnectSiblingAsync(cancellationToken);
        var buffer = ArrayPool<byte>.Shared.Rent(IoBufferBytes);
        var transferred = 0L;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            while (transferred < totalBytes)
            {
                var partBytes = Math.Min(MaxProbeCommandBytes, totalBytes - transferred);
                await using var source = await worker.OpenMediaProbeAsync(
                    partBytes,
                    cancellationToken);
                using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var partReceived = 0L;
                while (true)
                {
                    var count = await source.ReadAsync(
                        buffer.AsMemory(0, IoBufferBytes),
                        cancellationToken);
                    if (count == 0) break;
                    digest.AppendData(buffer, 0, count);
                    partReceived += count;
                    transferred += count;
                    onProgress(transferred);
                }
                var completion = await source.Completion.WaitAsync(cancellationToken);
                FastMediaWorker.ValidateCompletion(
                    completion,
                    partReceived,
                    digest.GetHashAndReset());
            }
            stopwatch.Stop();
            return transferred / Math.Max(stopwatch.Elapsed.TotalSeconds, 0.001);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task<double> ProbeFastLanAsync(
        AgentClient control,
        long totalBytes,
        Action<long> onProgress,
        CancellationToken cancellationToken)
    {
        await using var fast = await FastMediaClient.ConnectAsync(
            control,
            workerCount: 1,
            cancellationToken: cancellationToken);
        var transferred = 0L;
        var stopwatch = Stopwatch.StartNew();
        while (transferred < totalBytes)
        {
            var partBytes = Math.Min(MaxProbeCommandBytes, totalBytes - transferred);
            _ = await fast.Workers[0].ProbeAsync(partBytes, cancellationToken);
            transferred += partBytes;
            onProgress(transferred);
        }
        stopwatch.Stop();
        return transferred / Math.Max(stopwatch.Elapsed.TotalSeconds, 0.001);
    }
}
