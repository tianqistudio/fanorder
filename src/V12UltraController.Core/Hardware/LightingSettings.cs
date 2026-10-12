namespace V12UltraController.Core.Hardware;

/// <summary>Vendor presets, not arbitrary RGB channels. Speed increases from 0 to 3.</summary>
public sealed record LightingSettings(bool Enabled, int Mode, int Color, int Brightness, int Speed)
{
    public void Validate()
    {
        if (Mode is < 0 or > 3 || Color is < 0 or > 4 ||
            Brightness is < 0 or > 255 || Speed is < 0 or > 3)
            throw new ArgumentOutOfRangeException(nameof(LightingSettings),
                "Lighting requires mode 0..3, color 0..4, brightness 0..255 and speed 0..3.");
    }

    public static LightingSettings? FromState(V12DeviceState state)
    {
        var mode = state.LightModeAndPower & 0x7F;
        if (mode > 3 || state.LightColorMode > 4 || state.LightSpeed > 3) return null;
        return new((state.LightModeAndPower & 0x80) == 0, mode,
            state.LightColorMode, state.LightPower, 3 - state.LightSpeed);
    }
}
