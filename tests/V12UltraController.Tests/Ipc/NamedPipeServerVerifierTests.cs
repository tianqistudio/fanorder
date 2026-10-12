using System.IO.Pipes;
using System.ComponentModel;
using System.Security.Principal;
using V12UltraController.Core.Ipc;

namespace V12UltraController.Tests.Ipc;

public sealed class NamedPipeServerVerifierTests
{
    [Fact]
    public async Task ApiClient_VerifiesConnectedServerBeforeSendingRequest()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var pipeName = $"V12UltraController.Tests.{Guid.NewGuid():N}";
        await using var server = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
        var verifier = new RecordingVerifier();
        var client = new NamedPipeApiClient(verifier, pipeName);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var serverTask = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync(timeout.Token);
            var request = await PipeMessageFraming.ReadAsync<PipeRequest>(server, timeout.Token);
            await PipeMessageFraming.WriteAsync(
                server,
                PipeResponse.Success(request.RequestId, new { accepted = true }),
                timeout.Token);
        }, timeout.Token);

        var response = await client.SendAsync(
            PipeRequest.Create(PipeOperations.StatusGet, new { }),
            TimeSpan.FromSeconds(5),
            timeout.Token);
        await serverTask;

        Assert.True(response.Ok);
        Assert.Equal(1, verifier.CallCount);
        Assert.True(verifier.WasConnected);
    }

    [Fact]
    public async Task ApiClient_DoesNotSendRequestWhenServerVerificationFails()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var pipeName = $"V12UltraController.Tests.{Guid.NewGuid():N}";
        await using var server = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
        var client = new NamedPipeApiClient(new RejectingVerifier(), pipeName);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var connected = server.WaitForConnectionAsync(timeout.Token);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => client.SendAsync(
            PipeRequest.Create(PipeOperations.StatusGet, new { }),
            TimeSpan.FromSeconds(5),
            timeout.Token));
        await connected;

        var oneByte = new byte[1];
        Assert.Equal(0, await server.ReadAsync(oneByte, timeout.Token));
    }

    [Theory]
    [InlineData(42u, 42u, true, null, true)]
    [InlineData(42u, 42u, true, "S-1-5-18", true)]
    [InlineData(41u, 42u, true, "S-1-5-18", false)]
    [InlineData(42u, 42u, false, "S-1-5-18", false)]
    [InlineData(42u, 0u, true, "S-1-5-18", false)]
    [InlineData(42u, 42u, true, "S-1-5-21-1-2-3-1001", false)]
    public async Task Verifier_EnforcesScmPidAndLocalSystemWhenTokenIsQueryable(
        uint pipePid,
        uint servicePid,
        bool running,
        string? processSid,
        bool accepted)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using var pipe = await CreateConnectedClientAsync();
        var verifier = new NamedPipeServerVerifier(new StubIdentitySource(
            new PipeServerIdentity(pipePid, servicePid, running, processSid)));

        if (accepted)
        {
            verifier.Verify(pipe.Client);
        }
        else
        {
            Assert.Throws<UnauthorizedAccessException>(() => verifier.Verify(pipe.Client));
        }
    }

    [Fact]
    public async Task Verifier_ConvertsNativeQueryFailureToClientTransportFailure()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using var pipe = await CreateConnectedClientAsync();
        var verifier = new NamedPipeServerVerifier(new ThrowingIdentitySource());

        var exception = Assert.Throws<IOException>(() => verifier.Verify(pipe.Client));
        Assert.IsType<Win32Exception>(exception.InnerException);
    }

    private static async Task<ConnectedPipePair> CreateConnectedClientAsync()
    {
        var name = $"V12UltraController.Tests.{Guid.NewGuid():N}";
        var server = new NamedPipeServerStream(
            name,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
        var client = new NamedPipeClientStream(
            ".",
            name,
            PipeDirection.InOut,
            PipeOptions.Asynchronous,
            TokenImpersonationLevel.Identification);
        var connecting = client.ConnectAsync(5_000);
        await server.WaitForConnectionAsync();
        await connecting;
        return new ConnectedPipePair(server, client);
    }

    private sealed class RecordingVerifier : INamedPipeServerVerifier
    {
        internal int CallCount { get; private set; }
        internal bool WasConnected { get; private set; }

        public void Verify(NamedPipeClientStream pipe)
        {
            CallCount++;
            WasConnected = pipe.IsConnected;
        }
    }

    private sealed class RejectingVerifier : INamedPipeServerVerifier
    {
        public void Verify(NamedPipeClientStream pipe) =>
            throw new UnauthorizedAccessException("Test rejection.");
    }

    private sealed class StubIdentitySource(PipeServerIdentity identity) : IPipeServerIdentitySource
    {
        public PipeServerIdentity Query(NamedPipeClientStream pipe, string serviceName) => identity;
    }

    private sealed class ThrowingIdentitySource : IPipeServerIdentitySource
    {
        public PipeServerIdentity Query(NamedPipeClientStream pipe, string serviceName) =>
            throw new Win32Exception(5);
    }

    private sealed class ConnectedPipePair(
        NamedPipeServerStream server,
        NamedPipeClientStream client) : IAsyncDisposable
    {
        internal NamedPipeClientStream Client { get; } = client;

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            await server.DisposeAsync();
        }
    }
}
