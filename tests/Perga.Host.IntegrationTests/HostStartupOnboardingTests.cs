using System.Net.Sockets;
using Microsoft.AspNetCore.Connections;
using Perga.Host.Cli;

namespace Perga.Host.IntegrationTests;

public sealed class HostStartupOnboardingTests
{
    [Fact]
    public void FormatStartupErrorHandlesPortInUseAndPermissionDenied()
    {
        var addressInUse = HostStartupOnboarding.FormatStartupError(
            new IOException("Failed to bind", new AddressInUseException("port busy")));
        Assert.NotNull(addressInUse);
        Assert.Contains("error: Could not start Perga because the address/port is already in use.", addressInUse);
        Assert.Contains("perga --urls http://127.0.0.1:4042", addressInUse);

        var socketInUse = HostStartupOnboarding.FormatStartupError(
            new IOException("Failed to bind", new SocketException((int)SocketError.AddressAlreadyInUse)));
        Assert.Equal(addressInUse, socketInUse);

        var permission = HostStartupOnboarding.FormatStartupError(new UnauthorizedAccessException("Access denied"));
        Assert.NotNull(permission);
        Assert.Contains("insufficient permissions", permission);
        Assert.Contains("perga --urls http://127.0.0.1:4041", permission);

        var socketAccess = HostStartupOnboarding.FormatStartupError(
            new SocketException((int)SocketError.AccessDenied));
        Assert.NotNull(socketAccess);
        Assert.Contains("insufficient permissions", socketAccess);

        Assert.Null(HostStartupOnboarding.FormatStartupError(new InvalidOperationException("foreign_key_check failed")));
        Assert.Null(HostStartupOnboarding.FormatStartupError(new ArgumentOutOfRangeException("unrelated")));
    }

    [Fact]
    public void FormatConfigurationErrorHandlesBindingAndValidationFailures()
    {
        foreach (var exception in new Exception[]
                 {
                     new FormatException("Invalid integer value"),
                     new InvalidOperationException("Invalid auth policy"),
                     new ArgumentException("Invalid auth enum"),
                     new OverflowException("Integer value out of range")
                 })
        {
            var message = HostStartupOnboarding.FormatConfigurationError(exception);
            Assert.StartsWith("error: Invalid Perga configuration:", message);
            Assert.Contains(exception.Message, message);
        }
    }
}
