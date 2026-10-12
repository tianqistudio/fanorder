namespace V12UltraController.Core.Status;

/// <summary>Piecewise interpolation of user-observed display values, not live RPM telemetry.</summary>
public static class FanDisplay
{
    private static readonly (int Percent, int Rpm)[] Calibration = [(10, 550), (14, 650), (24, 900), (100, 2800)];

    public static int EstimateRpm(byte percent)
    {
        if (percent is < 10 or > 100) throw new ArgumentOutOfRangeException(nameof(percent));
        for (var i = 1; i < Calibration.Length; i++)
        {
            var left = Calibration[i - 1];
            var right = Calibration[i];
            if (percent <= right.Percent)
                return (int)Math.Round(left.Rpm + (percent - left.Percent) *
                    (double)(right.Rpm - left.Rpm) / (right.Percent - left.Percent), MidpointRounding.AwayFromZero);
        }
        throw new InvalidOperationException("Calibration must cover the entire supported range.");
    }

    public static string Estimate(byte percent)
    {
        if (percent is < 10 or > 100) throw new ArgumentOutOfRangeException(nameof(percent));
        return $"{percent}% · 约 {EstimateRpm(percent)} RPM（估算）";
    }

    public static string Readback(byte? percent, bool? poweredOn, bool fresh)
    {
        if (!fresh) return "设备回读：未知 / RPM 未知";
        if (poweredOn == false) return "设备回读：0% · 0 RPM（已关闭）";
        if (poweredOn != true || percent is not (>= 10 and <= 100))
            return "设备回读：未知 / RPM 未知";
        return $"设备回读：{Estimate(percent.Value)}";
    }
}
