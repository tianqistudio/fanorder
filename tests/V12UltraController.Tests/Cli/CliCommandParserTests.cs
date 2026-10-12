using System.Text.Json;
using V12UltraController.Cli;
using V12UltraController.Core.Ipc;

namespace V12UltraController.Tests.Cli;

public sealed class CliCommandParserTests
{
    [Fact]
    public void ReleasedDeviceNeverPresentsCachedFanStateAsCurrent()
    {
        using var document = JsonDocument.Parse("""
            {"device":{"connected":false},"fan":{"mode":"auto","requestedPercent":100,
            "reportedControlPercent":100,"poweredOn":true},"lastSuccessfulRead":"2026-09-05T18:15:01Z"}
            """);
        var text = string.Join("\n", CliApplication.FormatHumanStatus(document.RootElement));
        Assert.Contains("last observed 100%", text);
        Assert.Contains("Current speed/power unknown", text);
        Assert.DoesNotContain(", powered on", text);
    }
    [Theory]
    [InlineData("on", true)]
    [InlineData("off", false)]
    [InlineData("ON", true)]
    public void StartupChangeContainsOnlyIndependentEnabledSetting(string value, bool enabled)
    {
        var command = CliCommandParser.Parse(["startup", value]);
        Assert.Equal(PipeOperations.StartupSet, command.Operation);
        Assert.Equal(enabled, command.Payload.GetProperty("enabled").GetBoolean());
        Assert.Single(command.Payload.EnumerateObject());
    }

    [Fact]
    public void StartupReadAndInvalidArguments()
    {
        var command = CliCommandParser.Parse(["startup", "show", "--json"]);
        Assert.Equal(PipeOperations.StartupGet, command.Operation);
        Assert.True(command.JsonOutput);
        Assert.Empty(command.Payload.EnumerateObject());
        foreach (var args in new string[][] {
            ["startup"], ["startup", "toggle"], ["startup", "on", "--force"],
            ["startup", "off", "--json"], ["startup", "show", "--force"] })
            Assert.Throws<CliUsageException>(() => CliCommandParser.Parse(args));
    }

    [Fact]
    public void ManualPower_OffRequiresConfirmationAndRejectsExtraArguments()
    {
        Assert.True(CliCommandParser.Parse(["power", "on"]).Payload.GetProperty("poweredOn").GetBoolean());
        var off = CliCommandParser.Parse(["power", "off", "--confirm"]);
        Assert.Equal(PipeOperations.PowerSet, off.Operation);
        Assert.False(off.Payload.GetProperty("poweredOn").GetBoolean());
        Assert.Throws<CliUsageException>(() => CliCommandParser.Parse(["power", "off"]));
        Assert.Throws<CliUsageException>(() => CliCommandParser.Parse(["power", "off", "--confirm", "--force"]));
        Assert.Throws<CliUsageException>(() => CliCommandParser.Parse(["power", "toggle"]));
    }
    [Fact]
    public void NearFullTest_RequiresExplicitAttendedFlagAndCannotApplyToCommit()
    {
        var session = Guid.NewGuid().ToString();
        var command = CliCommandParser.Parse(["commission", "test", "--session", session, "--attended", "--near-full"]);
        Assert.Equal(PipeOperations.CommissionTest, command.Operation);
        Assert.True(command.Payload.GetProperty("nearFullOnly").GetBoolean());
        Assert.Throws<CliUsageException>(() => CliCommandParser.Parse(["commission", "test", "--session", session, "--near-full"]));
        Assert.Throws<CliUsageException>(() => CliCommandParser.Parse(["commission", "commit", "--session", session, "--attended", "--near-full"]));
    }
    [Fact]
    public void HumanStatus_IncludesConfiguredAndObservedSafetyState()
    {
        using var document = JsonDocument.Parse("""
            {
              "serviceState": "Running",
              "commissioned": true,
              "owner": "configured-user",
              "device": {
                "connected": true,
                "vendorId": "374A",
                "productId": "B101",
                "firmware": "1602",
                "protocolCompatible": true
              },
              "fan": {
                "mode": "Manual",
                "requestedPercent": 100,
                "reportedControlPercent": 100,
                "poweredOn": false,
                "configuredManualPercent": 40
              },
              "thermal": {
                "cpuCelsius": 78,
                "gpuCelsius": [67, 71],
                "controlCelsius": 78,
                "healthy": false,
                "lastUpdated": "2026-09-05T12:30:00Z"
              },
              "failSafe": {
                "active": true,
                "reason": "TelemetryStale",
                "since": "2026-09-05T12:30:10Z"
              },
              "lastSuccessfulRead": "2026-09-05T12:29:59Z",
              "lastSuccessfulWrite": "2026-09-05T12:30:11Z",
              "hardwareSafety": { "poisoned": false },
              "commissioning": {
                "active": true,
                "sessionId": "11111111-2222-3333-4444-555555555555",
                "stage": "IdempotentVerified",
                "expiresAt": "2026-09-05T12:39:00Z"
              },
              "shutdown": {
                "enabled": true,
                "qualified": true,
                "behavior": "turnOffOnSystemShutdown",
                "qualifiedAt": "2026-09-05T12:20:00Z",
                "matchesCurrentHandle": true,
                "qualifiedIdentity": {
                  "vendorId": "374A",
                  "productId": "B101",
                  "firmware": "1602",
                  "usagePage": "FF00",
                  "usage": "0001",
                  "featureReportLength": 9,
                  "protocolRevision": 1
                }
              }
            }
            """);

        var rendered = string.Join(Environment.NewLine,
            CliApplication.FormatHumanStatus(document.RootElement));

        Assert.Contains("Owner:         configured-user", rendered, StringComparison.Ordinal);
        Assert.Contains("protocol compatible", rendered, StringComparison.Ordinal);
        Assert.Contains("powered OFF", rendered, StringComparison.Ordinal);
        Assert.Contains("configured manual 40%", rendered, StringComparison.Ordinal);
        Assert.Contains("GPU 67°C, 71°C", rendered, StringComparison.Ordinal);
        Assert.Contains("signal INVALID", rendered, StringComparison.Ordinal);
        Assert.Contains("2026-09-05T12:30:10Z", rendered, StringComparison.Ordinal);
        Assert.Contains("IdempotentVerified", rendered, StringComparison.Ordinal);
        Assert.Contains("11111111-2222-3333-4444-555555555555", rendered, StringComparison.Ordinal);
        Assert.Contains("read 2026-09-05T12:29:59Z", rendered, StringComparison.Ordinal);
        Assert.Contains("write 2026-09-05T12:30:11Z", rendered, StringComparison.Ordinal);
        Assert.Contains("Shutdown:      ENABLED", rendered, StringComparison.Ordinal);
        Assert.Contains("Handle match:  yes", rendered, StringComparison.Ordinal);
        Assert.Contains("VID_374A/PID_B101/FW_1602", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void HumanStatus_HardwarePoisonNeverPresentsFailClosedConnectionAsReleased()
    {
        using var document = JsonDocument.Parse("""
            {
              "serviceState": "HardwarePoisoned",
              "commissioned": true,
              "device": {
                "connected": false,
                "vendorId": "374A",
                "productId": "B101",
                "firmware": "1602"
              },
              "hardwareSafety": {
                "poisoned": true,
                "reason": "DEVICE_OPEN_ABANDONED",
                "since": "2026-09-05T12:34:56Z"
              },
              "fan": { "mode": "Auto", "requestedPercent": 100 },
              "thermal": { "cpuCelsius": 70, "controlCelsius": 70 },
              "failSafe": { "active": true, "reason": "HidFailure" }
            }
            """);

        var rendered = string.Join(Environment.NewLine,
            CliApplication.FormatHumanStatus(document.RootElement));

        Assert.Contains("hardware ownership uncertain", rendered, StringComparison.Ordinal);
        Assert.Contains("DEVICE_OPEN_ABANDONED", rendered, StringComparison.Ordinal);
        Assert.Contains("2026-09-05T12:34:56Z", rendered, StringComparison.Ordinal);
        Assert.Contains("Handle may still be retained", rendered, StringComparison.Ordinal);
        Assert.Contains("do not start Myth.Cool", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("Device:        not connected", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void HumanStatus_ForcedUninstallRecoveryIsProminentAndDoesNotClaimFailSafeWrite()
    {
        using var document = JsonDocument.Parse("""
            {
              "serviceState": "ForcedUninstallRecoveryRequired",
              "commissioned": true,
              "owner": "configured-user",
              "forcedUninstallRecovery": {
                "required": true,
                "markerValid": true,
                "markerId": "11111111-2222-3333-4444-555555555555",
                "reason": "verified rollback was skipped"
              },
              "device": { "connected": false, "vendorId": "374A", "productId": "B101" },
              "hardwareSafety": { "poisoned": false },
              "fan": { "mode": "Auto", "requestedPercent": 100 },
              "thermal": {},
              "failSafe": { "active": true, "reason": "ConfigurationInvalid" }
            }
            """);

        var rendered = string.Join(Environment.NewLine,
            CliApplication.FormatHumanStatus(document.RootElement));

        Assert.Contains("Recovery:      REQUIRED after forced uninstall", rendered,
            StringComparison.Ordinal);
        Assert.Contains("11111111-2222-3333-4444-555555555555", rendered,
            StringComparison.Ordinal);
        Assert.Contains("Normal HID access is blocked", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("sent 100", rendered, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HumanStatus_AuditMutationBarrierIsProminentAndActionable()
    {
        using var document = JsonDocument.Parse("""
            {
              "serviceState": "AuditMutationRecoveryBlocked",
              "commissioned": true,
              "owner": "configured-user",
              "auditMutation": {
                "blocked": true,
                "pending": true,
                "transactionId": "11111111-2222-3333-4444-555555555555",
                "eventName": "shutdown.enable",
                "stage": "StateApplied",
                "reason": "audit append requires recovery"
              },
              "device": { "connected": false },
              "hardwareSafety": { "poisoned": false },
              "fan": { "mode": "Auto", "requestedPercent": 100 },
              "thermal": {},
              "failSafe": { "active": true, "reason": "ConfigurationInvalid" }
            }
            """);

        var rendered = string.Join(Environment.NewLine,
            CliApplication.FormatHumanStatus(document.RootElement));

        Assert.Contains("Audit barrier:  BLOCKED", rendered, StringComparison.Ordinal);
        Assert.Contains("11111111-2222-3333-4444-555555555555", rendered,
            StringComparison.Ordinal);
        Assert.Contains("shutdown.enable / StateApplied", rendered, StringComparison.Ordinal);
        Assert.Contains("normal HID control remain blocked", rendered, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("9")]
    [InlineData("101")]
    [InlineData("50.5")]
    [InlineData("not-a-number")]
    public void SpeedOutsideProtocolContract_IsRejected(string value)
    {
        _ = Assert.Throws<CliUsageException>(() => CliCommandParser.Parse(["speed", value]));
    }

    [Fact]
    public void DiagnosticsExport_RefusesToOverwriteExistingFile()
    {
        var path = Path.GetTempFileName();
        try
        {
            _ = Assert.Throws<CliUsageException>(
                () => CliCommandParser.Parse(["diagnostics", "export", path]));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void CommissioningRequiresAttendedAndSession()
    {
        _ = Assert.Throws<CliUsageException>(() => CliCommandParser.Parse(["commission", "probe"]));
        _ = Assert.Throws<CliUsageException>(
            () => CliCommandParser.Parse(["commission", "commit", "--attended"]));

        var session = Guid.NewGuid().ToString("D");
        var command = CliCommandParser.Parse(
            ["commission", "commit", "--session", session, "--attended"]);
        Assert.Equal(PipeOperations.CommissionCommit, command.Operation);
        Assert.Equal(session, command.Payload.GetProperty("sessionId").GetString());
        Assert.True(command.Payload.GetProperty("attended").GetBoolean());
    }

    [Fact]
    public void CommissionProbe_RejectsIrrelevantSessionOption()
    {
        _ = Assert.Throws<CliUsageException>(() => CliCommandParser.Parse(
            ["commission", "probe", "--attended", "--session", Guid.NewGuid().ToString("D")]));

        var command = CliCommandParser.Parse(["commission", "probe", "--attended"]);
        Assert.Equal(PipeOperations.CommissionProbe, command.Operation);
        Assert.False(command.Payload.TryGetProperty("sessionId", out _));
    }

    [Fact]
    public void SpeedHelp_StatesThatTheCommandSwitchesToManualMode()
    {
        Assert.Contains("switches to manual mode", CliCommandParser.HelpText, StringComparison.Ordinal);
    }

    [Fact]
    public void CommissionAbort_RequiresOnlySessionAndMapsToAbortOperation()
    {
        _ = Assert.Throws<CliUsageException>(() => CliCommandParser.Parse(["commission", "abort"]));
        _ = Assert.Throws<CliUsageException>(() => CliCommandParser.Parse(
            ["commission", "abort", "--session", Guid.NewGuid().ToString("D"), "--attended"]));

        var session = Guid.NewGuid().ToString("D");
        var command = CliCommandParser.Parse(
            ["commission", "abort", "--session", session]);

        Assert.Equal(PipeOperations.CommissionAbort, command.Operation);
        Assert.Equal(session, command.Payload.GetProperty("sessionId").GetString());
        Assert.False(command.Payload.TryGetProperty("attended", out _));
    }

    [Fact]
    public void TaskTransitionRecovery_RequiresAttendedAndAcceptsNoBypassOptions()
    {
        _ = Assert.Throws<CliUsageException>(() =>
            CliCommandParser.Parse(["commission", "recover-task-transition"]));
        _ = Assert.Throws<CliUsageException>(() => CliCommandParser.Parse(
            ["commission", "recover-task-transition", "--attended", "--session", Guid.NewGuid().ToString("D")]));
        _ = Assert.Throws<CliUsageException>(() => CliCommandParser.Parse(
            ["commission", "recover-task-transition", "--attended", "--start-mythcool", "false"]));

        var command = CliCommandParser.Parse(
            ["commission", "recover-task-transition", "--attended"]);

        Assert.Equal(PipeOperations.CommissionRecoverTaskTransition, command.Operation);
        Assert.True(command.Payload.GetProperty("attended").GetBoolean());
        Assert.False(command.Payload.TryGetProperty("sessionId", out _));
    }

    [Fact]
    public void CommissionRollback_MapsExplicitAttendedEvidenceWithoutRequiringItForNormalRollback()
    {
        var normal = CliCommandParser.Parse(["commission", "rollback"]);
        Assert.Equal(PipeOperations.CommissionRollback, normal.Operation);
        Assert.False(normal.Payload.GetProperty("attended").GetBoolean());
        Assert.False(normal.Payload.GetProperty("startMythCool").GetBoolean());

        var attended = CliCommandParser.Parse(
            ["commission", "rollback", "--attended", "--start-mythcool", "false"]);
        Assert.True(attended.Payload.GetProperty("attended").GetBoolean());
        Assert.False(attended.Payload.GetProperty("startMythCool").GetBoolean());
        Assert.Contains("forced-uninstall recovery marker", CliCommandParser.HelpText,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ShutdownSurface_IsNarrowAttendedAndHasNoImmediateOffCommand()
    {
        var status = CliCommandParser.Parse(["shutdown", "status", "--json"]);
        Assert.Equal(PipeOperations.ShutdownStatus, status.Operation);
        Assert.True(status.JsonOutput);

        _ = Assert.Throws<CliUsageException>(() =>
            CliCommandParser.Parse(["shutdown", "qualify"]));
        var qualify = CliCommandParser.Parse(["shutdown", "qualify", "--attended"]);
        Assert.Equal(PipeOperations.ShutdownQualify, qualify.Operation);
        Assert.True(qualify.Payload.GetProperty("attended").GetBoolean());

        var qualificationId = Guid.NewGuid();
        _ = Assert.Throws<CliUsageException>(() =>
            CliCommandParser.Parse(["shutdown", "enable", qualificationId.ToString("D")]));
        _ = Assert.Throws<CliUsageException>(() =>
            CliCommandParser.Parse(["shutdown", "enable", "not-a-guid", "--attended"]));
        var enable = CliCommandParser.Parse(
            ["shutdown", "enable", qualificationId.ToString("D"), "--attended"]);
        Assert.Equal(PipeOperations.ShutdownEnable, enable.Operation);
        Assert.Equal(qualificationId,
            enable.Payload.GetProperty("qualificationId").GetGuid());
        Assert.True(enable.Payload.GetProperty("attended").GetBoolean());

        var disable = CliCommandParser.Parse(["shutdown", "disable"]);
        Assert.Equal(PipeOperations.ShutdownDisable, disable.Operation);
        Assert.Empty(disable.Payload.EnumerateObject());

        _ = Assert.Throws<CliUsageException>(() =>
            CliCommandParser.Parse(["shutdown", "off-now"]));
        _ = Assert.Throws<CliUsageException>(() =>
            CliCommandParser.Parse(["shutdown", "disable", "--attended"]));
        Assert.DoesNotContain("v12ctl shutdown off-now", CliCommandParser.HelpText,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("briefly turns the fan OFF", CliCommandParser.HelpText, StringComparison.Ordinal);
        Assert.Contains("never enables shutdown OFF by itself", CliCommandParser.HelpText,
            StringComparison.Ordinal);
        Assert.Contains("shutdown enable <qualification-id> --attended", CliCommandParser.HelpText,
            StringComparison.Ordinal);
        Assert.Contains("Restart", CliCommandParser.HelpText, StringComparison.Ordinal);
        Assert.Contains("Fast Startup", CliCommandParser.HelpText, StringComparison.Ordinal);
    }

    [Fact]
    public void HumanShutdownStatus_ExposesPendingTokenWithoutClaimingEnabled()
    {
        using var document = JsonDocument.Parse("""
            {
              "enabled": false,
              "qualified": false,
              "behavior": "keepAt100",
              "matchesCurrentHandle": false,
              "pending": true,
              "pendingQualificationId": "11111111-2222-3333-4444-555555555555",
              "pendingExpiresAt": "2026-09-05T12:32:00Z"
            }
            """);

        var rendered = string.Join(Environment.NewLine,
            CliApplication.FormatHumanShutdownStatus(document.RootElement));

        Assert.Contains("Shutdown:      disabled", rendered, StringComparison.Ordinal);
        Assert.Contains("11111111-2222-3333-4444-555555555555", rendered, StringComparison.Ordinal);
        Assert.Contains("shutdown OFF is NOT enabled", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void CurveArray_IsWrappedInVersionedPayloadShape()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"v12-cli-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "curve.json");
        try
        {
            File.WriteAllText(path, "[{\"temperatureCelsius\":0,\"fanPercent\":36},{\"temperatureCelsius\":90,\"fanPercent\":100}]");
            var command = CliCommandParser.Parse(["curve", "apply", path]);

            Assert.Equal(PipeOperations.CurveSet, command.Operation);
            Assert.Equal(JsonValueKind.Array, command.Payload.GetProperty("points").ValueKind);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(PipeErrorCodes.InvalidArgument, 2)]
    [InlineData(PipeErrorCodes.ServiceUnavailable, 3)]
    [InlineData(PipeErrorCodes.InternalError, 3)]
    [InlineData(PipeErrorCodes.NotCommissioned, 4)]
    [InlineData(PipeErrorCodes.DeviceNotFound, 4)]
    [InlineData(PipeErrorCodes.UnsupportedDevice, 4)]
    [InlineData(PipeErrorCodes.TelemetryStale, 5)]
    [InlineData(PipeErrorCodes.FailSafeActive, 5)]
    [InlineData(PipeErrorCodes.MythCoolConflict, 6)]
    [InlineData(PipeErrorCodes.HidTimeout, 7)]
    [InlineData(PipeErrorCodes.ProtocolChecksum, 7)]
    public void ServiceErrors_MapToStableExitCodes(string code, int expected)
    {
        Assert.Equal(expected, CliExitCodes.ForError(code));
    }
}
