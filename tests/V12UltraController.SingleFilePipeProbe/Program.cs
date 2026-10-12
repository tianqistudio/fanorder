using System.IO.Pipes;
using System.Security.Principal;
using V12UltraController.Service;

if (!OperatingSystem.IsWindows())
{
    return 0;
}

var pipeName = $"V12UltraController.SingleFilePipeProbe.{Guid.NewGuid():N}";
await using var server = new NamedPipeServerStream(
    pipeName,
    PipeDirection.InOut,
    1,
    PipeTransmissionMode.Byte,
    PipeOptions.Asynchronous);
await using var client = new NamedPipeClientStream(
    ".",
    pipeName,
    PipeDirection.InOut,
    PipeOptions.Asynchronous,
    TokenImpersonationLevel.Identification);
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

var connectTask = client.ConnectAsync(timeout.Token);
await server.WaitForConnectionAsync(timeout.Token);
await connectTask;

// This must be the process's first WindowsIdentity path. It exercises the real
// single-file cold-load boundary inside PipePeer before this program obtains the
// expected SID outside impersonation.
var peer = PipePeer.Inspect(server);
using var processIdentity = WindowsIdentity.GetCurrent();
var expectedSid = processIdentity.User?.Value
    ?? throw new InvalidOperationException("The probe process has no Windows SID.");
if (!string.Equals(peer.CallerSid, expectedSid, StringComparison.OrdinalIgnoreCase))
{
    throw new InvalidOperationException(
        $"Pipe peer SID mismatch. Expected {expectedSid}, found {peer.CallerSid ?? "<null>"}.");
}

Console.WriteLine($"single-file pipe identity verified: {expectedSid}");
return 0;
