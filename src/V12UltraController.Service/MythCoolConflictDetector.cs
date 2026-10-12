using System.ComponentModel;
using System.Diagnostics;

namespace V12UltraController.Service;

internal interface IMythCoolConflictDetector
{
    MythCoolConflictSnapshot Inspect();
}

internal sealed record MythCoolConflictSnapshot(
    bool HasConflict,
    IReadOnlyList<string> Processes,
    bool InspectionFailed = false);

internal interface IProcessPresenceSource
{
    bool HasProcessNamed(string processName);
}

internal sealed class SystemProcessPresenceSource : IProcessPresenceSource
{
    public bool HasProcessNamed(string processName)
    {
        var processes = Process.GetProcessesByName(processName);
        try
        {
            return processes.Length > 0;
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }
}

internal sealed class MythCoolConflictDetector : IMythCoolConflictDetector
{
    private static readonly string[] ConflictingProcessNames = ["MythCool", "GPP_USB_Center"];
    private readonly IProcessPresenceSource processPresenceSource;

    public MythCoolConflictDetector()
        : this(new SystemProcessPresenceSource())
    {
    }

    internal MythCoolConflictDetector(IProcessPresenceSource processPresenceSource)
    {
        ArgumentNullException.ThrowIfNull(processPresenceSource);
        this.processPresenceSource = processPresenceSource;
    }

    public MythCoolConflictSnapshot Inspect()
    {
        var found = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var processName in ConflictingProcessNames)
        {
            try
            {
                if (processPresenceSource.HasProcessNamed(processName))
                {
                    // Return only the fixed allow-listed display name. Never surface
                    // process IDs, paths, command lines, or exception messages.
                    found.Add($"{processName}.exe");
                }
            }
            catch (Exception exception) when (IsRecoverableInspectionFailure(exception))
            {
                // An incomplete process snapshot cannot establish that the vendor
                // controller is absent. Fail closed without exposing exception data.
                return new MythCoolConflictSnapshot(true, [], InspectionFailed: true);
            }
        }

        return new MythCoolConflictSnapshot(found.Count > 0, found.ToArray());
    }

    private static bool IsRecoverableInspectionFailure(Exception exception) =>
        exception is Win32Exception or InvalidOperationException or NotSupportedException or
            UnauthorizedAccessException or IOException;
}
