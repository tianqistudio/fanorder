using V12UltraController.Core.Control;

namespace V12UltraController.Core.Configuration;

public sealed class ConfigurationValidationException : IOException
{
    public ConfigurationValidationException(string message)
        : base(message)
    {
    }

    public ConfigurationValidationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed record ControllerConfiguration
{
    public const int CurrentSchemaVersion = 3;

    public const byte DefaultStartupFanPercent = 50;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public bool Commissioned { get; init; }

    public ControlMode Mode { get; init; } = ControlMode.Auto;

    public byte ManualSpeedPercent { get; init; } = 100;

    /// <summary>
    /// One-shot speed used after the backend starts and the first verified device
    /// read reports that the fan is not running. It never overrides an explicit
    /// manual mode or a runtime OFF request.
    /// </summary>
    public byte StartupFanPercent { get; init; } = DefaultStartupFanPercent;

    public FanCurve Curve { get; init; } = FanCurve.RemoteHostSafe;

    public SystemShutdownFanBehavior SystemShutdownFanBehavior { get; init; } =
        SystemShutdownFanBehavior.KeepAt100;

    public SystemShutdownOffQualification? ShutdownOffQualification { get; init; }

    public static ControllerConfiguration CreateDefault() => new();

    public void Validate()
    {
        if (SchemaVersion != CurrentSchemaVersion)
        {
            throw new ConfigurationValidationException(
                $"Unsupported configuration schema {SchemaVersion}; expected {CurrentSchemaVersion}.");
        }

        if (!Enum.IsDefined(Mode))
        {
            throw new ConfigurationValidationException("The controller mode is invalid.");
        }

        if (!Enum.IsDefined(SystemShutdownFanBehavior))
        {
            throw new ConfigurationValidationException("The system-shutdown fan behavior is invalid.");
        }

        ShutdownOffQualification?.Validate();
        if (SystemShutdownFanBehavior == SystemShutdownFanBehavior.TurnOffOnSystemShutdown &&
            ShutdownOffQualification is null)
        {
            throw new ConfigurationValidationException(
                "Turning the fan off at system shutdown requires an exact attended device qualification.");
        }

        if (ManualSpeedPercent is < 10 or > 100)
        {
            throw new ConfigurationValidationException("Manual fan control must be between 10 and 100 percent.");
        }

        if (StartupFanPercent is < 10 or > 100)
        {
            throw new ConfigurationValidationException("Startup fan control must be between 10 and 100 percent.");
        }

        if (Curve is null)
        {
            throw new ConfigurationValidationException("The fan curve is required.");
        }

        Curve.Validate();
    }

    internal ControllerConfiguration UpgradeToCurrentSchema()
    {
        return SchemaVersion switch
        {
            CurrentSchemaVersion => this,
            1 => this with
            {
                SchemaVersion = CurrentSchemaVersion,
                SystemShutdownFanBehavior = SystemShutdownFanBehavior.KeepAt100,
                ShutdownOffQualification = null,
                StartupFanPercent = DefaultStartupFanPercent,
            },
            2 => this with
            {
                SchemaVersion = CurrentSchemaVersion,
                StartupFanPercent = DefaultStartupFanPercent,
            },
            _ => this,
        };
    }
}
