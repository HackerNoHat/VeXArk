namespace PhoneBackup.Core;

public enum UsbLinkSpeed
{
    Unknown,
    LowSpeed,
    FullSpeed,
    HighSpeed,
    SuperSpeed,
    SuperSpeedPlus
}

public enum ConnectionLimitReason
{
    Unknown,
    Usb2Link,
    FastLanFaster,
    WirelessUnavailable,
    Comparable
}

public sealed record ConnectionBenchmarkAssessment(
    MediaTransportMode RecommendedTransport,
    ConnectionLimitReason LimitReason,
    double EffectiveBytesPerSecond);

public static class ConnectionBenchmarkPolicy
{
    public static ConnectionBenchmarkAssessment Assess(
        UsbLinkSpeed usbLinkSpeed,
        bool deviceSuperSpeedCapable,
        double adbBytesPerSecond,
        double fastLanBytesPerSecond)
    {
        var hasAdb = adbBytesPerSecond > 0;
        var hasFastLan = fastLanBytesPerSecond > 0;
        var preferFastLan = hasFastLan &&
                            (!hasAdb || fastLanBytesPerSecond > adbBytesPerSecond * 1.10);
        var recommended = preferFastLan
            ? MediaTransportMode.FastLan
            : MediaTransportMode.Adb;
        var effective = preferFastLan ? fastLanBytesPerSecond : adbBytesPerSecond;

        var reason = usbLinkSpeed == UsbLinkSpeed.HighSpeed && deviceSuperSpeedCapable
            ? ConnectionLimitReason.Usb2Link
            : !hasFastLan
                ? ConnectionLimitReason.WirelessUnavailable
                : preferFastLan
                    ? ConnectionLimitReason.FastLanFaster
                    : hasAdb
                        ? ConnectionLimitReason.Comparable
                        : ConnectionLimitReason.Unknown;

        return new(recommended, reason, effective);
    }

    public static TimeSpan? Estimate(long bytes, double bytesPerSecond)
    {
        if (bytes <= 0 || bytesPerSecond <= 0 ||
            double.IsNaN(bytesPerSecond) || double.IsInfinity(bytesPerSecond))
            return null;
        return TimeSpan.FromSeconds(bytes / bytesPerSecond);
    }
}
