using V12UltraController.Cli;
using V12UltraController.Core.Security;

if (InstallerEnrollmentPreflight.IsRequested(args))
{
    var preflight = new InstallerEnrollmentPreflight(
        new WindowsRegistryInstallerEnrollmentReader(),
        Console.Out,
        Console.Error);
    return preflight.Run(args);
}

if (InstallerForcedUninstallMarkerCommand.IsRequested(args))
{
    using var markerStore = new ForcedUninstallRecoveryMarkerStore();
    var markerCommand = new InstallerForcedUninstallMarkerCommand(
        new CurrentWindowsIdentity(),
        markerStore,
        Console.Out,
        Console.Error);
    return await markerCommand.RunAsync(args, CancellationToken.None).ConfigureAwait(false);
}

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};

var application = new CliApplication(Console.Out, Console.Error);
return await application.RunAsync(args, cancellation.Token).ConfigureAwait(false);
