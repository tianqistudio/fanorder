using V12UltraController.Core.Hardware;

namespace V12UltraController.Tests.Hardware;

// Production packet/device code with an in-memory transport; no locator or native HID.
public sealed class V12ManualSpeedTests
{
    [Fact]
    public void SoftwareManualSpeedHasIndependentGoldenVector()
    {
        var before = V12FeatureReport.ParseState(
            V12FeatureReportTests.CreateStateReport(0x88, 44, 1, 0x83, 0, 2, 128));

        var report = V12FeatureReport.CreateSoftwareManualSpeed(before, 50);

        Assert.Equal(new byte[] { 0, 1, 0x32, 0, 0x83, 0, 2, 0x80, 0xC7 }, report);
        Assert.True(V12FeatureReport.HasValidChecksum(report));
        Assert.Equal((byte)44, before.FanSpeedPercent);
        Assert.False(before.PoweredOn);
        Assert.Equal((byte)0x88, before.RawModeStatus);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(51)]
    [InlineData(59)]
    [InlineData(100)]
    public async Task SoftwareManualTargetWritesOneOnFrameAtRequestedSpeedPreservingLighting(byte percent)
    {
        var transport = new ManualTransport();
        var waits = new List<TimeSpan>();
        await using var device = new V12Device(transport, (duration, token) =>
        {
            token.ThrowIfCancellationRequested(); waits.Add(duration); return Task.CompletedTask;
        });
        await device.ReadStateAsync(TestContext.Current.CancellationToken);

        await ((IV12ManualSpeedDevice)device).SelectSoftwareControlAtSpeedAsync(percent,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, transport.Reports.Count);
        Assert.Equal(V12FeatureReport.CreateReadQuery(), transport.Reports[0]);
        var command = transport.Reports[1];
        Assert.Equal((byte)1, command[1]);
        Assert.Equal(percent, command[2]);
        Assert.Equal((byte)0, command[3]);
        Assert.Equal(new byte[] { 0x83, 4, 2, 177 }, command[4..8]);
        Assert.True(V12FeatureReport.HasValidChecksum(command));
        Assert.Equal(Enumerable.Repeat(TimeSpan.FromMilliseconds(10), 3), waits);
        Assert.Equal(1, transport.Gets);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(9)]
    [InlineData(101)]
    [InlineData(255)]
    public async Task InvalidSoftwareManualTargetNeverReachesTransport(byte percent)
    {
        var transport = new ManualTransport();
        await using var device = new V12Device(transport, ImmediateDelay);
        var before = await device.ReadStateAsync(TestContext.Current.CancellationToken);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            V12FeatureReport.CreateSoftwareManualSpeed(before, percent));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            device.SelectSoftwareControlAtSpeedAsync(percent, TestContext.Current.CancellationToken));

        Assert.Equal(V12FeatureReport.CreateReadQuery(), Assert.Single(transport.Reports));
    }

    [Fact]
    public async Task SoftwareManualTargetRequiresVerifiedState()
    {
        var transport = new ManualTransport();
        await using var device = new V12Device(transport, ImmediateDelay);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            device.SelectSoftwareControlAtSpeedAsync(51, TestContext.Current.CancellationToken));

        Assert.Empty(transport.Reports);
        Assert.Equal(0, transport.Gets);
    }

    [Fact]
    public async Task BadInitialReadDoesNotArmSoftwareManualTarget()
    {
        var transport = new ManualTransport();
        transport.StateReport[^1] ^= 1;
        await using var device = new V12Device(transport, ImmediateDelay);
        await Assert.ThrowsAsync<V12ProtocolException>(() => device.ReadStateAsync(TestContext.Current.CancellationToken));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            device.SelectSoftwareControlAtSpeedAsync(51, TestContext.Current.CancellationToken));

        Assert.Equal(V12FeatureReport.CreateReadQuery(), Assert.Single(transport.Reports));
    }

    [Fact]
    public void IncomingModeByteIsNeverEchoedIntoSoftwareManualCommand()
    {
        for (var mode = 0; mode <= byte.MaxValue; mode++)
        {
            var observed = V12FeatureReport.ParseState(
                V12FeatureReportTests.CreateStateReport((byte)mode, 44, 0, 0xFF, 0xFC, 0xFE, 0xFD));
            var command = V12FeatureReport.CreateSoftwareManualSpeed(observed, 51);
            Assert.Equal((byte)1, command[1]);
            Assert.Equal(new byte[] { 0xFF, 0xFC, 0xFE, 0xFD }, command[4..8]);
            Assert.True(V12FeatureReport.HasValidChecksum(command));
        }
    }

    [Fact]
    public async Task SoftwareManualPacingCancellationPreventsSendAndReleasesSharedGate()
    {
        var transport = new ManualTransport();
        var spacing = new ManualSpacing();
        await using var device = new V12Device(transport, spacing.DelayAsync);
        await device.ReadStateAsync(TestContext.Current.CancellationToken);
        using var lifetime = new CancellationTokenSource();

        var command = device.SelectSoftwareControlAtSpeedAsync(51, lifetime.Token);
        try
        {
            await spacing.Entered.Task.WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
            Assert.Single(transport.Reports);
            await lifetime.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => command);
            Assert.Single(transport.Reports);
        }
        finally { spacing.Release.TrySetResult(); }

        await device.SelectSoftwareControlAtSpeedAsync(59, TestContext.Current.CancellationToken);
        Assert.Equal(2, transport.Reports.Count);
        Assert.Equal((byte)59, transport.Reports[1][2]);
    }

    [Fact]
    public async Task SoftwareManualRequestWaitingForGateDoesNotEnterPacingOrTransport()
    {
        var transport = new ManualTransport();
        var spacing = new ManualSpacing();
        await using var device = new V12Device(transport, spacing.DelayAsync);
        await device.ReadStateAsync(TestContext.Current.CancellationToken);
        var first = device.WriteFanSpeedAsync(44, TestContext.Current.CancellationToken);
        using var lifetime = new CancellationTokenSource();
        try
        {
            await spacing.Entered.Task.WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
            var waiting = device.SelectSoftwareControlAtSpeedAsync(51, lifetime.Token);
            Assert.False(waiting.IsCompleted);
            await lifetime.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
            Assert.Equal(3, spacing.Calls);
            Assert.Single(transport.Reports);
        }
        finally { spacing.Release.TrySetResult(); }
        await first;
        Assert.Equal((byte)0, transport.Reports[1][1]);
    }

    [Fact]
    public async Task IssuedSoftwareManualTransportKeepsGateUntilActualReturn()
    {
        var transport = new ManualTransport();
        await using var device = new V12Device(transport, ImmediateDelay);
        await device.ReadStateAsync(TestContext.Current.CancellationToken);
        transport.HoldIssuedCall = true;
        using var lifetime = new CancellationTokenSource();
        var command = device.SelectSoftwareControlAtSpeedAsync(51, lifetime.Token);
        try
        {
            await transport.Issued.Task.WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
            await lifetime.CancelAsync();
            Assert.False(command.IsCompleted);
            Assert.Equal(2, transport.Reports.Count);
        }
        finally { transport.NativeReturn.TrySetResult(); }
        await command;
        Assert.Equal((byte)51, transport.Reports[1][2]);
    }

    [Fact]
    public async Task ExistingFixedSoftwareRecoveryRemainsAtOneHundred()
    {
        var transport = new ManualTransport();
        await using var device = new V12Device(transport, ImmediateDelay);
        await device.ReadStateAsync(TestContext.Current.CancellationToken);
        await device.SelectSoftwareControlAtSpeedAsync(51, TestContext.Current.CancellationToken);
        await device.SelectSoftwareControlAtOneHundredAsync(TestContext.Current.CancellationToken);

        Assert.Equal(3, transport.Reports.Count);
        Assert.Equal((byte)51, transport.Reports[1][2]);
        Assert.Equal((byte)1, transport.Reports[2][1]);
        Assert.Equal((byte)100, transport.Reports[2][2]);
        Assert.Equal(transport.Reports[1][4..8], transport.Reports[2][4..8]);
    }

    private static Task ImmediateDelay(TimeSpan duration, CancellationToken token)
    { token.ThrowIfCancellationRequested(); return Task.CompletedTask; }

    private sealed class ManualSpacing
    {
        public int Calls { get; private set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task DelayAsync(TimeSpan duration, CancellationToken token)
        {
            Assert.Equal(TimeSpan.FromMilliseconds(10), duration);
            token.ThrowIfCancellationRequested();
            if (++Calls != 3) return Task.CompletedTask;
            Entered.TrySetResult();
            return Release.Task.WaitAsync(token);
        }
    }

    private sealed class ManualTransport : IHidFeatureTransport
    {
        public DeviceIdentity Identity { get; } = new(0x374A, 0xB101, 0x1602, 0xFF00, 1);
        public int FeatureReportLength => V12FeatureReport.ReportLength;
        public byte[] StateReport { get; } = V12FeatureReportTests.CreateStateReport(0x88, 44, 0, 0x83, 4, 2, 177);
        public List<byte[]> Reports { get; } = [];
        public int Gets { get; private set; }
        public bool HoldIssuedCall { get; set; }
        public TaskCompletionSource Issued { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource NativeReturn { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask SetFeatureAsync(ReadOnlyMemory<byte> report, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Reports.Add(report.ToArray());
            if (!HoldIssuedCall) return;
            Issued.TrySetResult();
            await NativeReturn.Task.ConfigureAwait(false);
        }
        public ValueTask<byte[]> GetFeatureAsync(byte reportId, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Gets++; return ValueTask.FromResult(StateReport.ToArray()); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
