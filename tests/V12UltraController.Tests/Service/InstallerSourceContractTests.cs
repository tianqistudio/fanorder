using System.Xml.Linq;
using System.Text.Json;

namespace V12UltraController.Tests.Service;

public sealed class InstallerSourceContractTests
{
    private static readonly XNamespace Wix = "http://wixtoolset.org/schemas/v4/wxs";
    private static readonly XNamespace Util = "http://wixtoolset.org/schemas/v4/wxs/util";

    [Fact]
    public void Build_CandidateOutputIsFreshAndCannotBeAnInstallerRelease()
    {
        var script = File.ReadAllText(ProjectPath("installer", "build.ps1"));
        Assert.Contains("CandidateId requires PublishOnly", script, StringComparison.Ordinal);
        Assert.Contains("Test-Path -LiteralPath $artifactsDirectory", script, StringComparison.Ordinal);
        Assert.Contains("Refusing to overwrite existing candidate", script, StringComparison.Ordinal);
        Assert.True(script.IndexOf("if ($CandidateId)", StringComparison.Ordinal) <
                    script.IndexOf("Reset-ProjectDirectory -LiteralPath $publishDirectory", StringComparison.Ordinal));
    }

    [Fact]
    public void Package_PreservesManualStartupWithoutChangingShutdownOrTrustingCommandLine()
    {
        var document = LoadXml("installer", "Package", "Package.wxs");
        var property = FindById(document, "Property", "V12ExistingServiceStart");
        Assert.Null(property.Attribute("Value"));
        Assert.Null(property.Attribute("Secure"));
        Assert.NotEqual("V12EXISTINGSERVICESTART", (string?)property.Attribute("Id"));
        var search = Assert.Single(property.Elements(Wix + "RegistrySearch"));
        Assert.Equal("HKLM", (string?)search.Attribute("Root"));
        Assert.Equal(@"SYSTEM\CurrentControlSet\Services\TianqiStudio.V12UltraController", (string?)search.Attribute("Key"));
        Assert.Equal("Start", (string?)search.Attribute("Name"));
        Assert.Equal("raw", (string?)search.Attribute("Type"));
        Assert.Equal("always64", (string?)search.Attribute("Bitness"));
        var launch = Assert.Single(document.Descendants(Wix + "Launch"), e =>
            ((string?)e.Attribute("Condition"))?.Contains("V12ExistingServiceStart", StringComparison.Ordinal) == true);
        Assert.Equal("REMOVE~=\"ALL\" OR NOT V12ExistingServiceStart OR V12ExistingServiceStart = \"#2\" OR V12ExistingServiceStart = \"#3\"",
            (string?)launch.Attribute("Condition"));
        foreach (var (id, execution, placement) in new[] {
            ("RestoreManualStartup", "deferred", "After"), ("RollbackManualStartup", "rollback", "Before") })
        {
            var action = FindById(document, "CustomAction", id);
            Assert.Equal("System64Folder", (string?)action.Attribute("Directory"));
            Assert.Equal("\"[System64Folder]sc.exe\" config TianqiStudio.V12UltraController start= demand",
                (string?)action.Attribute("ExeCommand"));
            Assert.Equal(execution, (string?)action.Attribute("Execute"));
            Assert.Equal("no", (string?)action.Attribute("Impersonate"));
            Assert.Equal("check", (string?)action.Attribute("Return"));
            var scheduled = Assert.Single(document.Descendants(Wix + "Custom"), e => (string?)e.Attribute("Action") == id);
            Assert.Equal("InstallServices", (string?)scheduled.Attribute(placement));
            Assert.Equal("NOT REMOVE~=\"ALL\" AND V12ExistingServiceStart = \"#3\"", (string?)scheduled.Attribute("Condition"));
        }
        Assert.Equal("auto", (string?)FindById(document, "ServiceInstall", "ControllerServiceInstall").Attribute("Start"));
    }

    [Fact]
    public void Package_ChecksPawnIoRegistrationWithoutBlockingExistingProductMaintenance()
    {
        var document = LoadXml("installer", "Package", "Package.wxs");
        var property = FindById(document, "Property", "V12PawnIoRegisteredVersion");
        Assert.Null(property.Attribute("Value"));
        Assert.NotEqual("V12PawnIoRegisteredVersion".ToUpperInvariant(), (string?)property.Attribute("Id"));
        var search = Assert.Single(property.Elements(Wix + "RegistrySearch"));
        Assert.Equal("HKLM", (string?)search.Attribute("Root"));
        Assert.Equal(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO",
            (string?)search.Attribute("Key"));
        Assert.Equal("DisplayVersion", (string?)search.Attribute("Name"));
        Assert.Equal("raw", (string?)search.Attribute("Type"));
        Assert.Equal("always64", (string?)search.Attribute("Bitness"));

        var condition = Assert.Single(document.Descendants(Wix + "Launch"), element =>
            ((string?)element.Attribute("Condition"))?.Contains("V12PawnIoRegisteredVersion", StringComparison.Ordinal) == true);
        Assert.Equal("Installed OR V12PawnIoRegisteredVersion = \"2.2.0.0\"",
            (string?)condition.Attribute("Condition"));
        var message = (string?)condition.Attribute("Message");
        Assert.Contains("不要选择 Unrestricted", message, StringComparison.Ordinal);
        Assert.Contains("不要自动降级", message, StringComparison.Ordinal);
        Assert.Contains("不代表驱动或 CPU 温度已通过验收", message, StringComparison.Ordinal);

        // A missing prerequisite must not trigger an installer download or driver mutation.
        var bundle = LoadXml("installer", "Bundle", "Bundle.wxs");
        Assert.Empty(bundle.Descendants(Wix + "ExePackage"));
        Assert.Empty(bundle.Descendants().Attributes("DownloadUrl"));
        Assert.DoesNotContain(document.Descendants(Wix + "CustomAction"), action =>
            action.ToString().Contains("PawnIO", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Package_RestrictsOperatingSystemAndPreservesOriginalEnrollment()
    {
        var document = LoadXml("installer", "Package", "Package.wxs");
        var launch = document.Descendants(Wix + "Launch")
            .Single(element => ((string?)element.Attribute("Message"))?.Contains("Windows 10/11") == true);
        var condition = (string?)launch.Attribute("Condition");
        Assert.Contains("VersionNT64", condition, StringComparison.Ordinal);
        Assert.DoesNotContain("VersionNT >=", condition, StringComparison.Ordinal);
        Assert.Contains("V12WindowsBuildNumber >= 17763", condition, StringComparison.Ordinal);
        Assert.Contains("PawnIO", (string?)launch.Attribute("Message"), StringComparison.Ordinal);
        Assert.Contains("MsiNTProductType = 1", condition, StringComparison.Ordinal);

        var osBuildProperty = FindById(document, "Property", "V12WindowsBuildNumber");
        Assert.Null(osBuildProperty.Attribute("Value"));
        var osBuildSearch = Assert.Single(osBuildProperty.Elements(Wix + "RegistrySearch"));
        Assert.Equal("HKLM", (string?)osBuildSearch.Attribute("Root"));
        Assert.Equal(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion",
            (string?)osBuildSearch.Attribute("Key"));
        Assert.Equal("CurrentBuildNumber", (string?)osBuildSearch.Attribute("Name"));
        Assert.Equal("raw", (string?)osBuildSearch.Attribute("Type"));
        Assert.Equal("always64", (string?)osBuildSearch.Attribute("Bitness"));

        var enrollment = FindById(document, "Component", "ProgramDataIdentityComponent");
        Assert.Null(enrollment.Attribute("Permanent"));
        Assert.Equal("yes", (string?)enrollment.Attribute("NeverOverwrite"));
        Assert.Equal("always64", (string?)enrollment.Attribute("Bitness"));
        Assert.Equal("DataFolder", (string?)enrollment.Attribute("Directory"));
        Assert.Equal("3FF9ADF9-40C8-4270-87E4-01381EBAF31B",
            (string?)enrollment.Attribute("Guid"));
        var enrollmentValue = FindById(document, "RegistryValue", "AuthorizedUserSidRegistryValue");
        Assert.Equal("HKLM", (string?)enrollmentValue.Attribute("Root"));
        Assert.Equal(@"Software\TianqiStudio\V12UltraController\Enrollment",
            (string?)enrollmentValue.Attribute("Key"));
        Assert.Equal("AuthorizedUserSid", (string?)enrollmentValue.Attribute("Name"));
        Assert.Equal("string", (string?)enrollmentValue.Attribute("Type"));
        Assert.Equal("[UserSID]", (string?)enrollmentValue.Attribute("Value"));
        Assert.Equal("yes", (string?)enrollmentValue.Attribute("KeyPath"));
        var enrollmentAcl = FindById(document, "PermissionEx", "AuthorizedUserSidRegistryAcl");
        Assert.Equal(Wix, enrollmentAcl.Name.Namespace);
        Assert.Equal("D:P(A;;KA;;;SY)(A;;KA;;;BA)", (string?)enrollmentAcl.Attribute("Sddl"));

        var majorUpgrade = document.Descendants(Wix + "MajorUpgrade").Single();
        Assert.Equal("afterInstallExecute", (string?)majorUpgrade.Attribute("Schedule"));
        Assert.Empty(document.Descendants(Util + "FormatFile"));
        Assert.DoesNotContain(document.Descendants(Wix + "File"), element =>
            string.Equals((string?)element.Attribute("Name"), "authorized-user.sid",
                StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(document.Descendants(Wix + "Binary"), element =>
            string.Equals((string?)element.Attribute("Id"), "AuthorizedUserSidTemplate",
                StringComparison.Ordinal));
    }

    [Fact]
    public void Package_PreflightsFreshRepairAndMajorEnrollmentBeforeRegistryWrites()
    {
        var document = LoadXml("installer", "Package", "Package.wxs");
        var fresh = FindById(document, "CustomAction", "PreflightFreshInstallerEnrollment");
        var existing = FindById(document, "CustomAction", "PreflightExistingInstallerEnrollment");
        var validateFresh = FindById(document, "CustomAction", "ValidateFreshInstallerEnrollment");

        AssertFixedInstalledCliPreflight(fresh, "installer-preflight fresh");
        AssertFixedInstalledCliPreflight(existing, "installer-preflight existing");
        AssertFixedInstalledCliPreflight(validateFresh, "installer-preflight existing");

        var sequence = document.Descendants(Wix + "InstallExecuteSequence").Single();
        var freshSequence = sequence.Elements(Wix + "Custom").Single(element =>
            (string?)element.Attribute("Action") == "PreflightFreshInstallerEnrollment");
        Assert.Equal("InstallFiles", (string?)freshSequence.Attribute("After"));
        Assert.Equal(
            "NOT Installed AND NOT WIX_UPGRADE_DETECTED AND NOT REMOVE~=&quot;ALL&quot;"
                .Replace("&quot;", "\"", StringComparison.Ordinal),
            (string?)freshSequence.Attribute("Condition"));

        var existingSequence = sequence.Elements(Wix + "Custom").Single(element =>
            (string?)element.Attribute("Action") == "PreflightExistingInstallerEnrollment");
        Assert.Equal("WriteRegistryValues", (string?)existingSequence.Attribute("Before"));
        Assert.Equal(
            "(Installed OR WIX_UPGRADE_DETECTED) AND NOT REMOVE~=&quot;ALL&quot;"
                .Replace("&quot;", "\"", StringComparison.Ordinal),
            (string?)existingSequence.Attribute("Condition"));

        var validateFreshSequence = sequence.Elements(Wix + "Custom").Single(element =>
            (string?)element.Attribute("Action") == "ValidateFreshInstallerEnrollment");
        Assert.Equal("WriteRegistryValues", (string?)validateFreshSequence.Attribute("After"));
        Assert.Equal(
            "NOT Installed AND NOT WIX_UPGRADE_DETECTED AND NOT REMOVE~=&quot;ALL&quot;"
                .Replace("&quot;", "\"", StringComparison.Ordinal),
            (string?)validateFreshSequence.Attribute("Condition"));
    }

    [Fact]
    public void Package_HoldsServiceRecoveryAclAndFailClosedUninstallContracts()
    {
        var document = LoadXml("installer", "Package", "Package.wxs");
        var service = FindById(document, "ServiceInstall", "ControllerServiceInstall");
        Assert.Equal("TianqiStudio.V12UltraController", (string?)service.Attribute("Name"));
        Assert.Equal("auto", (string?)service.Attribute("Start"));
        Assert.Equal("LocalSystem", (string?)service.Attribute("Account"));
        var recovery = service.Element(Wix + "ServiceConfigFailureActions");
        Assert.NotNull(recovery);
        Assert.Equal("86400", (string?)recovery.Attribute("ResetPeriod"));
        Assert.Equal(
            ["5000", "15000", "60000"],
            recovery.Elements(Wix + "Failure").Select(element => (string)element.Attribute("Delay")!).ToArray());

        var dataAcl = FindById(document, "PermissionEx", "ProgramDataDirectoryAcl");
        Assert.Equal(Wix, dataAcl.Name.Namespace);
        Assert.Equal("D:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)", (string?)dataAcl.Attribute("Sddl"));
        var logsAcl = FindById(document, "PermissionEx", "LogsDirectoryAcl");
        Assert.Equal(Wix, logsAcl.Name.Namespace);
        Assert.Equal("D:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)", (string?)logsAcl.Attribute("Sddl"));
        var cleanup = FindById(document, "RemoveFolderEx", "RemoveProgramDataOnExplicitUninstall");
        Assert.Equal(Util, cleanup.Name.Namespace);
        Assert.Equal("V12DataPath", (string?)cleanup.Attribute("Property"));
        Assert.Equal(
            "REMOVE_DATA = 1 AND FORCE_UNINSTALL <> 1 AND REMOVE~=&quot;ALL&quot; AND NOT UPGRADINGPRODUCTCODE"
                .Replace("&quot;", "\"", StringComparison.Ordinal),
            (string?)cleanup.Attribute("Condition"));

        var mutuallyExclusiveMaintenanceFlags = document.Descendants(Wix + "Launch")
            .Single(element =>
                (string?)element.Attribute("Condition") == "FORCE_UNINSTALL = 0 OR REMOVE_DATA = 0");
        Assert.Contains("恢复标记", (string?)mutuallyExclusiveMaintenanceFlags.Attribute("Message"),
            StringComparison.Ordinal);

        var rollback = FindById(document, "CustomAction", "RollbackCommissioningOnUninstall");
        Assert.Equal("CliExe", (string?)rollback.Attribute("FileRef"));
        Assert.Equal("no", (string?)rollback.Attribute("Impersonate"));
        Assert.Equal("check", (string?)rollback.Attribute("Return"));
        var rollbackSequence = document.Descendants(Wix + "Custom")
            .Single(element => (string?)element.Attribute("Action") == "RollbackCommissioningOnUninstall");
        Assert.Equal("StopServices", (string?)rollbackSequence.Attribute("Before"));
        var condition = (string?)rollbackSequence.Attribute("Condition");
        Assert.Contains("NOT UPGRADINGPRODUCTCODE", condition, StringComparison.Ordinal);
        Assert.Contains("FORCE_UNINSTALL <> 1", condition, StringComparison.Ordinal);

        var start = FindById(document, "CustomAction", "StartControllerForUninstallRollback");
        Assert.Equal("System64Folder", (string?)start.Attribute("Directory"));
        Assert.Equal("\"[System64Folder]sc.exe\" start TianqiStudio.V12UltraController",
            (string?)start.Attribute("ExeCommand"));
        Assert.Equal("deferred", (string?)start.Attribute("Execute"));
        Assert.Equal("no", (string?)start.Attribute("Impersonate"));
        Assert.Equal("ignore", (string?)start.Attribute("Return"));
        var startSequence = document.Descendants(Wix + "Custom")
            .Single(element =>
                (string?)element.Attribute("Action") == "StartControllerForUninstallRollback");
        Assert.Equal("RollbackCommissioningOnUninstall", (string?)startSequence.Attribute("Before"));
        Assert.Equal(condition, (string?)startSequence.Attribute("Condition"));

        var forcedMarker = FindById(document, "CustomAction", "MarkForcedUninstallRecovery");
        Assert.Equal("CliExe", (string?)forcedMarker.Attribute("FileRef"));
        Assert.Equal("installer mark-forced-uninstall",
            (string?)forcedMarker.Attribute("ExeCommand"));
        Assert.Equal("deferred", (string?)forcedMarker.Attribute("Execute"));
        Assert.Equal("no", (string?)forcedMarker.Attribute("Impersonate"));
        Assert.Equal("check", (string?)forcedMarker.Attribute("Return"));
        Assert.Null(forcedMarker.Attribute("Directory"));
        Assert.Null(forcedMarker.Attribute("Property"));

        var forcedMarkerSequence = document.Descendants(Wix + "Custom")
            .Single(element =>
                (string?)element.Attribute("Action") == "MarkForcedUninstallRecovery");
        Assert.Equal("StopServices", (string?)forcedMarkerSequence.Attribute("Before"));
        Assert.Equal(
            "REMOVE~=\"ALL\" AND NOT UPGRADINGPRODUCTCODE AND FORCE_UNINSTALL = 1",
            (string?)forcedMarkerSequence.Attribute("Condition"));
    }

    [Fact]
    public void Package_CliShortcutKeepsHelpWindowOpen()
    {
        var document = LoadXml("installer", "Package", "Package.wxs");
        var shortcut = FindById(document, "Shortcut", "CliStartMenuShortcut");

        Assert.Equal("[System64Folder]cmd.exe", (string?)shortcut.Attribute("Target"));
        Assert.Contains("/K", (string?)shortcut.Attribute("Arguments"), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("v12ctl.exe", (string?)shortcut.Attribute("Arguments"), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ShortcutChoicesAreIndependentDefaultOnAndPreserved()
    {
        var package = LoadXml("installer", "Package", "Package.wxs");
        var bundle = LoadXml("installer", "Bundle", "Bundle.wxs");
        Assert.Equal("tianqistudio", (string?)FindById(package, "Directory", "ProgramsFolder").Attribute("Name"));
        foreach (var (variable, property, component, saved) in new[] {
            ("CreateDesktopShortcut", "CREATE_DESKTOP_SHORTCUT", "DesktopShortcutComponent", "FanOrderSavedDesktop"),
            ("CreateStartMenuShortcut", "CREATE_START_MENU_SHORTCUT", "StartMenuCleanupComponent", "FanOrderSavedStartMenu") })
        {
            var setting = bundle.Descendants(Wix + "Variable").Single(x => (string?)x.Attribute("Name") == variable);
            Assert.Equal("1", (string?)setting.Attribute("Value"));
            Assert.Equal("yes", (string?)setting.Attribute("Persisted"));
            Assert.Equal("1", (string?)FindById(package, "Property", property).Attribute("Value"));
            Assert.Equal(property + " = 1", (string?)FindById(package, "Component", component).Attribute("Condition"));
            var restore = package.Descendants(Wix + "SetProperty").Where(x => (string?)x.Attribute("Id") == property).ToArray();
            Assert.Equal(2, restore.Length);
            foreach (var value in new[] { "0", "1" })
                Assert.Contains(restore, x => (string?)x.Attribute("Value") == value &&
                    (string?)x.Attribute("Condition") == saved + " = \"#" + value + "\"" &&
                    (string?)x.Attribute("Before") == "CostFinalize" && (string?)x.Attribute("Sequence") == "both");
        }
        Assert.Empty(FindById(package, "Component", "SettingsExecutableComponent").Descendants(Wix + "Shortcut"));
        Assert.Empty(FindById(package, "Component", "CliExecutableComponent").Descendants(Wix + "Shortcut"));
    }

    [Fact]
    public void Package_InstallsEveryAuditedLicenseAndNoticeFile()
    {
        var packagePath = ProjectPath("installer", "Package", "Package.wxs");
        var packageDirectory = Path.GetDirectoryName(packagePath)!;
        var document = XDocument.Load(packagePath);
        var expected = new Dictionary<string, (string FileId, string Name, string Source)>(StringComparer.Ordinal)
        {
            ["MplLicenseComponent"] =
                ("MplLicenseFile", "MPL-2.0.txt", @"..\..\docs\licenses\MPL-2.0.txt"),
            ["ApacheLicenseComponent"] =
                ("ApacheLicenseFile", "Apache-2.0.txt", @"..\..\docs\licenses\Apache-2.0.txt"),
            ["DotNetMitLicenseComponent"] =
                ("DotNetMitLicenseFile", "MIT-DotNet.txt", @"..\..\docs\licenses\MIT-DotNet.txt"),
            ["DotNetThirdPartyNoticesComponent"] =
                ("DotNetThirdPartyNoticesFile", "DotNet-10.0.11-ThirdPartyNotices.txt",
                    @"..\..\docs\licenses\DotNet-10.0.11-ThirdPartyNotices.txt"),
            ["MsrlLicenseComponent"] =
                ("MsrlLicenseFile", "MS-RL.txt", @"..\..\docs\licenses\MS-RL.txt"),
            ["WixOsmfEulaComponent"] =
                ("WixOsmfEulaFile", "WiX-7.0.0-OSMF-EULA.txt",
                    @"..\..\docs\licenses\WiX-7.0.0-OSMF-EULA.txt"),
        };

        var featureReferences = FindById(document, "Feature", "Complete")
            .Elements(Wix + "ComponentRef")
            .Select(element => (string)element.Attribute("Id")!)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var (componentId, expectedFile) in expected)
        {
            var component = FindById(document, "Component", componentId);
            Assert.Equal("LicensesFolder", (string?)component.Attribute("Directory"));

            var file = Assert.Single(component.Elements(Wix + "File"));
            Assert.Equal(expectedFile.FileId, (string?)file.Attribute("Id"));
            Assert.Equal(expectedFile.Name, (string?)file.Attribute("Name"));
            Assert.Equal(expectedFile.Source, (string?)file.Attribute("Source"));
            Assert.Equal("yes", (string?)file.Attribute("KeyPath"));
            Assert.Contains(componentId, featureReferences);

            var sourcePath = Path.GetFullPath(Path.Combine(packageDirectory, expectedFile.Source));
            Assert.True(File.Exists(sourcePath), $"Missing installer notice source: {sourcePath}");
            Assert.NotEmpty(File.ReadAllText(sourcePath));
        }

        var installedLicenseNames = document.Descendants(Wix + "Component")
            .Where(element => (string?)element.Attribute("Directory") == "LicensesFolder")
            .SelectMany(element => element.Elements(Wix + "File"))
            .Select(element => (string)element.Attribute("Name")!)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
            expected.Values.Select(value => value.Name).Order(StringComparer.Ordinal).ToArray(),
            installedLicenseNames);

        var thirdPartyNotices = FindById(document, "Component", "ThirdPartyNoticesComponent");
        Assert.Equal("INSTALLFOLDER", (string?)thirdPartyNotices.Attribute("Directory"));
        Assert.Contains("ThirdPartyNoticesComponent", featureReferences);
        var noticeFile = Assert.Single(thirdPartyNotices.Elements(Wix + "File"));
        Assert.Equal("THIRD-PARTY-NOTICES.md", (string?)noticeFile.Attribute("Name"));
        var noticeSource = Path.GetFullPath(
            Path.Combine(packageDirectory, (string)noticeFile.Attribute("Source")!));
        Assert.True(File.Exists(noticeSource), $"Missing installer notice source: {noticeSource}");
    }

    [Fact]
    public void Bundle_EmbedsTheMsiAndMapsOnlyFixedMaintenanceFlags()
    {
        var document = LoadXml("installer", "Bundle", "Bundle.wxs");
        var package = FindById(document, "MsiPackage", "V12UltraControllerMsi");
        Assert.Equal("yes", (string?)package.Attribute("Compressed"));
        Assert.Equal("yes", (string?)package.Attribute("Vital"));
        var properties = package.Elements(Wix + "MsiProperty")
            .ToDictionary(element => (string)element.Attribute("Name")!, element => (string)element.Attribute("Value")!);
        Assert.Equal("[RemoveData]", properties["REMOVE_DATA"]);
        Assert.Equal("[ForceUninstall]", properties["FORCE_UNINSTALL"]);
        Assert.Equal("[CreateDesktopShortcut]", properties["CREATE_DESKTOP_SHORTCUT"]);
        Assert.Equal("[CreateStartMenuShortcut]", properties["CREATE_START_MENU_SHORTCUT"]);
        Assert.Equal(4, properties.Count);
    }

    [Fact]
    public void BuildScript_RequiresThreePartVersionAndLegalGateAndDeliversOneSetup()
    {
        var script = File.ReadAllText(ProjectPath("installer", "build.ps1"));

        Assert.Contains("[ValidatePattern('^\\d+\\.\\d+\\.\\d+$')]", script, StringComparison.Ordinal);
        Assert.Contains("if (-not $AcceptWix7Eula)", script, StringComparison.Ordinal);
        Assert.Contains("-p:AcceptEula=wix7", script, StringComparison.Ordinal);
        Assert.Contains("Join-Path $outputDirectory 'V12UltraController.Setup.exe'", script,
            StringComparison.Ordinal);
        Assert.Contains("Join-Path $installerArtifactsDirectory $Version", script,
            StringComparison.Ordinal);
        Assert.Contains("Refusing to overwrite installer artifacts for released version", script,
            StringComparison.Ordinal);
        Assert.Contains("Increment the three-part version", script, StringComparison.Ordinal);
        Assert.True(
            script.IndexOf("if ($PublishOnly)", StringComparison.Ordinal) <
            script.IndexOf("if (-not $AcceptWix7Eula)", StringComparison.Ordinal));
        Assert.True(
            script.IndexOf("Refusing to overwrite installer artifacts", StringComparison.Ordinal) <
            script.IndexOf("Reset-ProjectDirectory -LiteralPath $outputDirectory", StringComparison.Ordinal));
        Assert.DoesNotContain("Copy-Item -LiteralPath $msiSource -Destination", script,
            StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimeEnrollmentReader_IsRegistry64ReadOnlyAndCliPreflightBypassesPipe()
    {
        var enrollment = File.ReadAllText(ProjectPath(
            "src", "V12UltraController.Core", "Security", "InstallerEnrollment.cs"));
        Assert.Contains("RegistryHive.LocalMachine, RegistryView.Registry64", enrollment,
            StringComparison.Ordinal);
        Assert.Contains("OpenSubKey(RegistrySubKey, writable: false)", enrollment,
            StringComparison.Ordinal);
        Assert.DoesNotContain("CreateSubKey", enrollment, StringComparison.Ordinal);
        Assert.DoesNotContain("SetValue", enrollment, StringComparison.Ordinal);

        var preflight = File.ReadAllText(ProjectPath(
            "src", "V12UltraController.Cli", "InstallerEnrollmentPreflight.cs"));
        Assert.DoesNotContain("NamedPipe", preflight, StringComparison.Ordinal);
        Assert.DoesNotContain("File.", preflight, StringComparison.Ordinal);
        Assert.DoesNotContain("RegistryKey", preflight, StringComparison.Ordinal);

        var program = File.ReadAllText(ProjectPath(
            "src", "V12UltraController.Cli", "Program.cs"));
        Assert.True(
            program.IndexOf("InstallerEnrollmentPreflight.IsRequested", StringComparison.Ordinal) <
            program.IndexOf("new CliApplication", StringComparison.Ordinal));
        Assert.True(
            program.IndexOf("InstallerForcedUninstallMarkerCommand.IsRequested", StringComparison.Ordinal) <
            program.IndexOf("new CliApplication", StringComparison.Ordinal));

        var markerCommand = File.ReadAllText(ProjectPath(
            "src", "V12UltraController.Cli", "InstallerForcedUninstallMarkerCommand.cs"));
        Assert.DoesNotContain("NamedPipe", markerCommand, StringComparison.Ordinal);
        Assert.DoesNotContain("RegistryKey", markerCommand, StringComparison.Ordinal);
        Assert.Contains("S-1-5-18", markerCommand, StringComparison.Ordinal);
        Assert.DoesNotContain("Console.", markerCommand, StringComparison.Ordinal);

        var markerStore = File.ReadAllText(ProjectPath(
            "src", "V12UltraController.Core", "Security", "ForcedUninstallRecoveryMarker.cs"));
        Assert.Contains(
            @"C:\ProgramData\TianqiStudio\V12UltraController\forced-uninstall-recovery.json",
            markerStore,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Environment.GetEnvironmentVariable", markerStore,
            StringComparison.Ordinal);
    }

    [Fact]
    public void WindowsPayload_ExplicitlyExcludesUnusedMonoPosixRuntimeAndNativeAssets()
    {
        var coreProject = XDocument.Load(ProjectPath(
            "src", "V12UltraController.Core", "V12UltraController.Core.csproj"));
        var monoReference = coreProject.Descendants("PackageReference")
            .Single(element => (string?)element.Attribute("Include") == "Mono.Posix.NETStandard");

        Assert.Equal("runtime;native", (string?)monoReference.Attribute("ExcludeAssets"));
        Assert.Equal("all", (string?)monoReference.Attribute("PrivateAssets"));

        var versions = XDocument.Load(ProjectPath("Directory.Packages.props"));
        var monoVersion = versions.Descendants("PackageVersion")
            .Single(element => (string?)element.Attribute("Include") == "Mono.Posix.NETStandard");
        Assert.Equal("1.0.0", (string?)monoVersion.Attribute("Version"));

        var script = File.ReadAllText(ProjectPath("installer", "build.ps1"));
        Assert.Contains("Assert-PublishedPayloadExcludesMonoPosix", script, StringComparison.Ordinal);
        Assert.Contains("Mono.Posix.NETStandard.dll", script, StringComparison.Ordinal);
        Assert.Contains("MonoPosixHelper.dll", script, StringComparison.Ordinal);
        Assert.Contains("libMonoPosixHelper.dll", script, StringComparison.Ordinal);
        Assert.Contains("Test-FileContainsAsciiText", script, StringComparison.Ordinal);
        Assert.Contains("$chunk.IndexOf($Text, [System.StringComparison]::Ordinal) -ge 0", script,
            StringComparison.Ordinal);
        Assert.DoesNotContain("$chunk.Contains($Text, [System.StringComparison]::Ordinal)", script,
            StringComparison.Ordinal);
        Assert.DoesNotContain("[System.IO.Path]::GetRelativePath", script, StringComparison.Ordinal);
        Assert.Contains("$artifact.FullName.Substring($publishPathPrefix.Length)", script,
            StringComparison.Ordinal);

        var serviceProject = XDocument.Load(ProjectPath(
            "src", "V12UltraController.Service", "V12UltraController.Service.csproj"));
        var lockFile = serviceProject.Descendants("Content")
            .Single(element => (string?)element.Attribute("Update") == "packages.lock.json");
        Assert.Equal("Never", (string?)lockFile.Attribute("CopyToOutputDirectory"));
        Assert.Equal("Never", (string?)lockFile.Attribute("CopyToPublishDirectory"));

        var replacementTool = File.ReadAllText(ProjectPath(
            "tools", "Replace-StoppedLiveTestService.ps1"));
        Assert.DoesNotContain("'MonoPosixHelper.dll'", replacementTool, StringComparison.Ordinal);
        Assert.DoesNotContain("'libMonoPosixHelper.dll'", replacementTool, StringComparison.Ordinal);
        Assert.DoesNotContain("'packages.lock.json'", replacementTool, StringComparison.Ordinal);
    }

    [Fact]
    public void RestoredWinX64Graphs_SelectNoMonoPosixRuntimeOrNativeBinary()
    {
        var coreTarget = LoadWinX64Target(
            "src", "V12UltraController.Core", "obj", "project.assets.json");
        var mono = coreTarget.GetProperty("Mono.Posix.NETStandard/1.0.0");

        AssertOnlyEmptyPlaceholders(mono, "runtime");
        AssertOnlyEmptyPlaceholders(mono, "native");

        foreach (var project in new[]
                 {
                     "V12UltraController.Service",
                     "V12UltraController.Settings",
                     "V12UltraController.Cli",
                 })
        {
            var applicationTarget = LoadWinX64Target("src", project, "obj", "project.assets.json");
            Assert.False(
                applicationTarget.TryGetProperty("Mono.Posix.NETStandard/1.0.0", out _),
                $"{project} must not receive Mono.Posix through its Core project reference.");
        }
    }

    private static XElement FindById(XDocument document, string localName, string id) =>
        document.Descendants().Single(element =>
            element.Name.LocalName == localName && (string?)element.Attribute("Id") == id);

    private static void AssertFixedInstalledCliPreflight(XElement action, string command)
    {
        Assert.Equal("CliExe", (string?)action.Attribute("FileRef"));
        Assert.Equal(command, (string?)action.Attribute("ExeCommand"));
        Assert.Equal("deferred", (string?)action.Attribute("Execute"));
        Assert.Equal("no", (string?)action.Attribute("Impersonate"));
        Assert.Equal("check", (string?)action.Attribute("Return"));
        Assert.Null(action.Attribute("Directory"));
        Assert.Null(action.Attribute("Property"));
    }

    private static XDocument LoadXml(params string[] segments) => XDocument.Load(ProjectPath(segments));

    private static JsonElement LoadWinX64Target(params string[] segments)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(ProjectPath(segments)));
        var targets = document.RootElement.GetProperty("targets");
        var target = targets.EnumerateObject()
            .Single(property => property.Name.EndsWith("/win-x64", StringComparison.Ordinal));
        return target.Value.Clone();
    }

    private static void AssertOnlyEmptyPlaceholders(JsonElement package, string assetGroup)
    {
        var assets = package.GetProperty(assetGroup).EnumerateObject().ToArray();
        Assert.NotEmpty(assets);
        Assert.All(
            assets,
            asset => Assert.EndsWith("/_._", asset.Name, StringComparison.Ordinal));
    }

    private static string ProjectPath(params string[] segments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "V12UltraController.slnx")))
        {
            directory = directory.Parent;
        }

        if (directory is null)
        {
            throw new DirectoryNotFoundException("Could not locate the V12UltraController project root.");
        }

        return segments.Aggregate(directory.FullName, Path.Combine);
    }
}
