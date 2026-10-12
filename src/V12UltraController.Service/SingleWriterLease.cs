namespace V12UltraController.Service;

internal sealed class SingleWriterLease : IDisposable
{
    private readonly string leasePath;
    private readonly object stateLock = new();
    private FileStream? leaseStream;

    public SingleWriterLease(ServicePaths paths)
        : this(paths.WriterLeasePath)
    {
    }

    internal SingleWriterLease(string leasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(leasePath);
        this.leasePath = Path.GetFullPath(leasePath);
    }

    internal bool IsHeld
    {
        get
        {
            lock (stateLock)
            {
                return leaseStream is not null;
            }
        }
    }

    public bool TryAcquire()
    {
        lock (stateLock)
        {
            if (leaseStream is not null)
            {
                return true;
            }

            try
            {
                var directory = Path.GetDirectoryName(leasePath)
                    ?? throw new InvalidOperationException("The writer-lease path has no parent directory.");
                Directory.CreateDirectory(directory);
                leaseStream = new FileStream(
                    leasePath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.WriteThrough);
                return true;
            }
            catch (IOException)
            {
                return false;
            }
        }
    }

    public void Release()
    {
        FileStream? stream;
        lock (stateLock)
        {
            stream = leaseStream;
            leaseStream = null;
        }

        stream?.Dispose();
    }

    public void Dispose()
    {
        Release();
    }
}
