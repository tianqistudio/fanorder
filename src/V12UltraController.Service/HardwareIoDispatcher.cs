using System.Collections.Concurrent;

namespace V12UltraController.Service;

/// <summary>
/// Serializes every hardware-library and HID call onto one dedicated background thread.
/// A native call that ignores cancellation keeps the operation armed so the watchdog can
/// terminate the service; disposal never waits indefinitely for such a call.
/// </summary>
internal class HardwareIoDispatcher : IDisposable
{
    private static readonly TimeSpan DisposeJoinTimeout = TimeSpan.FromMilliseconds(250);
    private readonly BlockingCollection<IHardwareWorkItem> queue = new();
    private readonly CancellationTokenSource shutdown = new();
    private readonly ManualResetEventSlim stopped = new(false);
    private readonly TimeProvider timeProvider;
    private readonly Thread worker;
    private readonly object stateLock = new();
    private int pendingOperations;
    private long? lastProgressTimestamp;
    private bool disposeRequested;

    public HardwareIoDispatcher(TimeProvider timeProvider)
    {
        this.timeProvider = timeProvider;
        worker = new Thread(Run)
        {
            IsBackground = true,
            Name = "V12UltraController.HardwareIo",
        };
        worker.Start();
    }

    public bool HasPendingOperations
    {
        get
        {
            lock (stateLock)
            {
                return pendingOperations > 0;
            }
        }
    }

    public bool HasStalledOperation(TimeSpan threshold)
    {
        lock (stateLock)
        {
            return pendingOperations > 0 &&
                   lastProgressTimestamp is { } progress &&
                   timeProvider.GetElapsedTime(progress, timeProvider.GetTimestamp()) > threshold;
        }
    }

    /// <summary>
    /// Advances the monotonic watchdog boundary for a completed, independently bounded stage
    /// inside the currently executing work item. It must never be called periodically from a
    /// blocked native operation: absence of stage progress is what lets the watchdog detect it.
    /// </summary>
    public void ReportProgress()
    {
        lock (stateLock)
        {
            if (pendingOperations > 0)
            {
                lastProgressTimestamp = timeProvider.GetTimestamp();
            }
        }
    }

    public Task<T> InvokeAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        TimeSpan timeout,
        CancellationToken cancellationToken) =>
        InvokeCoreAsync(operation, timeoutRecovery: null, timeout, cancellationToken);

    /// <summary>
    /// Invokes a HID operation and attaches an ON-at-100 recovery to the same serialized work
    /// item. If the caller's hard timeout wins, the recovery runs immediately after the late
    /// operation returns, even when that operation ignored cancellation. This is deliberately
    /// different from queueing an ordinary, caller-cancellable follow-up: a timed-out low-speed
    /// write must never be allowed to return late after its safety write was discarded.
    /// </summary>
    public Task<T> InvokeWithTimeoutRecoveryAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        Func<CancellationToken, Task> timeoutRecovery,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(timeoutRecovery);
        return InvokeCoreAsync(operation, timeoutRecovery, timeout, cancellationToken);
    }

    private async Task<T> InvokeCoreAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        Func<CancellationToken, Task>? timeoutRecovery,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        CancellationTokenSource linkedCancellation;
        HardwareWorkItem<T> item;
        lock (stateLock)
        {
            ObjectDisposedException.ThrowIf(disposeRequested, this);
            linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, shutdown.Token);
            item = new HardwareWorkItem<T>(operation, timeoutRecovery, linkedCancellation, EndOperation);
            BeginOperationLocked();
            try
            {
                queue.Add(item);
            }
            catch
            {
                EndOperationLocked();
                linkedCancellation.Dispose();
                throw;
            }
        }

        try
        {
            return await item.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Mark the work item abandoned before requesting cancellation. If the native call is
            // still running, recovery is appended to that exact work item. If completion won the
            // race but WaitAsync still observed its deadline, enqueue the same non-cancellable
            // recovery as the next item on this dispatcher.
            var needsStandaloneRecovery = item.MarkAbandoned();
            item.RequestCancellation();
            if (needsStandaloneRecovery && timeoutRecovery is not null)
            {
                EnqueueTerminalRecovery(timeoutRecovery);
            }

            throw;
        }
        catch (OperationCanceledException)
        {
            // Cancellation is also an abandonment boundary.  In particular, SCM can cancel
            // the controller loop while a synchronous HidD_SetFeature call is still running.
            // The late call must not leave a 10..99% request behind after the host-stop path
            // has already moved on, so give it the same serialized ON-at-100 successor as a
            // hard timeout.  Operations without a recovery delegate retain normal behavior.
            var needsStandaloneRecovery = item.MarkAbandoned();
            item.RequestCancellation();
            if (needsStandaloneRecovery && timeoutRecovery is not null)
            {
                EnqueueTerminalRecovery(timeoutRecovery);
            }

            throw;
        }
        catch
        {
            item.RequestCancellation();
            throw;
        }
    }

    private void EnqueueTerminalRecovery(Func<CancellationToken, Task> recovery)
    {
        lock (stateLock)
        {
            if (disposeRequested)
            {
                return;
            }

            BeginOperationLocked();
            try
            {
                queue.Add(new TerminalRecoveryWorkItem(recovery, EndOperation));
            }
            catch
            {
                EndOperationLocked();
            }
        }
    }

    public void Dispose()
    {
        var requestCancellation = false;
        lock (stateLock)
        {
            if (disposeRequested)
            {
                return;
            }

            disposeRequested = true;
            queue.CompleteAdding();
            requestCancellation = true;
        }

        // Cancellation callbacks are user/library code. Never invoke them while holding
        // stateLock, because a completing operation calls EndOperation under that lock.
        if (requestCancellation)
        {
            shutdown.Cancel();
        }

        if (Thread.CurrentThread != worker)
        {
            _ = stopped.Wait(DisposeJoinTimeout);
        }

        // Do not dispose the queue/CTS/event here: a cancellation-ignoring native call may
        // still be using them on the background thread. The thread is background-only, so it
        // cannot keep the service process alive after the watchdog selects non-zero exit.
    }

    private void Run()
    {
        try
        {
            foreach (var item in queue.GetConsumingEnumerable())
            {
                ReportProgress();
                item.Execute();
                ReportProgress();
            }
        }
        catch (ObjectDisposedException)
        {
            // Process teardown raced with dispatcher shutdown.
        }
        finally
        {
            stopped.Set();
        }
    }

    private void BeginOperationLocked()
    {
        if (pendingOperations == 0)
        {
            lastProgressTimestamp = timeProvider.GetTimestamp();
        }

        pendingOperations++;
    }

    private void EndOperation()
    {
        lock (stateLock)
        {
            EndOperationLocked();
        }
    }

    private void EndOperationLocked()
    {
        if (pendingOperations <= 0)
        {
            return;
        }

        pendingOperations--;
        if (pendingOperations == 0)
        {
            lastProgressTimestamp = null;
        }
        else
        {
            // Completing an item is forward progress for the next queued item. Its own stage
            // start will refresh this again when the worker begins executing it.
            lastProgressTimestamp = timeProvider.GetTimestamp();
        }
    }

    private interface IHardwareWorkItem
    {
        void Execute();
    }

    private sealed class HardwareWorkItem<T>(
        Func<CancellationToken, Task<T>> operation,
        Func<CancellationToken, Task>? timeoutRecovery,
        CancellationTokenSource cancellation,
        Action completed) : IHardwareWorkItem
    {
        private readonly TaskCompletionSource<T> completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object lifecycleLock = new();
        private bool executionFinished;
        private bool timedOut;

        public Task<T> Task => completion.Task;

        public void RequestCancellation()
        {
            try
            {
                cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The dedicated thread completed between WaitAsync and cancellation.
            }
        }

        /// <returns>
        /// True when execution already finished and recovery must be queued as a successor;
        /// otherwise the executing work item owns the recovery.
        /// </returns>
        public bool MarkAbandoned()
        {
            lock (lifecycleLock)
            {
                timedOut = true;
                return executionFinished;
            }
        }

        public void Execute()
        {
            T result = default!;
            OperationCanceledException? cancellationFailure = null;
            Exception? operationFailure = null;
            try
            {
                cancellation.Token.ThrowIfCancellationRequested();
                result = operation(cancellation.Token).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException exception)
            {
                cancellationFailure = exception;
            }
            catch (Exception exception)
            {
                operationFailure = exception;
            }
            finally
            {
                Func<CancellationToken, Task>? recovery = null;
                lock (lifecycleLock)
                {
                    executionFinished = true;
                    if (timedOut)
                    {
                        recovery = timeoutRecovery;
                    }
                }

                if (recovery is not null)
                {
                    try
                    {
                        // This narrow terminal recovery intentionally does not inherit either the
                        // expired operation token or the caller token.
                        recovery(CancellationToken.None).GetAwaiter().GetResult();
                    }
                    catch
                    {
                        // The coordinator has already latched hardware poison and records the
                        // timeout. Recovery failure cannot make ownership safe again.
                    }
                }

                if (cancellationFailure is not null)
                {
                    completion.TrySetCanceled(cancellationFailure.CancellationToken);
                }
                else if (operationFailure is not null)
                {
                    completion.TrySetException(operationFailure);
                }
                else
                {
                    completion.TrySetResult(result);
                }

                cancellation.Dispose();
                completed();
            }
        }
    }

    private sealed class TerminalRecoveryWorkItem(
        Func<CancellationToken, Task> recovery,
        Action completed) : IHardwareWorkItem
    {
        public void Execute()
        {
            try
            {
                recovery(CancellationToken.None).GetAwaiter().GetResult();
            }
            catch
            {
                // The process is already poisoned and will terminate non-zero. There is no caller
                // to receive this best-effort terminal recovery failure.
            }
            finally
            {
                completed();
            }
        }
    }
}

internal sealed class HidIoDispatcher(TimeProvider timeProvider) : HardwareIoDispatcher(timeProvider);

internal sealed class ThermalIoDispatcher(TimeProvider timeProvider) : HardwareIoDispatcher(timeProvider);
