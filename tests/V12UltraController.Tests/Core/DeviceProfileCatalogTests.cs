using V12UltraController.Core.Hardware;

namespace V12UltraController.Tests.Core;

public class DeviceProfileCatalogTests
{
    [Fact]
    public void OnlyValidatedV12ProfileCanBeSelected()
    {
        var profile = Assert.Single(DeviceProfileCatalog.Supported);
        Assert.Equal("llano V12Ultra", profile.DisplayName);
        Assert.True(DeviceAllowList.IsSupported(profile.Identity));
        Assert.True(DeviceProfileCatalog.CanSelect(profile.Id));
        Assert.False(DeviceProfileCatalog.CanSelect("future-device"));
        Assert.False(DeviceProfileCatalog.CanSelect(""));
    }
}
