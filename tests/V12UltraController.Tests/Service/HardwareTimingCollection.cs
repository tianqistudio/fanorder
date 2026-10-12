namespace V12UltraController.Tests.Service;

/// <summary>
/// These tests intentionally exercise the production 500 ms HID deadline with dedicated OS
/// threads. Running them beside the rest of the suite can starve a newly-created dispatcher long
/// enough to manufacture a timeout that cannot occur in the single service process under test.
/// Keep the timing collection isolated while preserving the production deadline unchanged.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class HardwareTimingCollection
{
    public const string Name = "V12 hardware timing";
}
