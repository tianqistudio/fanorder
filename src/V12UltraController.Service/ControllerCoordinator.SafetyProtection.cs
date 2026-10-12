using V12UltraController.Core.Ipc;
using V12UltraController.Core.Status;

namespace V12UltraController.Service;

internal sealed partial class ControllerCoordinator
{
    private readonly ISafetyProtectionStore safetyProtectionStore;
    private readonly object safetyProtectionSync = new();
    private readonly SemaphoreSlim safetyProtectionPersistence = new(1, 1);
    private readonly AsyncLocal<bool> explicitManualIo = new();
    private readonly AsyncLocal<long?> automaticIoEpoch = new();
    private readonly AsyncLocal<CancellationToken?> automaticIoCancellation = new();
    private readonly AsyncLocal<CancellationToken?> explicitSequenceCancellation = new();
    private long safetyProtectionEpoch;
    private CancellationTokenSource automaticOutputLifetime = new();
    private SafetyProtectionStatus safetyProtection = new(false, true, false, false, null, null);
    private int startupFanRecoveryAllowedFromPersistedSafetyLatch;
    private bool SafetyProtectionDisabled => Volatile.Read(ref safetyProtection).Disabled;

    private SafetyProtectionStatus ReadSafetyProtection() => Volatile.Read(ref safetyProtection) with
    {
        HasInFlightOperation = hidDispatcher.HasPendingOperations,
    };

    private async Task LoadSafetyProtectionAsync(CancellationToken cancellationToken)
    {
        try
        {
            var intent = await safetyProtectionStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            if (intent?.Disabled == true)
            {
                Volatile.Write(ref startupFanRecoveryAllowedFromPersistedSafetyLatch, 1);
                Volatile.Write(ref safetyProtection, new(true, true, true, false, null, intent.DisabledAt));
            }
            else
            {
                Volatile.Write(ref startupFanRecoveryAllowedFromPersistedSafetyLatch, 0);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Volatile.Write(ref startupFanRecoveryAllowedFromPersistedSafetyLatch, 0);
            Volatile.Write(ref safetyProtection, new(true, false, true, false,
                $"停止自动控制标记无法读取（{exception.GetType().Name}）；已停止自动输出，请明确重新保存禁用状态。", null));
        }
    }

    public async Task<ControllerStatus> DisableSafetyProtectionAsync(PipePeerIdentity peer,
        CancellationToken cancellationToken)
    {
        // Intentionally precedes the controller gate and all hardware/configuration barriers.
        // It stops NEW automatic work, not an already issued native call.
        SafetyProtectionIntent intent;
        long epoch;
        lock (safetyProtectionSync)
        {
            epoch = Interlocked.Increment(ref safetyProtectionEpoch);
            Volatile.Write(ref startupFanRecoveryAllowedFromPersistedSafetyLatch, 0);
            var current = safetyProtection;
            Volatile.Write(ref startupPureManualRecoveryAllowed, 0);
            var since = current.DisabledAt ?? timeProvider.GetUtcNow();
            Volatile.Write(ref safetyProtection, new(true, current.Disabled && current.Persisted,
                true, false, null, since));
            intent = new(1, true, since, peer.CallerSid);
            // Retire old requests while giving later explicit requests their own fresh epoch.
            // Automatic requests still fail the Disabled check before any dispatch.
            var retiredLifetime = automaticOutputLifetime;
            automaticOutputLifetime = new CancellationTokenSource();
            _ = retiredLifetime.CancelAsync();
        }
        // Client cancellation does not undo the accepted stop intent or prevent its save.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var entered = false;
        try
        {
            await safetyProtectionPersistence.WaitAsync(deadline.Token).ConfigureAwait(false);
            entered = true;
            var result = await SaveSafetyIntentVerifiedAsync(intent, deadline.Token).ConfigureAwait(false);
            lock (safetyProtectionSync)
            {
                if (epoch == safetyProtectionEpoch)
                    Volatile.Write(ref safetyProtection, safetyProtection with
                    { Persisted = result.Error is null, PersistenceError = result.Error });
            }
        }
        catch (Exception exception)
        {
            lock (safetyProtectionSync)
            {
                if (epoch == safetyProtectionEpoch)
                    Volatile.Write(ref safetyProtection, safetyProtection with
                    { Persisted = false, PersistenceError = $"已停止自动输出，但禁用状态未确认保存（{exception.GetType().Name}）；重启后不保证保留。" });
            }
        }
        finally { if (entered) safetyProtectionPersistence.Release(); }
        return GetStatus();
    }

    public async Task<ControllerStatus> ResumeSafetyProtectionAsync(bool confirmed, PipePeerIdentity peer,
        CancellationToken cancellationToken)
    {
        if (!confirmed) throw Invalid("请明确确认恢复已保存模式的自动控制与安全保护。");
        var requestEpoch = Interlocked.Read(ref safetyProtectionEpoch);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureInitialized(); ThrowIfDisposed();
            if (!SafetyProtectionDisabled) return GetStatus();
            if (commissioningSession is not null || HostStopDisposition != HostStopDisposition.None)
                throw Invalid("接管或后台停止过程中无法恢复自动控制。");
            await safetyProtectionPersistence.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (requestEpoch != Interlocked.Read(ref safetyProtectionEpoch))
                    throw Invalid("恢复请求已被更新的禁用意图替代；自动控制保持停止。");
                var epoch = Interlocked.Read(ref safetyProtectionEpoch);
                var result = await SaveSafetyIntentVerifiedAsync(new(1, false, null, peer.CallerSid), cancellationToken)
                    .ConfigureAwait(false);
                lock (safetyProtectionSync)
                {
                    if (epoch == safetyProtectionEpoch)
                    {
                        if (result.Error is null)
                        {
                            Interlocked.Increment(ref safetyProtectionEpoch);
                            _ = automaticOutputLifetime.CancelAsync();
                            automaticOutputLifetime = new CancellationTokenSource();
                            Volatile.Write(ref safetyProtection, new(false, true, false, false, null, null));
                            policy.Reset();
                            manualPower.RequestOn();
                        }
                        else Volatile.Write(ref safetyProtection, safetyProtection with
                        { Persisted = false, PersistenceError = result.Error });
                    }
                    else Volatile.Write(ref safetyProtection, safetyProtection with { Persisted = false });
                }
            }
            finally { safetyProtectionPersistence.Release(); }
            UpdateStatus(SafetyProtectionDisabled ? "SafetyProtectionDisabled" : "Starting");
            return GetStatus();
        }
        finally { gate.Release(); }
    }

    private async Task<(string? Error, bool Published)> SaveSafetyIntentVerifiedAsync(
        SafetyProtectionIntent intent, CancellationToken cancellationToken)
    {
        Exception? failure = null;
        try { await safetyProtectionStore.SaveAsync(intent, cancellationToken).ConfigureAwait(false); }
        catch (Exception exception) { failure = exception; }
        try
        {
            var readback = await safetyProtectionStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            if (readback == intent) return (null, true);
            failure ??= new InvalidDataException("Safety intent readback differs.");
        }
        catch (Exception exception) { failure ??= exception; }
        return ($"停止意图未确认持久保存（{failure?.GetType().Name}）；当前停止仍生效，重启后不保证保留。", false);
    }

    private void ThrowIfAutomaticOutputStopped(long? expectedEpoch = null)
    {
        if (explicitManualIo.Value)
        {
            if (explicitSequenceCancellation.Value?.IsCancellationRequested == true)
                throw new SafetyOutputSuppressedException();
            return;
        }
        var epoch = expectedEpoch ?? automaticIoEpoch.Value;
        // A native manual transaction already issued retains ownership through readback.
        // Its revision is checked before SET; pending input suppresses automatic work.
        if ((ManualRequestPending || observedManualControlHold && observedManualPersistenceUnconfirmed) &&
            manualOperationRevision.Value is null)
            throw new SafetyOutputSuppressedException();
        if (SafetyProtectionDisabled || (epoch is not null && epoch != Interlocked.Read(ref safetyProtectionEpoch)))
            throw new SafetyOutputSuppressedException();
    }

    // Deliberate suppression is not caller abandonment: the dispatcher must not convert it
    // to a cancelled task and attach another emergency recovery operation.
    internal sealed class SafetyOutputSuppressedException : Exception
    {
        public SafetyOutputSuppressedException() : base("自动控制已禁用；此自动设备操作已取消。") { }
    }
}
