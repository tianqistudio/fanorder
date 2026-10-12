namespace V12UltraController.Service;

internal sealed class ControllerLoopWorker(
    ControllerCoordinator coordinator,
    ServiceStopIntent stopIntent,
    FatalServiceFailureHandler fatalFailure) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await coordinator.InitializeAsync(stoppingToken).ConfigureAwait(false);
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            do
            {
                await coordinator.TickAsync(stoppingToken).ConfigureAwait(false);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal host shutdown is finalized by StopAsync below.
        }
        catch (Exception exception)
        {
            await fatalFailure.TerminateAsync("controller loop", exception).ConfigureAwait(false);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await base.StopAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await coordinator.FinalizeHostStopAsync(stopIntent.Reason).ConfigureAwait(false);
        }
    }
}

internal interface IProcessTerminator
{
    void Terminate(int exitCode);
}

internal sealed class ProcessTerminator : IProcessTerminator
{
    public void Terminate(int exitCode) => Environment.Exit(exitCode);
}

/// <summary>
/// Converts every unexpected hosted-worker failure into the same fail-closed process outcome.
/// BackgroundServiceExceptionBehavior.StopHost alone can otherwise turn a worker exception into
/// a clean exit code, preventing the SCM recovery actions from running.
/// </summary>
internal sealed class FatalServiceFailureHandler(
    ControllerCoordinator coordinator,
    IProcessTerminator processTerminator,
    ILogger<FatalServiceFailureHandler> logger)
{
    private int terminating;

    public async Task TerminateAsync(string workerName, Exception exception)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workerName);
        ArgumentNullException.ThrowIfNull(exception);
        if (Interlocked.Exchange(ref terminating, 1) != 0)
        {
            return;
        }

        Environment.ExitCode = 7;
        logger.LogCritical(exception,
            "The {WorkerName} worker failed unexpectedly; attempting fail-safe before non-zero termination.",
            workerName);
        if (!coordinator.IsHardwarePoisoned)
        {
            try
            {
                await coordinator.BestEffortWatchdogFailSafeAsync().ConfigureAwait(false);
            }
            catch (Exception failSafeException)
            {
                logger.LogCritical(failSafeException,
                    "The fatal-worker handler could not complete its fail-safe attempt.");
            }
        }

        processTerminator.Terminate(7);
    }
}

internal sealed class ControllerWatchdogWorker(
    ControllerCoordinator coordinator,
    IProcessTerminator processTerminator,
    FatalServiceFailureHandler fatalFailure,
    TimeProvider timeProvider,
    ILogger<ControllerWatchdogWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                if (await CheckForTerminationAsync().ConfigureAwait(false))
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal service shutdown.
        }
        catch (Exception exception)
        {
            await fatalFailure.TerminateAsync("controller watchdog", exception).ConfigureAwait(false);
        }
    }

    internal async Task<bool> CheckForTerminationAsync()
    {
        if (coordinator.IsHardwarePoisoned)
        {
            logger.LogCritical(
                "Hardware ownership is poisoned; terminating directly without dispatching another fail-safe operation.");
            Environment.ExitCode = 7;
            processTerminator.Terminate(7);
            return true;
        }

        // Probe the registered monotonic clock so a broken provider still takes the fatal-worker
        // path. Elapsed-time decisions themselves stay inside the coordinator/dispatchers that
        // own the corresponding timestamp origins.
        _ = timeProvider.GetTimestamp();
        var loopStalled = coordinator.RequiresControlHeartbeat &&
                           coordinator.HasStalledControlLoop(TimeSpan.FromSeconds(2));
        var hardwareStalled = coordinator.HasStalledHardwareOperation();
        if (!coordinator.IsWatchdogArmed || !loopStalled && !hardwareStalled)
        {
            return false;
        }

        logger.LogCritical(
            "The controller loop, thermal stage, or HID stage exceeded its safety deadline; invoking fail-safe termination.");
        await coordinator.BestEffortWatchdogFailSafeAsync().ConfigureAwait(false);
        Environment.ExitCode = 7;
        processTerminator.Terminate(7);
        return true;
    }
}
