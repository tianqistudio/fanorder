using V12UltraController.Core.Security;

namespace V12UltraController.Cli;

/// <summary>
/// Fixed-purpose MSI preflight. This command never opens the service pipe and
/// accepts no filesystem or registry path from its caller.
/// </summary>
internal sealed class InstallerEnrollmentPreflight(
    IInstallerEnrollmentReader enrollmentReader,
    TextWriter output,
    TextWriter error)
{
    internal const string CommandName = "installer-preflight";
    internal const string FreshExpectation = "fresh";
    internal const string ExistingExpectation = "existing";
    internal const int PreflightFailedExitCode = 1;

    public static bool IsRequested(IReadOnlyList<string> args) =>
        args.Count > 0 && args[0].Equals(CommandName, StringComparison.OrdinalIgnoreCase);

    public int Run(IReadOnlyList<string> args)
    {
        if (args.Count != 2)
        {
            error.WriteLine("Installer enrollment preflight requires exactly one fixed expectation.");
            return CliExitCodes.InvalidArguments;
        }

        var expectation = args[1].ToLowerInvariant();
        if (expectation is not (FreshExpectation or ExistingExpectation))
        {
            error.WriteLine("Installer enrollment preflight expectation is invalid.");
            return CliExitCodes.InvalidArguments;
        }

        string? rawSid;
        try
        {
            rawSid = enrollmentReader.ReadAuthorizedUserSid();
        }
        catch (Exception exception) when (InstallerEnrollmentSid.IsExpectedReadFailure(exception))
        {
            error.WriteLine(
                $"Installer enrollment preflight could not read the protected machine enrollment: {exception.Message}");
            return PreflightFailedExitCode;
        }

        if (expectation == FreshExpectation)
        {
            if (rawSid is not null)
            {
                error.WriteLine(
                    "Fresh installation refused because a prior machine enrollment already exists.");
                return PreflightFailedExitCode;
            }

            output.WriteLine("Fresh installer enrollment preflight passed.");
            return CliExitCodes.Success;
        }

        if (rawSid is null)
        {
            error.WriteLine(
                "Maintenance or major upgrade refused because the original machine enrollment is missing.");
            return PreflightFailedExitCode;
        }

        try
        {
            _ = InstallerEnrollmentSid.ParseSpecificUser(rawSid);
        }
        catch (Exception exception) when (InstallerEnrollmentSid.IsExpectedReadFailure(exception))
        {
            error.WriteLine(
                $"Maintenance or major upgrade refused because the original machine enrollment is invalid: {exception.Message}");
            return PreflightFailedExitCode;
        }

        output.WriteLine("Existing installer enrollment preflight passed.");
        return CliExitCodes.Success;
    }
}
