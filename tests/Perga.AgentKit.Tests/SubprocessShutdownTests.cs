using System.Diagnostics;
using Perga.AgentKit.Internal;

namespace Perga.AgentKit.Tests;

public sealed class SubprocessShutdownTests
{
    [Fact]
    public async Task BlockedStdinWriterDoesNotPreventProcessKill()
    {
        var process = StdioSubprocess.Start(new CommandInvocation("/bin/sh",
            ["-c", "sleep 30"], Path.GetTempPath(), new Dictionary<string, string>()));
        process.GracefulShutdownTimeout = TimeSpan.FromMilliseconds(25);
        process.KillWaitTimeout = TimeSpan.FromSeconds(2);
        using var child = Process.GetProcessById(process.ProcessId);
        var writing = process.WriteLineAsync(new string('x', 4 * 1024 * 1024), CancellationToken.None);
        try
        {
            Assert.False(writing.IsCompleted);
            await process.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(process.ShutdownConfirmed);
            Assert.True(child.HasExited);
            // The writer may fail with a broken pipe, but must release its live semaphore.
            var error = await Record.ExceptionAsync(() => writing.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.IsNotType<ObjectDisposedException>(error);
            Assert.IsNotType<TimeoutException>(error);
        }
        finally
        {
            if (!child.HasExited) child.Kill(entireProcessTree: true);
            await process.DisposeAsync();
            try { await writing; } catch { }
        }
    }

    [Fact]
    public async Task FailedOpenCanRetryShutdownOfTheSameRealSubprocess()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"perga-shutdown-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var script = Path.Combine(directory, "bridge.cjs");
        await File.WriteAllTextAsync(script, """
            const readline = require('node:readline');
            const input = readline.createInterface({ input: process.stdin });
            setInterval(() => {}, 1000);
            input.on('line', line => {
              const request = JSON.parse(line);
              const response = request.method === 'session/open'
                ? { error: { code: -32000, message: 'deliberate open failure' } }
                : { result: { closed: true } };
              console.log(JSON.stringify({ jsonrpc: '2.0', id: request.id, ...response }));
            });
            """);
        await using var runtime = new AgentRuntime(new AgentRuntimeOptions
        {
            NodeExecutablePath = OarNodeResolver.ResolveNode24Executable(null, null),
            BridgeScriptPath = script
        });
        var session = Assert.IsType<OarAgentSession>(runtime.CreateSession(new HarnessLaunchSpec(
            "codex", null, directory, null, null, new Dictionary<string, McpServerConfig>(), PermissionMode: "never")));
        var process = session.Subprocess;
        var pid = process.ProcessId;
        using var child = Process.GetProcessById(pid);
        var killedPids = new List<int>();
        process.GracefulShutdownTimeout = TimeSpan.FromMilliseconds(10);
        process.KillWaitTimeout = TimeSpan.FromMilliseconds(20);
        process.KillProcessTree = candidate =>
        {
            killedPids.Add(candidate.Id);
            // Model a temporarily refused kill while retaining a real live OS child.
            throw new System.ComponentModel.Win32Exception("deliberate kill failure");
        };
        try
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => session.InitializeAsync(CancellationToken.None));
            Assert.Contains("deliberate open failure", error.Message);
            Assert.False(child.HasExited);
            Assert.Empty(killedPids); // Initialize never hides disposal on failure.

            await session.DisposeAsync();
            Assert.False(session.ShutdownConfirmed);
            Assert.False(child.HasExited);
            Assert.Equal(pid, process.ProcessId);

            process.KillWaitTimeout = TimeSpan.FromSeconds(2);
            process.KillProcessTree = candidate =>
            {
                killedPids.Add(candidate.Id);
                candidate.Kill(entireProcessTree: true);
            };
            // The runtime must still own the failed, unconfirmed session.
            await runtime.DisposeAsync();
            Assert.True(session.ShutdownConfirmed);
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(new[] { pid, pid }, killedPids);
            await session.DisposeAsync();
            Assert.Equal(2, killedPids.Count);
        }
        finally
        {
            process.KillProcessTree = candidate => candidate.Kill(entireProcessTree: true);
            process.KillWaitTimeout = TimeSpan.FromSeconds(2);
            // Keep test failures (including deliberate mutations) from orphaning the child.
            if (!child.HasExited)
            {
                child.Kill(entireProcessTree: true);
                await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
            await session.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ConcurrentDisposalsJoinTheSameShutdownAttempt()
    {
        var process = StdioSubprocess.Start(new CommandInvocation("/bin/sh",
            ["-c", "while read -r line; do :; done; sleep 10"], Path.GetTempPath(), new Dictionary<string, string>()));
        process.GracefulShutdownTimeout = TimeSpan.FromMilliseconds(50);
        var kills = 0;
        process.KillProcessTree = child =>
        {
            Interlocked.Increment(ref kills);
            child.Kill(entireProcessTree: true);
        };
        var first = process.DisposeAsync().AsTask();
        var second = process.DisposeAsync().AsTask();
        Assert.Same(first, second);
        await Task.WhenAll(first, second);
        Assert.True(process.ShutdownConfirmed);
        Assert.Equal(1, kills);
    }

    [Fact]
    public async Task GracefulEofPreservesStdoutAndStderrPipes()
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disconnected = false;
        var process = StdioSubprocess.Start(new CommandInvocation("/bin/sh",
            ["-c", "printf '%s\\n' '{\"method\":\"ready\"}'; while read -r line; do :; done; sleep 0.05; printf '%s\\n' 'final stderr' >&2; printf '%s\\n' '{\"method\":\"drained\"}'"],
            Path.GetTempPath(), new Dictionary<string, string>()));
        await using var rpc = new JsonRpcStdioConnection(process, (method, _) =>
        {
            if (method == "ready") ready.TrySetResult();
            if (method == "drained") drained.TrySetResult();
        }, (_, _, _) => { }, _ => disconnected = true);
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await rpc.DisposeAsync();
        Assert.True(rpc.ShutdownConfirmed);
        Assert.True(drained.Task.IsCompletedSuccessfully);
        Assert.Contains("final stderr", process.GetStderrSummary());
        Assert.False(disconnected);
    }
}
