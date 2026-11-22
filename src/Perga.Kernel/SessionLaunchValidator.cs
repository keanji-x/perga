using Perga.AgentKit;

namespace Perga.Kernel;

internal static class SessionLaunchValidator
{
    internal static string? NormalizeTaskProviderId(string? provider)
    {
        if (string.IsNullOrWhiteSpace(provider)) return null;
        var trimmed = provider.Trim();
        var normalized = trimmed.ToLowerInvariant();
        return normalized switch
        {
            "antigravity" or "opencode" => $"acp:{normalized}",
            "codex" or "claude" => normalized,
            _ when normalized.StartsWith("acp:", StringComparison.Ordinal) => normalized,
            _ => trimmed
        };
    }

    internal static string NormalizeProvider(string provider) =>
        AgentRuntime.NormalizeProviderName(provider).ToLowerInvariant();

    internal static string? NormalizeRequestedModel(string? model)
    {
        var trimmed = model?.Trim();
        if (trimmed is { Length: 0 }) throw KernelException.InvalidArgument("Model must not be blank.");
        return trimmed;
    }

    internal static string? DefaultModel(ProviderStatus? status) =>
        status?.ModelCatalog?.FirstOrDefault(model => model.IsDefault)?.Model ?? status?.Models.FirstOrDefault();

    internal static ProviderStatus? FindProvider(IReadOnlyList<ProviderStatus> providers, string provider) =>
        providers.FirstOrDefault(status => NormalizeProvider(status.Provider) == provider);

    internal static KernelException UnknownProvider(string provider, IReadOnlyList<ProviderStatus> providers) =>
        KernelException.InvalidArgument(
            $"Unknown provider '{provider}'. Available providers: {AvailableProviders(providers)}.",
            "Call provider_list to inspect available providers.");

    internal static ProviderStatus? ValidateProvider(IReadOnlyList<ProviderStatus> providers, string provider,
        bool explicitlySelected, bool requireAvailable)
    {
        var status = FindProvider(providers, provider);
        if (explicitlySelected && (provider.Length == 0 || status is null && providers.Count > 0))
            throw UnknownProvider(provider, providers);
        if (status is { Available: false } && requireAvailable)
            throw KernelException.PreconditionFailed(
                $"Provider '{provider}' is unavailable: {status.Reason ?? "No diagnostic was reported"}. Available providers: {AvailableProviders(providers)}.",
                "Choose an available provider or call provider_list(refresh=true) to retry discovery.");
        return status;
    }

    internal static void ValidateModelAndEffort(string provider, ProviderStatus? status,
        string? requestedModel, string? effectiveModel, string? requestedEffort)
    {
        var nextStep = $"Call provider_list(provider='{provider}') to inspect valid models and reasoning efforts.";
        if (requestedModel is not null && status is { Models.Count: > 0 } &&
            !status.Models.Contains(requestedModel, StringComparer.Ordinal))
            throw KernelException.InvalidArgument(
                $"Unsupported model '{requestedModel}' for provider '{provider}'. Supported models: {FormatChoices(status.Models)}.",
                nextStep);

        if (requestedEffort is null) return;
        var model = effectiveModel ?? DefaultModel(status);
        var efforts = status?.ModelCatalog?.FirstOrDefault(entry => entry.Model == model)?.SupportedReasoningEfforts;
        if (efforts is { Count: > 0 } && !efforts.Any(entry => entry.ReasoningEffort == requestedEffort))
            throw KernelException.InvalidArgument(
                $"Unsupported reasoningEffort '{requestedEffort}' for model '{model}' on provider '{provider}'. Supported efforts: {FormatChoices(efforts.Select(entry => entry.ReasoningEffort).ToArray())}.",
                nextStep);
    }

    private static string AvailableProviders(IReadOnlyList<ProviderStatus> providers) =>
        FormatChoices(providers.Where(status => status.Available).Select(status => NormalizeProvider(status.Provider)).ToArray());

    private static string FormatChoices(IReadOnlyList<string> choices)
    {
        const int limit = 20;
        if (choices.Count == 0) return "(none)";
        var shown = string.Join(", ", choices.Take(limit));
        return choices.Count > limit ? $"{shown} (+{choices.Count - limit} more)" : shown;
    }
}
