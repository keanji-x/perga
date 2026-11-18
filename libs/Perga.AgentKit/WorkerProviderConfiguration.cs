namespace Perga.AgentKit;

public sealed record WorkerModelOption(
    string Id, string Name, IReadOnlyList<string> ReasoningEfforts, string? DefaultReasoningEffort = null);

/// <summary>The choices and proven defaults reported by one provider session.</summary>
public sealed record WorkerProviderConfiguration(
    IReadOnlyList<WorkerModelOption> Models,
    string? DefaultModel = null,
    string? DefaultReasoningEffort = null,
    IReadOnlyList<string>? SessionReasoningEfforts = null)
{
    public IReadOnlyList<string> GetReasoningEfforts(string? model) =>
        SessionReasoningEfforts ?? Models.FirstOrDefault(option => option.Id == (model ?? DefaultModel))?.ReasoningEfforts ?? [];

    public string? GetDefaultReasoningEffort(string? model) =>
        model is null || model == DefaultModel || Models.Count == 0 ? DefaultReasoningEffort
            : Models.FirstOrDefault(option => option.Id == model)?.DefaultReasoningEffort;
}
