using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace V12UltraController.Service;

internal sealed record ScheduledTaskBackupMetadata(
    string TaskName,
    bool OriginallyEnabled,
    string XmlSha256,
    DateTimeOffset CreatedAtUtc);

internal sealed record ScheduledTaskRecoveryInspection(
    bool Enabled,
    string CurrentXmlSha256,
    bool ExactBackupXmlMatch,
    bool DefinitionMatchesBackup);

internal interface IMythCoolScheduledTaskManager
{
    Task<ScheduledTaskBackupMetadata> BackupCurrentAsync(CancellationToken cancellationToken = default);

    Task DisableAsync(CancellationToken cancellationToken = default);

    Task RestoreAsync(bool startMythCool, CancellationToken cancellationToken = default);

    Task<bool> GetEnabledAsync(CancellationToken cancellationToken = default);

    Task<ScheduledTaskBackupMetadata?> TryReadVerifiedBackupAsync(
        CancellationToken cancellationToken = default);

    Task<ScheduledTaskRecoveryInspection> InspectCurrentAgainstBackupAsync(
        ScheduledTaskBackupMetadata expectedBackup,
        CancellationToken cancellationToken = default);
}

internal sealed class MythCoolScheduledTaskManager(
    ServicePaths paths,
    TimeProvider timeProvider,
    ISchtasksProcessRunner processRunner,
    ILogger<MythCoolScheduledTaskManager> logger) : IMythCoolScheduledTaskManager
{
    private readonly JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<ScheduledTaskBackupMetadata> BackupCurrentAsync(
        CancellationToken cancellationToken = default)
    {
        paths.EnsureDirectories();
        var query = await processRunner.RunAsync(
            ["/Query", "/TN", ServiceConstants.MythCoolTaskName, "/XML"],
            SchtasksCommandKind.Query,
            cancellationToken).ConfigureAwait(false);
        if (query.ExitCode != 0 || string.IsNullOrWhiteSpace(query.StandardOutput))
        {
            throw new InvalidOperationException(
                $"Cannot export {ServiceConstants.MythCoolTaskName}; schtasks exited {query.ExitCode}.");
        }

        var normalizedXml = NormalizeXmlForUtf8(query.StandardOutput);
        var originallyEnabled = ReadEnabled(normalizedXml);
        var hash = Sha256(normalizedXml);
        var metadata = new ScheduledTaskBackupMetadata(
            ServiceConstants.MythCoolTaskName,
            originallyEnabled,
            hash,
            timeProvider.GetUtcNow());

        await AtomicFile.WriteTextAsync(paths.TaskBackupXmlPath, normalizedXml, cancellationToken)
            .ConfigureAwait(false);
        await AtomicFile.WriteJsonAsync(paths.TaskBackupMetadataPath, metadata, cancellationToken)
            .ConfigureAwait(false);

        logger.LogInformation("Backed up scheduled task {TaskName}.", ServiceConstants.MythCoolTaskName);
        return metadata;
    }

    public async Task DisableAsync(CancellationToken cancellationToken = default)
    {
        var disable = await processRunner.RunAsync(
                ["/Change", "/TN", ServiceConstants.MythCoolTaskName, "/Disable"],
                SchtasksCommandKind.Mutation,
                cancellationToken)
            .ConfigureAwait(false);
        if (disable.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Cannot disable {ServiceConstants.MythCoolTaskName}; schtasks exited {disable.ExitCode}.");
        }

        // Cancellation is no longer an interruption point after a mutation starts. Complete
        // the readback so the coordinator may durably advance from TaskDisableInProgress
        // to TaskDisabled.
        await VerifyEnabledAsync(expectedEnabled: false, CancellationToken.None).ConfigureAwait(false);

        logger.LogInformation("Disabled and verified scheduled task {TaskName}.", ServiceConstants.MythCoolTaskName);
    }

    public async Task RestoreAsync(bool startMythCool, CancellationToken cancellationToken = default)
    {
        var verified = await TryReadVerifiedBackupWithXmlAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("A verified Myth.Cool scheduled-task backup is not available.");
        var metadata = verified.Metadata;

        var current = await processRunner.RunAsync(
                ["/Query", "/TN", metadata.TaskName, "/XML"],
                SchtasksCommandKind.Query,
                cancellationToken)
            .ConfigureAwait(false);
        var mutationStarted = false;
        bool currentEnabled;
        if (current.ExitCode == 0 && !string.IsNullOrWhiteSpace(current.StandardOutput))
        {
            var currentXml = NormalizeXmlForUtf8(current.StandardOutput);
            if (!FixedTimeHashEquals(
                    TaskDefinitionSha256(currentXml),
                    TaskDefinitionSha256(verified.Xml)))
            {
                throw new InvalidDataException(
                    "The existing Myth.Cool task definition differs from the verified backup; " +
                    "restore was refused before mutation.");
            }

            currentEnabled = ReadEnabled(currentXml);
        }
        else if (IsDefinitelyMissingTask(current))
        {
            var create = await processRunner.RunAsync(
                ["/Create", "/TN", metadata.TaskName, "/XML", paths.TaskBackupXmlPath, "/F"],
                SchtasksCommandKind.Mutation,
                cancellationToken).ConfigureAwait(false);
            mutationStarted = true;
            if (create.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"Cannot recreate {metadata.TaskName}; schtasks exited {create.ExitCode}.");
            }

            currentEnabled = !metadata.OriginallyEnabled;
        }
        else
        {
            throw new InvalidOperationException(
                $"Cannot safely inspect {metadata.TaskName}; schtasks exited {current.ExitCode}. " +
                "The result did not prove that the task is missing, so no mutation was attempted.");
        }

        if (mutationStarted || currentEnabled != metadata.OriginallyEnabled)
        {
            var desiredState = metadata.OriginallyEnabled ? "/Enable" : "/Disable";
            var change = await processRunner.RunAsync(
                    ["/Change", "/TN", metadata.TaskName, desiredState],
                    SchtasksCommandKind.Mutation,
                    mutationStarted ? CancellationToken.None : cancellationToken)
                .ConfigureAwait(false);
            mutationStarted = true;
            if (change.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"Cannot restore {metadata.TaskName} enabled state; schtasks exited {change.ExitCode}.");
            }
        }

        var finalInspection = mutationStarted
            ? await InspectCurrentAgainstBackupAsync(metadata, CancellationToken.None).ConfigureAwait(false)
            : CreateInspection(current.StandardOutput, verified.Xml);
        if (!finalInspection.DefinitionMatchesBackup ||
            finalInspection.Enabled != metadata.OriginallyEnabled)
        {
            throw new InvalidOperationException(
                "The restored Myth.Cool task did not match the verified definition and enabled state.");
        }

        if (startMythCool)
        {
            if (!metadata.OriginallyEnabled)
            {
                throw new InvalidOperationException(
                    "Myth.Cool was disabled before takeover and cannot be started implicitly.");
            }

            // /Run is another mutation and must not inherit a cancellation that arrived after
            // the enabled-state restoration already began.
            var run = await processRunner.RunAsync(
                    ["/Run", "/TN", metadata.TaskName],
                    SchtasksCommandKind.Mutation,
                    CancellationToken.None)
                .ConfigureAwait(false);
            if (run.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"Cannot start {metadata.TaskName}; schtasks exited {run.ExitCode}.");
            }
        }

        logger.LogInformation("Restored scheduled task {TaskName}; start requested: {StartRequested}.",
            metadata.TaskName, startMythCool);
    }

    public async Task<bool> GetEnabledAsync(CancellationToken cancellationToken = default)
    {
        var query = await processRunner.RunAsync(
            ["/Query", "/TN", ServiceConstants.MythCoolTaskName, "/XML"],
            SchtasksCommandKind.Query,
            cancellationToken)
            .ConfigureAwait(false);
        if (query.ExitCode != 0 || string.IsNullOrWhiteSpace(query.StandardOutput))
        {
            throw new InvalidOperationException(
                $"Cannot query {ServiceConstants.MythCoolTaskName}; schtasks exited {query.ExitCode}.");
        }

        return ReadEnabled(NormalizeXmlForUtf8(query.StandardOutput));
    }

    public async Task<ScheduledTaskBackupMetadata?> TryReadVerifiedBackupAsync(
        CancellationToken cancellationToken = default)
    {
        var verified = await TryReadVerifiedBackupWithXmlAsync(cancellationToken).ConfigureAwait(false);
        return verified?.Metadata;
    }

    public async Task<ScheduledTaskRecoveryInspection> InspectCurrentAgainstBackupAsync(
        ScheduledTaskBackupMetadata expectedBackup,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expectedBackup);
        var verified = await TryReadVerifiedBackupWithXmlAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException(
                "A verified Myth.Cool scheduled-task backup is not available for recovery inspection.");
        if (!BackupMetadataMatches(verified.Metadata, expectedBackup))
        {
            throw new InvalidDataException(
                "The verified Myth.Cool scheduled-task backup changed after the recovery journal was written.");
        }

        var query = await processRunner.RunAsync(
                ["/Query", "/TN", expectedBackup.TaskName, "/XML"],
                SchtasksCommandKind.Query,
                cancellationToken)
            .ConfigureAwait(false);
        if (query.ExitCode != 0 || string.IsNullOrWhiteSpace(query.StandardOutput))
        {
            throw new InvalidDataException(
                $"Cannot inspect {expectedBackup.TaskName} for recovery; schtasks exited {query.ExitCode}.");
        }

        var currentXml = NormalizeXmlForUtf8(query.StandardOutput);
        var currentHash = Sha256(currentXml);
        return new ScheduledTaskRecoveryInspection(
            ReadEnabled(currentXml),
            currentHash,
            FixedTimeHashEquals(currentHash, verified.Metadata.XmlSha256),
            FixedTimeHashEquals(
                TaskDefinitionSha256(currentXml),
                TaskDefinitionSha256(verified.Xml)));
    }

    private async Task<(ScheduledTaskBackupMetadata Metadata, string Xml)?> TryReadVerifiedBackupWithXmlAsync(
        CancellationToken cancellationToken)
    {
        var metadataJson = await ReadOptionalFileAsync(
                paths.TaskBackupMetadataPath,
                cancellationToken)
            .ConfigureAwait(false);
        var xml = await ReadOptionalFileAsync(paths.TaskBackupXmlPath, cancellationToken)
            .ConfigureAwait(false);
        if (metadataJson is null && xml is null)
        {
            return null;
        }

        if (metadataJson is null || xml is null)
        {
            throw new InvalidDataException(
                "The Myth.Cool scheduled-task backup is only partially present and cannot be trusted.");
        }

        try
        {
            var metadata = JsonSerializer.Deserialize<ScheduledTaskBackupMetadata>(metadataJson, jsonOptions);
            if (metadata is null ||
                !string.Equals(metadata.TaskName, ServiceConstants.MythCoolTaskName, StringComparison.Ordinal) ||
                !IsSha256Hex(metadata.XmlSha256) ||
                !CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(metadata.XmlSha256),
                    Convert.FromHexString(Sha256(xml))) ||
                metadata.OriginallyEnabled != ReadEnabled(xml))
            {
                throw new InvalidDataException("Myth.Cool scheduled-task backup integrity check failed.");
            }

            return (metadata, xml);
        }
        catch (Exception exception) when (exception is JsonException or FormatException or CryptographicException
            or System.Xml.XmlException)
        {
            throw new InvalidDataException("Myth.Cool scheduled-task backup cannot be verified.", exception);
        }
    }

    private static async Task<string?> ReadOptionalFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        try
        {
            return await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    private static bool ReadEnabled(string xml)
    {
        var document = XDocument.Parse(xml, LoadOptions.None);
        var (_, enabled) = FindTaskSettingsEnabled(document);
        return enabled is null || !string.Equals(enabled.Value.Trim(), "false", StringComparison.OrdinalIgnoreCase);
    }

    private static ScheduledTaskRecoveryInspection CreateInspection(
        string currentXml,
        string backupXml)
    {
        var normalizedCurrent = NormalizeXmlForUtf8(currentXml);
        var currentHash = Sha256(normalizedCurrent);
        var backupHash = Sha256(backupXml);
        return new ScheduledTaskRecoveryInspection(
            ReadEnabled(normalizedCurrent),
            currentHash,
            FixedTimeHashEquals(currentHash, backupHash),
            FixedTimeHashEquals(
                TaskDefinitionSha256(normalizedCurrent),
                TaskDefinitionSha256(backupXml)));
    }

    private static bool IsDefinitelyMissingTask(SchtasksProcessResult result)
    {
        if (result.ExitCode == 0 || !string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            return false;
        }

        var error = result.StandardError;
        return error.Contains("0x80070002", StringComparison.OrdinalIgnoreCase) ||
               error.Contains("ERROR_FILE_NOT_FOUND", StringComparison.OrdinalIgnoreCase) ||
               error.Contains("The system cannot find the file specified", StringComparison.OrdinalIgnoreCase) ||
               error.Contains("系统找不到指定的文件", StringComparison.Ordinal);
    }

    private static string TaskDefinitionSha256(string xml)
    {
        var document = XDocument.Parse(xml, LoadOptions.None);
        var (_, enabled) = FindTaskSettingsEnabled(document);
        enabled?.Remove();

        var canonical = new StringBuilder();
        AppendCanonicalElement(document.Root!, canonical);
        return Sha256(canonical.ToString());
    }

    private static (XElement Root, XElement? Enabled) FindTaskSettingsEnabled(XDocument document)
    {
        var root = document.Root;
        if (root is null || !string.Equals(root.Name.LocalName, "Task", StringComparison.Ordinal))
        {
            throw new InvalidDataException("The scheduled-task XML does not have a Task document root.");
        }

        var settingsName = root.Name.Namespace + "Settings";
        var settings = root.Elements()
            .Where(element => element.Name == settingsName)
            .ToArray();
        if (settings.Length > 1)
        {
            throw new InvalidDataException("The scheduled-task XML contains multiple Settings elements.");
        }

        var enabledName = root.Name.Namespace + "Enabled";
        var enabledElements = settings.Length == 0
            ? []
            : settings[0].Elements()
                .Where(element => element.Name == enabledName)
                .ToArray();
        if (enabledElements.Length > 1)
        {
            throw new InvalidDataException("The scheduled-task XML contains multiple Settings/Enabled elements.");
        }

        return (root, enabledElements.SingleOrDefault());
    }

    private static void AppendCanonicalElement(XElement element, StringBuilder builder)
    {
        AppendCanonicalToken(builder, 'E', ExpandedName(element.Name));
        foreach (var attribute in element.Attributes()
                     .Where(static attribute => !attribute.IsNamespaceDeclaration)
                     .OrderBy(static attribute => ExpandedName(attribute.Name), StringComparer.Ordinal)
                     .ThenBy(static attribute => attribute.Value, StringComparer.Ordinal))
        {
            AppendCanonicalToken(builder, 'A', ExpandedName(attribute.Name));
            AppendCanonicalToken(builder, 'V', attribute.Value);
        }

        foreach (var node in element.Nodes())
        {
            switch (node)
            {
                case XElement child:
                    AppendCanonicalElement(child, builder);
                    break;
                case XCData cdata when !string.IsNullOrWhiteSpace(cdata.Value):
                    AppendCanonicalToken(builder, 'T', cdata.Value);
                    break;
                case XText text when !string.IsNullOrWhiteSpace(text.Value):
                    AppendCanonicalToken(builder, 'T', text.Value);
                    break;
            }
        }

        builder.Append('Z');
    }

    private static void AppendCanonicalToken(StringBuilder builder, char kind, string value) =>
        builder.Append(kind).Append(value.Length).Append(':').Append(value);

    private static string ExpandedName(XName name) => $"{{{name.NamespaceName}}}{name.LocalName}";

    private static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static bool IsSha256Hex(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static bool BackupMetadataMatches(
        ScheduledTaskBackupMetadata actual,
        ScheduledTaskBackupMetadata expected) =>
        string.Equals(actual.TaskName, expected.TaskName, StringComparison.Ordinal) &&
        actual.OriginallyEnabled == expected.OriginallyEnabled &&
        IsSha256Hex(actual.XmlSha256) &&
        IsSha256Hex(expected.XmlSha256) &&
        FixedTimeHashEquals(actual.XmlSha256, expected.XmlSha256);

    private static bool FixedTimeHashEquals(string left, string right) =>
        IsSha256Hex(left) && IsSha256Hex(right) &&
        CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(left),
            Convert.FromHexString(right));

    private async Task VerifyEnabledAsync(bool expectedEnabled, CancellationToken cancellationToken)
    {
        var actualEnabled = await GetEnabledAsync(cancellationToken).ConfigureAwait(false);
        if (actualEnabled != expectedEnabled)
        {
            throw new InvalidOperationException(
                $"Scheduled-task readback mismatch: expected Enabled={expectedEnabled}, actual Enabled={actualEnabled}.");
        }
    }

    private static string NormalizeXmlForUtf8(string xml) => Regex.Replace(
        xml.TrimStart('\uFEFF'),
        "encoding\\s*=\\s*[\"']UTF-16[\"']",
        "encoding=\"UTF-8\"",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));
}
