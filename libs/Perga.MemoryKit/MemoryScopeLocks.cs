namespace Perga.MemoryKit;

// Coordinate engine instances in this process. Any writer outside the engine must
// coordinate separately with the owner of the memory directory.
internal static class MemoryScopeLocks
{
    private static readonly Lock Gate = new();
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static readonly Dictionary<string, ScopeLock> Scopes = new(PathComparer);

    internal static async Task<IDisposable> AcquireAllAsync(IEnumerable<string> directories, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var ordered = directories.Distinct(PathComparer).Order(PathComparer).ToArray();
        var leases = new List<IDisposable>(ordered.Length);
        var group = new LeaseGroup(leases);
        try
        {
            foreach (var directory in ordered)
                leases.Add(await AcquireAsync(directory, ct));
            return group;
        }
        catch
        {
            group.Dispose();
            throw;
        }
    }

    internal static async Task<IDisposable> AcquireAsync(string directory, CancellationToken ct)
    {
        ScopeLock scope;
        lock (Gate)
        {
            if (!Scopes.TryGetValue(directory, out scope!))
            {
                scope = new ScopeLock();
                Scopes.Add(directory, scope);
            }
            scope.Users++;
        }

        try
        {
            await scope.Semaphore.WaitAsync(ct);
            return new Lease(directory, scope);
        }
        catch
        {
            Dereference(directory, scope);
            throw;
        }
    }

    private static void Dereference(string directory, ScopeLock scope)
    {
        lock (Gate)
        {
            if (--scope.Users == 0)
            {
                Scopes.Remove(directory);
                scope.Semaphore.Dispose();
            }
        }
    }

    private sealed class ScopeLock
    {
        internal SemaphoreSlim Semaphore { get; } = new(1, 1);
        internal int Users { get; set; }
    }

    private sealed class LeaseGroup(List<IDisposable> leases) : IDisposable
    {
        public void Dispose()
        {
            for (var i = leases.Count - 1; i >= 0; i--)
                leases[i].Dispose();
        }
    }

    private sealed class Lease(string directory, ScopeLock scope) : IDisposable
    {
        private ScopeLock? _scope = scope;

        public void Dispose()
        {
            var held = Interlocked.Exchange(ref _scope, null);
            if (held is null)
                return;
            held.Semaphore.Release();
            Dereference(directory, held);
        }
    }
}
