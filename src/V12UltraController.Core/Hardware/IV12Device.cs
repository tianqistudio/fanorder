namespace V12UltraController.Core.Hardware;

public interface IV12Device : IAsyncDisposable
{
    DeviceIdentity Identity { get; }

    Task<V12DeviceState> ReadStateAsync(CancellationToken cancellationToken);

    Task WriteFanSpeedAsync(byte percent, CancellationToken cancellationToken);
}

internal interface IV12LightingDevice
{
    Task WriteLightingAsync(LightingSettings settings, CancellationToken cancellationToken);
}

internal interface IV12SystemShutdownDevice
{
    Task WriteSystemShutdownOffAsync(CancellationToken cancellationToken);
}

internal interface IV12SoftwareControlDevice
{
    Task SelectSoftwareControlAtOneHundredAsync(CancellationToken cancellationToken);
}

internal interface IV12ManualSpeedDevice
{
    Task SelectSoftwareControlAtSpeedAsync(byte percent, CancellationToken cancellationToken);
}

/// <summary>
/// Allows the service to restore a previously validated, identity-bound device state solely
/// so an ON+100 safety write can preserve lighting after a new handle's first read fails.
/// This is intentionally internal and does not expose a general raw-report write surface.
/// </summary>
internal interface IV12TrustedStateInitializer
{
    Task InitializeTrustedStateAsync(V12DeviceState state, CancellationToken cancellationToken);
}

public interface IV12DeviceLocator
{
    Task<IReadOnlyList<DeviceIdentity>> EnumerateAsync(CancellationToken cancellationToken);

    Task<V12DeviceOpenResult> OpenExpectedAsync(
        DeviceIdentity expectedIdentity,
        CancellationToken cancellationToken);
}

public enum V12DeviceOpenStatus
{
    Opened,
    DeviceNotFound,
    MultipleCandidates,
    ExpectedIdentityUnsupported,
    DescriptorIdentityMismatch,
    DeviceBusy,
    HandleValidationFailed,
    HandleIdentityMismatch,
}

public sealed record V12DeviceOpenResult
{
    private V12DeviceOpenResult(V12DeviceOpenStatus status, IV12Device? device)
    {
        Status = status;
        Device = device;
    }

    public V12DeviceOpenStatus Status { get; }

    public IV12Device? Device { get; }

    public static V12DeviceOpenResult Opened(IV12Device device) =>
        new(V12DeviceOpenStatus.Opened, device ?? throw new ArgumentNullException(nameof(device)));

    public static V12DeviceOpenResult Failed(V12DeviceOpenStatus status)
    {
        if (status == V12DeviceOpenStatus.Opened)
        {
            throw new ArgumentOutOfRangeException(nameof(status), status,
                "An opened result requires a device instance.");
        }

        return new V12DeviceOpenResult(status, null);
    }
}

internal interface IHidFeatureTransport : IAsyncDisposable
{
    DeviceIdentity Identity { get; }

    int FeatureReportLength { get; }

    ValueTask SetFeatureAsync(ReadOnlyMemory<byte> report, CancellationToken cancellationToken);

    ValueTask<byte[]> GetFeatureAsync(byte reportId, CancellationToken cancellationToken);
}

internal sealed class V12Device : IV12Device, IV12SystemShutdownDevice, IV12TrustedStateInitializer, IV12SoftwareControlDevice, IV12LightingDevice, IV12ManualSpeedDevice
{
    private readonly IHidFeatureTransport _transport;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    internal static readonly TimeSpan CommandSpacing = TimeSpan.FromMilliseconds(10);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private V12DeviceState? _lastState;
    private bool _disposed;

    public V12Device(IHidFeatureTransport transport, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _delay = delay ?? Task.Delay;
        if (!DeviceAllowList.IsSupported(transport.Identity))
        {
            throw new NotSupportedException("The HID transport does not identify a supported V12 Ultra device.");
        }

        if (transport.FeatureReportLength != V12FeatureReport.ReportLength)
        {
            throw new NotSupportedException(
                $"The V12 Ultra feature report length must be {V12FeatureReport.ReportLength} bytes.");
        }
    }

    public DeviceIdentity Identity => _transport.Identity;

    public async Task<V12DeviceState> ReadStateAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Pinned vendor query waits 10 ms before SET and another 10 ms before
            // GET (414F3A / 415014). Keep the entire paced query serialized.
            await _delay(CommandSpacing, cancellationToken).ConfigureAwait(false);
            await _transport
                .SetFeatureAsync(V12FeatureReport.CreateReadQuery(), cancellationToken)
                .ConfigureAwait(false);
            await _delay(CommandSpacing, cancellationToken).ConfigureAwait(false);
            var response = await _transport.GetFeatureAsync(V12FeatureReport.ReportId, cancellationToken)
                .ConfigureAwait(false);
            _lastState = V12FeatureReport.ParseState(response);
            return _lastState;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task WriteFanSpeedAsync(byte percent, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_lastState is null)
            {
                throw new InvalidOperationException(
                    "A verified state read is required before the first fan write so lighting fields can be preserved.");
            }

            var report = V12FeatureReport.CreateSetFanSpeed(_lastState, percent);
            await _delay(CommandSpacing, cancellationToken).ConfigureAwait(false);
            await _transport.SetFeatureAsync(report, cancellationToken).ConfigureAwait(false);
            _lastState = V12FeatureReport.ParseState(report);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task WriteLightingAsync(LightingSettings settings, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_lastState is null) throw new InvalidOperationException("Lighting requires a verified state read.");
            var report = V12FeatureReport.CreateSetLighting(_lastState, settings);
            await _delay(CommandSpacing, cancellationToken).ConfigureAwait(false);
            await _transport.SetFeatureAsync(report, cancellationToken).ConfigureAwait(false);
            _lastState = V12FeatureReport.ParseState(report);
        }
        finally { _gate.Release(); }
    }

    public async Task WriteSystemShutdownOffAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_lastState is null)
            {
                throw new InvalidOperationException(
                    "A verified state read is required before the system-shutdown fan-off write.");
            }

            var report = V12FeatureReport.CreateSystemShutdownOff(_lastState);
            await _delay(CommandSpacing, cancellationToken).ConfigureAwait(false);
            await _transport.SetFeatureAsync(report, cancellationToken).ConfigureAwait(false);
            _lastState = V12FeatureReport.ParseState(report);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SelectSoftwareControlAtOneHundredAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_lastState is null) throw new InvalidOperationException("Software takeover requires verified device state.");
            var report = V12FeatureReport.CreateSoftwareTakeoverAtOneHundred(_lastState);
            await _delay(CommandSpacing, cancellationToken).ConfigureAwait(false);
            await _transport.SetFeatureAsync(report, cancellationToken).ConfigureAwait(false);
            _lastState = V12FeatureReport.ParseState(report);
        }
        finally { _gate.Release(); }
    }

    public async Task SelectSoftwareControlAtSpeedAsync(byte percent, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_lastState is null)
                throw new InvalidOperationException("Explicit software speed selection requires verified device state.");
            var report = V12FeatureReport.CreateSoftwareManualSpeed(_lastState, percent);
            await _delay(CommandSpacing, cancellationToken).ConfigureAwait(false);
            await _transport.SetFeatureAsync(report, cancellationToken).ConfigureAwait(false);
            _lastState = V12FeatureReport.ParseState(report);
        }
        finally { _gate.Release(); }
    }

    public async Task InitializeTrustedStateAsync(
        V12DeviceState state,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_lastState is not null)
            {
                throw new InvalidOperationException(
                    "A live or cached V12 state has already initialized this device handle.");
            }

            // The service validates the cache's exact device identity, schema, SHA-256 and
            // protocol checksum before invoking this internal one-time initializer.
            _lastState = state;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await _transport.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }
}
