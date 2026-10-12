using System.Collections.Concurrent;
using V12UltraController.Core.Hardware;

namespace V12UltraController.Tests.Hardware;

// Uses the production V12Device pacing/gate code with an in-memory transport only.
// It never constructs WindowsV12DeviceLocator or a native HID transport.
public sealed class V12DeviceCancellationBoundaryTests
{
    [Theory]
    [InlineData("fan")]
    [InlineData("lighting")]
    [InlineData("off")]
    [InlineData("software")]
    public async Task CancellationDuringCommandSpacingPreventsSetFeatureAndReleasesDeviceGate(string operation)
    {
        var transport = new RecordingTransport();
        var spacing = new ControlledSpacing();
        await using var device = new V12Device(transport, spacing.DelayAsync);
        await InitializeCachedStateAsync(device);
        using var lifetime = new CancellationTokenSource();

        var command = WriteAsync(device, operation, lifetime.Token);
        try
        {
            await spacing.Entered.Task.WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
            Assert.False(command.IsCompleted);
            Assert.Equal(0, transport.SetFeatureCalls);
            await lifetime.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => command);
            Assert.Equal(0, transport.SetFeatureCalls);
            Assert.Empty(transport.Reports);
        }
        finally
        {
            await lifetime.CancelAsync();
            spacing.Release.TrySetResult();
        }

        // A new explicit request has a fresh token. Cancellation neither poisons the
        // device gate nor changes the cached lighting used by a later valid request.
        await device.WriteFanSpeedAsync(59, TestContext.Current.CancellationToken);
        var report = Assert.Single(transport.Reports);
        Assert.Equal((byte)59, report[2]);
        Assert.Equal(new byte[] { 0x83, 4, 2, 177 }, report[4..8]);
        Assert.Equal(1, transport.SetFeatureCalls);
    }

    [Fact]
    public async Task CancellationWhileWaitingForDeviceGateNeverEntersTransportOrPacing()
    {
        var transport = new RecordingTransport();
        var spacing = new ControlledSpacing();
        await using var device = new V12Device(transport, spacing.DelayAsync);
        await InitializeCachedStateAsync(device);
        using var waitingLifetime = new CancellationTokenSource();
        var first = device.WriteFanSpeedAsync(59, TestContext.Current.CancellationToken);
        try
        {
            await spacing.Entered.Task.WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
            var waiting = device.WriteFanSpeedAsync(100, waitingLifetime.Token);
            Assert.False(waiting.IsCompleted);
            await waitingLifetime.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
            Assert.Equal(1, spacing.Calls);
            Assert.Equal(0, transport.SetFeatureCalls);
        }
        finally
        {
            await waitingLifetime.CancelAsync();
            spacing.Release.TrySetResult();
        }
        await first;
        Assert.Equal((byte)59, Assert.Single(transport.Reports)[2]);
    }

    [Fact]
    public async Task AlreadyIssuedCancellationInsensitiveTransportRemainsInFlightUntilItsReturn()
    {
        var transport = new RecordingTransport { HoldIssuedCall = true };
        await using var device = new V12Device(transport, (_, token) =>
        {
            token.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        });
        await InitializeCachedStateAsync(device);
        using var lifetime = new CancellationTokenSource();
        var command = device.WriteFanSpeedAsync(100, lifetime.Token);
        try
        {
            await transport.Issued.Task.WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
            await lifetime.CancelAsync();
            Assert.False(command.IsCompleted);
            Assert.Equal(1, transport.SetFeatureCalls);
        }
        finally { transport.NativeReturn.TrySetResult(); }
        await command;
        Assert.Equal((byte)100, Assert.Single(transport.Reports)[2]);
    }

    private static Task InitializeCachedStateAsync(V12Device device) => device.InitializeTrustedStateAsync(
        V12FeatureReport.ParseState(V12FeatureReportTests.CreateStateReport(0x88, 44, 0, 0x83, 4, 2, 177)),
        TestContext.Current.CancellationToken);

    private static Task WriteAsync(V12Device device, string operation, CancellationToken token) => operation switch
    {
        "fan" => device.WriteFanSpeedAsync(100, token),
        "lighting" => device.WriteLightingAsync(new(true, 1, 2, 220, 3), token),
        "off" => device.WriteSystemShutdownOffAsync(token),
        "software" => device.SelectSoftwareControlAtOneHundredAsync(token),
        _ => throw new ArgumentOutOfRangeException(nameof(operation)),
    };

    private sealed class ControlledSpacing
    {
        private int calls;
        public int Calls => Volatile.Read(ref calls);
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task DelayAsync(TimeSpan duration, CancellationToken token)
        {
            Assert.Equal(TimeSpan.FromMilliseconds(10), duration);
            token.ThrowIfCancellationRequested();
            if (Interlocked.Increment(ref calls) != 1) return Task.CompletedTask;
            Entered.TrySetResult();
            return Release.Task.WaitAsync(token);
        }
    }

    private sealed class RecordingTransport : IHidFeatureTransport
    {
        private int setFeatureCalls;
        public DeviceIdentity Identity { get; } = new(0x374A, 0xB101, 0x1602, 0xFF00, 1);
        public int FeatureReportLength => V12FeatureReport.ReportLength;
        public int SetFeatureCalls => Volatile.Read(ref setFeatureCalls);
        public ConcurrentQueue<byte[]> Reports { get; } = new();
        public bool HoldIssuedCall { get; init; }
        public TaskCompletionSource Issued { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource NativeReturn { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask SetFeatureAsync(ReadOnlyMemory<byte> report, CancellationToken token)
        {
            Interlocked.Increment(ref setFeatureCalls);
            token.ThrowIfCancellationRequested();
            Reports.Enqueue(report.ToArray());
            Issued.TrySetResult();
            // Models a native operation only after its request has actually been issued.
            if (HoldIssuedCall) await NativeReturn.Task.ConfigureAwait(false);
        }
        public ValueTask<byte[]> GetFeatureAsync(byte reportId, CancellationToken token) =>
            throw new InvalidOperationException("This test initializes a verified cached state; no query is expected.");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
