using System.Security;
using System.Security.Principal;
using Microsoft.Win32;

namespace V12UltraController.Core.Security;

/// <summary>
/// Reads the one installer-owned principal that may use the local named-pipe API.
/// Implementations are read-only; the MSI is the only writer of this value.
/// </summary>
public interface IInstallerEnrollmentReader
{
    string? ReadAuthorizedUserSid();
}

/// <summary>
/// Reads the installer enrollment from the 64-bit machine registry view.
/// </summary>
public sealed class WindowsRegistryInstallerEnrollmentReader : IInstallerEnrollmentReader
{
    public const string RegistrySubKey = @"Software\TianqiStudio\V12UltraController\Enrollment";
    public const string RegistryValueName = "AuthorizedUserSid";

    public string? ReadAuthorizedUserSid()
    {
        using var localMachine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var enrollment = localMachine.OpenSubKey(RegistrySubKey, writable: false);
        var value = enrollment?.GetValue(
            RegistryValueName,
            defaultValue: null,
            RegistryValueOptions.DoNotExpandEnvironmentNames);

        return value switch
        {
            null => null,
            string text => text,
            _ => throw new InvalidDataException(
                "The installer enrollment registry value must be a string."),
        };
    }
}

public static class InstallerEnrollmentSid
{
    /// <summary>
    /// Parses a SID that identifies one concrete Windows account rather than a
    /// machine-wide, group, service, logon-session, or capability principal.
    /// The MSI supplies this value from Windows Installer's UserSID property.
    /// </summary>
    public static SecurityIdentifier ParseSpecificUser(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidDataException("The authorized-user SID is missing or empty.");
        }

        SecurityIdentifier sid;
        try
        {
            sid = new SecurityIdentifier(value.Trim());
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException("The authorized-user SID is malformed.", exception);
        }

        // Normal local/domain users have an account SID below S-1-5-21. Entra ID
        // users are represented locally as S-1-12-1 plus four 32-bit subauthorities.
        // Both forms identify one account. Other authorities include broad groups,
        // services, logon sessions, app containers and capabilities and are rejected.
        if (!sid.IsAccountSid() && !IsEntraUserSid(sid.Value))
        {
            throw new InvalidDataException(
                "The authorized-user SID must identify one specific Windows user.");
        }

        return sid;
    }

    public static string? ReadAndValidate(IInstallerEnrollmentReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var value = reader.ReadAuthorizedUserSid();
        return value is null ? null : ParseSpecificUser(value).Value;
    }

    public static bool IsExpectedReadFailure(Exception exception) =>
        exception is InvalidDataException or IOException or UnauthorizedAccessException or SecurityException;

    private static bool IsEntraUserSid(string value)
    {
        var parts = value.Split('-', StringSplitOptions.None);
        if (parts.Length != 8 ||
            !string.Equals(parts[0], "S", StringComparison.OrdinalIgnoreCase) ||
            parts[1] != "1" || parts[2] != "12" || parts[3] != "1")
        {
            return false;
        }

        return parts.Skip(4).All(static part => uint.TryParse(part, out _));
    }
}
