namespace Perga.AgentKit;

// A configuration is a complete selection. Default is an instruction, not an observed value.
public abstract record SessionSelection
{
    private SessionSelection() { }
    public sealed record ProviderDefault : SessionSelection;
    public sealed record Explicit : SessionSelection
    {
        public Explicit(string value)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(value);
            Value = value.Trim();
        }
        public string Value { get; }
    }
    public static SessionSelection FromNullable(string? value) =>
        string.IsNullOrWhiteSpace(value) ? new ProviderDefault() : new Explicit(value.Trim());
    public string? ValueOrNull => this is Explicit value ? value.Value : null;
}

public sealed record SessionConfiguration(SessionSelection Model, SessionSelection Effort, SessionSelection Permission)
{
    public static SessionConfiguration FromNullable(string? model, string? effort = null, string? permission = null) =>
        new(SessionSelection.FromNullable(model), SessionSelection.FromNullable(effort), SessionSelection.FromNullable(permission));
    public static SessionConfiguration FromLaunch(HarnessLaunchSpec spec) => FromNullable(spec.Model, spec.ReasoningEffort, spec.PermissionMode);
}

public enum ConfigurationSupport { Unknown, Unsupported, Supported }
public sealed record ConfigurationDimensionCapabilities(
    ConfigurationSupport Open,
    ConfigurationSupport LiveChange,
    ConfigurationSupport ResumeOverride,
    ConfigurationSupport DefaultReset,
    IReadOnlyList<string>? AdvertisedValues = null,
    string? NegotiatedDefault = null);

public sealed record SessionConfigurationCapabilities(
    ConfigurationDimensionCapabilities Model,
    ConfigurationDimensionCapabilities Effort,
    ConfigurationDimensionCapabilities Permission)
{
    public static ConfigurationDimensionCapabilities LaunchDimension(ConfigurationSupport support = ConfigurationSupport.Supported) =>
        new(support, ConfigurationSupport.Unsupported, support, support);
    public static SessionConfigurationCapabilities Unknown { get; } = new(
        new(ConfigurationSupport.Unknown, ConfigurationSupport.Unknown, ConfigurationSupport.Unknown, ConfigurationSupport.Unknown),
        new(ConfigurationSupport.Unknown, ConfigurationSupport.Unknown, ConfigurationSupport.Unknown, ConfigurationSupport.Unknown),
        new(ConfigurationSupport.Unknown, ConfigurationSupport.Unknown, ConfigurationSupport.Unknown, ConfigurationSupport.Unknown));
    public static SessionConfigurationCapabilities LaunchOnly { get; } = new(LaunchDimension(), LaunchDimension(), LaunchDimension());
}

public enum ConfigurationEvidence { LaunchAccepted, ProviderAcknowledged }
public sealed record ConfirmedConfiguration(
    SessionConfiguration Selection,
    string? NativeModel = null,
    string? NativeEffort = null,
    long? ContextUsageRevision = null,
    ConfigurationEvidence Evidence = ConfigurationEvidence.ProviderAcknowledged);
public sealed record ObservedConfiguration(string? NativeModel, string? NativeEffort, bool IsUsable);
public enum ConfigurationDimension { Model, Effort, Permission }
public enum ConfigurationFailureCode { InvalidSelection, InvalidAcknowledgement, OperationFailed, UnconfirmedLaunch }

public abstract record SessionConfigurationOutcome
{
    private SessionConfigurationOutcome() { }
    public sealed record Applied(ConfirmedConfiguration Configuration) : SessionConfigurationOutcome;
    public sealed record RequiresReopen(string Reason) : SessionConfigurationOutcome;
    public sealed record Unsupported(ConfigurationDimension Dimension, string Reason) : SessionConfigurationOutcome;
    public sealed record Rejected(ConfigurationFailureCode Code, string Reason, ObservedConfiguration ObservedState) : SessionConfigurationOutcome;
}

public sealed class SessionConfigurationException(SessionConfigurationOutcome outcome)
    : InvalidOperationException($"Session configuration failed: {outcome}")
{
    public SessionConfigurationOutcome Outcome { get; } = outcome;
}
