using V12UltraController.Core.Hardware;
using V12UltraController.Cli;
using V12UltraController.Core.Ipc;

namespace V12UltraController.Tests.Hardware;

public sealed class LightingTests
{
    [Fact]
    public void AllPresetsRoundTripAndPreserveFan()
    {
        var original = V12FeatureReport.ParseState(V12FeatureReportTests.CreateStateReport(0x88, 67, 0, 0x83, 0, 2, 128));
        foreach (var enabled in new[] { false, true })
        for (var mode = 0; mode <= 3; mode++)
        for (var color = 0; color <= 4; color++)
        for (var speed = 0; speed <= 3; speed++)
        for (var brightness = 0; brightness <= 255; brightness++)
        {
            var settings = new LightingSettings(enabled, mode, color, brightness, speed);
            var report = V12FeatureReport.CreateSetLighting(original, settings);
            Assert.Equal(new byte[] { 0, 0, 67, 0 }, report[..4]);
            Assert.Equal(enabled ? mode : mode | 128, report[4]);
            Assert.Equal(3 - speed, report[6]);
            Assert.True(V12FeatureReport.HasValidChecksum(report));
            Assert.Equal(settings, LightingSettings.FromState(V12FeatureReport.ParseState(report)));
        }
        Assert.Equal(0x83, original.LightModeAndPower);
    }

    [Theory]
    [InlineData(-1,0,128,0)] [InlineData(4,0,128,0)]
    [InlineData(0,-1,128,0)] [InlineData(0,5,128,0)]
    [InlineData(0,0,-1,0)] [InlineData(0,0,256,0)]
    [InlineData(0,0,128,-1)] [InlineData(0,0,128,4)]
    public void InvalidValuesRejected(int mode, int color, int brightness, int speed) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new LightingSettings(true, mode, color, brightness, speed).Validate());

    [Fact]
    public void OffAndUnknownReadbackNeverGuessed()
    {
        var off = V12FeatureReport.ParseState(V12FeatureReportTests.CreateStateReport(0x80, 100, 1, 0, 0, 0, 128));
        Assert.Throws<InvalidOperationException>(() => V12FeatureReport.CreateSetLighting(off, new(true,0,0,128,0)));
        var unknown = V12FeatureReport.ParseState(V12FeatureReportTests.CreateStateReport(0x80, 100, 0, 0x7F, 0, 5, 128));
        Assert.Null(LightingSettings.FromState(unknown));
        Assert.Equal(0x7F, V12FeatureReport.CreateSetFanSpeed(unknown,100)[4]);
    }

    [Fact]
    public void CliUsesStrictTypedPayload()
    {
        var command = CliCommandParser.Parse(["rgb","set","off","3","4","255","2"]);
        Assert.Equal(PipeOperations.LightingSet, command.Operation);
        Assert.False(command.Payload.GetProperty("enabled").GetBoolean());
        Assert.Equal(255, command.Payload.GetProperty("brightness").GetInt32());
        Assert.Equal(PipeOperations.LightingGet, CliCommandParser.Parse(["rgb","show","--json"]).Operation);
        Assert.Throws<CliUsageException>(() => CliCommandParser.Parse(["rgb","set","on","4","0","128","0"]));
        Assert.Throws<CliUsageException>(() => CliCommandParser.Parse(["rgb","set","off"]));
    }
}
