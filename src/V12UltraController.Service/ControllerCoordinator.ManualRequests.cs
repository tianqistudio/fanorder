using V12UltraController.Core.Control;
using V12UltraController.Core.Ipc;

namespace V12UltraController.Service;

internal sealed partial class ControllerCoordinator
{
    private long manualRequestRevision;
    private long resolvedManualRequestRevision;
    private readonly AsyncLocal<long?> manualOperationRevision = new();
    private bool ManualRequestPending => Interlocked.Read(ref manualRequestRevision) >
        Interlocked.Read(ref resolvedManualRequestRevision);

    private long BeginManualRequest() => Interlocked.Increment(ref manualRequestRevision);
    private void ResolveManualRequest(long revision)
    {
        var resolved = Interlocked.Read(ref resolvedManualRequestRevision);
        while (resolved < revision)
        {
            var observed = Interlocked.CompareExchange(ref resolvedManualRequestRevision, revision, resolved);
            if (observed == resolved) break;
            resolved = observed;
        }
    }
    private void RequireCurrentManualRequest(long revision)
    {
        if (revision != Interlocked.Read(ref manualRequestRevision))
            throw new ControllerOperationException(PipeErrorCodes.DeviceStateChanged,
                "此操作已被较新的手动输入取代；保留最后一次手动设置。");
    }

    private async Task SetManualSpeedRequestAsync(byte percent, PipePeerIdentity peer, CancellationToken cancellationToken)
    {
        if (percent is < 10 or > 100) throw Invalid("percent must be between 10 and 100.");
        var revision = BeginManualRequest();
        try
        {
            if (SafetyProtectionDisabled || configuration.Mode is ControlMode.PureManual or ControlMode.Released)
            {
                await ExplicitManualCommandAsync(percent, true, peer, cancellationToken, revision).ConfigureAwait(false);
                return;
            }
            await UpdateConfigurationAsync(current =>
            {
                if (SafetyProtectionDisabled || current.Mode is ControlMode.PureManual or ControlMode.Released)
                    throw Invalid("Control mode changed concurrently; refresh before another command.");
                return current with { Mode = ControlMode.Manual, ManualSpeedPercent = percent };
            }, PipeOperations.SpeedSet, peer, cancellationToken, revision).ConfigureAwait(false);
        }
        finally { ResolveManualRequest(revision); }
    }

    public async Task<object> SetManualPowerAsync(bool poweredOn, bool confirmed, PipePeerIdentity peer,
        CancellationToken cancellationToken)
    {
        if (!poweredOn && !confirmed) throw Invalid("Explicit OFF confirmation is required.");
        var revision = BeginManualRequest();
        var previous = manualOperationRevision.Value;
        manualOperationRevision.Value = revision;
        try { return await SetManualPowerCoreAsync(poweredOn, confirmed, peer, cancellationToken, revision).ConfigureAwait(false); }
        finally { manualOperationRevision.Value = previous; ResolveManualRequest(revision); }
    }
}
