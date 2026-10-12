using System.Security.Principal;
using V12UltraController.Core.Security;

namespace V12UltraController.Cli;

internal interface ICurrentWindowsIdentity
{
    string? UserSid { get; }
}

internal sealed class CurrentWindowsIdentity : ICurrentWindowsIdentity
{
    public string? UserSid
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
            return identity.User?.Value;
        }
    }
}

/// <summary>
/// Fixed-purpose LocalSystem-only MSI command. It never opens the named pipe
/// and accepts no path, marker contents, or other caller-controlled payload.
/// </summary>
internal sealed class InstallerForcedUninstallMarkerCommand(
    ICurrentWindowsIdentity identity,
    IForcedUninstallRecoveryMarkerStore markerStore,
    TextWriter output,
    TextWriter error)
{
    internal const string InstallerCommand = "installer";
    internal const string MarkForcedUninstallCommand = "mark-forced-uninstall";
    internal const string LocalSystemSid = "S-1-5-18";
    internal const int MarkerFailedExitCode = 1;

    public static bool IsRequested(IReadOnlyList<string> args) =>
        args.Count > 0 &&
        args[0].Equals(InstallerCommand, StringComparison.OrdinalIgnoreCase);

    public async Task<int> RunAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        if (args.Count != 2 ||
            !args[0].Equals(InstallerCommand, StringComparison.OrdinalIgnoreCase) ||
            !args[1].Equals(MarkForcedUninstallCommand, StringComparison.OrdinalIgnoreCase))
        {
            await error.WriteLineAsync(
                    "The private installer command requires one fixed operation.")
                .ConfigureAwait(false);
            return CliExitCodes.InvalidArguments;
        }

        string? callerSid;
        try
        {
            callerSid = identity.UserSid;
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            await error.WriteLineAsync(
                    "Forced-uninstall recovery marking failed closed because the caller identity could not be verified.")
                .ConfigureAwait(false);
            return MarkerFailedExitCode;
        }

        if (!string.Equals(callerSid, LocalSystemSid, StringComparison.OrdinalIgnoreCase))
        {
            await error.WriteLineAsync(
                    "Forced-uninstall recovery marking is restricted to the LocalSystem installer action.")
                .ConfigureAwait(false);
            return MarkerFailedExitCode;
        }

        try
        {
            // A prior failed/rolled-back forced-uninstall attempt may already
            // have left the same kind of valid barrier. Preserve it exactly.
            var existing = await markerStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                ValidateFixedMarker(existing);
                await output.WriteLineAsync(
                        "Forced-uninstall recovery barrier is already present.")
                    .ConfigureAwait(false);
                return CliExitCodes.Success;
            }

            var requested = ForcedUninstallRecoveryMarker.CreateNew();
            var created = await markerStore.CreateAsync(requested, cancellationToken)
                .ConfigureAwait(false);
            if (created != requested)
            {
                throw new InvalidDataException(
                    "The marker store returned a different recovery marker.");
            }

            var readBack = await markerStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            if (readBack != requested)
            {
                throw new InvalidDataException(
                    "The recovery marker did not match its exact readback.");
            }

            await output.WriteLineAsync("Forced-uninstall recovery barrier recorded.")
                .ConfigureAwait(false);
            return CliExitCodes.Success;
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            await error.WriteLineAsync(
                    "Forced-uninstall recovery marking failed closed; forced uninstall was not authorized to continue.")
                .ConfigureAwait(false);
            return MarkerFailedExitCode;
        }
    }

    private static void ValidateFixedMarker(ForcedUninstallRecoveryMarker marker)
    {
        if (marker.SchemaVersion != ForcedUninstallRecoveryMarker.CurrentSchemaVersion ||
            marker.MarkerId == Guid.Empty ||
            !string.Equals(
                marker.Reason,
                ForcedUninstallRecoveryMarker.ForcedUninstallReason,
                StringComparison.Ordinal) ||
            marker.CreatedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new InvalidDataException(
                "The existing forced-uninstall recovery marker is not valid.");
        }
    }

    private static bool IsFatal(Exception exception) =>
        exception is OutOfMemoryException or StackOverflowException or AccessViolationException;
}
