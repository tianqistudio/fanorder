using V12UltraController.Core.Configuration;
using V12UltraController.Core.Control;
using V12UltraController.Service;

namespace V12UltraController.Tests.Service;

public sealed partial class ControllerCoordinatorTests
{
    [Fact]
    public async Task PureManualStartup_RuntimeDisableDuringReadPreventsOnWrite()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(true, initialConfiguration:
            ControllerConfiguration.CreateDefault() with { Commissioned = true, Mode = ControlMode.PureManual });
        fixture.Device.SetState(37, poweredOn: false);
        using var readGate = new ManualResetEventSlim(false);
        fixture.Device.ReadGate = readGate;
        var tick = fixture.Coordinator.TickAsync(CancellationToken.None);
        try
        {
            await fixture.Device.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
            await fixture.Coordinator.DisableSafetyProtectionAsync(ProfilePeer, CancellationToken.None);
        }
        finally { readGate.Set(); }
        await tick;
        Assert.False(fixture.Device.PoweredOn);
        Assert.Empty(fixture.Device.ManualSoftwareControlTargets);
    }

    [Fact]
    public async Task PureManualStartup_ExplicitOffBeforeFirstTickWins()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(true, initialConfiguration:
            ControllerConfiguration.CreateDefault() with { Commissioned = true, Mode = ControlMode.PureManual });
        await fixture.Coordinator.SetManualPowerAsync(false, true, ProfilePeer, CancellationToken.None);
        var count = fixture.Device.Writes.Count;
        await fixture.Coordinator.TickAsync(CancellationToken.None);
        Assert.False(fixture.Device.PoweredOn);
        Assert.Equal(count, fixture.Device.Writes.Count);
        Assert.Empty(fixture.Device.ManualSoftwareControlTargets);
    }

    [Fact]
    public async Task PureManualStartup_ReleasedModeNeverStartsFan()
    {
        var marker = new FakeSafetyProtectionStore
            { Current = new SafetyProtectionIntent(1, true, DateTimeOffset.UtcNow, "S-1-5-21-test") };
        await using var fixture = await CoordinatorFixture.CreateAsync(true, initialConfiguration:
            ControllerConfiguration.CreateDefault() with { Commissioned = true, Mode = ControlMode.Released },
            suppliedSafetyProtectionStore: marker);
        fixture.Device.SetState(37, poweredOn: false);
        await fixture.Coordinator.TickAsync(CancellationToken.None);
        Assert.False(fixture.Device.PoweredOn);
        Assert.Empty(fixture.Device.Writes);
        Assert.Empty(fixture.Device.ManualSoftwareControlTargets);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PureManualStartup_OpensOffFanAtSavedTargetOnceAndRetainsPolicy(bool disabled)
    {
        var marker = new FakeSafetyProtectionStore
        {
            Current = disabled ? new SafetyProtectionIntent(1, true, DateTimeOffset.UtcNow, "S-1-5-21-test") : null,
        };
        var config = ControllerConfiguration.CreateDefault() with
            { Commissioned = true, Mode = ControlMode.PureManual, ManualSpeedPercent = 61, StartupFanPercent = 50 };
        await using var fixture = await CoordinatorFixture.CreateAsync(true, initialConfiguration: config,
            suppliedSafetyProtectionStore: marker);
        fixture.Device.SetState(37, poweredOn: false);
        await fixture.Coordinator.TickAsync(CancellationToken.None);
        Assert.True(fixture.Device.PoweredOn);
        Assert.Equal([(byte)61], fixture.Device.ManualSoftwareControlTargets);
        Assert.Equal(ControlMode.PureManual, fixture.Coordinator.GetStatus().Fan.Mode);
        Assert.Equal(disabled, fixture.Coordinator.GetStatus().SafetyProtection!.Disabled);
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(config),
            System.Text.Json.JsonSerializer.Serialize(await fixture.ConfigurationStore.LoadAsync(CancellationToken.None)));
        var count = fixture.Device.Writes.Count;
        fixture.Device.SetState(61, poweredOn: false); // Later physical OFF must win.
        for (var i = 0; i < 3; i++) await fixture.Coordinator.TickAsync(CancellationToken.None);
        Assert.False(fixture.Device.PoweredOn);
        Assert.Equal(count, fixture.Device.Writes.Count);
        Assert.Single(fixture.Device.ManualSoftwareControlTargets);
    }

    [Fact]
    public async Task PureManualStartup_AlreadyRunningFanIsNotResetToSavedTarget()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(true, initialConfiguration:
            ControllerConfiguration.CreateDefault() with { Commissioned = true, Mode = ControlMode.PureManual, ManualSpeedPercent = 61 });
        fixture.Device.SetState(73, poweredOn: true);
        await fixture.Coordinator.TickAsync(CancellationToken.None);
        Assert.Equal((byte)73, fixture.Device.CurrentPercent);
        Assert.Empty(fixture.Device.Writes);
        Assert.Empty(fixture.Device.ManualSoftwareControlTargets);
    }

    [Fact]
    public async Task PureManualStartup_UnconfirmedWriteIsNotReplayed()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(true, initialConfiguration:
            ControllerConfiguration.CreateDefault() with { Commissioned = true, Mode = ControlMode.PureManual, ManualSpeedPercent = 61 });
        fixture.Device.SetState(37, poweredOn: false);
        fixture.Device.IgnoreManualSoftwareControl = true;
        await fixture.Coordinator.TickAsync(CancellationToken.None);
        Assert.Equal("StartupFanRecoveryUnavailable", fixture.Coordinator.GetStatus().ServiceState);
        Assert.Null(fixture.Coordinator.GetStatus().LastSuccessfulWrite);
        await fixture.Coordinator.TickAsync(CancellationToken.None);
        Assert.Single(fixture.Device.ManualSoftwareControlTargets);
        Assert.False(fixture.Device.PoweredOn);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PureManualStartup_RuntimeDisableOrUnreadableMarkerPreventsRecovery(bool unreadable)
    {
        var marker = new FakeSafetyProtectionStore { FailReadback = unreadable };
        await using var fixture = await CoordinatorFixture.CreateAsync(true, initialConfiguration:
            ControllerConfiguration.CreateDefault() with { Commissioned = true, Mode = ControlMode.PureManual },
            suppliedSafetyProtectionStore: marker);
        fixture.Device.SetState(37, poweredOn: false);
        if (!unreadable) await fixture.Coordinator.DisableSafetyProtectionAsync(ProfilePeer, CancellationToken.None);
        await fixture.Coordinator.TickAsync(CancellationToken.None);
        Assert.Empty(fixture.Device.ManualSoftwareControlTargets);
        Assert.False(fixture.Device.PoweredOn);
    }
}
