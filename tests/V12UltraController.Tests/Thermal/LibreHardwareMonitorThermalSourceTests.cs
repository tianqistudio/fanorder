using LibreHardwareMonitor.Hardware;
using V12UltraController.Core.Thermal;

namespace V12UltraController.Tests.Thermal;

public sealed class LibreHardwareMonitorThermalSourceTests
{
    [Theory]
    [InlineData(HardwareType.Cpu, "AMD Ryzen 9 8945HX 16-Core Processor", true)]
    [InlineData(HardwareType.Cpu, " AMD Ryzen 9 8945HX ", true)]
    [InlineData(HardwareType.Cpu, "AMD Ryzen 9 8945HX0", false)]
    [InlineData(HardwareType.Cpu, "Other AMD Ryzen 9 8945HX", false)]
    [InlineData(HardwareType.Cpu, "Intel Core Ultra 9", false)]
    [InlineData(HardwareType.GpuAmd, "AMD Radeon 610M", true)]
    [InlineData(HardwareType.GpuAmd, "AMD Radeon(TM) 610M", true)]
    [InlineData(HardwareType.GpuAmd, " amd radeon(tm) 610m ", true)]
    [InlineData(HardwareType.GpuAmd, "AMD Radeon 610M0", false)]
    [InlineData(HardwareType.GpuAmd, "Other AMD Radeon(TM) 610M", false)]
    [InlineData(HardwareType.GpuAmd, "AMD Radeon RX 7900 XTX", false)]
    [InlineData(HardwareType.GpuNvidia, "NVIDIA GeForce RTX 5070 Laptop GPU", true)]
    [InlineData(HardwareType.GpuNvidia, "NVIDIA GeForce RTX 5070 Laptop GPU0", false)]
    [InlineData(HardwareType.GpuNvidia, "Other NVIDIA GeForce RTX 5070 Laptop GPU", false)]
    [InlineData(HardwareType.GpuNvidia, "NVIDIA GeForce RTX 4090", false)]
    public void HardwareFilter_AcceptsOnlyTheFirstReleaseHostSensors(
        HardwareType hardwareType,
        string hardwareName,
        bool expected)
    {
        Assert.Equal(expected,
            LibreHardwareMonitorThermalSource.IsSupportedThermalHardware(hardwareType, hardwareName));
    }

    [Theory]
    [InlineData(HardwareType.Cpu, "CPU Package", true)]
    [InlineData(HardwareType.Cpu, "Core (Tctl/Tdie)", true)]
    [InlineData(HardwareType.Cpu, "CCD1 (Tdie)", true)]
    [InlineData(HardwareType.GpuNvidia, "GPU Core", true)]
    [InlineData(HardwareType.GpuNvidia, "GPU Hot Spot", true)]
    [InlineData(HardwareType.GpuAmd, "GPU Hotspot", true)]
    [InlineData(HardwareType.GpuAmd, "GPU VR SoC", true)]
    [InlineData(HardwareType.GpuNvidia, "GPU VR SoC", false)]
    [InlineData(HardwareType.GpuAmd, "GPU VR SoC voltage", false)]
    [InlineData(HardwareType.GpuNvidia, "GPU Memory", false)]
    [InlineData(HardwareType.GpuAmd, "GPU VRM", false)]
    [InlineData(HardwareType.Memory, "Temperature", false)]
    public void ControlSensorFilter_OnlyAcceptsRequiredCpuAndGpuSignals(
        HardwareType hardwareType,
        string sensorName,
        bool expected)
    {
        Assert.Equal(expected,
            LibreHardwareMonitorThermalSource.IsControlTemperatureSensor(hardwareType, sensorName));
    }

    [Fact]
    public void RecordedNames_IncludeAmdSocAndRetainCpuCcdMaximum()
    {
        var stamp = new DateTimeOffset(2026, 9, 5, 13, 39, 0, TimeSpan.Zero);
        var result = LibreHardwareMonitorThermalSource.AggregateReadings(stamp,
        [
            Cpu("Core (Tctl/Tdie)", 85), Cpu("CCD1 (Tdie)", 95.25000762939453),
            Cpu("CCD2 (Tdie)", 53.5),
            new(HardwareType.GpuAmd, "AMD Radeon(TM) 610M", SensorType.Temperature, "GPU VR SoC", 61),
            new(HardwareType.GpuNvidia, "NVIDIA GeForce RTX 5070 Laptop GPU", SensorType.Temperature, "GPU Core", 44.3125),
            new(HardwareType.GpuNvidia, "NVIDIA GeForce RTX 5070 Laptop GPU", SensorType.Temperature, "GPU Memory Junction", 110),
        ]);

        Assert.Equal(stamp, result.CapturedAt);
        Assert.Equal(95.25000762939453, result.CpuCelsius);
        Assert.Equal(result.CpuCelsius, result.ControlCelsius);
        Assert.Equal(new[] { 61d, 44.3125d }, result.GpuCelsius);
    }

    [Fact]
    public void AmdSoc_CanRaiseControlMaximumButCannotReplaceMissingCpu()
    {
        ThermalSensorReading amd = new(HardwareType.GpuAmd, "AMD Radeon(TM) 610M",
            SensorType.Temperature, "GPU VR SoC", 91);
        var warmGpu = LibreHardwareMonitorThermalSource.AggregateReadings(DateTimeOffset.UtcNow,
            [Cpu("Core (Tctl/Tdie)", 50), amd]);
        Assert.Equal(91, warmGpu.ControlCelsius);
        var noCpu = LibreHardwareMonitorThermalSource.AggregateReadings(DateTimeOffset.UtcNow,
            [Cpu("Core (Tctl/Tdie)", 0), amd]);
        Assert.False(noCpu.CpuIsValid);
        Assert.Null(noCpu.ControlCelsius);
        Assert.Equal(new[] { 91d }, noCpu.GpuCelsius);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0d)]
    [InlineData(-1d)]
    [InlineData(121d)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Aggregation_RejectsInvalidValuesAndNonTemperatureSensors(double? value)
    {
        var result = LibreHardwareMonitorThermalSource.AggregateReadings(DateTimeOffset.UtcNow,
        [
            Cpu("Core (Tctl/Tdie)", value),
            new(HardwareType.Cpu, "AMD Ryzen 9 8945HX", SensorType.Load, "CPU Core", 50),
            new(HardwareType.GpuAmd, "AMD Radeon(TM) 610M", SensorType.Temperature, "GPU VR SoC", value),
            new(HardwareType.GpuNvidia, "NVIDIA GeForce RTX 5070 Laptop GPU", SensorType.Temperature, "GPU VR SoC", 80),
        ]);
        Assert.Null(result.CpuCelsius);
        Assert.Null(result.ControlCelsius);
        Assert.Empty(result.GpuCelsius);
    }

    private static ThermalSensorReading Cpu(string name, double? value) =>
        new(HardwareType.Cpu, "AMD Ryzen 9 8945HX 16-Core Processor", SensorType.Temperature, name, value);
}
