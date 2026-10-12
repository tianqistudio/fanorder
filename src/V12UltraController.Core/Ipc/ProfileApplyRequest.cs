using V12UltraController.Core.Configuration;
using V12UltraController.Core.Status;

namespace V12UltraController.Core.Ipc;

public sealed record ProfileApplyRequest(ControlProfileSnapshot Profile, bool ConfirmResumeControl = false);
public sealed record ProfileApplyResult(ProfileApplicationStatus Application, ControllerStatus Status);
