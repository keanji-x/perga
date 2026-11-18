using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

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

        foreach (var kvp in invocation.Environment)
        {
            psi.Environment[kvp.Key] = kvp.Value;
        }

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

    public async Task WriteLineAsync(string line, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        await _stdinLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
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

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _disposeCts.CancelAsync().ConfigureAwait(false);

        try
        {
            _process.StandardInput.Close();
        }
        catch
        {
            // Ignore stdin close errors during shutdown.
        }

        try
        {
            if (!_process.HasExited)
            {
                using var graceCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
                try
                {
                    await _process.WaitForExitAsync(graceCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Proceed to force kill below.
                }
            }

            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                using var killWaitCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try
                {
                    await _process.WaitForExitAsync(killWaitCts.Token).ConfigureAwait(false);
                }
                catch
                {
                    // Best-effort wait after kill.
                }
            }
        }
        catch
        {
            // Process may have exited concurrently.
        }

        try
        {
            await _stderrPumpTask.WaitAsync(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
        }
        catch
        {
            // Best-effort drain.
        }

        try
        {
            ShutdownConfirmed = _process.HasExited;
        }
        catch (InvalidOperationException)
        {
            ShutdownConfirmed = false;
        }
        _process.Dispose();
        _stdinLock.Dispose();
        _disposeCts.Dispose();
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
