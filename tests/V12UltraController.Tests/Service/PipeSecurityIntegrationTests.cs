using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Extensions.Logging.Abstractions;
using V12UltraController.Core.Ipc;
using V12UltraController.Core.Security;
using V12UltraController.Service;

namespace V12UltraController.Tests.Service;

public sealed class PipeSecurityIntegrationTests
{
    [Fact]
    public void PipeWorker_ClientDisconnectWin32FailureDoesNotEscalateToFatalServiceFailure()
    {
        Assert.True(PipeServerWorker.IsExpectedClientFailure(new System.ComponentModel.Win32Exception(233)));
        Assert.True(PipeServerWorker.IsExpectedClientFailure(new IOException("disconnected")));
        Assert.True(PipeServerWorker.IsExpectedClientFailure(new UnauthorizedAccessException("rejected")));
        Assert.False(PipeServerWorker.IsExpectedClientFailure(new InvalidOperationException("service bug")));
    }

    [Fact]
    public void PipePeer_WarmsUpWindowsIdentityBeforeEnteringClientImpersonation()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        PipePeer.WarmUpWindowsIdentityDependencies();
    }

    [Fact]
    public void SecureFactory_DaclContainsOnlySystemAdministratorsAndAuthorizedUser()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var currentSid = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("The test process has no Windows SID.");
        var factory = new SecurePipeFactory(
            new FakeInstallerEnrollmentReader(currentSid.Value),
            NullLogger<SecurePipeFactory>.Instance);
        using var pipe = factory.Create(
            $"V12UltraController.Tests.{Guid.NewGuid():N}",
            firstInstance: true);

        Assert.Equal(
            SecurePipeFactory.PipeRejectRemoteClients,
            SecurePipeFactory.InspectNativePipeFlags(pipe) &
            SecurePipeFactory.PipeRejectRemoteClients);

        var rules = pipe.GetAccessControl().GetAccessRules(
            includeExplicit: true,
            includeInherited: false,
            typeof(SecurityIdentifier));
        var allowedRules = rules.Cast<PipeAccessRule>()
            .Where(static rule => rule.AccessControlType == AccessControlType.Allow)
            .ToArray();
        var allowed = allowedRules
            .Select(static rule => ((SecurityIdentifier)rule.IdentityReference).Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Contains(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value, allowed);
        Assert.Contains(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Value, allowed);
        Assert.Contains(currentSid.Value, allowed);
        Assert.DoesNotContain(new SecurityIdentifier(WellKnownSidType.WorldSid, null).Value, allowed);
        Assert.DoesNotContain(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null).Value, allowed);

        var systemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value;
        var administratorsSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Value;
        Assert.True(HasRight(allowedRules, systemSid, PipeAccessRights.CreateNewInstance));
        Assert.False(HasRight(allowedRules, administratorsSid, PipeAccessRights.CreateNewInstance));
        if (!string.Equals(currentSid.Value, systemSid, StringComparison.OrdinalIgnoreCase))
        {
            Assert.False(HasRight(allowedRules, currentSid.Value, PipeAccessRights.CreateNewInstance));
        }
    }

    [Fact]
    public async Task LocalClientWithIdentification_AllowsServerToIdentifyCallerSid()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var pipeName = $"V12UltraController.Tests.{Guid.NewGuid():N}";
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

        var connect = client.ConnectAsync(5_000);
        await server.WaitForConnectionAsync();
        await connect;

        var peer = PipePeer.Inspect(server);
        var expectedSid = WindowsIdentity.GetCurrent().User?.Value;
        Assert.False(string.IsNullOrWhiteSpace(expectedSid));
        Assert.Equal(expectedSid, peer.CallerSid);
        Assert.False(string.IsNullOrWhiteSpace(peer.Source));
    }

    [Fact]
    public void SecureFactory_UsesKernelRemoteRejectionAndFirstInstanceOnlyForBootstrap()
    {
        Assert.Equal(
            SecurePipeFactory.PipeRejectRemoteClients,
            SecurePipeFactory.BuildPipeMode() & SecurePipeFactory.PipeRejectRemoteClients);
        Assert.Equal(
            SecurePipeFactory.FileFlagFirstPipeInstance,
            SecurePipeFactory.BuildOpenMode(firstInstance: true) &
            SecurePipeFactory.FileFlagFirstPipeInstance);
        Assert.Equal(
            0u,
            SecurePipeFactory.BuildOpenMode(firstInstance: false) &
            SecurePipeFactory.FileFlagFirstPipeInstance);
    }

    [Fact]
    public async Task SecureFactory_FirstInstanceRejectsPreoccupiedName_AndConsecutiveInstancesCoexist()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var currentSid = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("The test process has no Windows SID.");
        const string enrolledClientSid = "S-1-5-21-111111111-222222222-333333333-1001";
        var factory = new SecurePipeFactory(
            new FakeInstallerEnrollmentReader(enrolledClientSid),
            NullLogger<SecurePipeFactory>.Instance,
            currentSid);
        var pipeName = $"V12UltraController.Tests.{Guid.NewGuid():N}";

        using var bootstrap = factory.Create(pipeName, firstInstance: true);
        Assert.Throws<IOException>(() => factory.Create(pipeName, firstInstance: true));

        await using var firstClient = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous,
            TokenImpersonationLevel.Identification);
        var firstConnect = firstClient.ConnectAsync(5_000);
        await bootstrap.WaitForConnectionAsync();
        await firstConnect;

        using var successor = factory.Create(pipeName, firstInstance: false);
        await using var secondClient = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous,
            TokenImpersonationLevel.Identification);
        var secondConnect = secondClient.ConnectAsync(5_000);
        await successor.WaitForConnectionAsync();
        await secondConnect;

        using var nextSuccessor = factory.Create(pipeName, firstInstance: false);
    }

    private static bool HasRight(
        IEnumerable<PipeAccessRule> rules,
        string sid,
        PipeAccessRights right) =>
        rules.Where(rule =>
                string.Equals(
                    ((SecurityIdentifier)rule.IdentityReference).Value,
                    sid,
                    StringComparison.OrdinalIgnoreCase))
            .Any(rule => (rule.PipeAccessRights & right) == right);

    private sealed class FakeInstallerEnrollmentReader(string? value) : IInstallerEnrollmentReader
    {
        public string? ReadAuthorizedUserSid() => value;
    }
}
