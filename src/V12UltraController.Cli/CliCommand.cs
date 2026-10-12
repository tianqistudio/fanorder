using System.Globalization;
using System.Text.Json;
using V12UltraController.Core.Ipc;

namespace V12UltraController.Cli;

internal sealed record CliCommand(
    string Operation,
    JsonElement Payload,
    bool JsonOutput = false,
    string? ExportPath = null);

internal static class CliCommandParser
{
    public static CliCommand Parse(IReadOnlyList<string> args)
    {
        if (args.Count == 0 || IsHelp(args[0]))
        {
            throw new CliUsageException(null, showHelpOnly: true);
        }

        return args[0].ToLowerInvariant() switch
        {
            "status" => ParseStatus(args),
            "mode" => ParseMode(args),
            "speed" => ParseSpeed(args),
            "power" => ParsePower(args),
            "rgb" => ParseLighting(args),
            "startup" => ParseStartup(args),
            "curve" => ParseCurve(args),
            "diagnostics" => ParseDiagnostics(args),
            "commission" => ParseCommission(args),
            "shutdown" => ParseShutdown(args),
            _ => throw new CliUsageException($"Unknown command: {args[0]}")
        };
    }

    public static string HelpText => """
        V12 Ultra Controller command line

        Usage:
          v12ctl status [--json]
          v12ctl mode auto|manual|puremanual|released
          v12ctl speed <10..100>    (stores the manual target and switches to manual mode)
          v12ctl power on
          v12ctl rgb show [--json]
          v12ctl rgb set <on|off> <mode:0..3> <color:0..4> <brightness:0..255> <speed:0..3>
          v12ctl power off --confirm (requires commissioned ownership; see mode-dependent safety below)
          v12ctl startup show [--json]
          v12ctl startup on|off (next-boot service setting only; does not change the running fan)
          v12ctl curve show [--json]
          v12ctl curve apply <curve.json>
          v12ctl diagnostics export <output.zip>
          v12ctl commission probe --attended
          v12ctl commission idempotent --session <id> --attended
          v12ctl commission test --session <id> --attended
          v12ctl commission test --session <id> --attended --near-full (99 -> 100 only; does not qualify for commit)
          v12ctl commission abort --session <id>
          v12ctl commission commit --session <id> --attended
          v12ctl commission rollback [--installer] [--attended] [--start-mythcool true|false]
          v12ctl commission recover-task-transition --attended
          v12ctl commission recover-safety --attended (repairs only your unfinished manual/automatic abort at ON/100)
          v12ctl shutdown status [--json]
          v12ctl shutdown qualify --attended
          v12ctl shutdown enable <qualification-id> --attended
          v12ctl shutdown disable

        Safety:
          Commissioning commands never stop Myth.Cool automatically. Close Myth.Cool first.
          A successful commit is the only command that disables the Myth.Cool scheduled task.
          Rollback defaults to restoring the task state without starting Myth.Cool.
          A retained forced-uninstall recovery marker additionally requires --attended and never
          starts Myth.Cool or accesses HID.
          Recovery only inspects an existing in-progress journal and the exact task state; it never
          runs a scheduled-task mutation or starts Myth.Cool.
          Shutdown qualify briefly turns the fan OFF, always restores and confirms ON at 100%, and
          creates only a two-minute in-memory token. It never enables shutdown OFF by itself. After
          observing the restored fan, use a separate attended shutdown enable command with that token.
          Both steps require the operator beside a low-load computer below 60 C. Restart may also briefly
          turn the fan OFF. Fast Startup, Sleep, and Hibernate are not guaranteed shutdown paths.
          In auto/manual mode with safety protection enabled, OFF requires fresh temperature below 60 C.
          That policy requests ON/100 at 85 C or on telemetry failure; successful recovery requires device communication.
          In puremanual/released mode or with safety protection disabled, OFF has no automatic heat/telemetry recovery.
          Use status to inspect the current mode and protection. No raw power API.
          RGB uses vendor presets only and requires healthy, commissioned, powered-on fan control.
          Use shutdown disable to restore the independent shutdown behavior to KeepAt100.
        """;

    private static CliCommand ParseStatus(IReadOnlyList<string> args)
    {
        EnsureOnlyOptions(args, 1, "--json");
        return Create("status.get", new { }, HasOption(args, "--json"));
    }

    private static CliCommand ParseMode(IReadOnlyList<string> args)
    {
        if (args.Count != 2 ||
            !string.Equals(args[1], "auto", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(args[1], "manual", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(args[1], "puremanual", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(args[1], "released", StringComparison.OrdinalIgnoreCase))
        {
            throw new CliUsageException("mode must be auto or manual.");
        }

        return Create("mode.set", new { mode = args[1].ToLowerInvariant() });
    }

    private static CliCommand ParseSpeed(IReadOnlyList<string> args)
    {
        if (args.Count != 2 ||
            !int.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out var percent) ||
            percent is < 10 or > 100)
        {
            throw new CliUsageException("speed must be an integer from 10 through 100.");
        }

        return Create("speed.set", new { percent });
    }

    private static CliCommand ParseStartup(IReadOnlyList<string> args)
    {
        if (args.Count >= 2 && string.Equals(args[1], "show", StringComparison.OrdinalIgnoreCase))
        {
            EnsureOnlyOptions(args, 2, "--json");
            return Create(PipeOperations.StartupGet, new { }, HasOption(args, "--json"));
        }

        if (args.Count == 2 && args[1].ToLowerInvariant() is "on" or "off")
            return Create(PipeOperations.StartupSet,
                new { enabled = string.Equals(args[1], "on", StringComparison.OrdinalIgnoreCase) });

        throw new CliUsageException("Use startup show [--json], startup on, or startup off.");
    }

    private static CliCommand ParseLighting(IReadOnlyList<string> args)
    {
        if (args.Count >= 2 && args[1] == "show")
        {
            EnsureOnlyOptions(args, 2, "--json");
            return Create(PipeOperations.LightingGet, new { }, HasOption(args, "--json"));
        }
        if (args.Count == 7 && args[1] == "set" && args[2] is "on" or "off" &&
            int.TryParse(args[3], NumberStyles.None, CultureInfo.InvariantCulture, out var mode) && mode is >= 0 and <= 3 &&
            int.TryParse(args[4], NumberStyles.None, CultureInfo.InvariantCulture, out var color) && color is >= 0 and <= 4 &&
            int.TryParse(args[5], NumberStyles.None, CultureInfo.InvariantCulture, out var brightness) && brightness is >= 0 and <= 255 &&
            int.TryParse(args[6], NumberStyles.None, CultureInfo.InvariantCulture, out var speed) && speed is >= 0 and <= 3)
            return Create(PipeOperations.LightingSet, new { enabled = args[2] == "on", mode, color, brightness, speed });
        throw new CliUsageException("Use rgb show [--json] or rgb set on|off <mode:0..3> <color:0..4> <brightness:0..255> <speed:0..3>.");
    }

    private static CliCommand ParsePower(IReadOnlyList<string> args)
    {
        if (args.Count == 2 && string.Equals(args[1], "on", StringComparison.OrdinalIgnoreCase))
        {
            return Create(PipeOperations.PowerSet, new { poweredOn = true, confirmed = true });
        }

        if (args.Count == 3 && string.Equals(args[1], "off", StringComparison.OrdinalIgnoreCase) && args[2] == "--confirm")
        {
            return Create(PipeOperations.PowerSet, new { poweredOn = false, confirmed = true });
        }

        throw new CliUsageException("Use power on or power off --confirm. OFF requires explicit confirmation.");
    }

    private static CliCommand ParseCurve(IReadOnlyList<string> args)
    {
        if (args.Count >= 2 && string.Equals(args[1], "show", StringComparison.OrdinalIgnoreCase))
        {
            EnsureOnlyOptions(args, 2, "--json");
            return Create("curve.get", new { }, HasOption(args, "--json"));
        }

        if (args.Count == 3 && string.Equals(args[1], "apply", StringComparison.OrdinalIgnoreCase))
        {
            var path = GetFullPath(args[2]);
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                var payload = document.RootElement.ValueKind == JsonValueKind.Array
                    ? JsonSerializer.SerializeToElement(new { points = document.RootElement.Clone() }, CliJson.Options)
                    : document.RootElement.Clone();
                return new CliCommand("curve.set", payload);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                throw new CliUsageException($"Cannot read curve file: {exception.Message}");
            }
        }

        throw new CliUsageException("curve requires either 'show' or 'apply <curve.json>'.");
    }

    private static CliCommand ParseDiagnostics(IReadOnlyList<string> args)
    {
        if (args.Count != 3 || !string.Equals(args[1], "export", StringComparison.OrdinalIgnoreCase))
        {
            throw new CliUsageException("diagnostics requires 'export <output.zip>'.");
        }

        var path = GetFullPath(args[2]);
        if (File.Exists(path))
        {
            throw new CliUsageException("The diagnostics output already exists; choose a new path.");
        }

        return Create("diagnostics.export", new { }, exportPath: path);
    }

    private static CliCommand ParseCommission(IReadOnlyList<string> args)
    {
        if (args.Count < 2)
        {
            throw new CliUsageException("commission requires a stage.");
        }

        var stage = args[1].ToLowerInvariant();
        if (stage == "recover-safety")
        {
            if (args.Count != 3 || args[2] != "--attended")
                throw new CliUsageException("commission recover-safety requires --attended.");
            return Create(PipeOperations.CommissionRecoverSafety, new { attended = true });
        }
        if (stage == "recover-task-transition")
        {
            EnsureOnlyCommissionOptions(args, ["--attended"]);
            if (!HasOption(args, "--attended"))
            {
                throw new CliUsageException(
                    "commission recover-task-transition requires --attended.");
            }

            return Create(PipeOperations.CommissionRecoverTaskTransition, new { attended = true });
        }

        if (stage == "rollback")
        {
            var installer = HasOption(args, "--installer");
            var attended = HasOption(args, "--attended");
            var start = ReadBooleanOption(args, "--start-mythcool", defaultValue: false);
            EnsureOnlyCommissionOptions(args, ["--installer", "--attended", "--start-mythcool"]);
            return Create("commission.rollback", new { installer, attended, startMythCool = start });
        }

        if (stage == "abort")
        {
            EnsureOnlyCommissionOptions(args, ["--session"]);
            var abortSessionId = ReadStringOption(args, "--session");
            if (string.IsNullOrWhiteSpace(abortSessionId))
            {
                throw new CliUsageException("commission abort requires --session <id>.");
            }

            return Create("commission.abort", new { sessionId = abortSessionId });
        }

        if (stage is not ("probe" or "idempotent" or "test" or "commit"))
        {
            throw new CliUsageException($"Unknown commissioning stage: {args[1]}");
        }

        EnsureOnlyCommissionOptions(
            args,
            stage == "probe" ? ["--attended"] : stage == "test"
                ? ["--attended", "--session", "--near-full"] : ["--attended", "--session"]);
        if (!HasOption(args, "--attended"))
        {
            throw new CliUsageException("This commissioning stage requires --attended.");
        }

        if (stage == "probe")
        {
            return Create(PipeOperations.CommissionProbe, new { attended = true });
        }

        var sessionId = ReadStringOption(args, "--session");
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            throw new CliUsageException($"commission {stage} requires --session <id>.");
        }

        if (stage == "test" && HasOption(args, "--near-full"))
        {
            return Create(PipeOperations.CommissionTest, new { attended = true, sessionId, nearFullOnly = true });
        }

        return Create($"commission.{stage}", new { attended = true, sessionId });
    }

    private static CliCommand ParseShutdown(IReadOnlyList<string> args)
    {
        if (args.Count < 2)
        {
            throw new CliUsageException("shutdown requires status, qualify, enable, or disable.");
        }

        return args[1].ToLowerInvariant() switch
        {
            "status" => ParseShutdownStatus(args),
            "qualify" => ParseShutdownQualify(args),
            "enable" => ParseShutdownEnable(args),
            "disable" => ParseShutdownDisable(args),
            _ => throw new CliUsageException($"Unknown shutdown command: {args[1]}")
        };
    }

    private static CliCommand ParseShutdownStatus(IReadOnlyList<string> args)
    {
        EnsureOnlyOptions(args, 2, "--json");
        return Create(PipeOperations.ShutdownStatus, new { }, HasOption(args, "--json"));
    }

    private static CliCommand ParseShutdownQualify(IReadOnlyList<string> args)
    {
        EnsureOnlyOptions(args, 2, "--attended");
        if (!HasOption(args, "--attended"))
        {
            throw new CliUsageException("shutdown qualify requires --attended.");
        }

        return Create(PipeOperations.ShutdownQualify, new { attended = true });
    }

    private static CliCommand ParseShutdownDisable(IReadOnlyList<string> args)
    {
        EnsureOnlyOptions(args, 2);
        return Create(PipeOperations.ShutdownDisable, new { });
    }

    private static CliCommand ParseShutdownEnable(IReadOnlyList<string> args)
    {
        if (args.Count < 3 || !Guid.TryParse(args[2], out var qualificationId) ||
            qualificationId == Guid.Empty)
        {
            throw new CliUsageException(
                "shutdown enable requires a valid <qualification-id> from shutdown qualify.");
        }

        EnsureOnlyOptions(args, 3, "--attended");
        if (!HasOption(args, "--attended"))
        {
            throw new CliUsageException("shutdown enable requires --attended.");
        }

        return Create(PipeOperations.ShutdownEnable, new { qualificationId, attended = true });
    }

    private static CliCommand Create(
        string operation,
        object payload,
        bool jsonOutput = false,
        string? exportPath = null)
    {
        var element = JsonSerializer.SerializeToElement(payload, CliJson.Options);
        return new CliCommand(operation, element, jsonOutput, exportPath);
    }

    private static void EnsureOnlyOptions(IReadOnlyList<string> args, int startIndex, params string[] allowed)
    {
        for (var index = startIndex; index < args.Count; index++)
        {
            if (!allowed.Contains(args[index], StringComparer.OrdinalIgnoreCase))
            {
                throw new CliUsageException($"Unexpected argument: {args[index]}");
            }
        }
    }

    private static void EnsureOnlyCommissionOptions(IReadOnlyList<string> args, IReadOnlyCollection<string> allowed)
    {
        for (var index = 2; index < args.Count; index++)
        {
            var argument = args[index];
            if (!argument.StartsWith("--", StringComparison.Ordinal) ||
                !allowed.Contains(argument, StringComparer.OrdinalIgnoreCase))
            {
                throw new CliUsageException($"Unexpected argument: {argument}");
            }

            if (argument.Equals("--session", StringComparison.OrdinalIgnoreCase) ||
                argument.Equals("--start-mythcool", StringComparison.OrdinalIgnoreCase))
            {
                if (++index >= args.Count || args[index].StartsWith("--", StringComparison.Ordinal))
                {
                    throw new CliUsageException($"Option {argument} requires a value.");
                }
            }
        }
    }

    private static bool HasOption(IReadOnlyList<string> args, string option) =>
        args.Any(value => value.Equals(option, StringComparison.OrdinalIgnoreCase));

    private static string? ReadStringOption(IReadOnlyList<string> args, string option)
    {
        for (var index = 0; index < args.Count - 1; index++)
        {
            if (args[index].Equals(option, StringComparison.OrdinalIgnoreCase))
            {
                return args[index + 1];
            }
        }

        return null;
    }

    private static bool ReadBooleanOption(IReadOnlyList<string> args, string option, bool defaultValue)
    {
        var value = ReadStringOption(args, option);
        if (value is null)
        {
            return defaultValue;
        }

        return bool.TryParse(value, out var result)
            ? result
            : throw new CliUsageException($"Option {option} must be true or false.");
    }

    private static bool IsHelp(string value) =>
        value is "help" or "--help" or "-h" or "/?";

    private static string GetFullPath(string value)
    {
        try
        {
            return Path.GetFullPath(value);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new CliUsageException($"Invalid path: {exception.Message}");
        }
    }
}

internal sealed class CliUsageException(string? message, bool showHelpOnly = false) : Exception(message)
{
    public bool ShowHelpOnly { get; } = showHelpOnly;
}

internal static class CliJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };
}
