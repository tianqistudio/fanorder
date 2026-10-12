using V12UltraController.Core.Hardware;

namespace V12UltraController.Tests.Hardware;

public sealed class V12CommandTimingTests
{
    [Fact]
    public void SoftwareTakeover_IsFixedOn100AndPreservesLighting()
    {
        var state = V12FeatureReport.ParseState(V12FeatureReportTests.CreateStateReport(0x88, 44, 0, 0x83, 0, 2, 128));
        var report = V12FeatureReport.CreateSoftwareTakeoverAtOneHundred(state);
        Assert.Equal(new byte[] { 0, 1, 100, 0, 0x83, 0, 2, 128, 0x95 }, report);
        Assert.True(V12FeatureReport.HasValidChecksum(report));
    }
    [Theory]
    [InlineData(false, 44)]
    [InlineData(true, 100)]
    public async Task DelayedFirmwareSnapshot_RequiresVendorSpacing(bool honorDelay, byte expected)
    {
        var transport = new DelayedSnapshotTransport();
        await using var device = new V12Device(transport, (duration, token) =>
        {
            token.ThrowIfCancellationRequested();
            Assert.Equal(TimeSpan.FromMilliseconds(10), duration);
            if (honorDelay) { transport.Now += duration; }
            return Task.CompletedTask;
        });
        await device.ReadStateAsync(TestContext.Current.CancellationToken);
        await device.WriteFanSpeedAsync(100, TestContext.Current.CancellationToken);
        var confirmed = await device.ReadStateAsync(TestContext.Current.CancellationToken);
        Assert.Equal(expected, confirmed.FanSpeedPercent);
        Assert.Equal(1, transport.SpeedWrites);
        Assert.True(confirmed.PoweredOn);
    }

    [Fact]
    public async Task CancelledSpacingNeverSendsAQuery()
    {
        var transport = new DelayedSnapshotTransport();
        await using var device = new V12Device(transport, (_, _) => Task.FromCanceled(new CancellationToken(true)));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => device.ReadStateAsync(CancellationToken.None));
        Assert.Equal(0, transport.QueryWrites);
    }

    private sealed class DelayedSnapshotTransport : IHidFeatureTransport
    {
        public DeviceIdentity Identity { get; } = new(0x374A, 0xB101, 0x1602, 0xFF00, 1);
        public int FeatureReportLength => 9;
        public TimeSpan Now { get; set; }
        public int SpeedWrites { get; private set; }
        public int QueryWrites { get; private set; }
        private TimeSpan? changedAt;
        private TimeSpan? queriedAt;
        private byte target = 44;
        private byte snapshot = 44;
        public ValueTask SetFeatureAsync(ReadOnlyMemory<byte> report, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (report.Span[1] == 0x80) { QueryWrites++; queriedAt = Now; }
            else { SpeedWrites++; target = report.Span[2]; changedAt = Now; }
            return ValueTask.CompletedTask;
        }
        public ValueTask<byte[]> GetFeatureAsync(byte reportId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (changedAt is { } changed && queriedAt is { } query &&
                query - changed >= TimeSpan.FromMilliseconds(10) && Now - query >= TimeSpan.FromMilliseconds(10))
            {
                snapshot = target;
            }
            return ValueTask.FromResult(V12FeatureReportTests.CreateStateReport(0x88, snapshot, 0, 0x83, 0, 2, 128));
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
