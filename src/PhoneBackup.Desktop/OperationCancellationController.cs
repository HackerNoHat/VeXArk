namespace PhoneBackup.Desktop;

internal sealed class OperationCancellationController : IDisposable
{
    private readonly object _gate = new();
    private CancellationTokenSource? _source;

    public bool IsActive
    {
        get
        {
            lock (_gate)
                return _source is not null;
        }
    }

    public bool CanCancel
    {
        get
        {
            lock (_gate)
                return _source is { IsCancellationRequested: false };
        }
    }

    public CancellationToken Begin()
    {
        lock (_gate)
        {
            if (_source is not null)
                throw new InvalidOperationException("A cancellable operation is already active.");
            _source = new CancellationTokenSource();
            return _source.Token;
        }
    }

    public bool Cancel()
    {
        lock (_gate)
        {
            if (_source is null || _source.IsCancellationRequested)
                return false;
            _source.Cancel();
            return true;
        }
    }

    public void Complete()
    {
        CancellationTokenSource? source;
        lock (_gate)
        {
            source = _source;
            _source = null;
        }
        source?.Dispose();
    }

    public void Dispose() => Complete();
}
