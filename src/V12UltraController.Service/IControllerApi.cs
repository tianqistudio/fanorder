using V12UltraController.Core.Configuration;
using V12UltraController.Core.Control;
using V12UltraController.Core.Hardware;
using V12UltraController.Core.Ipc;
using V12UltraController.Core.Status;

namespace V12UltraController.Service;

/// <summary>
/// Narrows the local API handler's dependency to the operations it can expose.
/// The production adapter delegates to the coordinator; tests can exercise the
/// request contract without constructing hardware or scheduled-task services.
/// </summary>
internal interface IControllerApi
{
    object GetStatus();
    Task<ControllerStatus> DisableSafetyProtectionAsync(PipePeerIdentity peer, CancellationToken cancellationToken);
    Task<ControllerStatus> ResumeSafetyProtectionAsync(bool confirmed, PipePeerIdentity peer, CancellationToken cancellationToken);
    Task<object> GetLightingAsync(CancellationToken cancellationToken);
    Task<object> SetLightingAsync(LightingSettings settings, PipePeerIdentity peer, CancellationToken cancellationToken);
    object GetStartup();
    Task<object> SetStartupAsync(bool enabled, PipePeerIdentity peer, CancellationToken cancellationToken);
    object GetStartupFan();
    Task<object> SetStartupFanPercentAsync(byte percent, PipePeerIdentity peer, CancellationToken cancellationToken);
    Task<object> RecoverCommissioningSafetyAsync(bool attended, PipePeerIdentity peer, CancellationToken cancellationToken);

    FanCurve GetCurve();
    Task ApplyCurveAdviceAsync(FanCurve expected, FanCurve proposed, PipePeerIdentity peer, CancellationToken cancellationToken);
    Task<ProfileApplyResult> ApplyProfileAsync(ProfileApplyRequest request, string operationId,
        PipePeerIdentity peer, CancellationToken cancellationToken);

    object GetDiagnostics();

    object GetShutdownStatus();

    Task SetModeAsync(ControlMode mode, PipePeerIdentity peer, CancellationToken cancellationToken);

    Task SetManualSpeedAsync(byte percent, PipePeerIdentity peer, CancellationToken cancellationToken);
    Task<object> SetManualPowerAsync(bool poweredOn, bool confirmed, PipePeerIdentity peer, CancellationToken cancellationToken);

    Task SetCurveAsync(FanCurve curve, PipePeerIdentity peer, CancellationToken cancellationToken);

    Task<object> ProbeCommissioningAsync(
        bool attended,
        PipePeerIdentity peer,
        CancellationToken cancellationToken);

    Task<object> VerifyIdempotentAsync(
        Guid sessionId,
        bool attended,
        PipePeerIdentity peer,
        CancellationToken cancellationToken);

    Task<object> TestCommissioningAsync(
        Guid sessionId,
        bool attended,
        PipePeerIdentity peer,
        CancellationToken cancellationToken,
        bool nearFullOnly = false);

    Task<object> CommitCommissioningAsync(
        Guid sessionId,
        bool attended,
        PipePeerIdentity peer,
        CancellationToken cancellationToken);

    Task<object> RollbackAsync(
        bool startMythCool,
        bool installer,
        bool attended,
        PipePeerIdentity peer,
        CancellationToken cancellationToken);

    Task<object> RecoverTaskTransitionAsync(
        bool attended,
        PipePeerIdentity peer,
        CancellationToken cancellationToken);

    Task<object> AbortCommissioningAsync(
        Guid sessionId,
        PipePeerIdentity peer,
        CancellationToken cancellationToken);

    Task<object> QualifySystemShutdownOffAsync(
        bool attended,
        PipePeerIdentity peer,
        CancellationToken cancellationToken);

    Task<object> EnableSystemShutdownOffAsync(
        Guid qualificationId,
        bool attended,
        PipePeerIdentity peer,
        CancellationToken cancellationToken);

    Task<object> DisableSystemShutdownOffAsync(
        PipePeerIdentity peer,
        CancellationToken cancellationToken);
}

internal sealed class ControllerApiAdapter(ControllerCoordinator coordinator) : IControllerApi
{
    private readonly ControllerCoordinator coordinator =
        coordinator ?? throw new ArgumentNullException(nameof(coordinator));

    public object GetStatus() => coordinator.GetStatus();
    public Task<ControllerStatus> DisableSafetyProtectionAsync(PipePeerIdentity peer, CancellationToken cancellationToken) =>
        coordinator.DisableSafetyProtectionAsync(peer, cancellationToken);
    public Task<ControllerStatus> ResumeSafetyProtectionAsync(bool confirmed, PipePeerIdentity peer, CancellationToken cancellationToken) =>
        coordinator.ResumeSafetyProtectionAsync(confirmed, peer, cancellationToken);
    public Task<object> GetLightingAsync(CancellationToken cancellationToken) => coordinator.GetLightingAsync(cancellationToken);
    public Task<object> SetLightingAsync(LightingSettings settings, PipePeerIdentity peer, CancellationToken cancellationToken) =>
        coordinator.SetLightingAsync(settings, peer, cancellationToken);
    public object GetStartup() => coordinator.GetStartup();
    public Task<object> SetStartupAsync(bool enabled, PipePeerIdentity peer, CancellationToken cancellationToken) =>
        coordinator.SetStartupAsync(enabled, peer, cancellationToken);
    public object GetStartupFan() => coordinator.GetStartupFan();
    public Task<object> SetStartupFanPercentAsync(byte percent, PipePeerIdentity peer, CancellationToken cancellationToken) =>
        coordinator.SetStartupFanPercentAsync(percent, peer, cancellationToken);
    public Task<object> RecoverCommissioningSafetyAsync(bool attended, PipePeerIdentity peer, CancellationToken cancellationToken) =>
        coordinator.RecoverCommissioningSafetyAsync(attended, peer, cancellationToken);

    public FanCurve GetCurve() => coordinator.GetCurve();
    public Task ApplyCurveAdviceAsync(FanCurve expected, FanCurve proposed, PipePeerIdentity peer, CancellationToken cancellationToken) =>
        coordinator.ApplyCurveAdviceAsync(expected, proposed, peer, cancellationToken);
    public Task<ProfileApplyResult> ApplyProfileAsync(ProfileApplyRequest request, string operationId,
        PipePeerIdentity peer, CancellationToken cancellationToken) =>
        coordinator.ApplyProfileAsync(request, operationId, peer, cancellationToken);

    public object GetDiagnostics() => coordinator.GetDiagnostics();

    public object GetShutdownStatus() => coordinator.GetShutdownStatus();
    public Task<object> SetManualPowerAsync(bool poweredOn, bool confirmed, PipePeerIdentity peer, CancellationToken cancellationToken) =>
        coordinator.SetManualPowerAsync(poweredOn, confirmed, peer, cancellationToken);

    public Task SetModeAsync(
        ControlMode mode,
        PipePeerIdentity peer,
        CancellationToken cancellationToken) =>
        coordinator.SetModeAsync(mode, peer, cancellationToken);

    public Task SetManualSpeedAsync(
        byte percent,
        PipePeerIdentity peer,
        CancellationToken cancellationToken) =>
        coordinator.SetManualSpeedAsync(percent, peer, cancellationToken);

    public Task SetCurveAsync(
        FanCurve curve,
        PipePeerIdentity peer,
        CancellationToken cancellationToken) =>
        coordinator.SetCurveAsync(curve, peer, cancellationToken);

    public Task<object> ProbeCommissioningAsync(
        bool attended,
        PipePeerIdentity peer,
        CancellationToken cancellationToken) =>
        coordinator.ProbeCommissioningAsync(attended, peer, cancellationToken);

    public Task<object> VerifyIdempotentAsync(
        Guid sessionId,
        bool attended,
        PipePeerIdentity peer,
        CancellationToken cancellationToken) =>
        coordinator.VerifyIdempotentAsync(sessionId, attended, peer, cancellationToken);

    public Task<object> TestCommissioningAsync(
        Guid sessionId,
        bool attended,
        PipePeerIdentity peer,
        CancellationToken cancellationToken,
        bool nearFullOnly = false) =>
        coordinator.TestCommissioningAsync(sessionId, attended, peer, cancellationToken, nearFullOnly);

    public Task<object> CommitCommissioningAsync(
        Guid sessionId,
        bool attended,
        PipePeerIdentity peer,
        CancellationToken cancellationToken) =>
        coordinator.CommitCommissioningAsync(sessionId, attended, peer, cancellationToken);

    public Task<object> RollbackAsync(
        bool startMythCool,
        bool installer,
        bool attended,
        PipePeerIdentity peer,
        CancellationToken cancellationToken) =>
        coordinator.RollbackAsync(startMythCool, installer, attended, peer, cancellationToken);

    public Task<object> RecoverTaskTransitionAsync(
        bool attended,
        PipePeerIdentity peer,
        CancellationToken cancellationToken) =>
        coordinator.RecoverTaskTransitionAsync(attended, peer, cancellationToken);

    public Task<object> AbortCommissioningAsync(
        Guid sessionId,
        PipePeerIdentity peer,
        CancellationToken cancellationToken) =>
        coordinator.AbortCommissioningAsync(sessionId, peer, cancellationToken);

    public Task<object> QualifySystemShutdownOffAsync(
        bool attended,
        PipePeerIdentity peer,
        CancellationToken cancellationToken) =>
        coordinator.QualifySystemShutdownOffAsync(attended, peer, cancellationToken);

    public Task<object> EnableSystemShutdownOffAsync(
        Guid qualificationId,
        bool attended,
        PipePeerIdentity peer,
        CancellationToken cancellationToken) =>
        coordinator.EnableSystemShutdownOffAsync(
            qualificationId, attended, peer, cancellationToken);

    public Task<object> DisableSystemShutdownOffAsync(
        PipePeerIdentity peer,
        CancellationToken cancellationToken) =>
        coordinator.DisableSystemShutdownOffAsync(peer, cancellationToken);
}
