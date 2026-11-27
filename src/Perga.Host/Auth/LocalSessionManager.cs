using System.Security.Cryptography;
using System.Text;
using Perga.Contracts;
using Perga.Host.Configuration;
using Perga.Host.Http;
using Perga.Host.WebSockets;
using Perga.Kernel.Ports;

namespace Perga.Host.Auth;

internal sealed class LocalSessionManager(
    PergaHostOptions options, ResolvedHostStoragePaths paths, ILogger<LocalSessionManager> logger) : IDisposable
{
    private const int MaxSessions = 16;
    private const UnixFileMode TokenMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode RuntimeMode = TokenMode | UnixFileMode.UserExecute;
    private readonly object _gate = new();
    private readonly OrderedDictionary<string, SessionIdentityDto> _sessions = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _ownerSetup = new(1, 1);
    private readonly string _tokenPath = Path.Combine(paths.DataDir, "runtime", "local-launch-token");
    private volatile byte[]? _launchDigest;

    public bool IsActive => _launchDigest is not null;

    public Task BeginOwnerSetupAsync(CancellationToken ct) => _ownerSetup.WaitAsync(ct);
    public void EndOwnerSetup() => _ownerSetup.Release();

    public static bool IsDirectLoopback(HttpContext context, PergaHostOptions options) =>
        options.AuthIngressMode == AuthIngressMode.Direct &&
        context.Features.Get<RawTransport>() is { } raw && raw.IsDirectLoopback(context) &&
        RawTransport.IsLoopbackHost(context.Request.Host.Host);

    public string Start()
    {
        var directory = Path.GetDirectoryName(_tokenPath)!;
        var runtime = new DirectoryInfo(directory);
        if (runtime.LinkTarget is not null)
            throw new InvalidOperationException("The local launch token requires a regular runtime directory.");
        if (OperatingSystem.IsWindows())
            Directory.CreateDirectory(directory);
        else
        {
            Directory.CreateDirectory(directory, RuntimeMode);
            File.SetUnixFileMode(directory, RuntimeMode);
        }

        var previous = new FileInfo(_tokenPath);
        if (previous.LinkTarget is not null || (previous.Exists && !IsPrivateTokenFile(previous)) || Directory.Exists(_tokenPath))
            throw new InvalidOperationException("The local launch token must be an owner-only regular file.");

        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var temporary = $"{_tokenPath}.{Guid.NewGuid():N}";
        try
        {
            var create = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) create.UnixCreateMode = TokenMode;
            using (var stream = new FileStream(temporary, create))
            {
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(stream.SafeFileHandle, TokenMode);
                stream.Write(Encoding.ASCII.GetBytes(token));
            }
            File.Move(temporary, _tokenPath, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }

        _launchDigest = Digest(token);
        return token;
    }

    public bool TryExchangeLaunchToken(string? token, DateTimeOffset now, AuthWebSocketRegistry sockets,
        out string cookie, out SessionIdentityDto identity)
    {
        cookie = string.Empty;
        identity = null!;
        if (token is not { Length: 64 } || !token.All(char.IsAsciiHexDigit))
            return false;

        var digest = Digest(token);
        lock (_gate)
        {
            if (_launchDigest is null || !CryptographicOperations.FixedTimeEquals(_launchDigest, digest))
                return false;

            var nowMs = now.ToUnixTimeMilliseconds();
            for (var i = _sessions.Count - 1; i >= 0; i--)
            {
                if (_sessions.GetAt(i).Value.ExpiresAtMs <= nowMs)
                    RemoveAt(i, sockets);
            }
            if (_sessions.Count >= MaxSessions)
                RemoveAt(0, sockets);

            var (value, tokenHash) = WebSessionCookie.Create();
            identity = new($"loc_{Guid.NewGuid():N}", "local-owner", "local", "Local", "owner",
                now.AddDays(options.AuthSessionLifetimeDays).ToUnixTimeMilliseconds(), AuthSessionMode.Local);
            _sessions[tokenHash] = identity;
            cookie = value;
            return true;
        }
    }

    private void RemoveAt(int index, AuthWebSocketRegistry sockets)
    {
        var session = _sessions.GetAt(index).Value;
        _sessions.RemoveAt(index);
        sockets.DisconnectSession(session.SessionId);
    }

    public SessionIdentityDto? TryGetValidSession(string tokenHash, long nowMs)
    {
        lock (_gate)
        {
            if (!_sessions.TryGetValue(tokenHash, out var session) || session.ExpiresAtMs <= nowMs)
                return null;

            _sessions.Remove(tokenHash);
            _sessions[tokenHash] = session;
            return session;
        }
    }

    public bool IsValidSessionId(string sessionId, long nowMs)
    {
        lock (_gate)
            return _sessions.Values.Any(session => session.SessionId == sessionId && session.ExpiresAtMs > nowMs);
    }

    public void Revoke(string tokenHash, AuthWebSocketRegistry sockets)
    {
        lock (_gate)
            if (_sessions.Remove(tokenHash, out var session))
                sockets.DisconnectSession(session.SessionId);
    }

    public async Task<bool> CheckOwnerAsync(IAuthStore store, AuthWebSocketRegistry sockets, CancellationToken ct)
    {
        if (await store.GetOwnerAsync(ct) is null)
            return false;

        if (IsActive)
        {
            // A Web setup can commit before its Owner cookie reaches the browser.
            await _ownerSetup.WaitAsync(ct);
            try { DisableAndRevokeAll(sockets); }
            finally { _ownerSetup.Release(); }
        }
        return true;
    }

    public void DisableAndRevokeAll(AuthWebSocketRegistry sockets) => Disable(sockets);

    public void Dispose() => Disable(null);

    private void Disable(AuthWebSocketRegistry? sockets)
    {
        lock (_gate)
        {
            if (_launchDigest is not { } digest)
                return;
            _launchDigest = null;
            _sessions.Clear();
            RemoveTokenFile(digest);
            sockets?.DisconnectLocalSessions();
        }
    }

    public void ClearStaleTokenFile() => RemoveTokenFile(null);

    private void RemoveTokenFile(byte[]? digest)
    {
        try
        {
            var file = new FileInfo(_tokenPath);
            if (file.Directory!.LinkTarget is null && IsPrivateTokenFile(file) &&
                (digest is null || CryptographicOperations.FixedTimeEquals(digest, Digest(File.ReadAllText(_tokenPath)))))
                File.Delete(_tokenPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not remove the local launch token file");
        }
    }

    private static bool IsPrivateTokenFile(FileInfo file) =>
        file.Exists && file.LinkTarget is null && file.Length == 64 &&
        (file.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory | FileAttributes.Device)) == 0 &&
        (OperatingSystem.IsWindows() || File.GetUnixFileMode(file.FullName) == TokenMode);

    private static byte[] Digest(string value) => SHA256.HashData(Encoding.ASCII.GetBytes(value));
}
