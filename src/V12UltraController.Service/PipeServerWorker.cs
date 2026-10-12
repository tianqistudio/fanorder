using System.Collections.Concurrent;
using System.ComponentModel;
using System.IO.Pipes;
using V12UltraController.Core.Ipc;

namespace V12UltraController.Service;

internal sealed class PipeServerWorker(
    ISecurePipeFactory pipeFactory,
    PipeRequestHandler handler,
    FatalServiceFailureHandler fatalFailure,
    ILogger<PipeServerWorker> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<int, Task> clients = new();
    private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(5);
    private int nextClientId;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                NamedPipeServerStream? listener = null;
                try
                {
                    listener = pipeFactory.Create(PipeProtocol.Name, firstInstance: true);
                    while (!stoppingToken.IsCancellationRequested)
                    {
                        await listener.WaitForConnectionAsync(stoppingToken).ConfigureAwait(false);
                        NamedPipeServerStream? connected = listener;
                        listener = null;

                        // Keep the connected instance alive until a successor exists. This
                        // leaves no service-running interval in which the pipe name can be
                        // pre-created by another process. Only the bootstrap instance uses
                        // FILE_FLAG_FIRST_PIPE_INSTANCE; successors must coexist with it.
                        try
                        {
                            listener = await CreateSuccessorAsync(stoppingToken).ConfigureAwait(false);
                            TrackClient(connected, stoppingToken);
                            connected = null;
                        }
                        finally
                        {
                            connected?.Dispose();
                        }
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    listener?.Dispose();
                    break;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    listener?.Dispose();
                    logger.LogError(exception, "The local named-pipe listener failed; retrying.");
                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal service shutdown, including cancellation during a retry delay.
        }
        catch (Exception exception)
        {
            await fatalFailure.TerminateAsync("named-pipe server", exception).ConfigureAwait(false);
        }
    }

    private async Task<NamedPipeServerStream> CreateSuccessorAsync(CancellationToken stoppingToken)
    {
        while (true)
        {
            try
            {
                return pipeFactory.Create(PipeProtocol.Name, firstInstance: false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                logger.LogError(
                    exception,
                    "Could not create the next local named-pipe instance; retaining the connected instance and retrying.");
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private void TrackClient(NamedPipeServerStream pipe, CancellationToken stoppingToken)
    {
        var clientId = Interlocked.Increment(ref nextClientId);
        var clientTask = HandleClientAsync(pipe, stoppingToken);
        clients[clientId] = clientTask;
        _ = clientTask.ContinueWith(
            completed => clients.TryRemove(clientId, out _),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        var snapshot = clients.Values.ToArray();
        if (snapshot.Length > 0)
        {
            try
            {
                await Task.WhenAll(snapshot).WaitAsync(TimeSpan.FromSeconds(2), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is TimeoutException or OperationCanceledException)
            {
                logger.LogWarning(exception, "Some local API clients did not finish before service shutdown.");
            }
        }
    }

    private async Task HandleClientAsync(NamedPipeServerStream pipe, CancellationToken stoppingToken)
    {
        await using (pipe.ConfigureAwait(false))
        {
            var phase = ClientPhase.ReadingRequest;
            try
            {
                var peer = PipePeer.Inspect(pipe);
                PipeRequest request;
                using (var frameDeadline = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken))
                {
                    frameDeadline.CancelAfter(FrameTimeout);
                    request = await PipeMessageFraming.ReadAsync<PipeRequest>(pipe, frameDeadline.Token)
                        .ConfigureAwait(false);
                }
                phase = ClientPhase.HandlingRequest;
                var response = await HandleRequestAsync(pipe, request.Operation,
                    token => handler.HandleAsync(request, peer, token), stoppingToken).ConfigureAwait(false);
                phase = ClientPhase.WritingResponse;
                await WriteResponseAsync(pipe, response, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Normal service shutdown.
            }
            catch (OperationCanceledException) when (phase == ClientPhase.ReadingRequest)
            {
                logger.LogWarning("Closed a local API connection that did not send a complete frame within five seconds.");
            }
            catch (OperationCanceledException)
            {
                logger.LogInformation("A local API request was cancelled during {Phase}; unfinished cancellable work was stopped.", phase);
            }
            catch (TimeoutException exception) when (phase == ClientPhase.WritingResponse)
            {
                logger.LogWarning(exception, "Closed a local API connection that did not receive its response within five seconds; the operation may already have completed.");
            }
            catch (InvalidDataException exception)
            {
                logger.LogWarning(exception, "Rejected an invalid local API frame during {Phase}.", phase);
            }
            catch (Exception exception) when (IsExpectedClientFailure(exception))
            {
                logger.LogWarning(exception, "Rejected or disconnected local API client.");
            }
            catch (Exception exception)
            {
                await fatalFailure.TerminateAsync("named-pipe client", exception).ConfigureAwait(false);
            }
        }
    }

    internal static Task<PipeResponse> HandleRequestAsync(Stream stream, string operation,
        Func<CancellationToken, Task<PipeResponse>> handle, CancellationToken stoppingToken) =>
        operation == PipeOperations.ProfileApply
            ? HandleWithDisconnectCancellationAsync(stream, handle, stoppingToken, cancelUntilAdmission: false)
            : operation is PipeOperations.ModeSet or PipeOperations.SpeedSet or
            PipeOperations.PowerSet or PipeOperations.CurveSet or PipeOperations.CurveAdviceApply or
            PipeOperations.LightingSet
            ? HandleWithDisconnectCancellationAsync(stream, handle, stoppingToken, cancelUntilAdmission: true)
            : handle(stoppingToken);

    internal static async Task WriteResponseAsync(Stream stream, PipeResponse response,
        CancellationToken stoppingToken, TimeSpan? timeout = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        deadline.CancelAfter(timeout ?? ResponseTimeout);
        try
        {
            await PipeMessageFraming.WriteAsync(stream, response, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (!stoppingToken.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            throw new TimeoutException("The local API response write exceeded its transport deadline.", exception);
        }
    }

    private static async Task<PipeResponse> HandleWithDisconnectCancellationAsync(Stream stream,
        Func<CancellationToken, Task<PipeResponse>> handle, CancellationToken stoppingToken, bool cancelUntilAdmission)
    {
        using var requestLifetime = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        using var monitorLifetime = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        // This protocol has one request per connection. EOF or extra bytes cancel its pending
        // cancellable work. The coordinator retains ownership of already-started transaction
        // readback, recovery and audit closure; disconnect never manufactures a retry/rollback.
        // Commissioning, shutdown qualification and persistent stop intent keep their existing
        // lifetimes until their complete transaction boundaries are reviewed separately.
        var monitor = MonitorDisconnectAsync();
        try
        {
            requestLifetime.Token.ThrowIfCancellationRequested();
            using var admission = cancelUntilAdmission
                ? new ControllerRequestAdmissionScope(requestLifetime.Token, stoppingToken) : null;
            return await handle(requestLifetime.Token).ConfigureAwait(false);
        }
        finally
        {
            await monitorLifetime.CancelAsync().ConfigureAwait(false);
            await monitor.ConfigureAwait(false);
        }

        async Task MonitorDisconnectAsync()
        {
            try
            {
                _ = await stream.ReadAsync(new byte[1], monitorLifetime.Token).ConfigureAwait(false);
                await requestLifetime.CancelAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (monitorLifetime.IsCancellationRequested) { }
            catch (Exception exception) when (IsExpectedClientFailure(exception) || exception is ObjectDisposedException)
            {
                await requestLifetime.CancelAsync().ConfigureAwait(false);
            }
        }
    }

    internal static bool IsExpectedClientFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or Win32Exception;

    private enum ClientPhase { ReadingRequest, HandlingRequest, WritingResponse }
}
