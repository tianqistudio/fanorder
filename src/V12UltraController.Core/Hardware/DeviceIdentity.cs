using System.Text.Json.Serialization;

namespace V12UltraController.Core.Hardware;

public sealed record DeviceIdentity(
    ushort VendorId,
    ushort ProductId,
    ushort Version,
    ushort UsagePage,
    ushort Usage,
    ushort FeatureReportLength = V12FeatureReport.ReportLength)
{
    [JsonIgnore]
    public bool IsSupportedV12Ultra => DeviceAllowList.IsSupported(this);
}

public static class DeviceAllowList
{
    public const ushort V12UltraVendorId = 0x374A;
    public const ushort V12UltraProductId = 0xB101;
    public const ushort V12UltraFirmwareVersion = 0x1602;
    public const ushort V12UltraUsagePage = 0xFF00;
    public const ushort V12UltraUsage = 0x0001;

    public static bool IsSupported(DeviceIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);

        return identity is
        {
            VendorId: V12UltraVendorId,
            ProductId: V12UltraProductId,
            Version: V12UltraFirmwareVersion,
            UsagePage: V12UltraUsagePage,
            Usage: V12UltraUsage,
            FeatureReportLength: V12FeatureReport.ReportLength,
        };
    }
}
