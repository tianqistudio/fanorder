using System.Text.Json;
using V12UltraController.Core.Diagnostics;
using V12UltraController.Core.Ipc;
using V12UltraController.Core.Thermal;

namespace V12UltraController.Cli;

internal sealed class CliApplication
{
    private readonly TextWriter output;
    private readonly TextWriter error;
    private readonly IPipeApiClient client;
    private readonly DiagnosticsArchiveWriter diagnosticsArchiveWriter;

    public CliApplication(TextWriter output, TextWriter error)
        : this(output, error, new NamedPipeApiClient(), new DiagnosticsArchiveWriter())
    {
    }

    internal CliApplication(
        TextWriter output,
        TextWriter error,
        IPipeApiClient client,
        DiagnosticsArchiveWriter diagnosticsArchiveWriter)
    {
        this.output = output ?? throw new ArgumentNullException(nameof(output));
        this.error = error ?? throw new ArgumentNullException(nameof(error));
        this.client = client ?? throw new ArgumentNullException(nameof(client));
        this.diagnosticsArchiveWriter = diagnosticsArchiveWriter ??
            throw new ArgumentNullException(nameof(diagnosticsArchiveWriter));
    }

    public async Task<int> RunAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        CliCommand command;
        try
        {
            command = CliCommandParser.Parse(args);
        }
        catch (CliUsageException exception)
        {
            if (!exception.ShowHelpOnly && !string.IsNullOrWhiteSpace(exception.Message))
            {
                await error.WriteLineAsync($"Error: {exception.Message}").ConfigureAwait(false);
                await error.WriteLineAsync().ConfigureAwait(false);
            }

            await (exception.ShowHelpOnly ? output : error).WriteLineAsync(CliCommandParser.HelpText)
                .ConfigureAwait(false);
            return exception.ShowHelpOnly ? CliExitCodes.Success : CliExitCodes.InvalidArguments;
        }

        try
        {
            var request = new PipeRequest(
                PipeProtocol.ApiVersion,
                Guid.NewGuid().ToString("D"),
                command.Operation,
                command.Payload);
            var timeout = command.Operation.StartsWith("commission.", StringComparison.Ordinal) ||
                          command.Operation == PipeOperations.ShutdownQualify ||
                          command.Operation == PipeOperations.ShutdownEnable
                ? TimeSpan.FromSeconds(60)
                : TimeSpan.FromSeconds(15);
            var response = await client.SendAsync(request, timeout, cancellationToken)
                .ConfigureAwait(false);
            if (response.ApiVersion != PipeProtocol.ApiVersion ||
                !string.Equals(response.RequestId, request.RequestId, StringComparison.Ordinal))
            {
                await error.WriteLineAsync(
                    "INTERNAL_ERROR: The service response version or requestId did not match the request.")
                    .ConfigureAwait(false);
                return CliExitCodes.ServiceUnavailable;
            }

            if (!response.Ok)
            {
                var code = response.Error?.Code ?? PipeErrorCodes.InternalError;
                await error.WriteLineAsync($"{code}: {response.Error?.Message ?? "The request failed."}")
                    .ConfigureAwait(false);
                return CliExitCodes.ForError(code);
            }

            if (response.Data is not { } data)
            {
                await error.WriteLineAsync("INTERNAL_ERROR: The service returned no data.").ConfigureAwait(false);
                return CliExitCodes.ServiceUnavailable;
            }

            if (command.ExportPath is not null)
            {
                try
                {
                    await diagnosticsArchiveWriter.WriteAsync(command.ExportPath, data, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    await error.WriteLineAsync($"INVALID_ARGUMENT: Cannot write diagnostics: {exception.Message}")
                        .ConfigureAwait(false);
                    return CliExitCodes.InvalidArguments;
                }

                await output.WriteLineAsync($"Diagnostics exported to {command.ExportPath}").ConfigureAwait(false);
                return CliExitCodes.Success;
            }

            if (command.JsonOutput)
            {
                await output.WriteLineAsync(JsonSerializer.Serialize(data, CliJson.Options)).ConfigureAwait(false);
            }
            else if (command.Operation == PipeOperations.StatusGet)
            {
                await WriteHumanStatusAsync(data).ConfigureAwait(false);
            }
            else if (command.Operation == PipeOperations.ShutdownStatus)
            {
                await WriteHumanShutdownStatusAsync(data).ConfigureAwait(false);
            }
            else
            {
                await output.WriteLineAsync(JsonSerializer.Serialize(data, CliJson.Options)).ConfigureAwait(false);
            }

            if (command.Operation == PipeOperations.PowerSet &&
                ReadBoolean(command.Payload, "poweredOn") == false)
            {
                // Keep stdout machine-readable, and do not infer a protection mode from a
                // power response that does not contain an atomic policy snapshot.
                await error.WriteLineAsync(
                    "OFF request completed. Automatic heat/telemetry recovery applies only in auto/manual mode " +
                    "with safety protection enabled. In puremanual/released mode or with protection disabled, " +
                    "OFF will not auto-resume. Use status to inspect the current policy.")
                    .ConfigureAwait(false);
            }

            return CliExitCodes.Success;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await error.WriteLineAsync("SERVICE_UNAVAILABLE: Timed out waiting for the local service.")
                .ConfigureAwait(false);
            return CliExitCodes.ServiceUnavailable;
        }
        catch (OperationCanceledException)
        {
            await error.WriteLineAsync("SERVICE_UNAVAILABLE: Operation cancelled.").ConfigureAwait(false);
            return CliExitCodes.ServiceUnavailable;
        }
        catch (InvalidDataException exception)
        {
            await error.WriteLineAsync($"INTERNAL_ERROR: Invalid service response: {exception.Message}")
                .ConfigureAwait(false);
            return CliExitCodes.ServiceUnavailable;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or TimeoutException)
        {
            await error.WriteLineAsync($"SERVICE_UNAVAILABLE: {FriendlyPipeMessage(exception)}")
                .ConfigureAwait(false);
            return CliExitCodes.ServiceUnavailable;
        }
        catch (JsonException exception)
        {
            await error.WriteLineAsync($"INTERNAL_ERROR: Invalid service response: {exception.Message}")
                .ConfigureAwait(false);
            return CliExitCodes.ServiceUnavailable;
        }
    }

    private async Task WriteHumanStatusAsync(JsonElement data)
    {
        foreach (var line in FormatHumanStatus(data))
        {
            await output.WriteLineAsync(line).ConfigureAwait(false);
        }
    }

    internal static IReadOnlyList<string> FormatHumanStatus(JsonElement data)
    {
        var lines = new List<string>();
        var serviceState = ReadString(data, "serviceState") ?? "unknown";
        var commissioned = ReadBoolean(data, "commissioned")?.ToString() ?? "unknown";
        lines.Add($"Service:       {serviceState}");
        lines.Add($"Commissioned:  {commissioned}");
        lines.Add($"Owner:         {ReadString(data, "owner") ?? "unknown"}");

        if (TryGet(data, "forcedUninstallRecovery", out var forcedRecovery) &&
            ReadBoolean(forcedRecovery, "required") == true)
        {
            var markerValid = ReadBoolean(forcedRecovery, "markerValid") == true;
            lines.Add(
                $"Recovery:      REQUIRED after forced uninstall; marker " +
                $"{(markerValid ? "valid" : "INVALID/UNREADABLE")}, " +
                $"id {ReadString(forcedRecovery, "markerId") ?? "unavailable"}");
            lines.Add(
                $"Recovery note: {ReadString(forcedRecovery, "reason") ?? "manual repair required"}");
            lines.Add("Safety:        Normal HID access is blocked; use an explicit attended rollback only.");
        }

        if (TryGet(data, "auditMutation", out var auditMutation) &&
            (ReadBoolean(auditMutation, "blocked") == true ||
             ReadBoolean(auditMutation, "pending") == true))
        {
            var blocked = ReadBoolean(auditMutation, "blocked") == true;
            lines.Add(
                $"Audit barrier:  {(blocked ? "BLOCKED" : "PENDING")}; " +
                $"transaction {ReadString(auditMutation, "transactionId") ?? "unavailable"}");
            lines.Add(
                $"Audit stage:    {ReadString(auditMutation, "eventName") ?? "unknown"} / " +
                $"{ReadString(auditMutation, "stage") ?? "unknown"}");
            if (!string.IsNullOrWhiteSpace(ReadString(auditMutation, "reason")))
            {
                lines.Add($"Audit note:     {ReadString(auditMutation, "reason")}");
            }

            lines.Add("Safety:        State changes and normal HID control remain blocked until recovery completes.");
        }

        if (TryGet(data, "safetyOperationAudit", out var safetyOperationAudit) &&
            (ReadBoolean(safetyOperationAudit, "blocked") == true ||
             ReadBoolean(safetyOperationAudit, "pending") == true))
        {
            var blocked = ReadBoolean(safetyOperationAudit, "blocked") == true;
            lines.Add(
                $"Safety audit:  {(blocked ? "BLOCKED" : "PENDING")}; " +
                $"transaction {ReadString(safetyOperationAudit, "transactionId") ?? "unavailable"}");
            lines.Add(
                $"Safety stage:  {ReadString(safetyOperationAudit, "eventName") ?? "unknown"} / " +
                $"{ReadString(safetyOperationAudit, "kind") ?? "unknown"} / " +
                $"{ReadString(safetyOperationAudit, "stage") ?? "unknown"} / " +
                $"{ReadString(safetyOperationAudit, "outcome") ?? "unknown"}");
            if (!string.IsNullOrWhiteSpace(ReadString(safetyOperationAudit, "reason")))
            {
                lines.Add($"Safety note:   {ReadString(safetyOperationAudit, "reason")}");
            }

            if (ReadBoolean(safetyOperationAudit, "requiresOn100Recovery") == true)
            {
                lines.Add("Recovery:      A verified ON/100% hardware recovery is required.");
            }

            lines.Add("Safety:        State changes and normal HID control remain blocked until this transaction is closed.");
        }

        var hardwarePoisoned = TryGet(data, "hardwareSafety", out var hardwareSafety) &&
            ReadBoolean(hardwareSafety, "poisoned") == true;

        if (TryGet(data, "device", out var device))
        {
            var connected = ReadBoolean(device, "connected") == true;
            var identity = $"VID_{ReadString(device, "vendorId") ?? "????"}/" +
                           $"PID_{ReadString(device, "productId") ?? "????"}";
            if (hardwarePoisoned)
            {
                lines.Add($"Device:        hardware ownership uncertain (public connection state is fail-closed)  expected {identity}");
            }
            else if (connected)
            {
                var protocol = ReadBoolean(device, "protocolCompatible") switch
                {
                    true => "compatible",
                    false => "INCOMPATIBLE",
                    null => "unknown",
                };
                lines.Add(
                    $"Device:        connected  {identity}  FW_{ReadString(device, "firmware") ?? "????"}  protocol {protocol}");
            }
            else
            {
                lines.Add($"Device:        not connected  expected {identity}; firmware/protocol not observed");
            }
        }

        if (hardwarePoisoned)
        {
            lines.Add(
                $"Hardware:      POISONED ({ReadString(hardwareSafety, "reason") ?? "unknown"})" +
                $" since {ReadString(hardwareSafety, "since") ?? "unknown"}");
            lines.Add("Safety:        Handle may still be retained; do not start Myth.Cool.");
        }

        if (TryGet(data, "fan", out var fan))
        {
            var historical = hardwarePoisoned || !TryGet(data, "device", out var fanDevice) ||
                             ReadBoolean(fanDevice, "connected") != true;
            var powered = ReadBoolean(fan, "poweredOn") switch
            {
                true => "on",
                false => "OFF",
                null => "unknown",
            };
            lines.Add(
                $"Fan:           {ReadString(fan, "mode") ?? "unknown"}, requested " +
                $"{ReadNumber(fan, "requestedPercent") ?? "?"}%" +
                (ReadNumber(fan, "reportedControlPercent") is { } reported
                    ? $", {(historical ? "last observed" : "reported")} {reported}%"
                    : ", reported unknown") +
                $", {(historical ? "last observed power" : "powered")} {powered}, configured manual {ReadNumber(fan, "configuredManualPercent") ?? "?"}%");
            if (historical)
                lines.Add("Fan freshness: Current speed/power unknown; retained readings are historical. See last successful read.");
        }

        var mode = ReadString(fan, "mode");
        var passiveMode = string.Equals(mode, "puremanual", StringComparison.OrdinalIgnoreCase) ||
                          string.Equals(mode, "released", StringComparison.OrdinalIgnoreCase);
        var activeMode = string.Equals(mode, "auto", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(mode, "manual", StringComparison.OrdinalIgnoreCase);
        var protectionDisabled = TryGet(data, "safetyProtection", out var protection)
            ? ReadBoolean(protection, "disabled") : null;
        lines.Add(passiveMode || protectionDisabled == true
            ? "Safety policy: Automatic heat/telemetry recovery is disabled; OFF will not auto-resume."
            : activeMode && protectionDisabled == false
                ? "Safety policy: Automatic heat/telemetry recovery is enabled by policy; successful recovery requires device communication."
                : "Safety policy: Automatic heat/telemetry recovery is unknown; do not assume OFF will auto-resume.");

        if (TryGet(data, "thermal", out var thermal))
        {
            var gpuValues = ReadNumberArray(thermal, "gpuCelsius");
            lines.Add(
                $"Temperature:   CPU {ReadNumber(thermal, "cpuCelsius") ?? "?"}°C, " +
                $"GPU {(gpuValues.Count == 0 ? "sleeping/unavailable" : string.Join("°C, ", gpuValues) + "°C")}, " +
                $"control {ReadNumber(thermal, "controlCelsius") ?? "?"}°C, " +
                $"signal {(ReadBoolean(thermal, "healthy") == true ? "healthy" : "INVALID")}, " +
                $"last sample attempt {ReadString(thermal, "lastUpdated") ?? "never"}");
            if (ReadBoolean(thermal, "healthy") != true)
            {
                lines.Add($"Temperature help: {ThermalTroubleshooting.English}");
            }
        }

        if (TryGet(data, "failSafe", out var failSafe))
        {
            lines.Add(
                $"Fail-safe:     {(ReadBoolean(failSafe, "active") == true ? "ACTIVE" : "inactive")}" +
                $" ({ReadString(failSafe, "reason") ?? "unknown"}), since " +
                $"{ReadString(failSafe, "since") ?? "not active"}");
        }

        if (TryGet(data, "commissioning", out var commissioning))
        {
            var active = ReadBoolean(commissioning, "active") == true;
            lines.Add(active
                ? $"Commissioning: ACTIVE, stage {ReadString(commissioning, "stage") ?? "unknown"}, " +
                  $"session {ReadString(commissioning, "sessionId") ?? "unknown"}, " +
                  $"expires {ReadString(commissioning, "expiresAt") ?? "unknown"}"
                : "Commissioning: inactive");
        }

        if (TryGet(data, "shutdown", out var shutdown))
        {
            lines.AddRange(FormatHumanShutdownStatus(shutdown));
        }

        lines.Add(
            $"Last I/O:       read {ReadString(data, "lastSuccessfulRead") ?? "never"}, " +
            $"write {ReadString(data, "lastSuccessfulWrite") ?? "never"}");

        return lines;
    }

    private async Task WriteHumanShutdownStatusAsync(JsonElement data)
    {
        foreach (var line in FormatHumanShutdownStatus(data))
        {
            await output.WriteLineAsync(line).ConfigureAwait(false);
        }
    }

    internal static IReadOnlyList<string> FormatHumanShutdownStatus(JsonElement data)
    {
        var enabled = ReadBoolean(data, "enabled") == true;
        var qualified = ReadBoolean(data, "qualified") == true;
        var pending = ReadBoolean(data, "pending") == true;
        var lines = new List<string>
        {
            $"Shutdown:      {(enabled ? "ENABLED" : "disabled")} ({ReadString(data, "behavior") ?? "unknown"})",
            $"Qualification: {(qualified ? "present" : "none")}, qualified at {ReadString(data, "qualifiedAt") ?? "never"}",
            $"Handle match:  {(ReadBoolean(data, "matchesCurrentHandle") == true ? "yes" : "NO")}",
        };
        lines.Add(pending
            ? $"Pending test:  YES, token {ReadString(data, "pendingQualificationId") ?? "unknown"}, " +
              $"expires {ReadString(data, "pendingExpiresAt") ?? "unknown"}; shutdown OFF is NOT enabled"
            : "Pending test:  none");
        if (TryGet(data, "qualifiedIdentity", out var identity))
        {
            lines.Add(
                $"Identity:      VID_{ReadString(identity, "vendorId") ?? "????"}/" +
                $"PID_{ReadString(identity, "productId") ?? "????"}/" +
                $"FW_{ReadString(identity, "firmware") ?? "????"}/" +
                $"UP_{ReadString(identity, "usagePage") ?? "????"}/" +
                $"U_{ReadString(identity, "usage") ?? "????"}, feature {ReadNumber(identity, "featureReportLength") ?? "?"}, " +
                $"protocol {ReadNumber(identity, "protocolRevision") ?? "?"}");
        }

        return lines;
    }

    private static string FriendlyPipeMessage(Exception exception) => exception switch
    {
        UnauthorizedAccessException => "Access denied. The installer must authorize this Windows user.",
        TimeoutException => "Timed out waiting for the local service.",
        _ when exception is IOException && exception.HResult == unchecked((int)0x80070002) =>
            "The local service is not running.",
        _ => exception.Message,
    };

    private static bool TryGet(JsonElement parent, string name, out JsonElement value)
    {
        if (parent.ValueKind == JsonValueKind.Object)
        {
            return parent.TryGetProperty(name, out value);
        }

        value = default;
        return false;
    }

    private static string? ReadString(JsonElement parent, string name) =>
        TryGet(parent, name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool? ReadBoolean(JsonElement parent, string name) =>
        TryGet(parent, name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;

    private static string? ReadNumber(JsonElement parent, string name) =>
        TryGet(parent, name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetRawText()
            : null;

    private static IReadOnlyList<string> ReadNumberArray(JsonElement parent, string name)
    {
        if (!TryGet(parent, name, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return value.EnumerateArray()
            .Where(static item => item.ValueKind == JsonValueKind.Number)
            .Select(static item => item.GetRawText())
            .ToArray();
    }
}
