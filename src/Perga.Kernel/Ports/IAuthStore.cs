using Perga.Contracts;

namespace Perga.Kernel.Ports;

public sealed record AuthUserRecord(
    string Id, string Username, string DisplayName, string Role, string PasswordHash,
    long CredentialVersion, long SessionGeneration, long? DisabledAtMs);

public sealed record NewAuthSession(
    string Id, string TokenHash, long CreatedAtMs, long ExpiresAtMs, string? UserAgent, string? CreatedIp);

public enum AuthSetupState { NeedsWebSetup, ExistingDataRequiresCli, Configured }
public enum InitialOwnerSetupOutcome { Created, AlreadyConfigured, ExistingDataRequiresCli }

public sealed record NewInitialOwner(string Username, string DisplayName, string PasswordHash);

public sealed record InitialOwnerSetupResult(InitialOwnerSetupOutcome Outcome, SessionIdentityDto? Identity = null);

public interface IAuthStore
{
    Task<AuthUserRecord?> GetOwnerAsync(CancellationToken ct);
    Task<AuthSetupState> GetSetupStateAsync(CancellationToken ct);
    Task<InitialOwnerSetupResult> TryCreateInitialOwnerWithSessionAsync(
        NewInitialOwner owner, NewAuthSession session, bool allowExistingBusinessData, CancellationToken ct);
    Task<SessionIdentityDto?> TryCreateSessionIfVersionMatchesAsync(AuthUserRecord owner, NewAuthSession session, CancellationToken ct);
    Task<bool> UpgradePasswordHashIfVersionMatchesAsync(AuthUserRecord owner, string hash, long nowMs, CancellationToken ct);
    Task<SessionIdentityDto?> GetValidOwnerSessionByTokenHashAsync(string tokenHash, long nowMs, CancellationToken ct);
    Task<bool> IsOwnerSessionValidAsync(string id, long nowMs, CancellationToken ct);
    Task TouchSessionLastSeenIfStaleAsync(string id, long nowMs, CancellationToken ct);
    Task RevokeSessionByTokenHashAsync(string tokenHash, long nowMs, CancellationToken ct);
    Task<SystemAuthStatusDto> GetAuthStatusAsync(long nowMs, CancellationToken ct);
}

public interface IAuthOwnerWriter
{
    Task<bool> CreateInitialOwnerAsync(string username, string displayName, string passwordHash, long nowMs, CancellationToken ct);
    Task<bool> RotateOwnerPasswordAsync(string passwordHash, long nowMs, CancellationToken ct);
    Task<bool> RenameOwnerAsync(string username, string? displayName, long nowMs, CancellationToken ct);
    Task<bool> SetOwnerDisabledAsync(bool disabled, long nowMs, CancellationToken ct);
    Task<bool> RevokeAllSessionsAsync(long nowMs, CancellationToken ct);
}
