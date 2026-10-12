namespace V12UltraController.Core.Status;

public sealed record HistorySample(DateTimeOffset Time, double? Temperature, double? Target, double? Reported,
    bool BreakBefore = false);

/// <summary>In-memory UI observation history. Never backfills stale telemetry.</summary>
public sealed class RecentHistory
{
    private readonly List<HistorySample> samples = [];
    public IReadOnlyList<HistorySample> Samples => samples.AsReadOnly();

    public void Add(DateTimeOffset now, ControllerStatus? status)
    {
        if (samples.Count > 0 && now < samples[^1].Time) samples.Clear();
        if (samples.Count > 0 && now - samples[^1].Time < TimeSpan.FromSeconds(1)) return;
        samples.RemoveAll(sample => sample.Time < now - TimeSpan.FromMinutes(10));
        var breakBefore = samples.Count > 0 && now - samples[^1].Time > TimeSpan.FromSeconds(10);
        static bool Fresh(DateTimeOffset now, DateTimeOffset? stamp) => stamp is { } value &&
            now >= value && now - value <= TimeSpan.FromSeconds(10);
        var connected = status is { Commissioned: true, Device.Connected: true, HardwareSafety.Poisoned: false };
        var temperature = status is { Thermal.Healthy: true } && Fresh(now, status.Thermal.LastUpdated) &&
            status.Thermal.ControlCelsius is >= 0 and <= 120 ? status.Thermal.ControlCelsius : null;
        samples.Add(new(now, temperature, connected ? status!.Fan.RequestedPercent : null,
            connected && Fresh(now, status!.LastSuccessfulRead) ? status!.Fan.ReportedControlPercent : null, breakBefore));
        while (samples.Count > 601) samples.RemoveAt(0);
    }
}
