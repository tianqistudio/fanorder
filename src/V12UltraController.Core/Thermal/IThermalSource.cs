namespace V12UltraController.Core.Thermal;

public sealed record ThermalSnapshot(
    DateTimeOffset CapturedAt,
    double? CpuCelsius,
    IReadOnlyList<double> GpuCelsius)
{
    public bool CpuIsValid => IsValidTemperature(CpuCelsius);

    public IReadOnlyList<double> ValidGpuCelsius => GpuCelsius
        .Where(static value => IsValidTemperature(value))
        .ToArray();

    public double? ControlCelsius
    {
        get
        {
            if (!CpuIsValid)
            {
                return null;
            }

            var maximum = CpuCelsius!.Value;
            foreach (var temperature in GpuCelsius)
            {
                if (IsValidTemperature(temperature))
                {
                    maximum = Math.Max(maximum, temperature);
                }
            }

            return maximum;
        }
    }

    public static bool IsValidTemperature(double? value) =>
        value is not null && double.IsFinite(value.Value) && value is > 0 and <= 120;
}

public interface IThermalSource : IAsyncDisposable
{
    Task<ThermalSnapshot> ReadAsync(CancellationToken cancellationToken);
}
