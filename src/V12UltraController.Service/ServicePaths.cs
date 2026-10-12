namespace V12UltraController.Service;

internal sealed class ServicePaths
{
    public ServicePaths()
        : this(Environment.GetEnvironmentVariable("V12ULTRA_DATA_DIR"))
    {
    }

    internal ServicePaths(string? dataDirectory)
    {
        DataDirectory = string.IsNullOrWhiteSpace(dataDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "TianqiStudio", "V12UltraController")
            : Path.GetFullPath(dataDirectory);
    }

    public string DataDirectory { get; }
    public string ConfigurationPath => Path.Combine(DataDirectory, "controller.json");
    public string SafetyProtectionPath => Path.Combine(DataDirectory, "safety-protection-intent.json");
    public string RuntimeStatePath => Path.Combine(DataDirectory, "runtime-state.json");
    public string DeviceStateCachePath => Path.Combine(DataDirectory, "device-state-cache.json");
    public string TransitionJournalPath => Path.Combine(DataDirectory, "takeover-transition.json");
    public string LogsDirectory => Path.Combine(DataDirectory, "logs");
    public string AuditLogPath => Path.Combine(LogsDirectory, "audit.jsonl");
    public string AuditMutationPendingPath => Path.Combine(DataDirectory, "audit-mutation-pending.json");
    public string SafetyOperationAuditPendingPath =>
        Path.Combine(DataDirectory, "safety-operation-audit-pending.json");
    public string ServiceLogPath => Path.Combine(LogsDirectory, "service.jsonl");
    public string WriterLeasePath => Path.Combine(DataDirectory, "device-writer.lock");
    public string TaskBackupDirectory => Path.Combine(DataDirectory, "mythcool-task-backup");
    public string TaskBackupXmlPath => Path.Combine(TaskBackupDirectory, "Myth.Cool.xml");
    public string TaskBackupMetadataPath => Path.Combine(TaskBackupDirectory, "metadata.json");

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(TaskBackupDirectory);
    }
}
