using V12UltraController.Core.Ipc;

namespace V12UltraController.Tests.Ipc;

public sealed class PipeFramingBoundaryTests
{
    [Fact]
    public async Task Framing_ReadsFragmentedBackToBackFramesWithoutCrossingBoundaries()
    {
        var first = PipeRequest.Create(PipeOperations.StatusGet, new { sequence = 1 });
        var second = PipeRequest.Create(PipeOperations.SpeedSet, new { percent = 70, sequence = 2 });
        await using var encoded = new MemoryStream();
        await PipeMessageFraming.WriteAsync(encoded, first, TestContext.Current.CancellationToken);
        await PipeMessageFraming.WriteAsync(encoded, second, TestContext.Current.CancellationToken);
        await using var fragmented = new FragmentedReadStream(encoded.ToArray(), maximumChunkBytes: 1);

        var decodedFirst = await PipeMessageFraming.ReadAsync<PipeRequest>(
            fragmented,
            TestContext.Current.CancellationToken);
        var decodedSecond = await PipeMessageFraming.ReadAsync<PipeRequest>(
            fragmented,
            TestContext.Current.CancellationToken);

        Assert.Equal(first.RequestId, decodedFirst.RequestId);
        Assert.Equal(1, decodedFirst.Payload.GetProperty("sequence").GetInt32());
        Assert.Equal(second.RequestId, decodedSecond.RequestId);
        Assert.Equal(70, decodedSecond.Payload.GetProperty("percent").GetInt32());
        Assert.Equal(2, decodedSecond.Payload.GetProperty("sequence").GetInt32());
        Assert.Equal(fragmented.Length, fragmented.Position);
    }

    [Fact]
    public async Task Framing_RejectsTruncatedPrefixAndJsonNull()
    {
        await using var truncatedPrefix = new MemoryStream([1, 0, 0]);
        _ = await Assert.ThrowsAsync<EndOfStreamException>(() =>
            PipeMessageFraming.ReadAsync<PipeRequest>(
                truncatedPrefix,
                TestContext.Current.CancellationToken));

        await using var jsonNull = new MemoryStream([4, 0, 0, 0, (byte)'n', (byte)'u', (byte)'l', (byte)'l']);
        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            PipeMessageFraming.ReadAsync<PipeRequest>(jsonNull, TestContext.Current.CancellationToken));
        Assert.Contains("JSON null", exception.Message, StringComparison.Ordinal);
    }

    private sealed class FragmentedReadStream(byte[] bytes, int maximumChunkBytes) : Stream
    {
        private readonly MemoryStream inner = new(bytes, writable: false);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            inner.Read(buffer, offset, Math.Min(count, maximumChunkBytes));

        public override int Read(Span<byte> buffer) =>
            inner.Read(buffer[..Math.Min(buffer.Length, maximumChunkBytes)]);

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            inner.ReadAsync(buffer[..Math.Min(buffer.Length, maximumChunkBytes)], cancellationToken);

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync();
            await base.DisposeAsync();
        }
    }
}
