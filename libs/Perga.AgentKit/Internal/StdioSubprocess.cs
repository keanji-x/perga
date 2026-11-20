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
    private readonly Action<Process> _killProcess;
    private readonly SemaphoreSlim _shutdownLock = new(1, 1);
    private readonly SemaphoreSlim _stdinLock = new(1, 1);
    private readonly ConcurrentQueue<string> _stderrTail = new();
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly Task _stderrPumpTask;
    private int _disposed;
    private bool _shutdownConfirmed;

    private StdioSubprocess(Process process, Action<Process>? killProcess)
    {
        _process = process;
        _killProcess = killProcess ?? (static child => child.Kill(entireProcessTree: true));
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

    public static StdioSubprocess Start(CommandInvocation invocation, Action<Process>? killProcess = null)
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

        return new StdioSubprocess(process, killProcess);
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

    internal bool ShutdownConfirmed => Volatile.Read(ref _shutdownConfirmed);

    public async ValueTask DisposeAsync()
    {
        await _shutdownLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (ShutdownConfirmed) return;
            var firstAttempt = Interlocked.Exchange(ref _disposed, 1) == 0;
            if (firstAttempt)
                await CloseInputAsync().ConfigureAwait(false);

            if (firstAttempt)
                await WaitForExitAsync(TimeSpan.FromMilliseconds(250)).ConfigureAwait(false);
            if (!TryConfirmExit())
            {
                try { _killProcess(_process); }
                catch (System.ComponentModel.Win32Exception) { /* A later attempt can retry the same process. */ }
                catch (InvalidOperationException) { /* The process may have exited concurrently. */ }
                await WaitForExitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }

            // Retain the original process handle until exit is observed. Reopening by PID
            // could signal an unrelated process after the identifier has been recycled.
            if (!TryConfirmExit()) return;
            Volatile.Write(ref _shutdownConfirmed, true);
            try
            {
                _process.StandardOutput.Dispose();
                _process.StandardError.Dispose();
            }
            finally { _process.Dispose(); }
        }
        finally { _shutdownLock.Release(); }
    }

    private async Task CloseInputAsync()
    {
        await _disposeCts.CancelAsync().ConfigureAwait(false);
        _process.StandardInput.BaseStream.Dispose();
        // Keep the read ends open while the bridge handles stdin EOF and shuts down its provider.
        try { await _stderrPumpTask.WaitAsync(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false); }
        catch (TimeoutException) { /* Cancellation was requested. Keep the pipe open until exit. */ }
        _disposeCts.Dispose();
    }

    private async Task WaitForExitAsync(TimeSpan timeout)
    {
        using var deadline = new CancellationTokenSource(timeout);
        try { await _process.WaitForExitAsync(deadline.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested) { }
        catch (InvalidOperationException) { /* No exit evidence from this wait. */ }
        catch (System.ComponentModel.Win32Exception) { /* No exit evidence from this wait. */ }
    }

    private bool TryConfirmExit()
    {
        try { return _process.HasExited; }
        catch (InvalidOperationException) { return false; }
        catch (System.ComponentModel.Win32Exception) { return false; }
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
