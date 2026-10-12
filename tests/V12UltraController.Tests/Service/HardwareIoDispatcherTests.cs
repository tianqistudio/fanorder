using System.Collections.Concurrent;
using System.Diagnostics;
using V12UltraController.Service;

namespace V12UltraController.Tests.Service;

[Collection(HardwareTimingCollection.Name)]
public sealed class HardwareIoDispatcherTests
{
    [Fact]
    public async Task InvokeAsync_SerializesCallsOnOneDedicatedBackgroundThread()
    {
        using var dispatcher = new HardwareIoDispatcher(TimeProvider.System);
        var callerThread = Environment.CurrentManagedThreadId;

        var first = dispatcher.InvokeAsync(
            _ => Task.FromResult(Environment.CurrentManagedThreadId),
            TimeSpan.FromSeconds(1),
            CancellationToken.None);
        var second = dispatcher.InvokeAsync(
            _ => Task.FromResult(Environment.CurrentManagedThreadId),
            TimeSpan.FromSeconds(1),
            CancellationToken.None);

        var threadIds = await Task.WhenAll(first, second);
        Assert.Equal(threadIds[0], threadIds[1]);
        Assert.NotEqual(callerThread, threadIds[0]);
        Assert.False(dispatcher.HasPendingOperations);
    }

    [Fact]
    public async Task TimedOutNativeCall_RemainsArmedAndDisposeIsBounded()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var dispatcher = new HardwareIoDispatcher(time);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        try
        {
            var invocation = dispatcher.InvokeAsync(
                _ =>
                {
                    entered.Set();
                    release.Wait();
                    return Task.FromResult(1);
                },
                TimeSpan.FromMilliseconds(50),
                CancellationToken.None);
            Assert.True(entered.Wait(TimeSpan.FromSeconds(1)));

            await Assert.ThrowsAsync<TimeoutException>(() => invocation);
            time.Advance(TimeSpan.FromSeconds(3));
            Assert.True(dispatcher.HasPendingOperations);
            Assert.True(dispatcher.HasStalledOperation(TimeSpan.FromSeconds(2)));

            var stopwatch = Stopwatch.StartNew();
            dispatcher.Dispose();
            stopwatch.Stop();
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1));
        }
        finally
        {
            release.Set();
            dispatcher.Dispose();
        }
    }

    [Fact]
    public async Task TimedOutNativeCall_RunsAttachedRecoveryAfterLateCancellationIgnoringReturn()
    {
        using var dispatcher = new HardwareIoDispatcher(TimeProvider.System);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var operations = new ConcurrentQueue<string>();
        var recovered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var invocation = dispatcher.InvokeWithTimeoutRecoveryAsync(
                _ =>
                {
                    operations.Enqueue("operation:start");
                    entered.Set();
                    release.Wait();
                    operations.Enqueue("operation:end");
                    return Task.FromResult(1);
                },
                _ =>
                {
                    operations.Enqueue("recovery");
                    recovered.TrySetResult(true);
                    return Task.CompletedTask;
                },
                TimeSpan.FromMilliseconds(50),
                CancellationToken.None);
            Assert.True(entered.Wait(TimeSpan.FromSeconds(1)));

            await Assert.ThrowsAsync<TimeoutException>(() => invocation);
            release.Set();
            await recovered.Task.WaitAsync(TimeSpan.FromSeconds(1));

            Assert.Equal(
                ["operation:start", "operation:end", "recovery"],
                operations.ToArray());
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public async Task CallerCancelledNativeCall_RunsAttachedRecoveryAfterLateCancellationIgnoringReturn()
    {
        using var dispatcher = new HardwareIoDispatcher(TimeProvider.System);
        using var callerCancellation = new CancellationTokenSource();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var operations = new ConcurrentQueue<string>();
        var recovered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var invocation = dispatcher.InvokeWithTimeoutRecoveryAsync(
                _ =>
                {
                    operations.Enqueue("operation:start");
                    entered.Set();
                    release.Wait();
                    operations.Enqueue("operation:end");
                    return Task.FromResult(1);
                },
                _ =>
                {
                    operations.Enqueue("recovery");
                    recovered.TrySetResult(true);
                    return Task.CompletedTask;
                },
                TimeSpan.FromSeconds(5),
                callerCancellation.Token);
            Assert.True(entered.Wait(TimeSpan.FromSeconds(1)));

            callerCancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => invocation);
            release.Set();
            await recovered.Task.WaitAsync(TimeSpan.FromSeconds(1));

            Assert.Equal(
                ["operation:start", "operation:end", "recovery"],
                operations.ToArray());
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public async Task QueuedOperation_DoesNotResetOldestOperationStart()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        using var dispatcher = new HardwareIoDispatcher(time);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        try
        {
            var first = dispatcher.InvokeAsync(
                _ =>
                {
                    entered.Set();
                    release.Wait();
                    return Task.FromResult(1);
                },
                TimeSpan.FromSeconds(5),
                CancellationToken.None);
            Assert.True(entered.Wait(TimeSpan.FromSeconds(1)));
            time.Advance(TimeSpan.FromSeconds(1));
            var second = dispatcher.InvokeAsync(
                _ => Task.FromResult(2),
                TimeSpan.FromSeconds(5),
                CancellationToken.None);

            time.Advance(TimeSpan.FromSeconds(1.1));
            Assert.True(dispatcher.HasStalledOperation(TimeSpan.FromSeconds(2)));

            release.Set();
            Assert.Equal(1, await first);
            Assert.Equal(2, await second);
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public async Task StalledOperation_UsesMonotonicTimeAcrossUtcRollback()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        using var dispatcher = new HardwareIoDispatcher(time);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        try
        {
            var invocation = dispatcher.InvokeAsync(
                _ =>
                {
                    entered.Set();
                    release.Wait();
                    return Task.FromResult(1);
                },
                TimeSpan.FromSeconds(5),
                CancellationToken.None);
            Assert.True(entered.Wait(TimeSpan.FromSeconds(1)));

            time.AdvanceMonotonic(TimeSpan.FromSeconds(3));
            time.JumpUtc(TimeSpan.FromDays(-2));

            Assert.True(dispatcher.HasStalledOperation(TimeSpan.FromSeconds(2)));
            release.Set();
            Assert.Equal(1, await invocation);
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public async Task CompletedStagesRefreshProgressButOneStalledStageStillTripsWatchdog()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        using var dispatcher = new HardwareIoDispatcher(time);
        using var finalStageEntered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        try
        {
            var invocation = dispatcher.InvokeAsync(
                _ =>
                {
                    time.AdvanceMonotonic(TimeSpan.FromMilliseconds(1500));
                    dispatcher.ReportProgress();
                    time.AdvanceMonotonic(TimeSpan.FromMilliseconds(1500));
                    dispatcher.ReportProgress();
                    finalStageEntered.Set();
                    release.Wait();
                    return Task.FromResult(1);
                },
                TimeSpan.FromSeconds(5),
                CancellationToken.None);
            Assert.True(finalStageEntered.Wait(TimeSpan.FromSeconds(1)));

            Assert.False(dispatcher.HasStalledOperation(TimeSpan.FromSeconds(2)));
            time.AdvanceMonotonic(TimeSpan.FromMilliseconds(2100));
            Assert.True(dispatcher.HasStalledOperation(TimeSpan.FromSeconds(2)));

            release.Set();
            Assert.Equal(1, await invocation);
        }
        finally
        {
            release.Set();
        }
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private long utcTicks = utcNow.UtcTicks;
        private long timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override DateTimeOffset GetUtcNow() =>
            new(Interlocked.Read(ref utcTicks), TimeSpan.Zero);

        public override long GetTimestamp() => Interlocked.Read(ref timestamp);

        public void Advance(TimeSpan duration)
        {
            JumpUtc(duration);
            AdvanceMonotonic(duration);
        }

        public void AdvanceMonotonic(TimeSpan duration) =>
            Interlocked.Add(ref timestamp, duration.Ticks);

        public void JumpUtc(TimeSpan duration) =>
            Interlocked.Add(ref utcTicks, duration.Ticks);
    }
}
