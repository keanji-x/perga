namespace Perga.Kernel.SystemUpgrade;

/// <summary>One admission boundary for provider launches and turns, independent of idle sessions.</summary>
public sealed class DispatchAdmission
{
    private readonly object _sync = new();
    private TaskCompletionSource _open = Completed();
    private bool _draining;
    private int _active;
    public event Action? Changed;
    public event Action? Resumed;
    public bool Draining
    {
        get
        {
            lock (_sync)
            {
                return _draining;
            }
        }
    }
    public int ActiveDispatches
    {
        get
        {
            lock (_sync)
            {
                return _active;
            }
        }
    }

    public void SetDraining(bool draining)
    {
        lock (_sync)
        {
            if (_draining == draining)
            {
                return;
            }
            _draining = draining;
            if (draining)
            {
                _open = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            else
            {
                _open.TrySetResult();
            }
        }
        Changed?.Invoke();
        if (!draining)
        {
            Resumed?.Invoke();
        }
    }

    public IDisposable? TryEnter()
    {
        lock (_sync)
        {
            if (_draining)
            {
                return null;
            }
            _active++;
        }
        Changed?.Invoke();
        return new Lease(this);
    }

    public async ValueTask<IDisposable> EnterAsync(CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (TryEnter() is { } lease)
            {
                return lease;
            }
            Task wait;
            lock (_sync)
            {
                wait = _open.Task;
            }
            await wait.WaitAsync(ct).ConfigureAwait(false);
        }
    }

    private sealed class Lease(DispatchAdmission owner) : IDisposable
    {
        private DispatchAdmission? _owner = owner;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _owner, null) is not { } value)
            {
                return;
            }
            lock (value._sync)
            {
                value._active--;
            }
            value.Changed?.Invoke();
        }
    }
    private static TaskCompletionSource Completed()
    {
        var value = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        value.SetResult();
        return value;
    }
}
