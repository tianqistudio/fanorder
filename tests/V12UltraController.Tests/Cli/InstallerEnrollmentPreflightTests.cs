using V12UltraController.Cli;
using V12UltraController.Core.Security;

namespace V12UltraController.Tests.Cli;

public sealed class InstallerEnrollmentPreflightTests
{
    private const string SpecificUserSid = "S-1-5-21-111111111-222222222-333333333-1001";

    [Theory]
    [InlineData(null, "fresh", 0)]
    [InlineData(SpecificUserSid, "fresh", InstallerEnrollmentPreflight.PreflightFailedExitCode)]
    [InlineData(SpecificUserSid, "existing", 0)]
    [InlineData(null, "existing", InstallerEnrollmentPreflight.PreflightFailedExitCode)]
    [InlineData("S-1-1-0", "existing", InstallerEnrollmentPreflight.PreflightFailedExitCode)]
    [InlineData("not-a-sid", "existing", InstallerEnrollmentPreflight.PreflightFailedExitCode)]
    public void FixedExpectations_AreReadOnlyAndFailClosed(
        string? enrolledSid,
        string expectation,
        int expectedExitCode)
    {
        var reader = new FakeInstallerEnrollmentReader(enrolledSid);
        using var output = new StringWriter();
        using var error = new StringWriter();
        var application = new InstallerEnrollmentPreflight(reader, output, error);

        var exitCode = application.Run([InstallerEnrollmentPreflight.CommandName, expectation]);

        Assert.Equal(expectedExitCode, exitCode);
        Assert.Equal(1, reader.ReadCount);
        Assert.DoesNotContain(SpecificUserSid, output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(SpecificUserSid, error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void UnexpectedArguments_AreRejectedBeforeRegistryRead()
    {
        var reader = new FakeInstallerEnrollmentReader((string?)null);
        using var output = new StringWriter();
        using var error = new StringWriter();
        var application = new InstallerEnrollmentPreflight(reader, output, error);

        Assert.Equal(
            CliExitCodes.InvalidArguments,
            application.Run([InstallerEnrollmentPreflight.CommandName, "fresh", "extra"]));
        Assert.Equal(
            CliExitCodes.InvalidArguments,
            application.Run([InstallerEnrollmentPreflight.CommandName, "unknown"]));
        Assert.Equal(0, reader.ReadCount);
    }

    [Fact]
    public void RegistryReadFailure_FailsClosed()
    {
        var reader = new FakeInstallerEnrollmentReader(
            new UnauthorizedAccessException("read denied"));
        using var output = new StringWriter();
        using var error = new StringWriter();
        var application = new InstallerEnrollmentPreflight(reader, output, error);

        var exitCode = application.Run(
            [InstallerEnrollmentPreflight.CommandName, InstallerEnrollmentPreflight.ExistingExpectation]);

        Assert.Equal(InstallerEnrollmentPreflight.PreflightFailedExitCode, exitCode);
        Assert.Contains("could not read", error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Command_RemainsHiddenFromPublicHelp()
    {
        Assert.True(InstallerEnrollmentPreflight.IsRequested(
            [InstallerEnrollmentPreflight.CommandName, InstallerEnrollmentPreflight.FreshExpectation]));
        Assert.DoesNotContain(
            InstallerEnrollmentPreflight.CommandName,
            CliCommandParser.HelpText,
            StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SpecificUserSid)]
    [InlineData("S-1-12-1-111111111-222222222-333333333-444444444")]
    public void Validator_AcceptsSupportedSpecificUserSidForms(string value)
    {
        Assert.Equal(value, InstallerEnrollmentSid.ParseSpecificUser(value).Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-sid")]
    [InlineData("S-1-1-0")]
    [InlineData("S-1-5-11")]
    [InlineData("S-1-5-18")]
    [InlineData("S-1-5-32-544")]
    [InlineData("S-1-5-80-12345")]
    [InlineData("S-1-12-1-1-2-3")]
    public void Validator_RejectsBroadOrNonUserPrincipals(string value)
    {
        _ = Assert.Throws<InvalidDataException>(() =>
            InstallerEnrollmentSid.ParseSpecificUser(value));
    }

    private sealed class FakeInstallerEnrollmentReader : IInstallerEnrollmentReader
    {
        private readonly string? value;
        private readonly Exception? exception;

        public FakeInstallerEnrollmentReader(string? value)
        {
            this.value = value;
        }

        public FakeInstallerEnrollmentReader(Exception exception)
        {
            this.exception = exception;
        }

        public int ReadCount { get; private set; }

        public string? ReadAuthorizedUserSid()
        {
            ReadCount++;
            if (exception is not null)
            {
                throw exception;
            }

            return value;
        }
    }
}
