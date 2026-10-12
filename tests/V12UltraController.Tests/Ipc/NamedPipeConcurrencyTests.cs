using System.IO.Pipes;
using V12UltraController.Core.Ipc;

namespace V12UltraController.Tests.Ipc;

public sealed class NamedPipeConcurrencyTests
{
    [Fact]
    public async Task ApiClient_ConcurrentLocalConnectionsKeepFramesAndCorrelationsIndependent()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const int connectionCount = 8;
        var pipeName = $"V12UltraController.Tests.Concurrent.{Guid.NewGuid():N}";
        var servers = Enumerable.Range(0, connectionCount)
            .Select(_ => new NamedPipeServerStream(
                pipeName,
                PipeDirection.InOut,
                connectionCount,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous))
            .ToArray();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        try
        {
            var serverTasks = servers.Select(server => AcceptAndEchoAsync(server, timeout.Token)).ToArray();
            var verifier = new CountingVerifier();
            var client = new NamedPipeApiClient(verifier, pipeName);
            var requests = Enumerable.Range(0, connectionCount)
                .Select(index => PipeRequest.Create(
                    PipeOperations.StatusGet,
                    new { index },
                    Guid.NewGuid().ToString("D")))
                .ToArray();

            var responses = await Task.WhenAll(requests.Select(request => client.SendAsync(
                request,
                TimeSpan.FromSeconds(5),
                timeout.Token)));
            await Task.WhenAll(serverTasks);

            Assert.Equal(connectionCount, verifier.CallCount);
            Assert.Equal(
                requests.Select(static request => request.RequestId).OrderBy(static id => id),
                responses.Select(static response => response.RequestId).OrderBy(static id => id));
            Assert.All(responses, static response =>
            {
                Assert.True(response.Ok);
                Assert.NotNull(response.Data);
                Assert.InRange(response.Data.Value.GetProperty("index").GetInt32(), 0, connectionCount - 1);
            });
            Assert.Equal(
                Enumerable.Range(0, connectionCount),
                responses.Select(static response => response.Data!.Value.GetProperty("index").GetInt32())
                    .OrderBy(static index => index));
        }
        finally
        {
            foreach (var server in servers)
            {
                await server.DisposeAsync();
            }
        }
    }

    private static async Task AcceptAndEchoAsync(
        NamedPipeServerStream server,
        CancellationToken cancellationToken)
    {
        await server.WaitForConnectionAsync(cancellationToken);
        var request = await PipeMessageFraming.ReadAsync<PipeRequest>(server, cancellationToken);
        await PipeMessageFraming.WriteAsync(
            server,
            PipeResponse.Success(
                request.RequestId,
                new { index = request.Payload.GetProperty("index").GetInt32() }),
            cancellationToken);
    }

    private sealed class CountingVerifier : INamedPipeServerVerifier
    {
        private int callCount;

        internal int CallCount => Volatile.Read(ref callCount);

        public void Verify(NamedPipeClientStream pipe)
        {
            Assert.True(pipe.IsConnected);
            Interlocked.Increment(ref callCount);
        }
    }
}
