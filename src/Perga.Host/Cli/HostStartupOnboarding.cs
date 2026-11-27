using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.AspNetCore.Connections;

namespace Perga.Host.Cli;

/// <summary>Interactive terminal detection and friendly startup error formatting for <c>perga</c>.</summary>
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
