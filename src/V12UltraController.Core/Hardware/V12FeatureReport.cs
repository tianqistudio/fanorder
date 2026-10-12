namespace V12UltraController.Core.Hardware;

public sealed class V12ProtocolException : IOException
{
    public V12ProtocolException(string message)
        : base(message)
    {
    }
}

public sealed class V12DeviceState
{
    private readonly byte[] _rawPayload;

    internal V12DeviceState(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != V12FeatureReport.PayloadLength)
        {
            throw new ArgumentException("A V12 state payload must contain exactly eight bytes.", nameof(payload));
        }

        _rawPayload = payload.ToArray();
    }

    // Incoming status is not the outgoing fan_mode_control command. The pinned
    // vendor decoder maps (b0 != 0x80) to fan_mode without an enum/range check.
    // Retain it verbatim; do not infer bit meanings or use it as a fan curve.
    public byte RawModeStatus => _rawPayload[0];

    public byte FanSpeedPercent => _rawPayload[1];

    public bool PoweredOn => _rawPayload[2] == 0;

    public byte LightModeAndPower => _rawPayload[3];

    public byte LightColorMode => _rawPayload[4];

    public byte LightSpeed => _rawPayload[5];

    public byte LightPower => _rawPayload[6];

    internal byte[] ExportFeatureReport()
    {
        var report = new byte[V12FeatureReport.ReportLength];
        report[0] = V12FeatureReport.ReportId;
        _rawPayload.CopyTo(report, 1);
        return report;
    }

}

public static class V12FeatureReport
{
    public const int ReportLength = 9;
    public const int PayloadLength = 8;
    public const byte ReportId = 0;
    public const byte ReadCommand = 0x80;

    private static readonly byte[] ReadQuery = [ReportId, ReadCommand, 0, 0, 0, 0, 0, 0, 0x7F];

    public static byte[] CreateReadQuery() => (byte[])ReadQuery.Clone();

    public static V12DeviceState ParseState(ReadOnlySpan<byte> report)
    {
        ValidateReport(report);

        var payload = report[1..];
        // See docs/receive-mode-evidence-2026-09-06.md. All byte values are opaque
        // receive status, not commands to echo. Ordinary writes use b0=0; only
        // the dedicated software-control selection methods produce b0=1.

        if (payload[1] is < 10 or > 100)
        {
            throw new V12ProtocolException($"Device returned an invalid fan control percentage: {payload[1]}.");
        }

        if (payload[2] > 1)
        {
            throw new V12ProtocolException($"Device returned an invalid fan power flag: {payload[2]}.");
        }

        return new V12DeviceState(payload);
    }

    public static byte[] CreateSetFanSpeed(V12DeviceState currentState, byte fanSpeedPercent)
    {
        ArgumentNullException.ThrowIfNull(currentState);

        if (fanSpeedPercent is < 10 or > 100)
        {
            throw new ArgumentOutOfRangeException(
                nameof(fanSpeedPercent),
                fanSpeedPercent,
                "Fan control percentage must be between 10 and 100 inclusive.");
        }

        return CreateFanCommand(currentState, fanSpeedPercent, powerFlag: 0);
    }

    internal static byte[] CreateSetLighting(V12DeviceState currentState, LightingSettings settings)
    {
        ArgumentNullException.ThrowIfNull(currentState);
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        // Vendor UI disables lighting edits while the fan is OFF. Never implicitly
        // turn it on or expose an OFF frame through the lighting operation.
        if (!currentState.PoweredOn)
            throw new InvalidOperationException("Turn the fan on before changing lighting.");
        var report = CreateSetFanSpeed(currentState, currentState.FanSpeedPercent);
        report[4] = (byte)((settings.Enabled ? 0 : 0x80) | settings.Mode);
        report[5] = (byte)settings.Color;
        report[6] = (byte)(3 - settings.Speed);
        report[7] = (byte)settings.Brightness;
        report[^1] = CalculateChecksum(report.AsSpan(1, 7));
        return report;
    }

    internal static byte[] CreateSystemShutdownOff(V12DeviceState currentState)
    {
        ArgumentNullException.ThrowIfNull(currentState);
        return CreateFanCommand(currentState, fanSpeedPercent: 100, powerFlag: 1);
    }

    internal static byte[] CreateSoftwareTakeoverAtOneHundred(V12DeviceState currentState)
    {
        var report = CreateSetFanSpeed(currentState, 100);
        report[1] = 1; // Vendor llano_fan_mode value:1 -> fan_mode_control:1.
        report[^1] = CalculateChecksum(report.AsSpan(1, 7));
        return report;
    }

    internal static byte[] CreateSoftwareManualSpeed(V12DeviceState currentState, byte fanSpeedPercent)
    {
        // The pinned vendor encoder carries the selected speed independently of
        // fan_mode_control=1. This explicit ON command never stages a 100% write.
        var report = CreateSetFanSpeed(currentState, fanSpeedPercent);
        report[1] = 1;
        report[^1] = CalculateChecksum(report.AsSpan(1, 7));
        return report;
    }

    private static byte[] CreateFanCommand(
        V12DeviceState currentState,
        byte fanSpeedPercent,
        byte powerFlag)
    {
        byte[] report =
        [
            ReportId,
            0,
            fanSpeedPercent,
            powerFlag,
            currentState.LightModeAndPower,
            currentState.LightColorMode,
            currentState.LightSpeed,
            currentState.LightPower,
            0,
        ];

        report[^1] = CalculateChecksum(report.AsSpan(1, 7));
        return report;
    }

    public static bool HasValidChecksum(ReadOnlySpan<byte> report)
    {
        if (report.Length != ReportLength || report[0] != ReportId)
        {
            return false;
        }

        var sum = 0;
        for (var index = 1; index < report.Length; index++)
        {
            sum += report[index];
        }

        return (sum & 0xFF) == 0xFF;
    }

    public static byte CalculateChecksum(ReadOnlySpan<byte> firstSevenPayloadBytes)
    {
        if (firstSevenPayloadBytes.Length != 7)
        {
            throw new ArgumentException("Exactly seven payload bytes are required.", nameof(firstSevenPayloadBytes));
        }

        var sum = 0;
        foreach (var value in firstSevenPayloadBytes)
        {
            sum += value;
        }

        return unchecked((byte)~(sum & 0xFF));
    }

    private static void ValidateReport(ReadOnlySpan<byte> report)
    {
        if (report.Length != ReportLength)
        {
            throw new V12ProtocolException(
                $"Expected a {ReportLength}-byte Windows HID feature report, received {report.Length} bytes.");
        }

        if (report[0] != ReportId)
        {
            throw new V12ProtocolException($"Unsupported HID report ID: {report[0]}.");
        }

        if (!HasValidChecksum(report))
        {
            throw new V12ProtocolException("The V12 feature report checksum is invalid.");
        }
    }
}
