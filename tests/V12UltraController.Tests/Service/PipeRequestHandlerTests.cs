using System.Collections.Concurrent;
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using V12UltraController.Core.Configuration;
using V12UltraController.Core.Control;
using V12UltraController.Core.Hardware;
using V12UltraController.Core.Ipc;
using V12UltraController.Core.Status;
using V12UltraController.Service;

namespace V12UltraController.Tests.Service;

public sealed class PipeRequestHandlerTests
{
    [Theory]
    [InlineData(PipeOperations.SafetyDisable, "null")]
    [InlineData(PipeOperations.SafetyDisable, "[]")]
    [InlineData(PipeOperations.SafetyDisable, "{\"confirmed\":true}")]
    [InlineData(PipeOperations.SafetyDisable, "{\"raw\":1}")]
    [InlineData(PipeOperations.SafetyResume, "{}")]
    [InlineData(PipeOperations.SafetyResume, "{\"confirmed\":false}")]
    [InlineData(PipeOperations.SafetyResume, "{\"confirmed\":null}")]
    [InlineData(PipeOperations.SafetyResume, "{\"confirmed\":1}")]
    [InlineData(PipeOperations.SafetyResume, "{\"confirmed\":\"true\"}")]
    [InlineData(PipeOperations.SafetyResume, "{\"Confirmed\":true}")]
    [InlineData(PipeOperations.SafetyResume, "{\"confirmed\":true,\"confirmed\":true}")]
    [InlineData(PipeOperations.SafetyResume, "{\"confirmed\":true,\"force\":true}")]
    public async Task SafetyProtectionStrictPayloadNeverDispatchesMalformedRequests(string operation, string json)
    {
        await using var fixture = new HandlerFixture();
        using var document = JsonDocument.Parse(json);
        var response = await fixture.Handler.HandleAsync(new(PipeProtocol.ApiVersion,
            Guid.NewGuid().ToString("D"), operation, document.RootElement.Clone()),
            UserPeer, TestContext.Current.CancellationToken);
        Assert.False(response.Ok);
        Assert.Equal(PipeErrorCodes.InvalidArgument, response.Error?.Code);
        Assert.Empty(fixture.Controller.Calls);
    }

    [Fact]
    public async Task SafetyProtectionDispatchPreservesPeerConfirmationAndCurrentStatus()
    {
        await using var fixture = new HandlerFixture();
        var disable = PipeRequest.Create(PipeOperations.SafetyDisable, new { });
        var disabled = await fixture.Handler.HandleAsync(disable, UserPeer, TestContext.Current.CancellationToken);
        Assert.True(disabled.Ok);
        Assert.Equal(disable.RequestId, disabled.RequestId);
        Assert.True(disabled.Data!.Value.GetProperty("safetyProtection").GetProperty("disabled").GetBoolean());
        var resume = PipeRequest.Create(PipeOperations.SafetyResume, new { confirmed = true });
        var resumed = await fixture.Handler.HandleAsync(resume, UserPeer, TestContext.Current.CancellationToken);
        Assert.True(resumed.Ok);
        Assert.Equal(resume.RequestId, resumed.RequestId);
        Assert.False(resumed.Data!.Value.GetProperty("safetyProtection").GetProperty("disabled").GetBoolean());
        Assert.Collection(fixture.Controller.Calls,
            call => { Assert.Equal(PipeOperations.SafetyDisable, call.Operation); Assert.Equal(UserPeer, call.Peer); },
            call => { Assert.Equal(PipeOperations.SafetyResume, call.Operation); Assert.Equal(true, call.Value); Assert.Equal(UserPeer, call.Peer); });
    }

    [Fact]
    public async Task SafetyDisableDispatchDoesNotWaitForPreOperationAudit()
    {
        await using var fixture = new HandlerFixture();
        Directory.CreateDirectory(fixture.Paths.DataDirectory);
        // A file occupying the audit directory makes any attempted audit write fail.
        await File.WriteAllTextAsync(fixture.Paths.LogsDirectory, "blocked", TestContext.Current.CancellationToken);
        var response = await fixture.Handler.HandleAsync(PipeRequest.Create(PipeOperations.SafetyDisable, new { }),
            UserPeer, TestContext.Current.CancellationToken);
        Assert.True(response.Ok);
        Assert.Equal(PipeOperations.SafetyDisable, Assert.Single(fixture.Controller.Calls).Operation);
    }

    [Theory]
    [InlineData(PipeOperations.SafetyDisable)]
    [InlineData(PipeOperations.SafetyResume)]
    public async Task SafetyProtectionBackendRejectionDoesNotReturnSuccess(string operation)
    {
        await using var fixture = new HandlerFixture();
        fixture.Controller.FailureOperation = operation;
        fixture.Controller.Failure = new ControllerOperationException(PipeErrorCodes.InvalidArgument, "caller rejected");
        var request = operation == PipeOperations.SafetyDisable
            ? PipeRequest.Create(operation, new { })
            : PipeRequest.Create(operation, new { confirmed = true });
        var response = await fixture.Handler.HandleAsync(request, UserPeer, TestContext.Current.CancellationToken);
        Assert.False(response.Ok);
        Assert.Equal(PipeErrorCodes.InvalidArgument, response.Error?.Code);
        Assert.Equal("caller rejected", response.Error?.Message);
        Assert.Null(response.Data);
        Assert.Empty(fixture.Controller.Calls);
    }

    [Fact]
    public async Task SafetySuppressionReturnsExplicitFailureWithoutChangingClientCancellationContract()
    {
        await using var fixture = new HandlerFixture();
        fixture.Controller.FailureOperation = PipeOperations.SpeedSet;
        fixture.Controller.Failure = new ControllerCoordinator.SafetyOutputSuppressedException();
        var response = await fixture.Handler.HandleAsync(PipeRequest.Create(PipeOperations.SpeedSet, new { percent = 60 }),
            UserPeer, TestContext.Current.CancellationToken);
        Assert.False(response.Ok);
        Assert.Equal(PipeErrorCodes.InvalidArgument, response.Error?.Code);
        Assert.Equal(fixture.Controller.Failure.Message, response.Error?.Message);
        Assert.Null(response.Data);
        Assert.Empty(fixture.Controller.Calls);
    }

    [Fact]
    public async Task ProfileApplyRejectsInvalidNestedPayloadBeforeControllerDispatch()
    {
        await using var fixture = new HandlerFixture();
        var valid = PipeRequest.Create(PipeOperations.ProfileApply, new ProfileApplyRequest(
            new("id", "组合方案", ControlMode.Manual, 60, FanCurve.RemoteHostSafe, new LightingSettings(true, 1, 2, 220, 3))));
        Action<JsonObject>[] mutations =
        [
            root => root["unexpectedPath"] = "C:\\private",
            root => root["confirmResumeControl"] = "true",
            root => root["profile"] = null,
            root => root["profile"]!["mode"] = "puremanual",
            root => root["profile"]!["mode"] = "released",
            root => root["profile"]!["mode"] = 0,
            root => root["profile"]!["manualSpeedPercent"] = 9,
            root => root["profile"]!["manualSpeedPercent"] = 60.5,
            root => root["profile"]!["id"] = "",
            root => root["profile"]!["name"] = new string('x', 81),
            root => root["profile"]!["poweredOn"] = false,
            root => root["profile"]!["lighting"]!["mode"] = 4,
            root => root["profile"]!["lighting"]!["raw"] = 1,
            root => root["profile"]!["curve"]!["points"] = new JsonArray(),
        ];
        foreach (var mutate in mutations)
        {
            var json = JsonNode.Parse(valid.Payload.GetRawText())!.AsObject();
            mutate(json);
            using var document = JsonDocument.Parse(json.ToJsonString());
            var response = await fixture.Handler.HandleAsync(valid with { Payload = document.RootElement.Clone() },
                UserPeer, TestContext.Current.CancellationToken);
            Assert.False(response.Ok);
            Assert.Equal(PipeErrorCodes.InvalidArgument, response.Error?.Code);
        }
        using var duplicate = JsonDocument.Parse(valid.Payload.GetRawText().Replace("\"id\":\"id\"", "\"id\":\"id\",\"id\":\"second\"", StringComparison.Ordinal));
        var duplicated = await fixture.Handler.HandleAsync(valid with { Payload = duplicate.RootElement.Clone() },
            UserPeer, TestContext.Current.CancellationToken);
        Assert.False(duplicated.Ok);
        Assert.Empty(fixture.Controller.Calls);
    }

    [Fact]
    public async Task ProfileApplyCarriesConfirmationAndSeparatelySerializedOutcomes()
    {
        await using var fixture = new HandlerFixture();
        var profile = new ControlProfileSnapshot("id", "组合方案", ControlMode.Auto, 100, FanCurve.RemoteHostSafe, null);
        var request = PipeRequest.Create(PipeOperations.ProfileApply, new ProfileApplyRequest(profile, true));
        var response = await fixture.Handler.HandleAsync(request, UserPeer, TestContext.Current.CancellationToken);
        Assert.True(response.Ok);
        Assert.Equal(request.RequestId, response.Data!.Value.GetProperty("application").GetProperty("operationId").GetString());
        Assert.Equal("completed", response.Data.Value.GetProperty("application").GetProperty("outcome").GetString());
        Assert.Equal("saved", response.Data.Value.GetProperty("application").GetProperty("configuration").GetString());
        Assert.Equal("notRequested", response.Data.Value.GetProperty("application").GetProperty("lighting").GetString());
        var call = Assert.Single(fixture.Controller.Calls);
        Assert.True(Assert.IsType<ProfileApplyRequest>(call.Value).ConfirmResumeControl);
    }

    [Fact]
    public async Task HistoryAnalysisAndAdviceApplyUseStrictPayloads()
    {
        await using var fixture = new HandlerFixture();
        var analysis = await fixture.Handler.HandleAsync(PipeRequest.Create(PipeOperations.HistoryAnalyze, new { }), UserPeer, CancellationToken.None);
        Assert.True(analysis.Ok);
        var apply = await fixture.Handler.HandleAsync(PipeRequest.Create(PipeOperations.CurveAdviceApply,
            new { expected = FanCurve.RemoteHostSafe, proposed = FanCurve.RemoteHostSafe }), UserPeer, CancellationToken.None);
        Assert.True(apply.Ok);
        foreach (var request in new[] {
            PipeRequest.Create(PipeOperations.HistoryAnalyze, new { path = "C:\\private" }),
            PipeRequest.Create(PipeOperations.CurveAdviceApply, new { expected = FanCurve.RemoteHostSafe }),
            PipeRequest.Create(PipeOperations.CurveAdviceApply, new { expected = FanCurve.RemoteHostSafe, proposed = FanCurve.RemoteHostSafe, mode = "manual" }) })
        {
            var rejected = await fixture.Handler.HandleAsync(request, UserPeer, CancellationToken.None);
            Assert.False(rejected.Ok);
            Assert.Equal(PipeErrorCodes.InvalidArgument, rejected.Error?.Code);
        }
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"enabled\":1,\"mode\":0,\"color\":0,\"brightness\":128,\"speed\":0}")]
    [InlineData("{\"enabled\":true,\"mode\":4,\"color\":0,\"brightness\":128,\"speed\":0}")]
    [InlineData("{\"enabled\":true,\"mode\":0,\"color\":0,\"brightness\":128.5,\"speed\":0}")]
    [InlineData("{\"enabled\":true,\"mode\":0,\"color\":0,\"brightness\":128,\"speed\":0,\"raw\":1}")]
    [InlineData("{\"enabled\":true,\"mode\":0,\"color\":0,\"brightness\":128,\"speed\":0,\"speed\":1}")]
    public async Task LightingMalformedPayloadNeverReachesController(string json)
    {
        await using var fixture = new HandlerFixture();
        using var document = JsonDocument.Parse(json);
        var response = await fixture.Handler.HandleAsync(new PipeRequest(PipeProtocol.ApiVersion,
            Guid.NewGuid().ToString("D"), PipeOperations.LightingSet, document.RootElement.Clone()),
            UserPeer, CancellationToken.None);
        Assert.False(response.Ok);
        Assert.Equal(PipeErrorCodes.InvalidArgument, response.Error?.Code);
        Assert.Empty(fixture.Controller.Calls);
    }

    [Fact]
    public async Task LightingValidPayloadDispatches()
    {
        await using var fixture = new HandlerFixture();
        var response = await fixture.Handler.HandleAsync(PipeRequest.Create(PipeOperations.LightingSet,
            new LightingSettings(true,3,4,255,2)),UserPeer,CancellationToken.None);
        Assert.True(response.Ok);
        Assert.Single(fixture.Controller.Calls);
    }
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"enabled\":null}")]
    [InlineData("{\"enabled\":1}")]
    [InlineData("{\"enabled\":\"true\"}")]
    [InlineData("{\"enabled\":true,\"shutdown\":true}")]
    [InlineData("{\"enabled\":true,\"enabled\":false}")]
    [InlineData("[]")]
    public async Task StartupMalformedPayloadNeverReachesController(string json)
    {
        await using var fixture = new HandlerFixture();
        using var document = JsonDocument.Parse(json);
        var request = new PipeRequest(PipeProtocol.ApiVersion, Guid.NewGuid().ToString("D"),
            PipeOperations.StartupSet, document.RootElement.Clone());
        var response = await fixture.Handler.HandleAsync(request, UserPeer, TestContext.Current.CancellationToken);
        Assert.False(response.Ok);
        Assert.Equal(PipeErrorCodes.InvalidArgument, response.Error?.Code);
        Assert.Empty(fixture.Controller.Calls);
    }
    [Fact]
    public async Task ManualPower_MalformedPayloadsAreRejectedBeforeDispatch()
    {
        await using var fixture = new HandlerFixture();
        var requests = new[]
        {
            PipeRequest.Create(PipeOperations.PowerSet, new { poweredOn = false }),
            PipeRequest.Create(PipeOperations.PowerSet, new { poweredOn = "false", confirmed = true }),
            PipeRequest.Create(PipeOperations.PowerSet, new { poweredOn = false, confirmed = 1 }),
            PipeRequest.Create(PipeOperations.PowerSet, new { poweredOn = false, confirmed = true, force = true }),
        };
        foreach (var request in requests)
        {
            var response = await fixture.Handler.HandleAsync(request, UserPeer, TestContext.Current.CancellationToken);
            Assert.False(response.Ok);
            Assert.Equal(PipeErrorCodes.InvalidArgument, response.Error?.Code);
        }
    }
    [Fact]
    public async Task NearFullTest_ExplicitFlagDispatchesButUnknownOrWrongTypePayloadIsRejected()
    {
        await using var fixture = new HandlerFixture();
        var sessionId = Guid.NewGuid();
        var response = await fixture.Handler.HandleAsync(
            PipeRequest.Create(PipeOperations.CommissionTest, new { attended = true, sessionId, nearFullOnly = true }),
            UserPeer, TestContext.Current.CancellationToken);
        Assert.True(response.Ok);
        Assert.True(response.Data!.Value.GetProperty("nearFullOnly").GetBoolean());
        var invalidRequests = new[]
        {
            PipeRequest.Create(PipeOperations.CommissionTest, new { attended = true, sessionId, nearFullOnly = "true" }),
            PipeRequest.Create(PipeOperations.CommissionTest, new { attended = true, sessionId, nearFullOnly = true, percent = 20 }),
            PipeRequest.Create(PipeOperations.CommissionCommit, new { attended = true, sessionId, nearFullOnly = true }),
        };
        foreach (var request in invalidRequests)
        {
            var rejected = await fixture.Handler.HandleAsync(request, UserPeer, TestContext.Current.CancellationToken);
            Assert.False(rejected.Ok);
            Assert.Equal(PipeErrorCodes.InvalidArgument, rejected.Error?.Code);
        }
    }
    private static readonly PipePeerIdentity UserPeer =
        new("S-1-5-21-111-222-333-1001", "v12ctl");

    private static readonly PipePeerIdentity SystemPeer =
        new("S-1-5-18", "msiexec");

    [Fact]
    public async Task EveryOperation_DispatchesTypedPayloadAndReturnsCorrelatedSuccess()
    {
        await using var fixture = new HandlerFixture();
        var sessionId = Guid.NewGuid();
        var qualificationId = Guid.NewGuid();
        var curve = new FanCurve([new(20, 30), new(90, 100)]);

        var requests = new[]
        {
            PipeRequest.Create(PipeOperations.StatusGet, new { }),
            PipeRequest.Create(PipeOperations.SafetyDisable, new { }),
            PipeRequest.Create(PipeOperations.SafetyResume, new { confirmed = true }),
            PipeRequest.Create(PipeOperations.LightingGet, new { }),
            PipeRequest.Create(PipeOperations.LightingSet, new LightingSettings(true,1,2,220,3)),
            PipeRequest.Create(PipeOperations.StartupGet, new { }),
            PipeRequest.Create(PipeOperations.StartupSet, new { enabled = true }),
            PipeRequest.Create(PipeOperations.StartupFanGet, new { }),
            PipeRequest.Create(PipeOperations.StartupFanSet, new { percent = 50 }),
            PipeRequest.Create(PipeOperations.ModeSet, new { mode = "auto" }),
            PipeRequest.Create(PipeOperations.ModeSet, new { mode = "manual" }),
            PipeRequest.Create(PipeOperations.SpeedSet, new { percent = 10 }),
            PipeRequest.Create(PipeOperations.SpeedSet, new { percent = 100 }),
            PipeRequest.Create(PipeOperations.PowerSet, new { poweredOn = true, confirmed = true }),
            PipeRequest.Create(PipeOperations.PowerSet, new { poweredOn = false, confirmed = true }),
            PipeRequest.Create(PipeOperations.CurveGet, new { }),
            PipeRequest.Create(PipeOperations.CurveSet, new { points = curve.Points }),
            PipeRequest.Create(PipeOperations.HistoryAnalyze, new { }),
            PipeRequest.Create(PipeOperations.CurveAdviceApply, new { expected = curve, proposed = curve }),
            PipeRequest.Create(PipeOperations.ProfileApply, new ProfileApplyRequest(
                new("test-profile", "测试方案", ControlMode.Manual, 60, curve, new LightingSettings(true, 1, 2, 220, 3)))),
            PipeRequest.Create(PipeOperations.DiagnosticsExport, new { }),
            PipeRequest.Create(PipeOperations.ShutdownStatus, new { }),
            PipeRequest.Create(PipeOperations.ShutdownQualify, new { attended = true }),
            PipeRequest.Create(PipeOperations.ShutdownEnable, new { attended = true, qualificationId }),
            PipeRequest.Create(PipeOperations.ShutdownDisable, new { }),
            PipeRequest.Create(PipeOperations.CommissionProbe, new { attended = true }),
            PipeRequest.Create(PipeOperations.CommissionIdempotent, new { attended = true, sessionId }),
            PipeRequest.Create(PipeOperations.CommissionTest, new { attended = true, sessionId }),
            PipeRequest.Create(PipeOperations.CommissionCommit, new { attended = true, sessionId }),
            PipeRequest.Create(PipeOperations.CommissionAbort, new { sessionId }),
            PipeRequest.Create(PipeOperations.CommissionRollback,
                new { startMythCool = false, installer = false, attended = false }),
            PipeRequest.Create(PipeOperations.CommissionRecoverTaskTransition,
                new { attended = true }),
            PipeRequest.Create(PipeOperations.CommissionRecoverSafety, new { attended = true }),
        };

        var declaredOperations = typeof(PipeOperations)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(static field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(static field => (string)field.GetRawConstantValue()!)
            .OrderBy(static operation => operation)
            .ToArray();
        Assert.Equal(
            declaredOperations,
            requests.Select(static request => request.Operation).Distinct().OrderBy(static operation => operation));

        foreach (var request in requests)
        {
            var response = await fixture.Handler.HandleAsync(
                request,
                UserPeer,
                TestContext.Current.CancellationToken);

            Assert.True(response.Ok);
            Assert.Equal(PipeProtocol.ApiVersion, response.ApiVersion);
            Assert.Equal(request.RequestId, response.RequestId);
            Assert.NotNull(response.Data);
            Assert.Null(response.Error);
        }

        var installerRollback = PipeRequest.Create(
            PipeOperations.CommissionRollback,
            new { startMythCool = true, installer = true, attended = true });
        var installerResponse = await fixture.Handler.HandleAsync(
            installerRollback,
            SystemPeer,
            TestContext.Current.CancellationToken);

        Assert.True(installerResponse.Ok);
        Assert.Contains(fixture.Controller.Calls,
            static call => call.Operation == PipeOperations.ModeSet && Equals(call.Value, ControlMode.Auto));
        Assert.Contains(fixture.Controller.Calls,
            static call => call.Operation == PipeOperations.ModeSet && Equals(call.Value, ControlMode.Manual));
        Assert.Contains(fixture.Controller.Calls,
            static call => call.Operation == PipeOperations.SpeedSet && Equals(call.Value, (byte)10));
        Assert.Contains(fixture.Controller.Calls,
            static call => call.Operation == PipeOperations.SpeedSet && Equals(call.Value, (byte)100));
        Assert.Contains(fixture.Controller.Calls,
            call => call.Operation == PipeOperations.CurveSet &&
                    ((FanCurve)call.Value!).Points.SequenceEqual(curve.Points));
        Assert.Contains(fixture.Controller.Calls,
            call => call.Operation == PipeOperations.CommissionProbe && call.Attended == true);
        Assert.Contains(fixture.Controller.Calls,
            call => call.Operation == PipeOperations.CommissionIdempotent &&
                    Equals(call.Value, sessionId) && call.Attended == true);
        Assert.Contains(fixture.Controller.Calls,
            call => call.Operation == PipeOperations.CommissionTest &&
                    Equals(call.Value, sessionId) && call.Attended == true);
        Assert.Contains(fixture.Controller.Calls,
            call => call.Operation == PipeOperations.CommissionCommit &&
                    Equals(call.Value, sessionId) && call.Attended == true);
        Assert.Contains(fixture.Controller.Calls,
            call => call.Operation == PipeOperations.CommissionAbort && Equals(call.Value, sessionId));
        Assert.Contains(fixture.Controller.Calls,
            static call => call.Operation == PipeOperations.CommissionRollback &&
                           call.Value is RollbackArguments(false, false, false));
        Assert.Contains(fixture.Controller.Calls,
            static call => call.Operation == PipeOperations.CommissionRollback &&
                           call.Value is RollbackArguments(true, true, true) &&
                           call.Peer == SystemPeer);
        Assert.Contains(fixture.Controller.Calls,
            static call => call.Operation == PipeOperations.CommissionRecoverTaskTransition &&
                           call.Attended == true);
        Assert.Contains(fixture.Controller.Calls,
            static call => call.Operation == PipeOperations.ShutdownStatus);
        Assert.Contains(fixture.Controller.Calls,
            static call => call.Operation == PipeOperations.ShutdownQualify && call.Attended == true);
        Assert.Contains(fixture.Controller.Calls,
            call => call.Operation == PipeOperations.ShutdownEnable && call.Attended == true &&
                    Equals(call.Value, qualificationId));
        Assert.Contains(fixture.Controller.Calls,
            static call => call.Operation == PipeOperations.ShutdownDisable);
    }

    [Fact]
    public async Task EnvelopeValidation_RejectsVersionRequestIdOperationPayloadAndUnknownOperation()
    {
        await using var fixture = new HandlerFixture();
        var id = Guid.NewGuid().ToString("D");
        var objectPayload = JsonSerializer.SerializeToElement(new { });
        var requests = new[]
        {
            new PipeRequest(PipeProtocol.ApiVersion + 1, id, PipeOperations.StatusGet, objectPayload),
            new PipeRequest(PipeProtocol.ApiVersion, "not-a-uuid", PipeOperations.StatusGet, objectPayload),
            new PipeRequest(PipeProtocol.ApiVersion, id, " ", objectPayload),
            new PipeRequest(PipeProtocol.ApiVersion, id, PipeOperations.StatusGet, default),
            new PipeRequest(PipeProtocol.ApiVersion, id, PipeOperations.StatusGet,
                JsonSerializer.SerializeToElement((object?)null)),
            new PipeRequest(PipeProtocol.ApiVersion, id, PipeOperations.StatusGet,
                JsonSerializer.SerializeToElement(Array.Empty<object>())),
            new PipeRequest(PipeProtocol.ApiVersion, id, "future.operation", objectPayload),
        };

        foreach (var request in requests)
        {
            var response = await fixture.Handler.HandleAsync(
                request,
                UserPeer,
                TestContext.Current.CancellationToken);

            Assert.False(response.Ok);
            Assert.Equal(PipeErrorCodes.InvalidArgument, response.Error?.Code);
            Assert.Null(response.Data);
        }

        var blankRequestId = new PipeRequest(
            PipeProtocol.ApiVersion,
            string.Empty,
            PipeOperations.StatusGet,
            objectPayload);
        var blankResponse = await fixture.Handler.HandleAsync(
            blankRequestId,
            UserPeer,
            TestContext.Current.CancellationToken);
        Assert.Equal("unknown", blankResponse.RequestId);
        Assert.Empty(fixture.Controller.Calls);
    }

    [Fact]
    public async Task PayloadValidation_RejectsInvalidModeSpeedCurveSessionShapeAndInstallerCaller()
    {
        await using var fixture = new HandlerFixture();
        var invalidCurve = new[] { new FanCurvePoint(20, 30) };
        var invalidRequests = new[]
        {
            PipeRequest.Create(PipeOperations.ModeSet, new { mode = "turbo" }),
            PipeRequest.Create(PipeOperations.ModeSet, new { }),
            PipeRequest.Create(PipeOperations.SpeedSet, new { percent = 9 }),
            PipeRequest.Create(PipeOperations.SpeedSet, new { percent = 101 }),
            PipeRequest.Create(PipeOperations.SpeedSet, new { percent = "fast" }),
            PipeRequest.Create(PipeOperations.CurveSet, new { points = invalidCurve }),
            PipeRequest.Create(PipeOperations.CurveSet, new { }),
            PipeRequest.Create(PipeOperations.CommissionIdempotent, new { attended = true }),
            PipeRequest.Create(PipeOperations.CommissionTest, new { attended = true, sessionId = Guid.Empty }),
            PipeRequest.Create(PipeOperations.CommissionCommit, new { attended = true }),
            PipeRequest.Create(PipeOperations.CommissionAbort, new { attended = true }),
            PipeRequest.Create(PipeOperations.CommissionRollback,
                new { startMythCool = false, installer = true }),
            PipeRequest.Create(PipeOperations.CommissionRecoverTaskTransition,
                new { attended = false }),
            PipeRequest.Create(PipeOperations.ShutdownEnable,
                new { attended = false, qualificationId = Guid.NewGuid() }),
            PipeRequest.Create(PipeOperations.ShutdownEnable,
                new { attended = true, qualificationId = Guid.Empty }),
            PipeRequest.Create(PipeOperations.ShutdownEnable, new { attended = true }),
        };

        foreach (var request in invalidRequests)
        {
            var response = await fixture.Handler.HandleAsync(
                request,
                UserPeer,
                TestContext.Current.CancellationToken);

            Assert.False(response.Ok);
            Assert.Equal(PipeErrorCodes.InvalidArgument, response.Error?.Code);
        }

        Assert.DoesNotContain(fixture.Controller.Calls,
            static call => call.Operation == PipeOperations.CommissionRollback);
        Assert.DoesNotContain(fixture.Controller.Calls,
            static call => call.Operation == PipeOperations.CommissionRecoverTaskTransition &&
                           call.Attended == false);
    }

    [Theory]
    [InlineData(PipeOperations.StatusGet, "{\"unexpected\":true}")]
    [InlineData(PipeOperations.ModeSet, "{\"Mode\":\"auto\"}")]
    [InlineData(PipeOperations.SpeedSet, "{\"percent\":70,\"percent\":80}")]
    [InlineData(PipeOperations.CommissionProbe, "{\"attended\":false,\"attended\":true}")]
    [InlineData(PipeOperations.CommissionProbe, "{\"Attended\":true}")]
    [InlineData(PipeOperations.CommissionProbe, "{\"attended\":\"true\"}")]
    [InlineData(PipeOperations.CommissionAbort, "{\"sessionId\":\"00000000-0000-0000-0000-000000000001\",\"attended\":true}")]
    [InlineData(PipeOperations.CommissionRollback, "{\"startMythCool\":false,\"Installer\":true,\"attended\":true}")]
    [InlineData(PipeOperations.CommissionRollback, "{\"startMythCool\":false,\"installer\":true,\"attended\":true,\"future\":false}")]
    [InlineData(PipeOperations.CommissionRollback, "{\"startMythCool\":false,\"installer\":1,\"attended\":true}")]
    [InlineData(PipeOperations.ShutdownEnable, "{\"attended\":true,\"qualificationId\":\"00000000-0000-0000-0000-000000000001\",\"qualificationId\":\"00000000-0000-0000-0000-000000000002\"}")]
    [InlineData(PipeOperations.ShutdownEnable, "{\"attended\":true,\"QualificationId\":\"00000000-0000-0000-0000-000000000001\"}")]
    [InlineData(PipeOperations.ShutdownEnable, "{\"attended\":true,\"qualificationId\":null}")]
    [InlineData(PipeOperations.CurveSet, "{\"points\":[{\"temperatureCelsius\":20,\"fanPercent\":30,\"future\":0},{\"temperatureCelsius\":90,\"fanPercent\":100}]}")]
    public async Task PayloadShape_RejectsUnknownNonCanonicalDuplicateAndOperationSpecificFields(
        string operation,
        string payloadJson)
    {
        await using var fixture = new HandlerFixture();
        using var document = JsonDocument.Parse(payloadJson);
        var request = new PipeRequest(
            PipeProtocol.ApiVersion,
            Guid.NewGuid().ToString("D"),
            operation,
            document.RootElement.Clone());

        var response = await fixture.Handler.HandleAsync(
            request,
            UserPeer,
            TestContext.Current.CancellationToken);

        Assert.False(response.Ok);
        Assert.Equal(PipeErrorCodes.InvalidArgument, response.Error?.Code);
        Assert.Empty(fixture.Controller.Calls);
    }

    [Fact]
    public async Task EnvelopeValidation_RequiresCanonicalNonEmptyRequestId()
    {
        await using var fixture = new HandlerFixture();
        var value = Guid.NewGuid();
        var payload = JsonSerializer.SerializeToElement(new { });
        var invalidIds = new[]
        {
            Guid.Empty.ToString("D"),
            value.ToString("B"),
            value.ToString("N"),
        };

        foreach (var requestId in invalidIds)
        {
            var response = await fixture.Handler.HandleAsync(
                new PipeRequest(PipeProtocol.ApiVersion, requestId, PipeOperations.StatusGet, payload),
                UserPeer,
                TestContext.Current.CancellationToken);

            Assert.False(response.Ok);
            Assert.Equal("unknown", response.RequestId);
            Assert.Equal(PipeErrorCodes.InvalidArgument, response.Error?.Code);
        }

        Assert.Empty(fixture.Controller.Calls);
    }

    public static TheoryData<string> ControllerErrorCodes
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var code in typeof(PipeErrorCodes)
                         .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                         .Where(static info => info.IsLiteral && info.FieldType == typeof(string))
                         .Select(static info => (string)info.GetRawConstantValue()!))
            {
                data.Add(code);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(ControllerErrorCodes))]
    public async Task ControllerOperationError_PreservesUnifiedErrorCode(string errorCode)
    {
        await using var fixture = new HandlerFixture();
        fixture.Controller.FailureOperation = PipeOperations.StatusGet;
        fixture.Controller.Failure = new ControllerOperationException(errorCode, "bounded failure");

        var response = await fixture.HandleStatusAsync();

        Assert.False(response.Ok);
        Assert.Equal(errorCode, response.Error?.Code);
        Assert.Equal("bounded failure", response.Error?.Message);
    }

    [Fact]
    public async Task ExceptionMapping_CoversValidationProtocolArgumentAndUnclassifiedInternalFailures()
    {
        var cases = new (Exception Exception, string Code, string? ExactMessage)[]
        {
            (new ConfigurationValidationException("invalid curve"), PipeErrorCodes.InvalidArgument, "invalid curve"),
            (new V12ProtocolException("bad checksum"), PipeErrorCodes.ProtocolChecksum, "bad checksum"),
            (new TimeoutException("unclassified timeout"), PipeErrorCodes.InternalError,
                "The service could not complete the request. Export diagnostics for details."),
            (new IOException("unclassified I/O"), PipeErrorCodes.InternalError,
                "The service could not complete the request. Export diagnostics for details."),
            (new Win32Exception(5), PipeErrorCodes.InternalError,
                "The service could not complete the request. Export diagnostics for details."),
            (new UnauthorizedAccessException("unclassified access"), PipeErrorCodes.InternalError,
                "The service could not complete the request. Export diagnostics for details."),
            (new JsonException("json"), PipeErrorCodes.InvalidArgument, "json"),
            (new FormatException("format"), PipeErrorCodes.InvalidArgument, "format"),
            (new ArgumentException("argument"), PipeErrorCodes.InvalidArgument, "argument"),
            (new InvalidOperationException("secret internal detail"), PipeErrorCodes.InternalError,
                "The service could not complete the request. Export diagnostics for details."),
        };

        foreach (var item in cases)
        {
            await using var fixture = new HandlerFixture();
            fixture.Controller.FailureOperation = PipeOperations.StatusGet;
            fixture.Controller.Failure = item.Exception;

            var response = await fixture.HandleStatusAsync();

            Assert.False(response.Ok);
            Assert.Equal(item.Code, response.Error?.Code);
            Assert.Equal(item.ExactMessage, response.Error?.Message);
        }
    }

    [Fact]
    public async Task OperationCancellation_PropagatesInsteadOfBecomingAnApiFailure()
    {
        await using var fixture = new HandlerFixture();
        fixture.Controller.FailureOperation = PipeOperations.StatusGet;
        fixture.Controller.Failure = new OperationCanceledException("request canceled");

        _ = await Assert.ThrowsAsync<OperationCanceledException>(() => fixture.HandleStatusAsync());
    }

    [Fact]
    public async Task ConcurrentRejectedRequests_ReturnCorrelatedResponsesAndCompleteAuditLines()
    {
        await using var fixture = new HandlerFixture();
        const int count = 24;
        var requests = Enumerable.Range(0, count)
            .Select(_ => PipeRequest.Create(PipeOperations.ModeSet, new { mode = "invalid" }))
            .ToArray();

        var responses = await Task.WhenAll(requests.Select(request =>
            fixture.Handler.HandleAsync(request, UserPeer, TestContext.Current.CancellationToken)));

        Assert.Equal(count, responses.Length);
        Assert.All(responses, response =>
        {
            Assert.False(response.Ok);
            Assert.Equal(PipeErrorCodes.InvalidArgument, response.Error?.Code);
        });
        Assert.Equal(
            requests.Select(static request => request.RequestId).OrderBy(static id => id),
            responses.Select(static response => response.RequestId).OrderBy(static id => id));

        var auditLines = await File.ReadAllLinesAsync(
            fixture.Paths.AuditLogPath,
            TestContext.Current.CancellationToken);
        Assert.Equal(count, auditLines.Length);
        foreach (var line in auditLines)
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            Assert.Equal(PipeOperations.ModeSet, root.GetProperty("eventName").GetString());
            Assert.Equal(PipeErrorCodes.InvalidArgument, root.GetProperty("result").GetString());
            Assert.Equal(UserPeer.CallerSid, root.GetProperty("callerSid").GetString());
            Assert.Equal(UserPeer.Source, root.GetProperty("source").GetString());
        }
    }

    private sealed class HandlerFixture : IAsyncDisposable
    {
        private readonly string directory =
            Path.Combine(Path.GetTempPath(), $"v12-pipe-handler-{Guid.NewGuid():N}");

        internal HandlerFixture()
        {
            Paths = new ServicePaths(directory);
            Controller = new RecordingControllerApi();
            Handler = new PipeRequestHandler(
                Controller,
                new JsonlAuditLog(Paths, TimeProvider.System),
                NullLogger<PipeRequestHandler>.Instance);
        }

        internal ServicePaths Paths { get; }

        internal RecordingControllerApi Controller { get; }

        internal PipeRequestHandler Handler { get; }

        internal Task<PipeResponse> HandleStatusAsync() => Handler.HandleAsync(
            PipeRequest.Create(PipeOperations.StatusGet, new { }),
            UserPeer,
            TestContext.Current.CancellationToken);

        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingControllerApi : IControllerApi
    {
        public Task<ControllerStatus> DisableSafetyProtectionAsync(PipePeerIdentity peer, CancellationToken cancellationToken)
        {
            Record(PipeOperations.SafetyDisable, peer: peer);
            return Task.FromResult(SafetyStatus(true));
        }

        public Task<ControllerStatus> ResumeSafetyProtectionAsync(bool confirmed, PipePeerIdentity peer, CancellationToken cancellationToken)
        {
            Record(PipeOperations.SafetyResume, confirmed, peer: peer);
            return Task.FromResult(SafetyStatus(false));
        }

        private static ControllerStatus SafetyStatus(bool disabled)
        {
            var now = DateTimeOffset.UtcNow;
            return new ControllerStatus("Running", true, "configured-user", new(true, "374A", "B101", "0100", true),
                new(ControlMode.Manual, 60, 60, true), new(50, [], 50, true, now),
                new(false, FailSafeReason.None, null), now, now, new(false, null, null),
                SafetyProtection: new(disabled, disabled, disabled, false, null, disabled ? now : null));
        }

        public Task<object> GetLightingAsync(CancellationToken cancellationToken)
        { Record(PipeOperations.LightingGet); return Task.FromResult<object>(new { supported = true }); }
        public Task<object> SetLightingAsync(V12UltraController.Core.Hardware.LightingSettings settings, PipePeerIdentity peer, CancellationToken cancellationToken)
        { Record(PipeOperations.LightingSet); return Task.FromResult<object>(new { settings, readbackConfirmed = true }); }
        public object GetStartup() { Record(PipeOperations.StartupGet); return new { enabled = false }; }
        public Task<object> SetStartupAsync(bool enabled, PipePeerIdentity peer, CancellationToken cancellationToken)
        {
            Record(PipeOperations.StartupSet, enabled, peer: peer);
            return Task.FromResult<object>(new { enabled, readbackConfirmed = true });
        }
        public object GetStartupFan() { Record(PipeOperations.StartupFanGet); return new { percent = 50, defaultPercent = 50, readbackConfirmed = true }; }
        public Task<object> SetStartupFanPercentAsync(byte percent, PipePeerIdentity peer, CancellationToken cancellationToken)
        {
            Record(PipeOperations.StartupFanSet, percent, peer: peer);
            return Task.FromResult<object>(new { percent, defaultPercent = 50, readbackConfirmed = true });
        }
        internal ConcurrentQueue<ApiCall> Calls { get; } = new();

        internal string? FailureOperation { get; set; }

        internal Exception? Failure { get; set; }

        public object GetStatus()
        {
            Record(PipeOperations.StatusGet);
            return new { marker = "status" };
        }

        public FanCurve GetCurve()
        {
            Record(PipeOperations.CurveGet);
            return FanCurve.RemoteHostSafe;
        }

        public object GetDiagnostics()
        {
            Record(PipeOperations.DiagnosticsExport);
            return new { marker = "diagnostics" };
        }

        public object GetShutdownStatus()
        {
            Record(PipeOperations.ShutdownStatus);
            return new { enabled = false, qualified = false };
        }

        public Task SetModeAsync(
            ControlMode mode,
            PipePeerIdentity peer,
            CancellationToken cancellationToken)
        {
            Record(PipeOperations.ModeSet, mode, peer: peer);
            return Task.CompletedTask;
        }

        public Task<object> SetManualPowerAsync(bool poweredOn, bool confirmed, PipePeerIdentity peer, CancellationToken cancellationToken)
        {
            Record(PipeOperations.PowerSet, new { poweredOn, confirmed }, peer: peer);
            return Task.FromResult<object>(new { poweredOn, readbackConfirmed = true });
        }

        public Task<object> RecoverCommissioningSafetyAsync(bool attended, PipePeerIdentity peer, CancellationToken cancellationToken)
        {
            Record(PipeOperations.CommissionRecoverSafety, attended: attended, peer: peer);
            return Task.FromResult<object>(new { recovered = true });
        }
        public Task SetManualSpeedAsync(
            byte percent,
            PipePeerIdentity peer,
            CancellationToken cancellationToken)
        {
            Record(PipeOperations.SpeedSet, percent, peer: peer);
            return Task.CompletedTask;
        }

        public Task ApplyCurveAdviceAsync(FanCurve expected, FanCurve proposed, PipePeerIdentity peer, CancellationToken cancellationToken)
        {
            expected.Validate(); proposed.Validate();
            Record(PipeOperations.CurveAdviceApply, proposed, peer: peer);
            return Task.CompletedTask;
        }

        public Task<ProfileApplyResult> ApplyProfileAsync(ProfileApplyRequest request, string operationId,
            PipePeerIdentity peer, CancellationToken cancellationToken)
        {
            Record(PipeOperations.ProfileApply, request, peer: peer);
            var now = DateTimeOffset.UtcNow;
            var application = new ProfileApplicationStatus(operationId, request.Profile.Id, request.Profile.Name,
                ProfileApplyStage.Finished, ProfileApplyOutcome.Completed, ProfileConfigurationState.Saved,
                ProfileFanState.Confirmed, request.Profile.Lighting is null ? ProfileLightingState.NotRequested : ProfileLightingState.Confirmed,
                null, now, now);
            var status = new ControllerStatus("Running", true, "configured-user", new(true, "374A", "B101", "0100", true),
                new(request.Profile.Mode, 60, 60, true), new(50, [], 50, true, now),
                new(false, FailSafeReason.None, null), now, now, new(false, null, null), ProfileApplication: application);
            return Task.FromResult(new ProfileApplyResult(application, status));
        }

        public Task SetCurveAsync(
            FanCurve curve,
            PipePeerIdentity peer,
            CancellationToken cancellationToken)
        {
            curve.Validate();
            Record(PipeOperations.CurveSet, curve, peer: peer);
            return Task.CompletedTask;
        }

        public Task<object> ProbeCommissioningAsync(
            bool attended,
            PipePeerIdentity peer,
            CancellationToken cancellationToken)
        {
            Record(PipeOperations.CommissionProbe, attended: attended, peer: peer);
            return Task.FromResult<object>(new { stage = "probed" });
        }

        public Task<object> VerifyIdempotentAsync(
            Guid sessionId,
            bool attended,
            PipePeerIdentity peer,
            CancellationToken cancellationToken)
        {
            Record(PipeOperations.CommissionIdempotent, sessionId, attended, peer);
            return Task.FromResult<object>(new { stage = "idempotent" });
        }

        public Task<object> TestCommissioningAsync(
            Guid sessionId,
            bool attended,
            PipePeerIdentity peer,
            CancellationToken cancellationToken,
            bool nearFullOnly = false)
        {
            Record(PipeOperations.CommissionTest, sessionId, attended, peer);
            return Task.FromResult<object>(new { stage = "tested", nearFullOnly });
        }

        public Task<object> CommitCommissioningAsync(
            Guid sessionId,
            bool attended,
            PipePeerIdentity peer,
            CancellationToken cancellationToken)
        {
            Record(PipeOperations.CommissionCommit, sessionId, attended, peer);
            return Task.FromResult<object>(new { stage = "committed" });
        }

        public Task<object> RollbackAsync(
            bool startMythCool,
            bool installer,
            bool attended,
            PipePeerIdentity peer,
            CancellationToken cancellationToken)
        {
            Record(
                PipeOperations.CommissionRollback,
                new RollbackArguments(startMythCool, installer, attended),
                attended,
                peer: peer);
            return Task.FromResult<object>(new { stage = "rolledBack" });
        }

        public Task<object> RecoverTaskTransitionAsync(
            bool attended,
            PipePeerIdentity peer,
            CancellationToken cancellationToken)
        {
            Record(PipeOperations.CommissionRecoverTaskTransition, attended: attended, peer: peer);
            return Task.FromResult<object>(new { stage = "taskTransitionRecovered" });
        }

        public Task<object> AbortCommissioningAsync(
            Guid sessionId,
            PipePeerIdentity peer,
            CancellationToken cancellationToken)
        {
            Record(PipeOperations.CommissionAbort, sessionId, peer: peer);
            return Task.FromResult<object>(new { stage = "aborted" });
        }

        public Task<object> QualifySystemShutdownOffAsync(
            bool attended,
            PipePeerIdentity peer,
            CancellationToken cancellationToken)
        {
            Record(PipeOperations.ShutdownQualify, attended: attended, peer: peer);
            return Task.FromResult<object>(new { enabled = false, qualified = false, pending = true });
        }

        public Task<object> EnableSystemShutdownOffAsync(
            Guid qualificationId,
            bool attended,
            PipePeerIdentity peer,
            CancellationToken cancellationToken)
        {
            Record(PipeOperations.ShutdownEnable, qualificationId, attended, peer);
            return Task.FromResult<object>(new { enabled = true, qualified = true, pending = false });
        }

        public Task<object> DisableSystemShutdownOffAsync(
            PipePeerIdentity peer,
            CancellationToken cancellationToken)
        {
            Record(PipeOperations.ShutdownDisable, peer: peer);
            return Task.FromResult<object>(new { enabled = false, qualified = false });
        }

        private void Record(
            string operation,
            object? value = null,
            bool? attended = null,
            PipePeerIdentity? peer = null)
        {
            if (string.Equals(FailureOperation, operation, StringComparison.Ordinal) && Failure is not null)
            {
                throw Failure;
            }

            Calls.Enqueue(new ApiCall(operation, value, attended, peer));
        }
    }

    private sealed record ApiCall(
        string Operation,
        object? Value,
        bool? Attended,
        PipePeerIdentity? Peer);

    private sealed record RollbackArguments(bool StartMythCool, bool Installer, bool Attended);
}
