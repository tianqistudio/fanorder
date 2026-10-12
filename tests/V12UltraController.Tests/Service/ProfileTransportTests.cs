using V12UltraController.Core.Ipc;
using V12UltraController.Service;

namespace V12UltraController.Tests.Service;

public sealed class ProfileTransportTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task DisconnectionOrUnexpectedExtraFrameCancelsPendingProfile(int readResult)
    {
        using var stream = new ControlledReadStream();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var advanced = false;
        var task = PipeServerWorker.HandleRequestAsync(stream, PipeOperations.ProfileApply, async token =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            advanced = true;
            return PipeResponse.Success(Guid.NewGuid().ToString("D"), new { });
        }, TestContext.Current.CancellationToken);
        await entered.Task;
        stream.ReadResult.SetResult(readResult);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.False(advanced);
    }

    [Fact]
    public async Task CompletedProfileStopsTheReadMonitorAndReturnsItsResponse()
    {
        using var stream = new ControlledReadStream();
        var expected = PipeResponse.Success(Guid.NewGuid().ToString("D"), new { outcome = "completed" });
        var result = await PipeServerWorker.HandleRequestAsync(stream, PipeOperations.ProfileApply,
            _ => Task.FromResult(expected), TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Same(expected, result);
        Assert.True(stream.ReadCancelled);
    }

    private sealed class ControlledReadStream : Stream
    {
        public TaskCompletionSource<int> ReadResult { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool ReadCancelled { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            try { return await ReadResult.Task.WaitAsync(cancellationToken); }
            catch (OperationCanceledException) { ReadCancelled = true; throw; }
        }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
