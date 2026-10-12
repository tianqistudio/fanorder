using V12UltraController.Cli;
using V12UltraController.Core.Security;

namespace V12UltraController.Tests.Cli;

public sealed class InstallerForcedUninstallMarkerCommandTests
{
    private static readonly ForcedUninstallRecoveryMarker ExistingMarker = new(
        ForcedUninstallRecoveryMarker.CurrentSchemaVersion,
        Guid.Parse("11111111-2222-3333-4444-555555555555"),
        ForcedUninstallRecoveryMarker.ForcedUninstallReason,
        new DateTimeOffset(2026, 9, 6, 1, 2, 3, TimeSpan.Zero));

    [Fact]
    public async Task LocalSystem_CreatesAndExactlyReadsBackMarkerWithoutSensitiveOutput()
    {
        var store = new FakeMarkerStore();
        using var output = new StringWriter();
        using var error = new StringWriter();
        var command = new InstallerForcedUninstallMarkerCommand(
            new FakeIdentity(InstallerForcedUninstallMarkerCommand.LocalSystemSid),
            store,
            output,
            error);

        var exitCode = await command.RunAsync(
            [InstallerForcedUninstallMarkerCommand.InstallerCommand,
                InstallerForcedUninstallMarkerCommand.MarkForcedUninstallCommand],
            TestContext.Current.CancellationToken);

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Equal(2, store.LoadCount);
        Assert.Equal(1, store.CreateCount);
        Assert.NotNull(store.Marker);
        Assert.NotEqual(Guid.Empty, store.Marker.MarkerId);
        Assert.Equal(TimeSpan.Zero, store.Marker.CreatedAtUtc.Offset);
        Assert.Equal(
            ForcedUninstallRecoveryMarker.ForcedUninstallReason,
            store.Marker.Reason);
        Assert.DoesNotContain(store.Marker.MarkerId.ToString("D"), output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(store.Marker.Reason, output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("ProgramData", output.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(error.ToString());
    }

    [Fact]
    public async Task ExistingValidBarrier_IsPreservedIdempotently()
    {
        var store = new FakeMarkerStore { Marker = ExistingMarker };
        using var output = new StringWriter();
        using var error = new StringWriter();
        var command = new InstallerForcedUninstallMarkerCommand(
            new FakeIdentity(InstallerForcedUninstallMarkerCommand.LocalSystemSid),
            store,
            output,
            error);

        var exitCode = await command.RunAsync(
            ["installer", "mark-forced-uninstall"],
            TestContext.Current.CancellationToken);

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Equal(1, store.LoadCount);
        Assert.Equal(0, store.CreateCount);
        Assert.Same(ExistingMarker, store.Marker);
    }

    [Theory]
    [InlineData("S-1-5-21-1-2-3-1001")]
    [InlineData("S-1-5-32-544")]
    [InlineData(null)]
    public async Task NonSystemCaller_IsRejectedBeforeMarkerAccess(string? sid)
    {
        var store = new FakeMarkerStore();
        using var output = new StringWriter();
        using var error = new StringWriter();
        var command = new InstallerForcedUninstallMarkerCommand(
            new FakeIdentity(sid),
            store,
            output,
            error);

        var exitCode = await command.RunAsync(
            ["installer", "mark-forced-uninstall"],
            TestContext.Current.CancellationToken);

        Assert.Equal(InstallerForcedUninstallMarkerCommand.MarkerFailedExitCode, exitCode);
        Assert.Equal(0, store.LoadCount);
        Assert.Equal(0, store.CreateCount);
        Assert.Empty(output.ToString());
        Assert.Contains("LocalSystem", error.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(InvalidArguments))]
    public async Task InvalidArguments_AreRejectedBeforeIdentityOrMarkerAccess(string[] args)
    {
        var identity = new FakeIdentity(InstallerForcedUninstallMarkerCommand.LocalSystemSid);
        var store = new FakeMarkerStore();
        using var output = new StringWriter();
        using var error = new StringWriter();
        var command = new InstallerForcedUninstallMarkerCommand(
            identity,
            store,
            output,
            error);

        var exitCode = await command.RunAsync(args, TestContext.Current.CancellationToken);

        Assert.Equal(CliExitCodes.InvalidArguments, exitCode);
        Assert.Equal(0, identity.ReadCount);
        Assert.Equal(0, store.LoadCount);
        Assert.Equal(0, store.CreateCount);
    }

    [Fact]
    public async Task MismatchedReadback_FailsClosedWithoutLeakingMarkerDetails()
    {
        var store = new FakeMarkerStore { ReplaceAfterCreate = true };
        using var output = new StringWriter();
        using var error = new StringWriter();
        var command = new InstallerForcedUninstallMarkerCommand(
            new FakeIdentity(InstallerForcedUninstallMarkerCommand.LocalSystemSid),
            store,
            output,
            error);

        var exitCode = await command.RunAsync(
            ["installer", "mark-forced-uninstall"],
            TestContext.Current.CancellationToken);

        Assert.Equal(InstallerForcedUninstallMarkerCommand.MarkerFailedExitCode, exitCode);
        Assert.Empty(output.ToString());
        Assert.Contains("failed closed", error.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            store.Marker!.MarkerId.ToString("D"),
            error.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task StoreFailure_FailsClosedWithoutEchoingExceptionOrFixedPath()
    {
        var store = new FakeMarkerStore
        {
            Exception = new IOException("secret-detail at C:\\ProgramData\\marker"),
        };
        using var output = new StringWriter();
        using var error = new StringWriter();
        var command = new InstallerForcedUninstallMarkerCommand(
            new FakeIdentity(InstallerForcedUninstallMarkerCommand.LocalSystemSid),
            store,
            output,
            error);

        var exitCode = await command.RunAsync(
            ["installer", "mark-forced-uninstall"],
            TestContext.Current.CancellationToken);

        Assert.Equal(InstallerForcedUninstallMarkerCommand.MarkerFailedExitCode, exitCode);
        Assert.Empty(output.ToString());
        Assert.DoesNotContain("secret-detail", error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("ProgramData", error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Command_IsInterceptedLocallyAndHiddenFromPublicHelp()
    {
        Assert.True(InstallerForcedUninstallMarkerCommand.IsRequested(
            ["installer", "mark-forced-uninstall"]));
        Assert.DoesNotContain(
            InstallerForcedUninstallMarkerCommand.MarkForcedUninstallCommand,
            CliCommandParser.HelpText,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "v12ctl installer",
            CliCommandParser.HelpText,
            StringComparison.OrdinalIgnoreCase);
    }

    public static TheoryData<string[]> InvalidArguments => new()
    {
        Array.Empty<string>(),
        new[] { "installer" },
        new[] { "installer", "unknown" },
        new[] { "installer", "mark-forced-uninstall", "extra" },
        new[] { "other", "mark-forced-uninstall" },
    };

    private sealed class FakeIdentity(string? sid) : ICurrentWindowsIdentity
    {
        public int ReadCount { get; private set; }

        public string? UserSid
        {
            get
            {
                ReadCount++;
                return sid;
            }
        }
    }

    private sealed class FakeMarkerStore : IForcedUninstallRecoveryMarkerStore
    {
        public ForcedUninstallRecoveryMarker? Marker { get; set; }

        public int LoadCount { get; private set; }

        public int CreateCount { get; private set; }

        public bool ReplaceAfterCreate { get; init; }

        public Exception? Exception { get; init; }

        public Task<ForcedUninstallRecoveryMarker?> LoadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LoadCount++;
            if (Exception is not null)
            {
                throw Exception;
            }

            return Task.FromResult(Marker);
        }

        public Task<ForcedUninstallRecoveryMarker> CreateAsync(
            ForcedUninstallRecoveryMarker marker,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CreateCount++;
            if (Exception is not null)
            {
                throw Exception;
            }

            Marker = ReplaceAfterCreate
                ? marker with { MarkerId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee") }
                : marker;
            return Task.FromResult(marker);
        }

        public Task DeleteExactAsync(
            ForcedUninstallRecoveryMarker expected,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
