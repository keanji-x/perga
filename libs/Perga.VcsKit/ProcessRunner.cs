using System.ComponentModel;
using System.Diagnostics;
using Perga.Processes;

namespace Perga.VcsKit;

public sealed record ProcessRunRequest(
    string FileName,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string?>? Environment = null,
    string? StandardInput = null,
    bool InheritEnvironment = true);

public sealed record ProcessRunResult(
    int ExitCode,
    string StandardOutput,
    string StandardError);

public interface IProcessRunner
{
    Task<ProcessRunResult> RunAsync(ProcessRunRequest request, CancellationToken ct = default);
}

public sealed class VcsCommandException : InvalidOperationException
{
    public string Command { get; }
    public int ExitCode { get; }
    public string StandardOutput { get; }
    public string StandardError { get; }

    public VcsCommandException(
        string command,
        int exitCode,
        string standardOutput,
        string standardError,
        string? message = null,
        Exception? innerException = null)
        : base(
            message ?? BuildDefaultMessage(command, exitCode, standardOutput, standardError),
            innerException)
    {
        Command = command;
        ExitCode = exitCode;
        StandardOutput = standardOutput;
        StandardError = standardError;
    }

    private static string BuildDefaultMessage(
        string command,
        int exitCode,
        string standardOutput,
        string standardError)
    {
        var detail = !string.IsNullOrWhiteSpace(standardError)
            ? standardError.Trim()
            : standardOutput.Trim();

        return string.IsNullOrEmpty(detail)
            ? $"Command '{command}' failed with exit code {exitCode}."
            : $"Command '{command}' failed with exit code {exitCode}: {detail}";
    }
}

public sealed class ProcessRunner : IProcessRunner
{
    public async Task<ProcessRunResult> RunAsync(ProcessRunRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.FileName);
        ArgumentNullException.ThrowIfNull(request.Arguments);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkingDirectory);

        ct.ThrowIfCancellationRequested();

        var startInfo = new ProcessStartInfo
        {
            FileName = request.FileName,
            WorkingDirectory = request.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
            StandardInputEncoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        };

        foreach (var arg in request.Arguments)
        {
            startInfo.ArgumentList.Add(arg);
        }

        if (request.InheritEnvironment)
            ChildProcessEnvironment.Apply(startInfo, ChildProcessEnvironment.Capture());
        else
            startInfo.Environment.Clear();

        // Non-interactive defaults so git / jj / gh never block on terminal prompts or pagers.
        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        startInfo.Environment["GCM_INTERACTIVE"] = "never";
        startInfo.Environment["GH_PROMPT_DISABLED"] = "1";
        startInfo.Environment["NO_COLOR"] = "1";

        if (request.Environment is not null)
        {
            foreach (var (key, value) in request.Environment)
            {
                if (value is null)
                {
                    startInfo.Environment.Remove(key);
                }
                else
                {
                    startInfo.Environment[key] = value;
                }
            }
        }

        using var process = new Process { StartInfo = startInfo };

        try
        {
            if (!process.Start())
            {
                throw new VcsCommandException(
                    FormatCommand(request),
                    -1,
                    string.Empty,
                    string.Empty,
                    $"Failed to start process '{request.FileName}'.");
            }
        }
        catch (Win32Exception ex)
        {
            throw new VcsCommandException(
                FormatCommand(request),
                -1,
                string.Empty,
                ex.Message,
                $"Executable '{request.FileName}' could not be started: {ex.Message}",
                ex);
        }

        try
        {
            if (request.StandardInput is not null)
            {
                await process.StandardInput.WriteAsync(request.StandardInput.AsMemory(), ct).ConfigureAwait(false);
                await process.StandardInput.FlushAsync(ct).ConfigureAwait(false);
            }

            process.StandardInput.Close();

            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = process.StandardError.ReadToEndAsync(ct);

            await process.WaitForExitAsync(ct).ConfigureAwait(false);

            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);

            return new ProcessRunResult(process.ExitCode, stdout, stderr);
        }
        catch (OperationCanceledException)
        {
            TryTerminateProcess(process);
            throw;
        }
    }

    internal static string FormatCommand(ProcessRunRequest request)
    {
        return request.Arguments.Count == 0
            ? request.FileName
            : $"{request.FileName} {string.Join(' ', request.Arguments)}";
    }

    private static void TryTerminateProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Ignore race if process exited concurrently during cancellation.
        }
    }
}
