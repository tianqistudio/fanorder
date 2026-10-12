namespace V12UltraController.Service;

internal enum ServiceStopReason
{
    Unknown,
    ServiceStop,
    SystemShutdown,
}

/// <summary>
/// Captures the first terminal signal delivered by the Windows Service Control
/// Manager. Unknown is intentionally fail-closed and must never be interpreted as
/// a system shutdown.
/// </summary>
internal sealed class ServiceStopIntent
{
    private int reason;

    public ServiceStopReason Reason => (ServiceStopReason)Volatile.Read(ref reason);

    public bool TrySet(ServiceStopReason value)
    {
        if (value is ServiceStopReason.Unknown || !Enum.IsDefined(value))
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, "A terminal stop reason is required.");
        }

        return Interlocked.CompareExchange(ref reason, (int)value, (int)ServiceStopReason.Unknown) ==
               (int)ServiceStopReason.Unknown;
    }
}
