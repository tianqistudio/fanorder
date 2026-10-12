using V12UltraController.Core.Hardware;

namespace V12UltraController.Tests.Hardware;

public sealed class V12FeatureReportTests
{
    [Fact]
    public void CreateReadQuery_ReturnsGoldenVectorAndDefensiveCopy()
    {
        var first = V12FeatureReport.CreateReadQuery();
        var second = V12FeatureReport.CreateReadQuery();

        Assert.Equal(new byte[] { 0x00, 0x80, 0, 0, 0, 0, 0, 0, 0x7F }, first);
        Assert.True(V12FeatureReport.HasValidChecksum(first));
        Assert.NotSame(first, second);

        first[1] = 0;
        Assert.Equal(0x80, second[1]);
    }

    [Fact]
    public void CreateSetFanSpeed_ReturnsGoldenVectorAndPreservesLighting()
    {
        var current = V12FeatureReport.ParseState(
            CreateStateReport(0x80, 44, 0, 0x83, 0, 2, 128));

        var report = V12FeatureReport.CreateSetFanSpeed(current, 50);

        Assert.Equal(new byte[] { 0x00, 0x00, 0x32, 0x00, 0x83, 0x00, 0x02, 0x80, 0xC8 }, report);
        Assert.True(V12FeatureReport.HasValidChecksum(report));
    }

    [Fact]
    public void SystemShutdownOffAndOnAtOneHundred_ReturnGoldenVectorsAndPreserveLighting()
    {
        var current = V12FeatureReport.ParseState(
            CreateStateReport(0x80, 44, 0, 0x83, 0, 2, 128));

        var turnOff = V12FeatureReport.CreateSystemShutdownOff(current);
        var turnOn = V12FeatureReport.CreateSetFanSpeed(current, 100);

        Assert.Equal(new byte[] { 0x00, 0x00, 0x64, 0x01, 0x83, 0x00, 0x02, 0x80, 0x95 }, turnOff);
        Assert.Equal(new byte[] { 0x00, 0x00, 0x64, 0x00, 0x83, 0x00, 0x02, 0x80, 0x96 }, turnOn);
        Assert.True(V12FeatureReport.HasValidChecksum(turnOff));
        Assert.True(V12FeatureReport.HasValidChecksum(turnOn));
    }

    [Theory]
    [InlineData(0x00, 0x00, 0x00, 0x00)]
    [InlineData(0x83, 0x04, 0x03, 0xFF)]
    [InlineData(0xFF, 0xFF, 0xFF, 0xFF)]
    public void SystemShutdownOff_PropertyAlwaysUsesNarrowPowerVectorAndPreservesLighting(
        byte lightModeAndPower,
        byte lightColor,
        byte lightSpeed,
        byte lightPower)
    {
        var current = V12FeatureReport.ParseState(
            CreateStateReport(0x80, 67, 0, lightModeAndPower, lightColor, lightSpeed, lightPower));

        var report = V12FeatureReport.CreateSystemShutdownOff(current);

        Assert.Equal(0, report[0]);
        Assert.Equal(0, report[1]);
        Assert.Equal(100, report[2]);
        Assert.Equal(1, report[3]);
        Assert.Equal(new[] { lightModeAndPower, lightColor, lightSpeed, lightPower }, report[4..8]);
        Assert.True(V12FeatureReport.HasValidChecksum(report));
        Assert.Equal(0xFF, report[1..].Sum(static value => value) & 0xFF);
    }

    [Theory]
    [InlineData(0x00)]
    [InlineData(0x80)]
    [InlineData(0x88)]
    public void ParseState_PreservesOpaqueFirmwareStatusWithoutTreatingItAsCurveIndex(byte modeStatus)
    {
        var state = V12FeatureReport.ParseState(
            CreateStateReport(modeStatus, 64, 0, 0x81, 3, 1, 200));

        Assert.Equal(modeStatus, state.RawModeStatus);
        Assert.Equal(64, state.FanSpeedPercent);
        Assert.True(state.PoweredOn);
        Assert.Equal(0x81, state.LightModeAndPower);
        Assert.Equal(3, state.LightColorMode);
        Assert.Equal(1, state.LightSpeed);
        Assert.Equal(200, state.LightPower);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(9)]
    [InlineData(101)]
    [InlineData(255)]
    public void CreateSetFanSpeed_RejectsUnsafePercentages(byte percent)
    {
        var state = V12FeatureReport.ParseState(CreateStateReport(0, 50, 0, 0, 0, 0, 20));

        _ = Assert.Throws<ArgumentOutOfRangeException>(
            () => V12FeatureReport.CreateSetFanSpeed(state, percent));
    }

    [Fact]
    public void ParseState_RejectsWrongLengthReportIdChecksumAndRanges()
    {
        _ = Assert.Throws<V12ProtocolException>(() => V12FeatureReport.ParseState(new byte[8]));

        var wrongId = CreateStateReport(0, 50, 0, 0, 0, 0, 20);
        wrongId[0] = 1;
        _ = Assert.Throws<V12ProtocolException>(() => V12FeatureReport.ParseState(wrongId));

        var badChecksum = CreateStateReport(0, 50, 0, 0, 0, 0, 20);
        badChecksum[^1]++;
        _ = Assert.Throws<V12ProtocolException>(() => V12FeatureReport.ParseState(badChecksum));

        _ = Assert.Throws<V12ProtocolException>(
            () => V12FeatureReport.ParseState(CreateStateReport(0, 0, 0, 0, 0, 0, 20)));
        _ = Assert.Throws<V12ProtocolException>(
            () => V12FeatureReport.ParseState(CreateStateReport(0, 9, 0, 0, 0, 0, 20)));
        _ = Assert.Throws<V12ProtocolException>(
            () => V12FeatureReport.ParseState(CreateStateReport(0, 101, 0, 0, 0, 0, 20)));
        _ = Assert.Throws<V12ProtocolException>(
            () => V12FeatureReport.ParseState(CreateStateReport(0, 50, 2, 0, 0, 0, 20)));
    }

    [Fact]
    public void OpaqueStatusProperty_AllBytesRoundTripButNeverBecomeOutgoingCommands()
    {
        // Synthetic vectors, not a captured real-device response. Vendor receive
        // code accepts every byte; this tests that none can alter our command b0.
        for (var statusByte = 0; statusByte <= byte.MaxValue; statusByte++)
        {
            foreach (var power in new byte[] { 0, 1 })
            {
                var input = CreateStateReport((byte)statusByte, 50, power, 0x83, 4, 2, 177);
                var state = V12FeatureReport.ParseState(input);
                Assert.Equal((byte)statusByte, state.RawModeStatus);
                Assert.Equal(input, state.ExportFeatureReport());
                foreach (var speed in new byte[] { 10, 50, 100 })
                {
                    var output = V12FeatureReport.CreateSetFanSpeed(state, speed);
                    Assert.Equal(0, output[0]);
                    Assert.Equal(0, output[1]);
                    Assert.Equal(speed, output[2]);
                    Assert.Equal(0, output[3]);
                    Assert.Equal(input[4..8], output[4..8]);
                    Assert.True(V12FeatureReport.HasValidChecksum(output));
                }
            }
        }
    }

    [Theory]
    [InlineData(0, 0, false)]
    [InlineData(9, 0, false)]
    [InlineData(101, 0, false)]
    [InlineData(255, 0, false)]
    [InlineData(50, 2, false)]
    [InlineData(50, 255, false)]
    [InlineData(50, 0, true)]
    public void ObservedStatusByte_DoesNotBypassOtherFieldOrChecksumValidation(
        byte speed, byte power, bool corruptChecksum)
    {
        var report = CreateStateReport(0x88, speed, power, 0x83, 4, 2, 177);
        if (corruptChecksum)
        {
            report[^1] ^= 1;
        }

        _ = Assert.Throws<V12ProtocolException>(() => V12FeatureReport.ParseState(report));
    }

    [Fact]
    public void ChecksumProperty_ExhaustsEveryPossibleModuloSum()
    {
        // The checksum depends only on sum(b0..b6) modulo 256. Exhausting all
        // 256 residues therefore covers every possible seven-byte combination.
        for (var residue = 0; residue <= byte.MaxValue; residue++)
        {
            byte[] firstSevenPayloadBytes = [(byte)residue, 0, 0, 0, 0, 0, 0];
            var checksum = V12FeatureReport.CalculateChecksum(firstSevenPayloadBytes);

            Assert.Equal(0xFF, (firstSevenPayloadBytes.Sum(static value => value) + checksum) & 0xFF);
        }
    }

    [Fact]
    public void SetFanSpeedProperty_ExhaustsEveryAllowedControlValueAndEveryLightingByteValue()
    {
        foreach (var mode in new byte[] { 0, 0x80 })
        {
            foreach (var power in new byte[] { 0, 1 })
            {
                for (byte speed = 10; speed <= 100; speed++)
                {
                    for (var lightingIndex = 0; lightingIndex < 4; lightingIndex++)
                    {
                        for (var lightingValue = 0; lightingValue <= byte.MaxValue; lightingValue++)
                        {
                            var lighting = new byte[4];
                            lighting[lightingIndex] = (byte)lightingValue;
                            var state = V12FeatureReport.ParseState(
                                CreateStateReport(mode, speed, power, lighting[0], lighting[1], lighting[2], lighting[3]));

                            var report = V12FeatureReport.CreateSetFanSpeed(state, speed);

                            Assert.Equal(lighting, report[4..8]);
                            Assert.True(V12FeatureReport.HasValidChecksum(report));
                        }
                    }
                }
            }
        }
    }

    internal static byte[] CreateStateReport(
        byte mode,
        byte speed,
        byte power,
        byte lightModeAndPower,
        byte lightColor,
        byte lightSpeed,
        byte lightPower)
    {
        byte[] report = [0, mode, speed, power, lightModeAndPower, lightColor, lightSpeed, lightPower, 0];
        report[^1] = V12FeatureReport.CalculateChecksum(report.AsSpan(1, 7));
        return report;
    }
}
