namespace Perga.Contracts;

public sealed record SessionIdentityDto(
    string SessionId, string UserId, string Username, string DisplayName, string Role, long ExpiresAtMs);

public sealed record SystemAuthStatusDto(
    int SchemaVersion, bool Configured, string? Username, string? DisplayName, bool Disabled, long ActiveSessions);
