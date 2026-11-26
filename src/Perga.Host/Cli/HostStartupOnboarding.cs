using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.AspNetCore.Connections;
using Perga.Host.Auth;
using Perga.Kernel.Ports;
using Perga.Storage.Sqlite;

namespace Perga.Host.Cli;

/// <summary>First-run interactive owner onboarding and friendly startup error formatting for <c>perga</c>.</summary>
public static class HostStartupOnboarding
{
    public const string InitOwnerCommand = "perga auth init-owner --username owner --allow-migrate";

    public static bool IsForegroundInteractiveTerminal()
    {
        if (Console.IsInputRedirected || Console.IsOutputRedirected) return false;
        if (OperatingSystem.IsWindows()) return true;
        try { var foregroundGroup = tcgetpgrp(0); return foregroundGroup > 0 && foregroundGroup == getpgrp(); }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return false; }
    }

    [DllImport("libc")]
    private static extern int tcgetpgrp(int fd);

    [DllImport("libc")]
    private static extern int getpgrp();

    /// <summary>
    /// Prompts to create the initial owner account on an interactive first start against persistent storage.
    /// Returns <c>0</c> to continue server startup, or a non-zero exit code if owner creation failed.
    /// </summary>
    public static async Task<int> EnsureInteractiveOwnerIfNeededAsync(
        IAuthStore authStore,
        string dbPath,
        bool isEphemeral,
        bool isTestingEnvironment,
        bool isInteractive,
        TextReader input,
        TextWriter output,
        TextWriter error,
        Func<string[], TextReader, TextWriter, TextWriter, CancellationToken, Task<int>>? runAuthCliAsync = null,
        CancellationToken ct = default)
    {
        if (!isInteractive || isTestingEnvironment || isEphemeral)
            return 0;

        if (authStore is SqliteTrackStore sqliteStore)
            await sqliteStore.InitializeAsync(ct).ConfigureAwait(false);

        if (await authStore.GetOwnerAsync(ct).ConfigureAwait(false) is not null)
            return 0;

        await output.WriteLineAsync($"No owner account is configured yet for {dbPath}.").ConfigureAwait(false);
        await output.WriteAsync("Create one now so you can sign in? [Y/n] ").ConfigureAwait(false);
        await output.FlushAsync(ct).ConfigureAwait(false);

        var answer = await input.ReadLineAsync(ct).ConfigureAwait(false);
        var trimmed = answer?.Trim();
        var shouldCreate = answer is not null &&
                           (trimmed!.Length == 0 ||
                            trimmed.Equals("y", StringComparison.OrdinalIgnoreCase) ||
                            trimmed.Equals("yes", StringComparison.OrdinalIgnoreCase));

        if (!shouldCreate)
        {
            var pathArgument = FormatCliPathArgument(dbPath);
            await output.WriteLineAsync($"Hint: run '{InitOwnerCommand} --db-path {pathArgument}' before signing in.").ConfigureAwait(false);
            return 0;
        }

        await output.WriteAsync("Username [owner]: ").ConfigureAwait(false);
        await output.FlushAsync(ct).ConfigureAwait(false);
        var rawUsername = await input.ReadLineAsync(ct).ConfigureAwait(false);
        var username = string.IsNullOrWhiteSpace(rawUsername) ? "owner" : rawUsername.Trim();

        var runner = runAuthCliAsync ?? AuthCliCommand.RunAsync;
        return await runner(
            ["auth", "init-owner", "--username", username, "--allow-migrate", "--db-path", dbPath],
            input,
            output,
            error,
            ct).ConfigureAwait(false);
    }

    private static string FormatCliPathArgument(string path) =>
        path.Length > 0 && path.All(static c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '/' or '+' or ':' or '@' or '-')
            ? path
            : $"'{path.Replace("'", "'\\''", StringComparison.Ordinal)}'";

    public static string? FormatConfigurationError(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (FindException(exception, static ex => ex is InvalidOperationException or FormatException or ArgumentException or OverflowException)
            is not { } invalidConfig)
            return null;

        return $"""
            error: Invalid Perga configuration: {invalidConfig.Message}
            hint:  Check your PERGA_* environment variables, appsettings.json, or command-line options.
            """;
    }

    /// <summary>Formats address conflicts and permission failures during runtime startup.</summary>
    public static string? FormatStartupError(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        if (FindException(exception, static ex =>
                ex is AddressInUseException ||
                ex is SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse }) is not null)
        {
            return """
                error: Could not start Perga because the address/port is already in use.
                hint:  Is Perga already running? Choose another port with: perga --urls http://127.0.0.1:4042
                """;
        }

        if (FindException(exception, static ex =>
                ex is UnauthorizedAccessException ||
                ex is SocketException { SocketErrorCode: SocketError.AccessDenied }) is { } accessDenied)
        {
            return $"""
                error: Could not start Perga due to insufficient permissions ({accessDenied.Message}).
                hint:  Check permissions on your data directory or choose a non-privileged port (> 1024) with: perga --urls http://127.0.0.1:4041
                """;
        }

        return null;
    }

    private static Exception? FindException(Exception root, Func<Exception, bool> predicate)
    {
        var stack = new Stack<Exception>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (predicate(current))
                return current;

            if (current is AggregateException aggregate)
            {
                for (var i = aggregate.InnerExceptions.Count - 1; i >= 0; i--)
                    stack.Push(aggregate.InnerExceptions[i]);
            }
            else if (current.InnerException is not null)
            {
                stack.Push(current.InnerException);
            }
        }

        return null;
    }
}
