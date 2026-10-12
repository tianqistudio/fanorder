using System.Buffers.Binary;
using System.Text;
using V12UltraController.Core.Ipc;

namespace V12UltraController.Tests.Ipc;

public sealed class PipeProtocolTests
{
    [Fact]
    public async Task Framing_RoundTripsVersionedRequestWithLittleEndianLength()
    {
        var requestId = Guid.NewGuid().ToString("D");
        var request = PipeRequest.Create(PipeOperations.SpeedSet, new { percent = 70 }, requestId);
        await using var stream = new MemoryStream();

        await PipeMessageFraming.WriteAsync(stream, request, TestContext.Current.CancellationToken);
        var bytes = stream.ToArray();
        var bodyLength = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(0, sizeof(int)));
        Assert.Equal(bytes.Length - sizeof(int), bodyLength);

        stream.Position = 0;
        var decoded = await PipeMessageFraming.ReadAsync<PipeRequest>(
            stream,
            TestContext.Current.CancellationToken);

        Assert.Equal(PipeProtocol.ApiVersion, decoded.ApiVersion);
        Assert.Equal(requestId, decoded.RequestId);
        Assert.Equal(PipeOperations.SpeedSet, decoded.Operation);
        Assert.Equal(70, decoded.Payload.GetProperty("percent").GetInt32());
    }

    [Fact]
    public async Task Framing_RequestEnvelope_AllowsAnyPropertyOrderButRequiresExactShapeAndTypes()
    {
        var requestId = Guid.NewGuid().ToString("D");
        string WithId(string json) => json.Replace("$REQUEST_ID$", requestId, StringComparison.Ordinal);
        var reordered = WithId(
            """{"payload":{},"operation":"status.get","requestId":"$REQUEST_ID$","apiVersion":1}""");

        var accepted = await ReadRawRequestAsync(reordered);
        Assert.Equal(requestId, accepted.RequestId);
        Assert.Equal(PipeOperations.StatusGet, accepted.Operation);

        var unsupportedVersion = await ReadRawRequestAsync(WithId(
            """{"apiVersion":2,"requestId":"$REQUEST_ID$","operation":"status.get","payload":{}}"""));
        Assert.Equal(2, unsupportedVersion.ApiVersion);
        Assert.Equal(requestId, unsupportedVersion.RequestId);

        var rejected = new[]
        {
            WithId("""{"ApiVersion":1,"requestId":"$REQUEST_ID$","operation":"status.get","payload":{}}"""),
            WithId("""{"apiVersion":1,"apiVersion":1,"requestId":"$REQUEST_ID$","operation":"status.get","payload":{}}"""),
            WithId("""{"apiVersion":1,"requestId":"$REQUEST_ID$","operation":"status.get","payload":{},"future":true}"""),
            WithId("""{"requestId":"$REQUEST_ID$","operation":"status.get","payload":{}}"""),
            WithId("""{"apiVersion":2147483648,"requestId":"$REQUEST_ID$","operation":"status.get","payload":{}}"""),
            WithId("""{"apiVersion":1.0,"requestId":"$REQUEST_ID$","operation":"status.get","payload":{}}"""),
            WithId("""{"apiVersion":1e0,"requestId":"$REQUEST_ID$","operation":"status.get","payload":{}}"""),
            WithId("""{"apiVersion":"1","requestId":"$REQUEST_ID$","operation":"status.get","payload":{}}"""),
            """{"apiVersion":1,"requestId":42,"operation":"status.get","payload":{}}""",
            WithId("""{"apiVersion":1,"requestId":"$REQUEST_ID$","operation":42,"payload":{}}"""),
            WithId("""{"apiVersion":1,"requestId":"$REQUEST_ID$","operation":"status.get","payload":null}"""),
            WithId("""{"apiVersion":1,"requestId":"$REQUEST_ID$","operation":"status.get","payload":[]}"""),
        };

        foreach (var json in rejected)
        {
            _ = await Assert.ThrowsAsync<InvalidDataException>(() => ReadRawRequestAsync(json));
        }
    }

    [Fact]
    public async Task Framing_RejectsInvalidLengthTruncationAndMalformedJson()
    {
        await using var invalidLength = new MemoryStream(new byte[4]);
        _ = await Assert.ThrowsAsync<InvalidDataException>(
            () => PipeMessageFraming.ReadAsync<PipeRequest>(
                invalidLength,
                TestContext.Current.CancellationToken));

        var truncatedBytes = new byte[6];
        BinaryPrimitives.WriteInt32LittleEndian(truncatedBytes.AsSpan(0, 4), 10);
        await using var truncated = new MemoryStream(truncatedBytes);
        _ = await Assert.ThrowsAsync<EndOfStreamException>(
            () => PipeMessageFraming.ReadAsync<PipeRequest>(truncated, TestContext.Current.CancellationToken));

        byte[] malformedBytes = [1, 0, 0, 0, (byte)'{'];
        await using var malformed = new MemoryStream(malformedBytes);
        _ = await Assert.ThrowsAsync<InvalidDataException>(
            () => PipeMessageFraming.ReadAsync<PipeRequest>(malformed, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Framing_RejectsOversizedOutboundAndInboundMessages()
    {
        await using var outbound = new MemoryStream();
        var oversized = new string('x', PipeProtocol.MaxMessageBytes);
        _ = await Assert.ThrowsAsync<InvalidDataException>(
            () => PipeMessageFraming.WriteAsync(
                outbound,
                oversized,
                TestContext.Current.CancellationToken));

        var prefix = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, PipeProtocol.MaxMessageBytes + 1);
        await using var inbound = new MemoryStream(prefix);
        _ = await Assert.ThrowsAsync<InvalidDataException>(
            () => PipeMessageFraming.ReadAsync<PipeRequest>(inbound, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void ResponseFactories_CreateMutuallyExclusiveDataAndErrorShapes()
    {
        var success = PipeResponse.Success("one", new { healthy = true });
        var failure = PipeResponse.Failure("two", PipeErrorCodes.DeviceNotFound, "not connected");

        Assert.True(success.Ok);
        Assert.NotNull(success.Data);
        Assert.Null(success.Error);
        Assert.True(success.Data!.Value.GetProperty("healthy").GetBoolean());

        Assert.False(failure.Ok);
        Assert.Null(failure.Data);
        Assert.Equal(PipeErrorCodes.DeviceNotFound, failure.Error?.Code);
    }

    private static async Task<PipeRequest> ReadRawRequestAsync(string json)
    {
        var body = Encoding.UTF8.GetBytes(json);
        var framed = new byte[sizeof(int) + body.Length];
        BinaryPrimitives.WriteInt32LittleEndian(framed.AsSpan(0, sizeof(int)), body.Length);
        body.CopyTo(framed.AsSpan(sizeof(int)));
        await using var stream = new MemoryStream(framed, writable: false);
        return await PipeMessageFraming.ReadAsync<PipeRequest>(
            stream,
            TestContext.Current.CancellationToken);
    }
}
