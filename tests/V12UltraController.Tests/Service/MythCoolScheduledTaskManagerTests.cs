using Microsoft.Extensions.Logging.Abstractions;
using V12UltraController.Service;

namespace V12UltraController.Tests.Service;

public sealed class MythCoolScheduledTaskManagerTests
{
    [Fact]
    public async Task Disable_UsesInjectedRunnerAndVerifiesOnlyAfterCommandCompletes()
    {
        using var fixture = new TaskManagerFixture();
        fixture.Runner.Results.Enqueue(new SchtasksProcessResult(0, string.Empty, string.Empty));
        fixture.Runner.Results.Enqueue(new SchtasksProcessResult(
            0,
            "<?xml version=\"1.0\"?><Task><Settings><Enabled>false</Enabled></Settings></Task>",
            string.Empty));

        await fixture.Manager.DisableAsync(CancellationToken.None);

        Assert.Equal(2, fixture.Runner.Calls.Count);
        Assert.Equal("/Disable", fixture.Runner.Calls[0].Arguments[^1]);
        Assert.Equal(SchtasksCommandKind.Mutation, fixture.Runner.Calls[0].Kind);
        Assert.Equal("/XML", fixture.Runner.Calls[1].Arguments[^1]);
        Assert.Equal(SchtasksCommandKind.Query, fixture.Runner.Calls[1].Kind);
    }

    [Fact]
    public async Task Disable_UncertainChildOwnershipNeverStartsAReadbackOrSecondCommand()
    {
        using var fixture = new TaskManagerFixture();
        fixture.Runner.Failures.Enqueue(new ScheduledTaskCommandOwnershipUncertainException(
            "Injected unconfirmed process termination.",
            new TimeoutException()));

        await Assert.ThrowsAsync<ScheduledTaskCommandOwnershipUncertainException>(() =>
            fixture.Manager.DisableAsync(CancellationToken.None));

        Assert.Single(fixture.Runner.Calls);
        Assert.Equal("/Disable", fixture.Runner.Calls[0].Arguments[^1]);
    }

    [Fact]
    public async Task Disable_PreStartCancellationPropagatesWithoutStartingAReadback()
    {
        using var fixture = new TaskManagerFixture();
        fixture.Runner.Failures.Enqueue(new OperationCanceledException(
            "Injected cancellation after child exit confirmation."));

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            fixture.Manager.DisableAsync(CancellationToken.None));

        Assert.Single(fixture.Runner.Calls);
        Assert.Equal("/Disable", fixture.Runner.Calls[0].Arguments[^1]);
    }

    [Fact]
    public async Task Disable_CallerCancellationAfterMutationStartsCannotInterruptDefinitiveReadback()
    {
        using var fixture = new TaskManagerFixture();
        var mutationStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var mutationExit = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Runner.Handler = async (_, kind, _) =>
        {
            if (kind == SchtasksCommandKind.Mutation)
            {
                mutationStarted.TrySetResult(true);
                await mutationExit.Task.ConfigureAwait(false);
                return new SchtasksProcessResult(0, string.Empty, string.Empty);
            }

            return new SchtasksProcessResult(
                0,
                "<?xml version=\"1.0\"?><Task><Settings><Enabled>false</Enabled></Settings></Task>",
                string.Empty);
        };
        using var cancellation = new CancellationTokenSource();

        var disable = fixture.Manager.DisableAsync(cancellation.Token);
        await mutationStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        cancellation.Cancel();
        Assert.False(disable.IsCompleted);
        mutationExit.TrySetResult(true);
        await disable;

        Assert.Equal(2, fixture.Runner.Calls.Count);
        Assert.Equal(SchtasksCommandKind.Mutation, fixture.Runner.Calls[0].Kind);
        Assert.Equal(SchtasksCommandKind.Query, fixture.Runner.Calls[1].Kind);
        Assert.False(fixture.Runner.Calls[1].CancellationToken.CanBeCanceled);
    }

    [Fact]
    public async Task Restore_CallerCancellationAfterMutationStartsCannotInterruptDefinitiveReadback()
    {
        using var fixture = new TaskManagerFixture();
        fixture.Runner.Results.Enqueue(new SchtasksProcessResult(
            0,
            EnabledTaskXml(enabled: true),
            string.Empty));
        _ = await fixture.Manager.BackupCurrentAsync(CancellationToken.None);

        var mutationStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var mutationExit = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var restoredEnabled = false;
        fixture.Runner.Handler = async (arguments, kind, _) =>
        {
            if (kind == SchtasksCommandKind.Mutation)
            {
                mutationStarted.TrySetResult(true);
                await mutationExit.Task.ConfigureAwait(false);
                restoredEnabled = true;
                return new SchtasksProcessResult(0, string.Empty, string.Empty);
            }

            return arguments.Contains("/XML", StringComparer.OrdinalIgnoreCase)
                ? new SchtasksProcessResult(0, EnabledTaskXml(restoredEnabled), string.Empty)
                : new SchtasksProcessResult(0, string.Empty, string.Empty);
        };
        using var cancellation = new CancellationTokenSource();

        var restore = fixture.Manager.RestoreAsync(startMythCool: false, cancellation.Token);
        await mutationStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        cancellation.Cancel();
        Assert.False(restore.IsCompleted);
        mutationExit.TrySetResult(true);
        await restore;

        var restoreCalls = fixture.Runner.Calls.Skip(1).ToArray();
        Assert.Equal(3, restoreCalls.Length);
        Assert.Equal(SchtasksCommandKind.Query, restoreCalls[0].Kind);
        Assert.Equal(SchtasksCommandKind.Mutation, restoreCalls[1].Kind);
        Assert.Equal(SchtasksCommandKind.Query, restoreCalls[2].Kind);
        Assert.False(restoreCalls[2].CancellationToken.CanBeCanceled);
    }

    [Fact]
    public async Task Restore_MetadataEnabledMismatchWithVerifiedXmlFailsBeforeAnyTaskMutation()
    {
        using var fixture = new TaskManagerFixture();
        fixture.Runner.Results.Enqueue(new SchtasksProcessResult(
            0,
            EnabledTaskXml(enabled: false),
            string.Empty));
        var metadata = await fixture.Manager.BackupCurrentAsync(CancellationToken.None);
        await File.WriteAllTextAsync(
            fixture.Paths.TaskBackupMetadataPath,
            System.Text.Json.JsonSerializer.Serialize(
                metadata with { OriginallyEnabled = true },
                new System.Text.Json.JsonSerializerOptions(
                    System.Text.Json.JsonSerializerDefaults.Web)));
        var callsBeforeRestore = fixture.Runner.Calls.Count;

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            fixture.Manager.RestoreAsync(startMythCool: false, CancellationToken.None));

        Assert.Equal(callsBeforeRestore, fixture.Runner.Calls.Count);
        Assert.DoesNotContain(
            fixture.Runner.Calls.Skip(callsBeforeRestore),
            static call => call.Kind == SchtasksCommandKind.Mutation);
    }

    [Fact]
    public async Task Restore_ExistingDefinitionDriftFailsBeforeAnyMutation()
    {
        using var fixture = new TaskManagerFixture();
        var backupXml = EnabledTaskXml(enabled: true);
        fixture.Runner.Results.Enqueue(new SchtasksProcessResult(0, backupXml, string.Empty));
        _ = await fixture.Manager.BackupCurrentAsync(CancellationToken.None);
        var callsBeforeRestore = fixture.Runner.Calls.Count;
        fixture.Runner.Results.Enqueue(new SchtasksProcessResult(
            0,
            "<?xml version=\"1.0\"?><Task><Settings><Enabled>false</Enabled></Settings>" +
            "<Actions><Exec><Command>drifted.exe</Command></Exec></Actions></Task>",
            string.Empty));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            fixture.Manager.RestoreAsync(startMythCool: false, CancellationToken.None));

        Assert.Single(fixture.Runner.Calls.Skip(callsBeforeRestore));
        Assert.DoesNotContain(
            fixture.Runner.Calls.Skip(callsBeforeRestore),
            static call => call.Kind == SchtasksCommandKind.Mutation);
    }

    [Fact]
    public async Task Restore_AlreadyExactIsVerifiedNoOp()
    {
        using var fixture = new TaskManagerFixture();
        var backupXml = EnabledTaskXml(enabled: true);
        fixture.Runner.Results.Enqueue(new SchtasksProcessResult(0, backupXml, string.Empty));
        _ = await fixture.Manager.BackupCurrentAsync(CancellationToken.None);
        var callsBeforeRestore = fixture.Runner.Calls.Count;
        fixture.Runner.Results.Enqueue(new SchtasksProcessResult(0, backupXml, string.Empty));

        await fixture.Manager.RestoreAsync(startMythCool: false, CancellationToken.None);

        var restoreCalls = fixture.Runner.Calls.Skip(callsBeforeRestore).ToArray();
        var query = Assert.Single(restoreCalls);
        Assert.Equal(SchtasksCommandKind.Query, query.Kind);
        Assert.DoesNotContain(restoreCalls, static call => call.Kind == SchtasksCommandKind.Mutation);
    }

    [Fact]
    public async Task Restore_DefinitelyMissingRecreatesThenVerifiesDefinitionAndEnabledState()
    {
        using var fixture = new TaskManagerFixture();
        var backupXml = EnabledTaskXml(enabled: true);
        fixture.Runner.Results.Enqueue(new SchtasksProcessResult(0, backupXml, string.Empty));
        _ = await fixture.Manager.BackupCurrentAsync(CancellationToken.None);
        var callsBeforeRestore = fixture.Runner.Calls.Count;
        fixture.Runner.Results.Enqueue(new SchtasksProcessResult(
            1,
            string.Empty,
            "ERROR_FILE_NOT_FOUND (0x80070002)"));
        fixture.Runner.Results.Enqueue(new SchtasksProcessResult(0, string.Empty, string.Empty));
        fixture.Runner.Results.Enqueue(new SchtasksProcessResult(0, string.Empty, string.Empty));
        fixture.Runner.Results.Enqueue(new SchtasksProcessResult(0, backupXml, string.Empty));

        await fixture.Manager.RestoreAsync(startMythCool: false, CancellationToken.None);

        var restoreCalls = fixture.Runner.Calls.Skip(callsBeforeRestore).ToArray();
        Assert.Equal(4, restoreCalls.Length);
        Assert.Equal(SchtasksCommandKind.Query, restoreCalls[0].Kind);
        Assert.Contains("/Create", restoreCalls[1].Arguments);
        Assert.Contains("/Enable", restoreCalls[2].Arguments);
        Assert.Equal(SchtasksCommandKind.Query, restoreCalls[3].Kind);
    }

    [Fact]
    public async Task Restore_AmbiguousQueryFailureNeverRecreatesOrMutates()
    {
        using var fixture = new TaskManagerFixture();
        var backupXml = EnabledTaskXml(enabled: true);
        fixture.Runner.Results.Enqueue(new SchtasksProcessResult(0, backupXml, string.Empty));
        _ = await fixture.Manager.BackupCurrentAsync(CancellationToken.None);
        var callsBeforeRestore = fixture.Runner.Calls.Count;
        fixture.Runner.Results.Enqueue(new SchtasksProcessResult(
            1,
            string.Empty,
            "Access is denied."));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Manager.RestoreAsync(startMythCool: false, CancellationToken.None));

        Assert.DoesNotContain(
            fixture.Runner.Calls.Skip(callsBeforeRestore),
            static call => call.Kind == SchtasksCommandKind.Mutation);
    }

    [Fact]
    public async Task VerifiedBackup_PartialPresenceIsCorruptionNotAbsence()
    {
        using var fixture = new TaskManagerFixture();
        await File.WriteAllTextAsync(
            fixture.Paths.TaskBackupXmlPath,
            EnabledTaskXml(enabled: true),
            CancellationToken.None);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            fixture.Manager.TryReadVerifiedBackupAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-sha256")]
    public async Task VerifiedBackup_InvalidMetadataHashFailsClosedWithoutTaskCommand(string? invalidHash)
    {
        using var fixture = new TaskManagerFixture();
        fixture.Runner.Results.Enqueue(new SchtasksProcessResult(
            0,
            EnabledTaskXml(enabled: true),
            string.Empty));
        var metadata = await fixture.Manager.BackupCurrentAsync(CancellationToken.None);
        await File.WriteAllTextAsync(
            fixture.Paths.TaskBackupMetadataPath,
            System.Text.Json.JsonSerializer.Serialize(
                metadata with { XmlSha256 = invalidHash! },
                new System.Text.Json.JsonSerializerOptions(
                    System.Text.Json.JsonSerializerDefaults.Web)));
        var callsBeforeRead = fixture.Runner.Calls.Count;

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            fixture.Manager.TryReadVerifiedBackupAsync(CancellationToken.None));

        Assert.Equal(callsBeforeRead, fixture.Runner.Calls.Count);
    }

    [Fact]
    public async Task RecoveryInspection_UsesOnlyQueryAndDistinguishesExactFromEnabledOnlyChange()
    {
        using var fixture = new TaskManagerFixture();
        var backupXml = """
            <?xml version="1.0"?>
            <Task xmlns="urn:test-task">
              <Triggers><BootTrigger><Enabled>true</Enabled></BootTrigger></Triggers>
              <Settings><Enabled>true</Enabled></Settings>
              <Actions><Exec><Command>C:\Program Files (x86)\Myth.Cool\MythCool.exe</Command></Exec></Actions>
            </Task>
            """;
        fixture.Runner.Results.Enqueue(new SchtasksProcessResult(0, backupXml, string.Empty));
        var backup = await fixture.Manager.BackupCurrentAsync(CancellationToken.None);
        fixture.Runner.Results.Enqueue(new SchtasksProcessResult(
            0,
            backupXml.Replace(
                "<Settings><Enabled>true</Enabled></Settings>",
                "<Settings><Enabled>false</Enabled></Settings>",
                StringComparison.Ordinal),
            string.Empty));

        var inspection = await fixture.Manager.InspectCurrentAgainstBackupAsync(
            backup,
            CancellationToken.None);

        Assert.False(inspection.Enabled);
        Assert.False(inspection.ExactBackupXmlMatch);
        Assert.True(inspection.DefinitionMatchesBackup);
        Assert.Equal(64, inspection.CurrentXmlSha256.Length);
        Assert.Equal(SchtasksCommandKind.Query, fixture.Runner.Calls[^1].Kind);
        Assert.Equal("/XML", fixture.Runner.Calls[^1].Arguments[^1]);
        Assert.DoesNotContain(fixture.Runner.Calls,
            static call => call.Kind == SchtasksCommandKind.Mutation);
    }

    [Fact]
    public async Task RecoveryInspection_RequiresExactBackupForRestoredStateAndDetectsDefinitionDrift()
    {
        using var fixture = new TaskManagerFixture();
        var backupXml = EnabledTaskXml(enabled: true);
        fixture.Runner.Results.Enqueue(new SchtasksProcessResult(0, backupXml, string.Empty));
        var backup = await fixture.Manager.BackupCurrentAsync(CancellationToken.None);
        fixture.Runner.Results.Enqueue(new SchtasksProcessResult(0, backupXml, string.Empty));
        fixture.Runner.Results.Enqueue(new SchtasksProcessResult(
            0,
            "<?xml version=\"1.0\"?><Task><Settings><Enabled>false</Enabled></Settings>" +
            "<Actions><Exec><Command>unexpected.exe</Command></Exec></Actions></Task>",
            string.Empty));

        var exact = await fixture.Manager.InspectCurrentAgainstBackupAsync(backup, CancellationToken.None);
        var drifted = await fixture.Manager.InspectCurrentAgainstBackupAsync(backup, CancellationToken.None);

        Assert.True(exact.Enabled);
        Assert.True(exact.ExactBackupXmlMatch);
        Assert.True(exact.DefinitionMatchesBackup);
        Assert.False(drifted.Enabled);
        Assert.False(drifted.ExactBackupXmlMatch);
        Assert.False(drifted.DefinitionMatchesBackup);
        Assert.DoesNotContain(fixture.Runner.Calls,
            static call => call.Kind == SchtasksCommandKind.Mutation);
    }

    [Fact]
    public async Task RecoveryInspection_BackupMismatchFailsBeforeAnyTaskQuery()
    {
        using var fixture = new TaskManagerFixture();
        fixture.Runner.Results.Enqueue(new SchtasksProcessResult(
            0,
            EnabledTaskXml(enabled: true),
            string.Empty));
        var backup = await fixture.Manager.BackupCurrentAsync(CancellationToken.None);
        var callsBeforeInspection = fixture.Runner.Calls.Count;

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            fixture.Manager.InspectCurrentAgainstBackupAsync(
                backup with { XmlSha256 = new string('F', 64) },
                CancellationToken.None));

        Assert.Equal(callsBeforeInspection, fixture.Runner.Calls.Count);
    }

    [Fact]
    public async Task WindowsRunner_PreCancelledMutationDoesNotReachProcessStart()
    {
        var runner = new WindowsSchtasksProcessRunner();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            runner.RunAsync(
                ["/Change", "/TN", ServiceConstants.MythCoolTaskName, "/Disable"],
                SchtasksCommandKind.Mutation,
                cancellation.Token));
    }

    private static string EnabledTaskXml(bool enabled) =>
        $"<?xml version=\"1.0\"?><Task><Settings><Enabled>{enabled.ToString().ToLowerInvariant()}</Enabled></Settings></Task>";

    private sealed class TaskManagerFixture : IDisposable
    {
        private readonly string directory =
            Path.Combine(Path.GetTempPath(), $"v12-task-manager-tests-{Guid.NewGuid():N}");

        public TaskManagerFixture()
        {
            Paths = new ServicePaths(directory);
            Paths.EnsureDirectories();
            Runner = new FakeSchtasksProcessRunner();
            Manager = new MythCoolScheduledTaskManager(
                Paths,
                TimeProvider.System,
                Runner,
                NullLogger<MythCoolScheduledTaskManager>.Instance);
        }

        public ServicePaths Paths { get; }

        public FakeSchtasksProcessRunner Runner { get; }

        public MythCoolScheduledTaskManager Manager { get; }

        public void Dispose()
        {
            for (var attempt = 0; attempt < 5 && Directory.Exists(directory); attempt++)
            {
                try
                {
                    Directory.Delete(directory, recursive: true);
                }
                catch (IOException) when (attempt < 4)
                {
                    // Windows can briefly retain a just-closed async file handle while the
                    // full parallel suite is under I/O pressure. Retry only the fixture's
                    // unique temp directory; a persistent leak still fails the test.
                    Thread.Sleep(TimeSpan.FromMilliseconds(25 * (attempt + 1)));
                }
            }
        }
    }

    private sealed class FakeSchtasksProcessRunner : ISchtasksProcessRunner
    {
        public List<SchtasksCall> Calls { get; } = [];

        public Queue<SchtasksProcessResult> Results { get; } = new();

        public Queue<Exception> Failures { get; } = new();

        public Func<IReadOnlyList<string>, SchtasksCommandKind, CancellationToken,
            Task<SchtasksProcessResult>>? Handler
        { get; set; }

        public Task<SchtasksProcessResult> RunAsync(
            IReadOnlyList<string> arguments,
            SchtasksCommandKind kind,
            CancellationToken cancellationToken)
        {
            Calls.Add(new SchtasksCall(arguments.ToArray(), kind, cancellationToken));
            if (Handler is not null)
            {
                return Handler(arguments, kind, cancellationToken);
            }

            if (Failures.TryDequeue(out var failure))
            {
                return Task.FromException<SchtasksProcessResult>(failure);
            }

            return Task.FromResult(Results.Dequeue());
        }
    }

    private sealed record SchtasksCall(
        IReadOnlyList<string> Arguments,
        SchtasksCommandKind Kind,
        CancellationToken CancellationToken);
}
