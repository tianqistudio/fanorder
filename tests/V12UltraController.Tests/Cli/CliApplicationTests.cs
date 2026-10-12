using System.IO.Compression;
using System.Text;
using System.Text.Json;
using V12UltraController.Cli;
using V12UltraController.Core.Diagnostics;
using V12UltraController.Core.Ipc;

namespace V12UltraController.Tests.Cli;

public sealed class CliApplicationTests
{
    [Fact]
    public async Task RunAsync_SuccessMapsCommandToVersionedCorrelatedRequest()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var client = new FakeApiClient((request, timeout, _) =>
        {
            Assert.Equal(PipeProtocol.ApiVersion, request.ApiVersion);
            Assert.True(Guid.TryParseExact(request.RequestId, "D", out var requestId));
            Assert.NotEqual(Guid.Empty, requestId);
            Assert.Equal(PipeOperations.SpeedSet, request.Operation);
            Assert.Equal(55, request.Payload.GetProperty("percent").GetInt32());
            Assert.Equal(TimeSpan.FromSeconds(15), timeout);
            return Task.FromResult(PipeResponse.Success(request.RequestId, new
            {
                mode = "manual",
                requestedPercent = 55,
            }));
        });
        var application = CreateApplication(output, error, client);

        var exitCode = await application.RunAsync(
            ["speed", "55"],
            TestContext.Current.CancellationToken);

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Single(client.Requests);
        Assert.Contains("\"mode\": \"manual\"", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("\"requestedPercent\": 55", output.ToString(), StringComparison.Ordinal);
        Assert.Empty(error.ToString());
    }

    [Fact]
    public async Task RunAsync_AttendedHardwareOperationUsesExtendedTimeout()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var client = new FakeApiClient((request, timeout, _) =>
        {
            Assert.Equal(PipeOperations.ShutdownQualify, request.Operation);
            Assert.True(request.Payload.GetProperty("attended").GetBoolean());
            Assert.Equal(TimeSpan.FromSeconds(60), timeout);
            return Task.FromResult(PipeResponse.Success(request.RequestId, new
            {
                pending = true,
                qualificationId = Guid.NewGuid(),
            }));
        });

        var exitCode = await CreateApplication(output, error, client).RunAsync(
            ["shutdown", "qualify", "--attended"],
            TestContext.Current.CancellationToken);

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Single(client.Requests);
        Assert.Empty(error.ToString());
    }

    [Theory]
    [InlineData(2, false)]
    [InlineData(1, true)]
    public async Task RunAsync_RejectsMismatchedResponseVersionOrRequestId(
        int responseVersion,
        bool replaceRequestId)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var client = new FakeApiClient((request, _, _) => Task.FromResult(new PipeResponse(
            responseVersion,
            replaceRequestId ? Guid.NewGuid().ToString("D") : request.RequestId,
            true,
            JsonSerializer.SerializeToElement(new { serviceState = "Running" }),
            null,
            DateTimeOffset.UtcNow)));

        var exitCode = await CreateApplication(output, error, client).RunAsync(
            ["status"],
            TestContext.Current.CancellationToken);

        Assert.Equal(CliExitCodes.ServiceUnavailable, exitCode);
        Assert.Empty(output.ToString());
        Assert.Contains("response version or requestId did not match", error.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_TransportFailureReturnsServiceUnavailable()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var client = new FakeApiClient((_, _, _) =>
            Task.FromException<PipeResponse>(new IOException("injected pipe failure")));

        var exitCode = await CreateApplication(output, error, client).RunAsync(
            ["status"],
            TestContext.Current.CancellationToken);

        Assert.Equal(CliExitCodes.ServiceUnavailable, exitCode);
        Assert.Empty(output.ToString());
        Assert.Contains("SERVICE_UNAVAILABLE: injected pipe failure", error.ToString(),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(PipeErrorCodes.InvalidArgument, CliExitCodes.InvalidArguments)]
    [InlineData(PipeErrorCodes.ServiceUnavailable, CliExitCodes.ServiceUnavailable)]
    [InlineData(PipeErrorCodes.InternalError, CliExitCodes.ServiceUnavailable)]
    [InlineData(PipeErrorCodes.NotCommissioned, CliExitCodes.DeviceUnavailable)]
    [InlineData(PipeErrorCodes.DeviceNotFound, CliExitCodes.DeviceUnavailable)]
    [InlineData(PipeErrorCodes.UnsupportedDevice, CliExitCodes.DeviceUnavailable)]
    [InlineData(PipeErrorCodes.TelemetryStale, CliExitCodes.FailSafeActive)]
    [InlineData(PipeErrorCodes.FailSafeActive, CliExitCodes.FailSafeActive)]
    [InlineData(PipeErrorCodes.MythCoolConflict, CliExitCodes.MythCoolConflict)]
    [InlineData(PipeErrorCodes.HidTimeout, CliExitCodes.HidOrProtocolError)]
    [InlineData(PipeErrorCodes.ProtocolChecksum, CliExitCodes.HidOrProtocolError)]
    public async Task RunAsync_MapsServiceErrorsToStableExitCodes(string errorCode, int expectedExitCode)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var client = new FakeApiClient((request, _, _) => Task.FromResult(
            PipeResponse.Failure(request.RequestId, errorCode, "stable test message")));

        var exitCode = await CreateApplication(output, error, client).RunAsync(
            ["status"],
            TestContext.Current.CancellationToken);

        Assert.Equal(expectedExitCode, exitCode);
        Assert.Empty(output.ToString());
        Assert.StartsWith($"{errorCode}: stable test message", error.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_JsonOutputWritesOnlyResponseData()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var client = SuccessfulClient(new
        {
            serviceState = "Running",
            commissioned = true,
            fan = new { requestedPercent = 68 },
        });

        var exitCode = await CreateApplication(output, error, client).RunAsync(
            ["status", "--json"],
            TestContext.Current.CancellationToken);

        Assert.Equal(CliExitCodes.Success, exitCode);
        using var document = JsonDocument.Parse(output.ToString());
        Assert.Equal("Running", document.RootElement.GetProperty("serviceState").GetString());
        Assert.True(document.RootElement.GetProperty("commissioned").GetBoolean());
        Assert.Equal(68,
            document.RootElement.GetProperty("fan").GetProperty("requestedPercent").GetInt32());
        Assert.False(document.RootElement.TryGetProperty("apiVersion", out _));
        Assert.Empty(error.ToString());
    }

    [Fact]
    public async Task RunAsync_HumanStatusWritesReadableSummary()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var client = SuccessfulClient(new
        {
            serviceState = "FailSafe",
            commissioned = true,
            owner = "configured-user",
            device = new
            {
                connected = false,
                vendorId = "374A",
                productId = "B101",
            },
            fan = new
            {
                mode = "Auto",
                requestedPercent = 100,
                configuredManualPercent = 50,
            },
            thermal = new { healthy = false },
            failSafe = new { active = true, reason = "TelemetryStale" },
        });

        var exitCode = await CreateApplication(output, error, client).RunAsync(
            ["status"],
            TestContext.Current.CancellationToken);

        var rendered = output.ToString();
        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Contains("Service:       FailSafe", rendered, StringComparison.Ordinal);
        Assert.Contains("Commissioned:  True", rendered, StringComparison.Ordinal);
        Assert.Contains("Device:        not connected", rendered, StringComparison.Ordinal);
        Assert.Contains("Fail-safe:     ACTIVE (TelemetryStale)", rendered, StringComparison.Ordinal);
        Assert.Contains("official signed PawnIO", rendered, StringComparison.Ordinal);
        Assert.Contains("while attended", rendered, StringComparison.Ordinal);
        Assert.Contains("last sample attempt", rendered, StringComparison.Ordinal);
        Assert.Contains("does not establish the root cause", rendered, StringComparison.Ordinal);
        Assert.Empty(error.ToString());
    }

    [Fact]
    public async Task RunAsync_HealthyTemperatureDoesNotShowPrerequisiteWarning()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var client = SuccessfulClient(new { thermal = new { healthy = true, cpuCelsius = 55 } });
        var exitCode = await CreateApplication(output, error, client).RunAsync(
            ["status"], TestContext.Current.CancellationToken);
        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.DoesNotContain("Temperature help:", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_HumanStatusMakesPendingSafetyOperationAuditVisible()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var client = SuccessfulClient(new
        {
            serviceState = "FailSafe",
            commissioned = true,
            owner = "configured-user",
            safetyOperationAudit = new
            {
                blocked = true,
                pending = true,
                transactionId = "11111111-2222-3333-4444-555555555555",
                eventName = "shutdown.qualify",
                kind = "HidWithOn100Recovery",
                stage = "EffectInProgress",
                outcome = "Undetermined",
                requiresOn100Recovery = true,
                reason = "Startup recovery has not completed.",
            },
        });

        var exitCode = await CreateApplication(output, error, client).RunAsync(
            ["status"],
            TestContext.Current.CancellationToken);

        var rendered = output.ToString();
        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Contains("Safety audit:  BLOCKED", rendered, StringComparison.Ordinal);
        Assert.Contains("shutdown.qualify / HidWithOn100Recovery / EffectInProgress / Undetermined", rendered,
            StringComparison.Ordinal);
        Assert.Contains("A verified ON/100% hardware recovery is required", rendered, StringComparison.Ordinal);
        Assert.Contains("Startup recovery has not completed", rendered, StringComparison.Ordinal);
        Assert.Empty(error.ToString());
    }

    [Fact]
    public async Task RunAsync_DiagnosticsExportCreatesValidArchive()
    {
        using var directory = new TemporaryDirectory();
        var destination = Path.Combine(directory.Path, "diagnostics.zip");
        using var output = new StringWriter();
        using var error = new StringWriter();
        var client = SuccessfulClient(new
        {
            serviceState = "FailSafe",
            reason = "offline fake",
        });

        var exitCode = await CreateApplication(output, error, client).RunAsync(
            ["diagnostics", "export", destination],
            TestContext.Current.CancellationToken);

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.True(File.Exists(destination));
        using var archive = ZipFile.OpenRead(destination);
        var entry = Assert.Single(archive.Entries);
        Assert.Equal("diagnostics.json", entry.FullName);
        using var entryReader = new StreamReader(entry.Open(), Encoding.UTF8);
        using var document = JsonDocument.Parse(await entryReader.ReadToEndAsync(
            TestContext.Current.CancellationToken));
        Assert.Equal("FailSafe", document.RootElement.GetProperty("serviceState").GetString());
        Assert.Contains(destination, output.ToString(), StringComparison.Ordinal);
        Assert.Empty(error.ToString());
        AssertNoTemporaryFiles(directory.Path);
    }

    [Fact]
    public async Task RunAsync_DiagnosticsExportNeverOverwritesExistingDestinationOrCallsService()
    {
        using var directory = new TemporaryDirectory();
        var destination = Path.Combine(directory.Path, "diagnostics.zip");
        var original = "existing evidence"u8.ToArray();
        await File.WriteAllBytesAsync(
            destination,
            original,
            TestContext.Current.CancellationToken);
        using var output = new StringWriter();
        using var error = new StringWriter();
        var client = new FakeApiClient((_, _, _) => throw new InvalidOperationException(
            "The transport must not be called for an existing destination."));

        var exitCode = await CreateApplication(output, error, client).RunAsync(
            ["diagnostics", "export", destination],
            TestContext.Current.CancellationToken);

        Assert.Equal(CliExitCodes.InvalidArguments, exitCode);
        Assert.Empty(client.Requests);
        Assert.Equal(
            original,
            await File.ReadAllBytesAsync(destination, TestContext.Current.CancellationToken));
        Assert.Contains("output already exists", error.ToString(), StringComparison.Ordinal);
        AssertNoTemporaryFiles(directory.Path);
    }

    [Fact]
    public async Task RunAsync_DiagnosticsPayloadFailureRemovesPartialArchive()
    {
        using var directory = new TemporaryDirectory();
        var destination = Path.Combine(directory.Path, "diagnostics.zip");
        using var output = new StringWriter();
        using var error = new StringWriter();
        var writer = new DiagnosticsArchiveWriter(
            new IOExceptionAfterPartialPayloadWriter(),
            FileSystemDiagnosticsArchiveCommitter.Instance);

        var exitCode = await new CliApplication(
                output,
                error,
                SuccessfulClient(new { serviceState = "test" }),
                writer)
            .RunAsync(
                ["diagnostics", "export", destination],
                TestContext.Current.CancellationToken);

        Assert.Equal(CliExitCodes.InvalidArguments, exitCode);
        Assert.False(File.Exists(destination));
        Assert.Empty(output.ToString());
        Assert.Contains("Cannot write diagnostics", error.ToString(), StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory.Path));
    }

    [Fact]
    public async Task RunAsync_DiagnosticsCommitRacePreservesOtherWritersDestinationAndCleansTemporaryFile()
    {
        using var directory = new TemporaryDirectory();
        var destination = Path.Combine(directory.Path, "diagnostics.zip");
        var racingContents = "created concurrently"u8.ToArray();
        using var output = new StringWriter();
        using var error = new StringWriter();
        var writer = new DiagnosticsArchiveWriter(
            JsonDiagnosticsArchivePayloadWriter.Instance,
            new RacingDestinationCommitter(racingContents));

        var exitCode = await new CliApplication(
                output,
                error,
                SuccessfulClient(new { serviceState = "test" }),
                writer)
            .RunAsync(
                ["diagnostics", "export", destination],
                TestContext.Current.CancellationToken);

        Assert.Equal(CliExitCodes.InvalidArguments, exitCode);
        Assert.Equal(
            racingContents,
            await File.ReadAllBytesAsync(destination, TestContext.Current.CancellationToken));
        Assert.Contains("output already exists", error.ToString(), StringComparison.Ordinal);
        AssertNoTemporaryFiles(directory.Path);
    }

    [Theory]
    [InlineData("auto", false, "enabled by policy")]
    [InlineData("Manual", false, "enabled by policy")]
    [InlineData("auto", true, "disabled; OFF will not auto-resume")]
    [InlineData("manual", true, "disabled; OFF will not auto-resume")]
    [InlineData("pureManual", false, "disabled; OFF will not auto-resume")]
    [InlineData("Released", false, "disabled; OFF will not auto-resume")]
    [InlineData("pureManual", null, "disabled; OFF will not auto-resume")]
    [InlineData("manual", null, "unknown; do not assume OFF will auto-resume")]
    [InlineData("futureMode", false, "unknown; do not assume OFF will auto-resume")]
    [InlineData(null, null, "unknown; do not assume OFF will auto-resume")]
    public async Task RunAsync_HumanStatusReportsModeDependentRecoveryWithoutInventingMissingProtection(
        string? mode, bool? disabled, string expected)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var client = SuccessfulClient(new
        {
            fan = new { mode, poweredOn = false },
            safetyProtection = disabled.HasValue ? new { disabled = disabled.Value } : null,
        });

        var exitCode = await CreateApplication(output, error, client).RunAsync(
            ["status"], TestContext.Current.CancellationToken);

        Assert.Equal(CliExitCodes.Success, exitCode);
        var policy = Assert.Single(output.ToString().Split(Environment.NewLine),
            line => line.StartsWith("Safety policy:", StringComparison.Ordinal));
        Assert.Contains(expected, policy, StringComparison.Ordinal);
        Assert.Single(client.Requests);
        Assert.Empty(error.ToString());
    }

    [Fact]
    public async Task RunAsync_ConfirmedOffKeepsJsonOutputAndExplainsConditionalRecovery()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var client = SuccessfulClient(new { poweredOn = false, readbackConfirmed = true });

        var exitCode = await CreateApplication(output, error, client).RunAsync(
            ["power", "off", "--confirm"], TestContext.Current.CancellationToken);

        Assert.Equal(CliExitCodes.Success, exitCode);
        using var document = JsonDocument.Parse(output.ToString());
        Assert.False(document.RootElement.GetProperty("poweredOn").GetBoolean());
        Assert.True(document.RootElement.GetProperty("readbackConfirmed").GetBoolean());
        Assert.Contains("puremanual/released", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("OFF will not auto-resume", error.ToString(), StringComparison.Ordinal);
        var request = Assert.Single(client.Requests);
        Assert.Equal(PipeOperations.PowerSet, request.Operation);
        Assert.True(request.Payload.GetProperty("confirmed").GetBoolean());
    }

    private static CliApplication CreateApplication(
        TextWriter output,
        TextWriter error,
        IPipeApiClient client) =>
        new(output, error, client, new DiagnosticsArchiveWriter());

    private static FakeApiClient SuccessfulClient(object data) => new(
        (request, _, _) => Task.FromResult(PipeResponse.Success(request.RequestId, data)));

    private static void AssertNoTemporaryFiles(string directory) =>
        Assert.DoesNotContain(
            Directory.EnumerateFiles(directory),
            path => Path.GetFileName(path).StartsWith(".", StringComparison.Ordinal) &&
                    path.EndsWith(".tmp", StringComparison.Ordinal));

    private sealed class FakeApiClient(
        Func<PipeRequest, TimeSpan, CancellationToken, Task<PipeResponse>> handler)
        : IPipeApiClient
    {
        public List<PipeRequest> Requests { get; } = [];

        public Task<PipeResponse> SendAsync(
            PipeRequest request,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return handler(request, timeout, cancellationToken);
        }
    }

    private sealed class IOExceptionAfterPartialPayloadWriter : IDiagnosticsArchivePayloadWriter
    {
        public async Task WriteAsync(
            Stream destination,
            JsonElement diagnostics,
            JsonSerializerOptions serializerOptions,
            CancellationToken cancellationToken)
        {
            await destination.WriteAsync("partial"u8.ToArray(), cancellationToken);
            throw new IOException("injected diagnostics payload failure");
        }
    }

    private sealed class RacingDestinationCommitter(byte[] destinationContents)
        : IDiagnosticsArchiveCommitter
    {
        public void CommitNew(string temporaryPath, string destinationPath)
        {
            File.WriteAllBytes(destinationPath, destinationContents);
            File.Move(temporaryPath, destinationPath, overwrite: false);
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"v12-cli-application-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
