using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Perga.Processes;

namespace Perga.AgentKit.Internal;

internal sealed class StdioSubprocess : IAsyncDisposable
{
    private const int PosixSigInt = 2;
    private const int MaxStderrLines = 64;
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private readonly Process _process;
    private readonly SemaphoreSlim _stdinLock = new(1, 1);
    private readonly ConcurrentQueue<string> _stderrTail = new();
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly Task _stderrPumpTask;
    private int _disposed;
    private readonly object _sync = new();
    private Task? _activeShutdownTask;
    private int _stdinClosed;

    // Internal bounds allow real-child tests to exercise an unconfirmed shutdown.
    internal TimeSpan GracefulShutdownTimeout { get; set; } = TimeSpan.FromMilliseconds(250);
    internal TimeSpan KillWaitTimeout { get; set; } = TimeSpan.FromSeconds(2);
    internal Action<Process> KillProcessTree { get; set; } = process => process.Kill(entireProcessTree: true);

    private StdioSubprocess(Process process)
    {
        _process = process;
        _stderrPumpTask = Task.Run(PumpStderrAsync);
    }

    public int ProcessId => _process.Id;

    public bool HasExited
    {
        get
        {
            try
            {
                return _process.HasExited;
            }
            catch
            {
                return true;
            }
        }
    }

    public int? TryGetExitCode()
    {
        try
        {
            return _process.HasExited ? _process.ExitCode : null;
        }
        catch
        {
            return null;
        }
    }

    public static StdioSubprocess Start(CommandInvocation invocation)
    {
        ArgumentNullException.ThrowIfNull(invocation);

        var psi = new ProcessStartInfo
        {
            FileName = invocation.FileName,
            WorkingDirectory = invocation.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardInputEncoding = Utf8NoBom,
            StandardOutputEncoding = Utf8NoBom,
            StandardErrorEncoding = Utf8NoBom
        };

        foreach (var arg in invocation.Arguments)
        {
            psi.ArgumentList.Add(arg);
        }

        ChildProcessEnvironment.Apply(psi, invocation.Environment);

        var process = new Process
        {
            StartInfo = psi,
            EnableRaisingEvents = true
        };

        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException($"Failed to start process '{invocation.FileName}'.");
            }
        }
        catch (Exception ex)
        {
            process.Dispose();
            throw new InvalidOperationException(
                $"Failed to launch provider executable '{invocation.FileName}': {ex.Message}",
                ex);
        }

        return new StdioSubprocess(process);
    }

    public Task WriteLineAsync(string line, CancellationToken ct)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            return WriteLineCoreAsync(line, ct);
        }
    }

    private async Task WriteLineCoreAsync(string line, CancellationToken ct)
    {
        await _stdinLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            await _process.StandardInput.WriteLineAsync(line.AsMemory(), ct).ConfigureAwait(false);
            await _process.StandardInput.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _stdinLock.Release();
        }
    }

    public async ValueTask<string?> ReadStdoutLineAsync(CancellationToken ct)
    {
        try
        {
            return await _process.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (ObjectDisposedException)
        {
            return null;
        }
    }

    public string GetStderrSummary()
    {
        var items = _stderrTail.ToArray();
        return items.Length == 0 ? string.Empty : string.Join(Environment.NewLine, items);
    }

    public bool SendInterruptSignal()
    {
        if (HasExited)
        {
            return false;
        }

        try
        {
            if (!OperatingSystem.IsWindows())
            {
                return PosixKill(_process.Id, PosixSigInt) == 0;
            }

            _process.Kill(entireProcessTree: true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    internal bool ShutdownConfirmed { get; private set; }

    public ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            if (ShutdownConfirmed) return ValueTask.CompletedTask;
            if (_activeShutdownTask is null || _activeShutdownTask.IsCompleted)
            {
                Interlocked.Exchange(ref _disposed, 1);
                _activeShutdownTask = ShutdownAsync();
            }
            return new ValueTask(_activeShutdownTask);
        }
    }

    private void TryCloseStdinOnce()
    {
        if (Interlocked.Exchange(ref _stdinClosed, 1) != 0) return;
        try { _process.StandardInput.Close(); }
        catch { /* The child may already have closed its input. */ }
    }

    private async Task WaitForExitAsync(TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try { await _process.WaitForExitAsync(cts.Token).ConfigureAwait(false); }
        catch { /* Retain ownership unless HasExited confirms exit below. */ }
    }

    private async Task ShutdownAsync()
    {
        // Serialize closure with writers so no writer's finally can release a disposed lock.
        if (await _stdinLock.WaitAsync(GracefulShutdownTimeout).ConfigureAwait(false))
        {
            try { TryCloseStdinOnce(); }
            finally { _stdinLock.Release(); }
        }

        if (!_process.HasExited)
            await WaitForExitAsync(GracefulShutdownTimeout).ConfigureAwait(false);
        if (!_process.HasExited)
        {
            try { KillProcessTree(_process); }
            catch { /* A later disposal retries the same process if it remains alive. */ }
            await WaitForExitAsync(KillWaitTimeout).ConfigureAwait(false);
        }

        if (!_process.HasExited) return;

        try { await _stderrPumpTask.WaitAsync(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false); }
        catch { /* Bound draining in case a descendant still owns the pipe. */ }
        await _disposeCts.CancelAsync().ConfigureAwait(false);
        // Publish confirmation only after cleanup; concurrent disposals join this attempt.
        _process.Dispose();
        // A blocked writer may still release this semaphore after the child exits.
        // Leave the managed semaphore alive until all references are collected.
        _disposeCts.Dispose();
        ShutdownConfirmed = true;
    }

    private async Task PumpStderrAsync()
    {
        try
        {
            while (!_disposeCts.IsCancellationRequested)
            {
                var line = await _process.StandardError.ReadLineAsync(_disposeCts.Token).ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                _stderrTail.Enqueue(line);
                while (_stderrTail.Count > MaxStderrLines && _stderrTail.TryDequeue(out _))
                {
                }
            }
        }
        catch
        {
            // Ignore cancellation/stream closure on teardown.
        }
    }

    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int PosixKill(int pid, int sig);
}
