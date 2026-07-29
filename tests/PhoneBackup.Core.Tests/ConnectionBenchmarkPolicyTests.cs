using PhoneBackup.Core;

namespace PhoneBackup.Core.Tests;

public sealed class ConnectionBenchmarkPolicyTests
{
    [Fact]
    public void Usb2Link_IsReported_WhenSuperSpeedDeviceNegotiatesHighSpeed()
    {
        var result = ConnectionBenchmarkPolicy.Assess(
            UsbLinkSpeed.HighSpeed,
            deviceSuperSpeedCapable: true,
            adbBytesPerSecond: 39 * 1024 * 1024,
            fastLanBytesPerSecond: 58 * 1024 * 1024);

        Assert.Equal(MediaTransportMode.FastLan, result.RecommendedTransport);
        Assert.Equal(ConnectionLimitReason.Usb2Link, result.LimitReason);
    }

    [Fact]
    public void AdbWins_WhenFastLanIsNotAtLeastTenPercentFaster()
    {
        var result = ConnectionBenchmarkPolicy.Assess(
            UsbLinkSpeed.SuperSpeed,
            deviceSuperSpeedCapable: true,
            adbBytesPerSecond: 80 * 1024 * 1024,
            fastLanBytesPerSecond: 86 * 1024 * 1024);

        Assert.Equal(MediaTransportMode.Adb, result.RecommendedTransport);
        Assert.Equal(ConnectionLimitReason.Comparable, result.LimitReason);
    }

    [Fact]
    public void Estimate_UsesMeasuredThroughput()
    {
        var estimate = ConnectionBenchmarkPolicy.Estimate(
            50L * 1024 * 1024 * 1024,
            50d * 1024 * 1024);

        Assert.Equal(TimeSpan.FromSeconds(1024), estimate);
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(10, 0)]
    [InlineData(10, double.NaN)]
    public void Estimate_RejectsInvalidInputs(long bytes, double speed)
    {
        Assert.Null(ConnectionBenchmarkPolicy.Estimate(bytes, speed));
    }
}
