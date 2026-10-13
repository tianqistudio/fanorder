namespace V12UltraController.Core.Hardware;

/// <summary>UI discovery metadata, not permission to open an arbitrary HID device.</summary>
public sealed record DeviceProfile(string Id, string DisplayName, DeviceIdentity Identity);

public static class DeviceProfileCatalog
{
    public static DeviceProfile V12Ultra { get; } = new("llano-v12-ultra", "llano V12Ultra",
        new(DeviceAllowList.V12UltraVendorId, DeviceAllowList.V12UltraProductId,
            DeviceAllowList.V12UltraFirmwareVersion, DeviceAllowList.V12UltraUsagePage,
            DeviceAllowList.V12UltraUsage));

    public static IReadOnlyList<DeviceProfile> Supported { get; } = Array.AsReadOnly(new[] { V12Ultra });

    // Adding a catalog entry alone must never enable control. New protocols require a
    // service adapter, identity validation, commissioning and capability tests first.
    public static bool CanSelect(string id) => id == V12Ultra.Id;
}
