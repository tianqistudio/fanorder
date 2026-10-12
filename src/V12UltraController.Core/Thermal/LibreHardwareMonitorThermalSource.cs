using LibreHardwareMonitor.Hardware;

namespace V12UltraController.Core.Thermal;

public sealed class LibreHardwareMonitorThermalSource : IThermalSource
{
    private const string SupportedCpuName = "AMD Ryzen 9 8945HX";
    private readonly Computer _computer;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _opened;
    private bool _disposed;

    public LibreHardwareMonitorThermalSource()
    {
        _computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
        };
    }

    public async Task<ThermalSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_opened)
            {
                _computer.Open();
                _opened = true;
            }

            foreach (var hardware in _computer.Hardware)
            {
                UpdateRecursively(hardware);
            }

            var readings = new List<ThermalSensorReading>();
            foreach (var hardware in _computer.Hardware)
            {
                CollectTemperatures(hardware, readings);
            }

            return AggregateReadings(DateTimeOffset.UtcNow, readings);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_opened)
            {
                _computer.Close();
            }
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

    private static void UpdateRecursively(IHardware hardware)
    {
        hardware.Update();
        foreach (var child in hardware.SubHardware)
        {
            UpdateRecursively(child);
        }
    }

    private static void CollectTemperatures(
        IHardware hardware,
        ICollection<ThermalSensorReading> readings)
    {
        if (IsSupportedThermalHardware(hardware))
        {
            foreach (var sensor in hardware.Sensors)
            {
                readings.Add(new(hardware.HardwareType, hardware.Name,
                    sensor.SensorType, sensor.Name, sensor.Value));
            }
        }

        foreach (var child in hardware.SubHardware)
        {
            CollectTemperatures(child, readings);
        }
    }

    // Pure projection, shared by the live collector and offline recorded-name fixtures.
    // Calling this never constructs Computer or accesses hardware.
    internal static ThermalSnapshot AggregateReadings(
        DateTimeOffset capturedAt, IEnumerable<ThermalSensorReading> readings)
    {
        var cpu = new List<double>();
        var gpu = new List<double>();
        foreach (var reading in readings)
        {
            if (reading.SensorType != SensorType.Temperature ||
                !IsSupportedThermalHardware(reading.HardwareType, reading.HardwareName) ||
                !ThermalSnapshot.IsValidTemperature(reading.Value) ||
                !IsControlTemperatureSensor(reading.HardwareType, reading.SensorName))
            {
                continue;
            }

            (reading.HardwareType == HardwareType.Cpu ? cpu : gpu).Add(reading.Value!.Value);
        }

        return new ThermalSnapshot(capturedAt, cpu.Count == 0 ? null : cpu.Max(), gpu);
    }

    internal static bool IsSupportedThermalHardware(IHardware hardware) =>
        IsSupportedThermalHardware(hardware.HardwareType, hardware.Name);

    internal static bool IsSupportedThermalHardware(HardwareType hardwareType, string hardwareName) =>
        hardwareType switch
        {
            HardwareType.Cpu => IsCpuModel(hardwareName),
            HardwareType.GpuAmd => MatchesAlias(hardwareName,
                "AMD Radeon 610M", "AMD Radeon(TM) 610M", "Radeon 610M"),
            HardwareType.GpuNvidia => MatchesAlias(hardwareName,
                "NVIDIA GeForce RTX 5070 Laptop GPU", "RTX 5070 Laptop GPU"),
            _ => false,
        };

    private static bool IsCpuModel(string name)
    {
        var trimmed = name.Trim();
        return trimmed.StartsWith(SupportedCpuName, StringComparison.OrdinalIgnoreCase) &&
            (trimmed.Length == SupportedCpuName.Length || char.IsWhiteSpace(trimmed[SupportedCpuName.Length]));
    }

    private static bool MatchesAlias(string name, params string[] aliases) =>
        aliases.Contains(name.Trim(), StringComparer.OrdinalIgnoreCase);

    internal static bool IsControlTemperatureSensor(HardwareType hardwareType, string sensorName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sensorName);
        return hardwareType switch
        {
            HardwareType.Cpu =>
                sensorName.Contains("Package", StringComparison.OrdinalIgnoreCase) ||
                sensorName.Contains("Core", StringComparison.OrdinalIgnoreCase) ||
                sensorName.Contains("Tctl", StringComparison.OrdinalIgnoreCase) ||
                sensorName.Contains("Tdie", StringComparison.OrdinalIgnoreCase) ||
                sensorName.Contains("CCD", StringComparison.OrdinalIgnoreCase),
            // LHM labels the AMD SoC voltage-regulator temperature separately from GPU Core.
            // Include that exact AMD-only temperature, without relabeling it as CPU or GPU Core.
            HardwareType.GpuAmd when sensorName.Equals("GPU VR SoC", StringComparison.OrdinalIgnoreCase) => true,
            HardwareType.GpuAmd or HardwareType.GpuNvidia =>
                sensorName.Contains("Core", StringComparison.OrdinalIgnoreCase) ||
                sensorName.Contains("Hot Spot", StringComparison.OrdinalIgnoreCase) ||
                sensorName.Contains("Hotspot", StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
    }
}

internal sealed record ThermalSensorReading(
    HardwareType HardwareType, string HardwareName, SensorType SensorType, string SensorName, double? Value);
