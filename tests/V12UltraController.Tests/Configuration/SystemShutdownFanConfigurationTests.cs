using V12UltraController.Core.Configuration;
using V12UltraController.Core.Hardware;

namespace V12UltraController.Tests.Configuration;

public sealed class SystemShutdownFanConfigurationTests
{
    [Fact]
    public void Default_IsSchemaThreeAndKeepsFanAt100WithoutQualification()
    {
        var configuration = ControllerConfiguration.CreateDefault();

        Assert.Equal(3, configuration.SchemaVersion);
        Assert.Equal(50, configuration.StartupFanPercent);
        Assert.Equal(SystemShutdownFanBehavior.KeepAt100, configuration.SystemShutdownFanBehavior);
        Assert.Null(configuration.ShutdownOffQualification);
        configuration.Validate();
    }

    [Fact]
    public void TurnOffBehavior_RequiresQualification()
    {
        var configuration = ControllerConfiguration.CreateDefault() with
        {
            SystemShutdownFanBehavior = SystemShutdownFanBehavior.TurnOffOnSystemShutdown,
        };

        _ = Assert.Throws<ConfigurationValidationException>(configuration.Validate);
    }

    [Theory]
    [InlineData(9)]
    [InlineData(101)]
    public void StartupFanPercent_StaysWithinTheSafeControlRange(byte percent)
    {
        var configuration = ControllerConfiguration.CreateDefault() with { StartupFanPercent = percent };

        _ = Assert.Throws<ConfigurationValidationException>(configuration.Validate);
    }

    [Fact]
    public void TurnOffBehavior_AcceptsOnlyExactCompletedSupportedDeviceQualification()
    {
        var configuration = ControllerConfiguration.CreateDefault() with
        {
            SystemShutdownFanBehavior = SystemShutdownFanBehavior.TurnOffOnSystemShutdown,
            ShutdownOffQualification = ValidQualification(),
        };

        configuration.Validate();
    }

    [Fact]
    public void TurnOffBehavior_RejectsQualificationForDifferentFirmware()
    {
        var configuration = ControllerConfiguration.CreateDefault() with
        {
            SystemShutdownFanBehavior = SystemShutdownFanBehavior.TurnOffOnSystemShutdown,
            ShutdownOffQualification = ValidQualification() with
            {
                FirmwareVersion = DeviceAllowList.V12UltraFirmwareVersion + 1,
            },
        };

        _ = Assert.Throws<ConfigurationValidationException>(configuration.Validate);
    }

    [Fact]
    public void TurnOffBehavior_RejectsNonUtcQualificationTimestamp()
    {
        var configuration = ControllerConfiguration.CreateDefault() with
        {
            SystemShutdownFanBehavior = SystemShutdownFanBehavior.TurnOffOnSystemShutdown,
            ShutdownOffQualification = ValidQualification() with
            {
                QualifiedAtUtc = new DateTimeOffset(2026, 9, 5, 8, 0, 0, TimeSpan.FromHours(8)),
            },
        };

        _ = Assert.Throws<ConfigurationValidationException>(configuration.Validate);
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public void TurnOffBehavior_RejectsIncompleteAttendedEvidence(
        bool offConfirmed,
        bool onConfirmed,
        bool lightingPreserved)
    {
        var configuration = ControllerConfiguration.CreateDefault() with
        {
            SystemShutdownFanBehavior = SystemShutdownFanBehavior.TurnOffOnSystemShutdown,
            ShutdownOffQualification = ValidQualification() with
            {
                ShutdownOffReadbackConfirmed = offConfirmed,
                StartupOnAt100ReadbackConfirmed = onConfirmed,
                LightingStatePreserved = lightingPreserved,
            },
        };

        _ = Assert.Throws<ConfigurationValidationException>(configuration.Validate);
    }

    private static SystemShutdownOffQualification ValidQualification() => new()
    {
        ProtocolRevision = SystemShutdownOffQualification.CurrentProtocolRevision,
        VendorId = DeviceAllowList.V12UltraVendorId,
        ProductId = DeviceAllowList.V12UltraProductId,
        FirmwareVersion = DeviceAllowList.V12UltraFirmwareVersion,
        UsagePage = DeviceAllowList.V12UltraUsagePage,
        Usage = DeviceAllowList.V12UltraUsage,
        FeatureReportLength = V12FeatureReport.ReportLength,
        ShutdownOffReadbackConfirmed = true,
        StartupOnAt100ReadbackConfirmed = true,
        LightingStatePreserved = true,
        QualifiedAtUtc = new DateTimeOffset(2026, 9, 5, 0, 0, 0, TimeSpan.Zero),
    };
}
