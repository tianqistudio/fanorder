namespace V12UltraController.Service;

/// <summary>
/// A disconnected ordinary API client can abandon its place in the coordinator queue.
/// Once the coordinator admits the request, the existing operation owns its readback,
/// recovery and audit closure under the service lifetime. Profile application has its
/// own stage-aware cancellation contract and does not enter this scope.
/// </summary>
internal sealed class ControllerRequestAdmissionScope : IDisposable
{
    private static readonly AsyncLocal<ControllerRequestAdmissionScope?> Current = new();
    private readonly ControllerRequestAdmissionScope? previous;
    private readonly CancellationToken pendingToken;
    private readonly CancellationToken serviceToken;

    internal ControllerRequestAdmissionScope(CancellationToken pendingToken, CancellationToken serviceToken)
    {
        this.pendingToken = pendingToken;
        this.serviceToken = serviceToken;
        previous = Current.Value;
        Current.Value = this;
    }

    // Call only after acquiring the coordinator gate, inside its try/finally. The check
    // is the admission boundary: a prior disconnect rejects the request; a later one
    // cannot cancel the service token selected for the operation already admitted.
    internal static CancellationToken Admit(CancellationToken cancellationToken)
    {
        if (Current.Value is not { } scope || scope.pendingToken != cancellationToken)
            return cancellationToken;
        cancellationToken.ThrowIfCancellationRequested();
        return scope.serviceToken;
    }

    public void Dispose() => Current.Value = previous;
}
