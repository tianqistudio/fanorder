namespace V12UltraController.Core.Configuration;

public sealed record FanCurvePoint(double TemperatureCelsius, byte FanPercent);

public sealed class FanCurve
{
    public FanCurve(IReadOnlyList<FanCurvePoint> points)
    {
        Points = Array.AsReadOnly(points?.ToArray() ?? throw new ArgumentNullException(nameof(points)));
    }

    public IReadOnlyList<FanCurvePoint> Points { get; }

    public static FanCurve RemoteHostSafe => new(
    [
        new(0, 36),
        new(30, 36),
        new(40, 43),
        new(50, 50),
        new(60, 57),
        new(70, 68),
        new(80, 79),
        new(85, 90),
        new(90, 100),
        new(100, 100),
    ]);

    public void Validate()
    {
        if (Points.Count is < 2 or > 20)
        {
            throw new ConfigurationValidationException("A fan curve must contain between 2 and 20 points.");
        }

        double? previousTemperature = null;
        byte? previousFan = null;
        foreach (var point in Points)
        {
            if (!double.IsFinite(point.TemperatureCelsius)
                || point.TemperatureCelsius is < 0 or > 100)
            {
                throw new ConfigurationValidationException("Fan-curve temperatures must be finite values from 0 to 100°C.");
            }

            if (point.FanPercent is < 10 or > 100)
            {
                throw new ConfigurationValidationException("Fan-curve percentages must be between 10 and 100.");
            }

            if (previousTemperature is not null && point.TemperatureCelsius <= previousTemperature)
            {
                throw new ConfigurationValidationException("Fan-curve temperatures must be strictly increasing.");
            }

            if (previousFan is not null && point.FanPercent < previousFan)
            {
                throw new ConfigurationValidationException("Fan-curve percentages must not decrease as temperature rises.");
            }

            previousTemperature = point.TemperatureCelsius;
            previousFan = point.FanPercent;
        }
    }

    public double Interpolate(double temperatureCelsius)
    {
        Validate();
        if (!double.IsFinite(temperatureCelsius))
        {
            throw new ArgumentOutOfRangeException(nameof(temperatureCelsius));
        }

        if (temperatureCelsius <= Points[0].TemperatureCelsius)
        {
            return Points[0].FanPercent;
        }

        for (var index = 1; index < Points.Count; index++)
        {
            var upper = Points[index];
            if (temperatureCelsius > upper.TemperatureCelsius)
            {
                continue;
            }

            var lower = Points[index - 1];
            var fraction = (temperatureCelsius - lower.TemperatureCelsius)
                / (upper.TemperatureCelsius - lower.TemperatureCelsius);
            return lower.FanPercent + ((upper.FanPercent - lower.FanPercent) * fraction);
        }

        return Points[^1].FanPercent;
    }
}
