namespace Perga.AgentKit;

/// <summary>A failed open still owns a provider handle whose shutdown has not been confirmed.</summary>
public sealed class ProviderOpenShutdownUnconfirmedException(IAgentSession session, Exception inner)
    : Exception("Provider shutdown could not be confirmed after opening failed.", inner)
{
    public IAgentSession Session { get; } = session;
}
