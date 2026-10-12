namespace V12UltraController.Cli;

internal static class CliExitCodes
{
    public const int Success = 0;
    public const int InvalidArguments = 2;
    public const int ServiceUnavailable = 3;
    public const int DeviceUnavailable = 4;
    public const int FailSafeActive = 5;
    public const int MythCoolConflict = 6;
    public const int HidOrProtocolError = 7;

    public static int ForError(string? code) => code switch
    {
        "INVALID_ARGUMENT" => InvalidArguments,
        "SERVICE_UNAVAILABLE" => ServiceUnavailable,
        "NOT_COMMISSIONED" or "DEVICE_NOT_FOUND" or "UNSUPPORTED_DEVICE" => DeviceUnavailable,
        "TELEMETRY_STALE" or "FAILSAFE_ACTIVE" => FailSafeActive,
        "MYTHCOOL_CONFLICT" => MythCoolConflict,
        "HID_TIMEOUT" or "PROTOCOL_CHECKSUM" or "DEVICE_STATE_CHANGED" or "COMMAND_READBACK_MISMATCH" => HidOrProtocolError,
        _ => ServiceUnavailable,
    };
}
