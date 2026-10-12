using System.ComponentModel;
using System.IO.Pipes;
using Microsoft.Extensions.Logging.Abstractions;
using V12UltraController.Core.Configuration;
using V12UltraController.Core.Control;
using V12UltraController.Core.Ipc;
using V12UltraController.Service;

namespace V12UltraController.Tests.Service;

public sealed class RequestTransportTests
{
    [Theory]
    [InlineData(PipeOperations.ModeSet)]
    [InlineData(PipeOperations.SpeedSet)]
    [InlineData(PipeOperations.PowerSet)]
    [InlineData(PipeOperations.CurveSet)]
    [InlineData(PipeOperations.CurveAdviceApply)]
    [InlineData(PipeOperations.LightingSet)]
    [InlineData(PipeOperations.ProfileApply)]
    public async Task ScopedControlRequestStopsAfterDisconnect(string operation)
    {
        using var stream = new RequestLifetimeTestStream();
        var task = PipeServerWorker.HandleRequestAsync(stream, operation, async token =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Response();
        }, TestContext.Current.CancellationToken);
        stream.ReadResult.SetResult(0);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Theory]
    [InlineData(PipeOperations.CommissionProbe)]
    [InlineData(PipeOperations.CommissionCommit)]
    [InlineData(PipeOperations.CommissionAbort)]
    [InlineData(PipeOperations.CommissionRollback)]
    [InlineData(PipeOperations.ShutdownQualify)]
    [InlineData(PipeOperations.ShutdownEnable)]
    [InlineData(PipeOperations.ShutdownDisable)]
    [InlineData(PipeOperations.SafetyDisable)]
    [InlineData(PipeOperations.SafetyResume)]
    [InlineData(PipeOperations.StartupSet)]
    [InlineData(PipeOperations.StartupFanSet)]
    public async Task UnreviewedTransactionKeepsItsExistingServiceLifetime(string operation)
    {
        using var stream = new RequestLifetimeTestStream();
        stream.ReadResult.SetResult(0);
        using var stopping = new CancellationTokenSource();
        var expected = Response();
        var actual = await PipeServerWorker.HandleRequestAsync(stream, operation, token =>
        {
            Assert.Equal(stopping.Token, token);
            return Task.FromResult(expected);
        }, stopping.Token);
        Assert.Same(expected, actual);
        Assert.Equal(0, stream.ReadCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadFailureCancelsPendingWork(bool nativeFailure)
    {
        using var stream = new RequestLifetimeTestStream();
        var task = PipeServerWorker.HandleRequestAsync(stream, PipeOperations.SpeedSet, async token =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Response();
        }, TestContext.Current.CancellationToken);
        stream.ReadResult.SetException(nativeFailure ? new Win32Exception(233) : new IOException("Disconnected."));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task ServiceStopCancelsRequestAndReleasesMonitor()
    {
        using var stream = new RequestLifetimeTestStream();
        using var stopping = new CancellationTokenSource();
        var task = PipeServerWorker.HandleRequestAsync(stream, PipeOperations.SpeedSet, async token =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Response();
        }, stopping.Token);
        await stopping.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.True(stream.ReadCancelled);
    }

    [Fact]
    public async Task KnownDisconnectedClientDoesNotEnterHandler()
    {
        using var stream = new RequestLifetimeTestStream();
        stream.ReadResult.SetResult(0);
        var entered = false;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PipeServerWorker.HandleRequestAsync(
            stream, PipeOperations.PowerSet, _ =>
            {
                entered = true;
                return Task.FromResult(Response());
            }, TestContext.Current.CancellationToken));
        Assert.False(entered);
    }

    [Fact]
    public async Task ResponseWriteDeadlineCancelsBlockedTransport()
    {
        using var stream = new BlockedResponseStream();
        var failure = await Assert.ThrowsAsync<TimeoutException>(() => PipeServerWorker.WriteResponseAsync(
            stream, Response(), TestContext.Current.CancellationToken, TimeSpan.FromMilliseconds(50))
            .WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal("The local API response write exceeded its transport deadline.", failure.Message);
        Assert.IsAssignableFrom<OperationCanceledException>(failure.InnerException);
        Assert.True(stream.WriteCancelled);
    }

    [Fact]
    public async Task ResponseWriteServiceStopRemainsCancellation()
    {
        using var stream = new BlockedResponseStream();
        using var stopping = new CancellationTokenSource();
        var task = PipeServerWorker.WriteResponseAsync(stream, Response(), stopping.Token);
        await stopping.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.True(stream.WriteCancelled);
    }

    [Fact]
    public async Task CompletedResponseKeepsItsFrameAndCorrelation()
    {
        using var stream = new MemoryStream();
        var expected = Response();
        await PipeServerWorker.WriteResponseAsync(stream, expected, TestContext.Current.CancellationToken);
        stream.Position = 0;
        var actual = await PipeMessageFraming.ReadAsync<PipeResponse>(stream, TestContext.Current.CancellationToken);
        Assert.Equal(expected.RequestId, actual.RequestId);
        Assert.True(actual.Ok);
    }

    [Fact]
    public async Task RandomTestPipeThatDoesNotReadLargeResponseHitsWriteDeadline()
    {
        var pipeName = $"V12UltraController.Tests.ResponseDeadline.{Guid.NewGuid():N}";
        await using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 1024, 1024);
        await using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var connected = server.WaitForConnectionAsync(lifetime.Token);
        await client.ConnectAsync(lifetime.Token);
        await connected;
        var response = PipeResponse.Success(Guid.NewGuid().ToString("D"), new { data = new string('x', 512 * 1024) });
        // Only this random test pipe exists; the client deliberately never reads the response.
        await Assert.ThrowsAsync<TimeoutException>(() => PipeServerWorker.WriteResponseAsync(
            server, response, lifetime.Token, TimeSpan.FromMilliseconds(100)));
    }

    private static PipeResponse Response() => PipeResponse.Success(Guid.NewGuid().ToString("D"), new { });

    private sealed class BlockedResponseStream : RequestLifetimeTestStream
    {
        public bool WriteCancelled { get; private set; }
        public override bool CanWrite => true;
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            catch (OperationCanceledException) { WriteCancelled = true; throw; }
        }
    }
}

// Reuse the established coordinator fixture: all device and Task Scheduler dependencies are fake.
public sealed partial class ControllerCoordinatorTests
{
    [Theory]
    [InlineData(PipeOperations.SpeedSet, 0)]
    [InlineData(PipeOperations.SpeedSet, 1)]
    [InlineData(PipeOperations.ModeSet, 0)]
    [InlineData(PipeOperations.ModeSet, 1)]
    [InlineData(PipeOperations.PowerSet, 0)]
    [InlineData(PipeOperations.PowerSet, 1)]
    public async Task DisconnectedOrdinaryHandlerCannotExecuteAfterCoordinatorGateIsReleased(string operation, int readResult)
    {
        var store = new TransportBlockingConfigurationStore();
        await using var fixture = await CoordinatorFixture.CreateAsync(true, suppliedStore: store);
        var peer = new PipePeerIdentity("S-1-5-21-111-222-333-1001", "TransportTest");
        var handler = new PipeRequestHandler(fixture.Coordinator,
            new JsonlAuditLog(fixture.Paths, TimeProvider.System), NullLogger<PipeRequestHandler>.Instance);
        using var stream = new RequestLifetimeTestStream();
        var before = store.Current;
        var hardwareBefore = fixture.CaptureHardwareCounts();
        store.BlockSaves = true;
        // A real configuration transaction holds the coordinator gate throughout its readback.
        var gateHolder = fixture.Coordinator.SetCurveAsync(before.Curve, peer, TestContext.Current.CancellationToken);
        try
        {
            await store.SaveStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var request = operation switch
            {
                PipeOperations.SpeedSet => PipeRequest.Create(operation, new { percent = 61 }),
                PipeOperations.ModeSet => PipeRequest.Create(operation, new { mode = "manual" }),
                _ => PipeRequest.Create(operation, new { poweredOn = false, confirmed = true }),
            };
            var pending = PipeServerWorker.HandleRequestAsync(stream, operation,
                token => handler.HandleAsync(request, peer, token), TestContext.Current.CancellationToken);
            Assert.False(pending.IsCompleted);
            stream.ReadResult.SetResult(readResult);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            store.ReleaseSave.TrySetResult();
            await gateHolder.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.Equal(before.Mode, store.Current.Mode);
        Assert.Equal(before.ManualSpeedPercent, store.Current.ManualSpeedPercent);
        Assert.Equal(2, store.SaveCalls); // Fixture initialization plus the gate holder only.
        Assert.Equal(hardwareBefore, fixture.CaptureHardwareCounts());
        Assert.Null(await fixture.AuditMutationJournalStore.LoadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DisconnectionAfterMutationStartedStillFinishesConfigurationReadbackAndAudit()
    {
        var store = new TransportBlockingConfigurationStore();
        await using var fixture = await CoordinatorFixture.CreateAsync(true, suppliedStore: store);
        var peer = new PipePeerIdentity("S-1-5-21-111-222-333-1001", "TransportTest");
        var handler = new PipeRequestHandler(fixture.Coordinator,
            new JsonlAuditLog(fixture.Paths, TimeProvider.System), NullLogger<PipeRequestHandler>.Instance);
        using var stream = new RequestLifetimeTestStream();
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.BlockSaves = true;
        var request = PipeRequest.Create(PipeOperations.SpeedSet, new { percent = 61 });
        var pending = PipeServerWorker.HandleRequestAsync(stream, request.Operation, async token =>
        {
            using var registration = token.Register(() => disconnected.TrySetResult());
            return await handler.HandleAsync(request, peer, token);
        }, TestContext.Current.CancellationToken);
        try
        {
            await store.SaveStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(store.BlockedSaveToken.CanBeCanceled);
            var loadsBeforeCompletion = store.LoadCalls;
            stream.ReadResult.SetResult(0);
            await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(pending.IsCompleted);
            store.ReleaseSave.TrySetResult();
            var response = await pending.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(response.Ok);
            Assert.Equal((byte)61, store.Current.ManualSpeedPercent);
            Assert.Equal(ControlMode.Manual, store.Current.Mode);
            Assert.True(store.LoadCalls > loadsBeforeCompletion);
            Assert.Null(await fixture.AuditMutationJournalStore.LoadAsync(TestContext.Current.CancellationToken));
            Assert.Empty(fixture.Device.Writes);
        }
        finally
        {
            store.ReleaseSave.TrySetResult();
            await pending.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private sealed class TransportBlockingConfigurationStore : IConfigurationStore
    {
        public ControllerConfiguration Current { get; private set; } = ControllerConfiguration.CreateDefault();
        public bool BlockSaves { get; set; }
        public int SaveCalls { get; private set; }
        public int LoadCalls { get; private set; }
        public CancellationToken BlockedSaveToken { get; private set; }
        public TaskCompletionSource SaveStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseSave { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<ControllerConfiguration> LoadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LoadCalls++;
            return Task.FromResult(Current);
        }
        public async Task SaveAsync(ControllerConfiguration configuration, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SaveCalls++;
            if (BlockSaves)
            {
                BlockedSaveToken = cancellationToken;
                SaveStarted.TrySetResult();
                await ReleaseSave.Task.WaitAsync(cancellationToken);
            }
            Current = configuration;
        }
    }
}

internal class RequestLifetimeTestStream : Stream
{
    public TaskCompletionSource<int> ReadResult { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int ReadCalls { get; private set; }
    public bool ReadCancelled { get; private set; }
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ReadCalls++;
        try { return await ReadResult.Task.WaitAsync(cancellationToken); }
        catch (OperationCanceledException) { ReadCancelled = true; throw; }
    }
    public override void Flush() => throw new NotSupportedException();
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
