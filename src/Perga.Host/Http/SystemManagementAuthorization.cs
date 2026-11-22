using System.Security.Cryptography;
using System.Text;
using Perga.Host.Configuration;

namespace Perga.Host.Http;

/// <summary>Same-user capability, shared with perga-ctl through an owner-only runtime file.</summary>
public sealed class SystemManagementAuthorization
{
    private readonly byte[] _digest;
    private readonly byte[]? _configuredDigest;
    public string TokenFilePath { get; }

    public SystemManagementAuthorization(PergaHostOptions options, ResolvedHostStoragePaths paths)
    {
        var runtime = Path.Combine(options.InstallationRoot ?? paths.DataDir, "runtime");
        Directory.CreateDirectory(runtime);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(runtime, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        TokenFilePath = Path.Combine(runtime, "system-management-token");
        if (!File.Exists(TokenFilePath))
        {
            var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
            try
            {
                var createOptions = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
                if (!OperatingSystem.IsWindows()) createOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                using var stream = new FileStream(TokenFilePath, createOptions);
                stream.Write(Encoding.ASCII.GetBytes(token));
                stream.Flush(flushToDisk: true);
            }
            catch (IOException) when (File.Exists(TokenFilePath)) { }
        }
        if (new FileInfo(TokenFilePath).LinkTarget is not null || (!OperatingSystem.IsWindows() &&
            (File.GetUnixFileMode(TokenFilePath) & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0))
            throw new InvalidOperationException("System management token must be an owner-only regular file.");
        if ((File.GetAttributes(TokenFilePath) & (FileAttributes.ReparsePoint | FileAttributes.Directory | FileAttributes.Device)) != 0)
            throw new InvalidOperationException("Input must be a regular file.");
        var current = File.ReadAllText(TokenFilePath).Trim();
        if (current.Length != 64 || !current.All(char.IsAsciiHexDigit))
        {
            throw new InvalidOperationException("Invalid system management token file.");
        }
        _digest = SHA256.HashData(Encoding.ASCII.GetBytes(current));
        if (options.SystemManagementTokenSha256 is { Length: 64 } verifier && verifier.All(char.IsAsciiHexDigit))
            _configuredDigest = Convert.FromHexString(verifier);
    }

    internal bool VerifyDirectLoopbackSystemRequest(HttpContext context)
    {
        var request = context.Request;
        return request.Path.StartsWithSegments("/api/v1/system", out var subpath) && subpath.HasValue &&
            context.Features.Get<RawTransport>() is { } raw && raw.IsDirectLoopback(context) &&
            RawTransport.IsLoopbackHost(request.Host.Host) && !request.Headers.ContainsKey("Origin") &&
            !request.Headers.Keys.Any(name => name.StartsWith("Sec-Fetch-", StringComparison.OrdinalIgnoreCase)) &&
            Verify(context);
    }

    public bool Verify(HttpContext context)
    {
        if (context.Request.Headers.ContainsKey("Origin") || context.Request.Headers.ContainsKey("Sec-Fetch-Site"))
        {
            if (!RequestOriginGuard.TryOrigin(context.Request.Headers.Origin.ToString(), out var origin) ||
                !RequestOriginGuard.TryOrigin($"{context.Request.Scheme}://{context.Request.Host}", out var ownOrigin) ||
                origin != ownOrigin || context.Request.Headers["Sec-Fetch-Site"] == "cross-site") return false;
        }

        var raw = context.Features.Get<RawTransport>();
        var local = RawTransport.IsLoopback(raw?.RemoteIp ?? context.Connection.RemoteIpAddress) &&
            RawTransport.IsLoopbackHost(context.Request.Host.Host);
        if (!context.Request.IsHttps && (!local || raw?.HasForwardedHeaders == true))
            return false;
        var auth = context.Request.Headers.Authorization.ToString();
        if (!auth.StartsWith("Bearer ", StringComparison.Ordinal))
        {
            return false;
        }
        var actual = SHA256.HashData(Encoding.UTF8.GetBytes(auth[7..]));
        return CryptographicOperations.FixedTimeEquals(_digest, actual) ||
            (_configuredDigest is not null && CryptographicOperations.FixedTimeEquals(_configuredDigest, actual));
    }
}
