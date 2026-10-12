using V12UltraController.Core.Control;
using V12UltraController.Core.Ipc;

namespace V12UltraController.Service;

internal sealed partial class ControllerCoordinator
{
    // Explicit repair of a failed attended abort; not automatic startup ownership,
    // not commissioning, and never an arbitrary journal-clear or raw HID API.
    public async Task<object> RecoverCommissioningSafetyAsync(
        bool attended, PipePeerIdentity peer, CancellationToken cancellationToken)
    {
        RequireAttended(attended);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureInitialized();
            ThrowIfHardwarePoisoned();
            if (configuration.Commissioned || runtimeState != ServiceRuntimeState.Initial ||
                configurationError is not null || configurationLoadInvalid || runtimeStateLoadInvalid ||
                commissioningStateMismatch || invalidConfigurationWithPendingJournal ||
                auditMutationBlocked || activeAuditMutation is not null ||
                HasForcedUninstallRecoveryBarrier || requiresProcessRestartForRecovery ||
                commissioningSession is not null || device is not null || writerLease.IsHeld ||
                hidDispatcher.HasPendingOperations)
            {
                throw Invalid("Attended abort recovery requires an unowned, valid controller with no other recovery barrier.");
            }

            if (await transitionJournalStore.LoadAsync(cancellationToken).ConfigureAwait(false) is not null)
            {
                throw Invalid("A task transition must be resolved separately; no device access was attempted.");
            }

            var journal = await safetyOperationAuditJournalStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            // Automatic thermal/session aborts retain the original session owner's SID.
            // Accept that exact abort only, not arbitrary failed hardware operations.
            if (journal is null ||
                (journal.EventName != PipeOperations.CommissionAbort && journal.EventName != "commission.safety-abort") ||
                journal.Kind != SafetyOperationKind.HidWithOn100Recovery ||
                journal.Stage != SafetyOperationAuditStage.EffectInProgress ||
                !journal.RequiresOn100Recovery ||
                !string.Equals(journal.CallerSid, peer.CallerSid, StringComparison.OrdinalIgnoreCase))
            {
                throw Invalid("Recovery requires this Windows user's exact unfinished manual or automatic commissioning-abort journal.");
            }

            activeSafetyOperationAudit = journal;
            try
            {
                var after = await RecoverSafetyOperationHidAtOneHundredAsync(cancellationToken,
                    attendedUncommissioned: true).ConfigureAwait(false);
                await ResolveAndFinishSafetyOperationAuditAsync(journal, SafetyOperationAuditOutcome.Compensated,
                    new { recovery = "attended-abort-repair", poweredOn = after.PoweredOn,
                        fanPercent = after.FanSpeedPercent, readbackConfirmed = true }, CancellationToken.None)
                    .ConfigureAwait(false);
                hardwareAccessBlocked = false;
                // Release ownership without replaying lower values. Keep the confirmed
                // observation for status, explicitly timestamped, after closing the handle.
                var released = await ReleaseDeviceAsync().ConfigureAwait(false);
                lastDeviceState = after;
                if (!released) { ThrowIfHardwarePoisoned(); throw Invalid("Device handle release was not confirmed."); }
                UpdateStatus("NotCommissioned", FailSafeReason.NotCommissioned);
                return new { poweredOn = true, retainedPercent = 100, readbackConfirmed = true,
                    commissioned = false, journalResolved = true };
            }
            catch (Exception exception)
            {
                LatchSafetyOperationAuditBlocked(exception, activeSafetyOperationAudit ?? journal,
                    "attended commissioning abort repair");
                throw;
            }
        }
        finally { MarkHeartbeat(); gate.Release(); }
    }
}
