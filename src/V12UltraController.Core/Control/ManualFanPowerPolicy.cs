namespace V12UltraController.Core.Control;

/// <summary>
/// Runtime-only manual power intent. This does not perform I/O or claim a device
/// has changed state. The coordinator must separately verify every power write.
/// </summary>
public sealed class ManualFanPowerPolicy
{
    public const double OffEntryBelowCelsius = 60;
    public const double ResumeAtCelsius = 85;
    public static readonly TimeSpan MaximumSampleAge = TimeSpan.FromSeconds(10);

    public bool OffRequested { get; private set; }

    // Observation adopts a power state that already exists; it never requests
    // an OFF write and is still evaluated against the independent protection policy.
    public void ObserveDevicePower(bool poweredOn) => OffRequested = !poweredOn;

    public ManualFanPowerDecision RequestOff(ManualFanPowerInput input, bool confirmed)
    {
        ArgumentNullException.ThrowIfNull(input);
        // Rejected requests must not alter a previously accepted state.
        if (!confirmed)
        {
            return new(OffRequested, false, "CONFIRMATION_REQUIRED");
        }

        var rejection = GetUnsafeReason(input);
        if (rejection is not null)
        {
            return new(OffRequested, false, rejection);
        }

        if (input.ControlCelsius >= OffEntryBelowCelsius)
        {
            return new(OffRequested, false, "TEMPERATURE_TOO_HIGH_TO_STOP");
        }

        OffRequested = true;
        return new(true, true, null);
    }

    public ManualFanPowerDecision RequestOn()
    {
        OffRequested = false;
        return new(false, true, null);
    }

    public ManualFanPowerDecision Evaluate(ManualFanPowerInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!OffRequested)
        {
            return new(false, true, null);
        }

        var reason = GetUnsafeReason(input);
        if (reason is null && input.ControlCelsius >= ResumeAtCelsius)
        {
            reason = "THERMAL_AUTO_RESUME";
        }

        if (reason is not null)
        {
            // A recovered sensor must not silently stop the fan again.
            OffRequested = false;
            return new(false, true, reason);
        }

        return new(true, true, null);
    }

    private static string? GetUnsafeReason(ManualFanPowerInput input)
    {
        if (!input.OwnershipVerified)
        {
            return "OWNERSHIP_UNAVAILABLE";
        }

        if (!input.CommunicationHealthy || input.FailSafeActive)
        {
            return "FAILSAFE_ACTIVE";
        }

        if (!input.CpuValid || input.ControlCelsius is not { } temperature ||
            !double.IsFinite(temperature) || temperature is < 0 or > 120 ||
            input.SampleAge < TimeSpan.Zero || input.SampleAge >= MaximumSampleAge)
        {
            return "TELEMETRY_STALE";
        }

        return null;
    }
}

public sealed record ManualFanPowerInput(
    bool OwnershipVerified,
    bool CommunicationHealthy,
    bool FailSafeActive,
    bool CpuValid,
    double? ControlCelsius,
    TimeSpan SampleAge);

public sealed record ManualFanPowerDecision(bool KeepOff, bool Accepted, string? Reason);
