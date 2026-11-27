namespace Perga.Contracts;

public enum AuthSessionMode { Owner, Local }
public enum AuthSetupMode { Configured, SetupRequired, LocalTokenRequired }
public enum AuthSetupBlockedReason { NonLoopback, ExistingData }

public sealed record SessionIdentityDto(
    string SessionId, string UserId, string Username, string DisplayName, string Role, long ExpiresAtMs,
    AuthSessionMode Mode = AuthSessionMode.Owner);

public sealed record AuthSetupStatusDto(
    AuthSetupMode Mode, bool NeedsOwnerSetup, bool CanSetupInBrowser,
    AuthSetupBlockedReason? SetupBlockedReason = null, string? NextStep = null);

public sealed record SystemAuthStatusDto(
    int SchemaVersion, bool Configured, string? Username, string? DisplayName, bool Disabled, long ActiveSessions);
