namespace V12UltraController.Service;

internal sealed class ControllerOperationException(string code, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public string Code { get; } = code;
}
