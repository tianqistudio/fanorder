namespace V12UltraController.Core.Control;

/// <summary>Pure input math; no device or desktop access. One wheel detent = one percentage point.</summary>
public sealed class TrayWheelAccumulator
{
    private int remainder;

    public byte Apply(int delta, byte baseline)
    {
        if (baseline is < 10 or > 100) throw new ArgumentOutOfRangeException(nameof(baseline));
        var total = (long)remainder + delta;
        var steps = total / 120;
        remainder = (int)(total % 120);
        return (byte)Math.Clamp(baseline + steps, 10, 100);
    }

    public void Reset() => remainder = 0;
}
