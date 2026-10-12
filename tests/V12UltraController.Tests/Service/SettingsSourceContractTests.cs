using System.Xml.Linq;

namespace V12UltraController.Tests.Service;

public sealed class SettingsSourceContractTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void MainWindow_ContainsStatusControlCurveAndCommissioningSurfaces()
    {
        var document = XDocument.Load(ProjectPath(
            "src", "V12UltraController.Settings", "MainWindow.xaml"));
        var names = document.Descendants()
            .Select(element => (string?)element.Attribute(Xaml + "Name"))
            .Where(static name => name is not null)
            .ToHashSet(StringComparer.Ordinal);

        string[] requiredNames =
        [
            "DeviceStateText", "DeviceIdentityText", "ControlTemperatureText",
            "TemperatureDetailText", "FanPercentText", "FanModeText",
            "FailSafeStateText", "FailSafeReasonText", "ServiceStateText",
            "CommissionedText", "LastReadText", "LastWriteText",
            "AuditMutationBorder", "AuditMutationText",
            "ManualSpeedSlider", "CurveGrid", "AttendedCheckBox",
            "ProbeButton", "IdempotentButton", "TestButton", "CommitButton",
            "RecoverTaskTransitionButton", "TaskTransitionRecoveryResultText",
            "RollbackButton", "StartMythCoolCheckBox", "ForcedRecoveryAttendedCheckBox",
            "ShutdownStateText", "ShutdownQualificationText",
            "ShutdownAttendedCheckBox", "QualifyShutdownButton", "EnableShutdownButton",
            "DisableShutdownButton", "ShutdownAndTurnOffButton", "StartupFanPercentSlider",
            "StartupFanPercentInput", "SaveStartupFanButton",
        ];
        Assert.All(requiredNames, name => Assert.Contains(name, names));
    }

    [Fact]
    public void MainWindow_BindsEverySafetyWorkflowButtonToAnExplicitHandler()
    {
        var document = XDocument.Load(ProjectPath(
            "src", "V12UltraController.Settings", "MainWindow.xaml"));
        var handlerNames = document.Descendants()
            .Attributes()
            .Where(attribute => attribute.Name.LocalName is "Click" or "Checked" or "Unchecked" or "Closing")
            .Select(attribute => attribute.Value)
            .ToHashSet(StringComparer.Ordinal);
        var codeBehind = string.Join(Environment.NewLine,
            Directory.GetFiles(ProjectPath("src", "V12UltraController.Settings"), "MainWindow*.cs")
                .Select(File.ReadAllText));

        Assert.NotEmpty(handlerNames);
        foreach (var handlerName in handlerNames)
        {
            Assert.Contains($"{handlerName}(", codeBehind, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void SettingsProject_ReferencesCoreAndSharedShortcutsWithoutHardwareOrNetworkPackage()
    {
        var document = XDocument.Load(ProjectPath(
            "src", "V12UltraController.Settings", "V12UltraController.Settings.csproj"));
        var references = document.Descendants("ProjectReference")
            .Select(element => (string?)element.Attribute("Include"))
            .ToArray();
        var packages = document.Descendants("PackageReference")
            .Select(element => (string?)element.Attribute("Include"))
            .Where(static value => value is not null)
            .ToArray();

        Assert.Equal(3, references.Length);
        Assert.Contains(references, reference => reference!.EndsWith("V12UltraController.Core.csproj", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(references, reference => reference!.EndsWith("TianqiStudio.DesktopShortcuts.csproj", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(references, reference => reference!.EndsWith("TianqiStudio.DesktopShortcuts.Wpf.csproj", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(packages,
            package => package!.Contains("Hid", StringComparison.OrdinalIgnoreCase) ||
                       package.Contains("Http", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SettingsAndCli_UseTheSharedClientSideDiagnosticsArchiveWriter()
    {
        var settings = File.ReadAllText(ProjectPath(
            "src", "V12UltraController.Settings", "MainWindow.xaml.cs"));
        var cli = File.ReadAllText(ProjectPath(
            "src", "V12UltraController.Cli", "CliApplication.cs"));

        Assert.Contains("DiagnosticsArchiveWriter", settings, StringComparison.Ordinal);
        Assert.Contains("DiagnosticsArchiveWriter", cli, StringComparison.Ordinal);
        Assert.Contains("OverwritePrompt = false", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("ZipArchive", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("ZipArchive", cli, StringComparison.Ordinal);
        Assert.DoesNotContain("FileMode.Create", settings, StringComparison.Ordinal);
    }

    [Fact]
    public void Settings_ConsumesServiceProjectedControlAndCommissioningState()
    {
        var settings = File.ReadAllText(ProjectPath(
            "src", "V12UltraController.Settings", "MainWindow.xaml.cs"));

        Assert.Contains("status.Fan.ConfiguredManualPercent", settings, StringComparison.Ordinal);
        Assert.Contains("SynchronizeCommissioningStatus(status.Commissioning)", settings,
            StringComparison.Ordinal);
        Assert.Contains("serviceStatus.SessionId", settings, StringComparison.Ordinal);
        Assert.Contains("serviceStatus.ExpiresAt", settings, StringComparison.Ordinal);
        Assert.Contains("refreshAfterSuccess: true", settings, StringComparison.Ordinal);
        Assert.Contains("ReadRequiredBooleanProperty", settings, StringComparison.Ordinal);
        Assert.Contains("startedMythCool", settings, StringComparison.Ordinal);
        Assert.Contains("PipeOperations.CommissionRecoverTaskTransition", settings, StringComparison.Ordinal);
        Assert.Contains("commitAbandonedOriginalState", settings, StringComparison.Ordinal);
        Assert.Contains("rollbackCompensatedToController", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("_idempotentPassed", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("_stagedTestPassed", settings, StringComparison.Ordinal);
    }

    [Fact]
    public void Settings_AutoTakesOverReleasedCommissionedControlOnStartup()
    {
        var startup = File.ReadAllText(ProjectPath(
            "src", "V12UltraController.Settings", "MainWindow.TrayStartup.cs"));
        var refresh = File.ReadAllText(ProjectPath(
            "src", "V12UltraController.Settings", "MainWindow.xaml.cs"));
        var xaml = File.ReadAllText(ProjectPath(
            "src", "V12UltraController.Settings", "MainWindow.xaml"));

        Assert.Contains("await AutoTakeoverReleasedControlAsync();", refresh,
            StringComparison.Ordinal);
        Assert.Contains("status.Fan.Mode != ControlMode.Released", startup,
            StringComparison.Ordinal);
        Assert.Contains("new { mode = \"auto\" }", startup,
            StringComparison.Ordinal);
        Assert.Contains("Manual and pure-manual modes remain untouched", startup,
            StringComparison.Ordinal);
        Assert.Contains("启动到托盘并自动接管", xaml, StringComparison.Ordinal);
        Assert.Contains("手动和纯手动优先", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void Settings_ForcedUninstallRecoveryRequiresExplicitAttendedConfirmation()
    {
        var xamlPath = ProjectPath("src", "V12UltraController.Settings", "MainWindow.xaml");
        var document = XDocument.Load(xamlPath);
        var codeBehind = File.ReadAllText(ProjectPath(
            "src", "V12UltraController.Settings", "MainWindow.xaml.cs"));
        var attendedCheckBox = document.Descendants()
            .Single(element => string.Equals(
                (string?)element.Attribute(Xaml + "Name"),
                "ForcedRecoveryAttendedCheckBox",
                StringComparison.Ordinal));
        var rollbackButton = document.Descendants()
            .Single(element => string.Equals(
                (string?)element.Attribute(Xaml + "Name"),
                "RollbackButton",
                StringComparison.Ordinal));

        Assert.Equal("我在电脑旁并会观察设备", (string?)attendedCheckBox.Attribute("Content"));
        Assert.Equal("False", (string?)attendedCheckBox.Attribute("IsEnabled"));
        Assert.Equal("ForcedRecoveryAttendedCheckBox_Changed",
            (string?)attendedCheckBox.Attribute("Checked"));
        Assert.Equal("ForcedRecoveryAttendedCheckBox_Changed",
            (string?)attendedCheckBox.Attribute("Unchecked"));
        Assert.Equal("False", (string?)rollbackButton.Attribute("IsEnabled"));
        Assert.Contains("var attended = ForcedRecoveryAttendedCheckBox.IsChecked == true;", codeBehind,
            StringComparison.Ordinal);
        Assert.Contains("if (forcedRecoveryRequired)", codeBehind, StringComparison.Ordinal);
        Assert.Contains("if (!attended)", codeBehind, StringComparison.Ordinal);
        Assert.Contains("new { startMythCool, installer = false, attended }", codeBehind,
            StringComparison.Ordinal);
        Assert.Contains("forcedRecovery?.MarkerValid == true && forcedRecoveryAttended", codeBehind,
            StringComparison.Ordinal);
        Assert.Contains("var statusKnown = _lastStatus is not null;", codeBehind,
            StringComparison.Ordinal);
        Assert.Contains("RollbackButton.IsEnabled = statusKnown", codeBehind,
            StringComparison.Ordinal);
        Assert.Contains("尚未取得服务状态", codeBehind, StringComparison.Ordinal);
        Assert.Contains("_lastStatus = null;\r\n            _currentMode = null;\r\n            ForcedRecoveryAttendedCheckBox.IsChecked = false;",
            codeBehind.ReplaceLineEndings("\r\n"),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "new { startMythCool, installer = false, attended = true }",
            codeBehind,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Settings_ExposesExplicitTwoStageAttendedShutdownQualificationAndSafeDisable()
    {
        var xaml = File.ReadAllText(ProjectPath(
            "src", "V12UltraController.Settings", "MainWindow.xaml"));
        var codeBehind = File.ReadAllText(ProjectPath(
            "src", "V12UltraController.Settings", "MainWindow.xaml.cs"));

        Assert.Contains("PipeOperations.ShutdownQualify", codeBehind, StringComparison.Ordinal);
        Assert.Contains("PipeOperations.ShutdownEnable", codeBehind, StringComparison.Ordinal);
        Assert.Contains("PipeOperations.ShutdownDisable", codeBehind, StringComparison.Ordinal);
        Assert.Contains("new { attended = true }", codeBehind, StringComparison.Ordinal);
        Assert.Contains("GET → ON 100% → GET → OFF → GET → ON 100% → GET", codeBehind,
            StringComparison.Ordinal);
        Assert.Contains("Windows Restart", xaml + codeBehind, StringComparison.Ordinal);
        Assert.Contains("Fast Startup", xaml + codeBehind, StringComparison.Ordinal);
        Assert.Contains("低于 60°C", xaml + codeBehind, StringComparison.Ordinal);
        Assert.Contains("短暂", xaml + codeBehind, StringComparison.Ordinal);
        Assert.Contains("最终 ON 100%", xaml + codeBehind, StringComparison.Ordinal);
        Assert.Contains("2 分钟的内存资格", xaml + codeBehind, StringComparison.Ordinal);
        Assert.Contains("2. 已观察恢复，确认启用", xaml, StringComparison.Ordinal);
        Assert.Contains("尚未启用", codeBehind, StringComparison.Ordinal);
        Assert.Contains("shutdown.PendingQualificationId", codeBehind, StringComparison.Ordinal);
        var qualifyHandler = codeBehind[codeBehind.IndexOf(
            "QualifyShutdownButton_Click", StringComparison.Ordinal)..codeBehind.IndexOf(
            "EnableShutdownButton_Click", StringComparison.Ordinal)];
        Assert.DoesNotContain("PipeOperations.ShutdownEnable", qualifyHandler,
            StringComparison.Ordinal);
        Assert.Contains("status.Shutdown", codeBehind, StringComparison.Ordinal);
        Assert.Contains("shutdown.MatchesCurrentHandle", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("PipeOperations.ShutdownOff", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("off-now", codeBehind, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("raw power", codeBehind, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Settings_RendersDurableAuditMutationRecoveryBarrier()
    {
        var codeBehind = File.ReadAllText(ProjectPath(
            "src", "V12UltraController.Settings", "MainWindow.xaml.cs"));

        Assert.Contains("RenderAuditMutation(status.AuditMutation)", codeBehind,
            StringComparison.Ordinal);
        Assert.Contains("mutation.TransactionId", codeBehind, StringComparison.Ordinal);
        Assert.Contains("mutation.EventName", codeBehind, StringComparison.Ordinal);
        Assert.Contains("mutation.Stage", codeBehind, StringComparison.Ordinal);
        Assert.Contains("拒绝新的状态变更和普通 HID 控制", codeBehind, StringComparison.Ordinal);
    }

    [Fact]
    public void Settings_DistinguishesLocalDiagnosticsWritesFromPipeFailures()
    {
        var settings = File.ReadAllText(ProjectPath(
            "src", "V12UltraController.Settings", "MainWindow.xaml.cs"));

        Assert.Contains("LocalDiagnosticsWriteException", settings, StringComparison.Ordinal);
        Assert.Contains("当前用户无权写入所选诊断文件位置", settings, StringComparison.Ordinal);
        Assert.Contains("诊断数据已从服务取得，但无法写入所选文件", settings, StringComparison.Ordinal);
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
