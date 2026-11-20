using System.Text.Json;
using Perga.AgentKit.Internal;

namespace Perga.AgentKit;

public sealed partial class AcpAgentSession
{
    private string? _advertisedEffortResetValue;
    private AcpSessionOptions? _advertisedOptions;

    private bool UsesAdvertisedModelOptions => _advertisedOptions is not null;

    private static string? ResolveAdvertisedEffortResetValue(string provider, JsonElement initialized)
    {
        // ACP advertises selectable values but has no standard marker for a default effort.
        // Only interpret this token for the OpenCode protocol version that defines it.
        return string.Equals(provider, "acp:opencode", StringComparison.OrdinalIgnoreCase) &&
            initialized.ValueKind == JsonValueKind.Object && initialized.TryGetProperty("agentInfo", out var agent) &&
            string.Equals(TryGetString(agent, "name"), "OpenCode", StringComparison.OrdinalIgnoreCase) &&
            TryGetString(agent, "version")?.StartsWith("2.", StringComparison.Ordinal) == true
            ? "default" : null;
    }

    private void RefreshAdvertisedModelOptions(JsonElement response)
    {
        if (ReadAdvertisedModelOptions(response) is { } options)
            Volatile.Write(ref _advertisedOptions, options);
    }

    private static AcpSessionOptions? ReadAdvertisedModelOptions(JsonElement response)
    {
        if (AcpSessionOptions.Read(response) is not { Model: not null } options)
            return null;

        // A malformed thought-level option is different from a model that has no such option.
        if (options.Reasoning is null && response.GetProperty("configOptions").EnumerateArray()
            .Any(option => TryGetString(option, "category") == "thought_level"))
            return null;
        return options;
    }

    private bool MatchesConfirmedModelOptions()
    {
        var observed = Volatile.Read(ref _advertisedOptions)!;
        return ConfigurationState is { } confirmed && observed.Model!.Current == confirmed.NativeModel &&
            observed.Reasoning?.Current == confirmed.NativeEffort;
    }

    private async Task<bool> ApplyAdvertisedModelOptionsAsync(
        string model, string? effort, bool explicitModel, CancellationToken ct)
    {
        var observed = Volatile.Read(ref _advertisedOptions)!;
        var changingModel = observed.Model!.Current != model;
        if (changingModel)
        {
            var response = await SetAdvertisedModelOptionAsync(observed.Model, model, ct).ConfigureAwait(false);
            if (response?.Model?.Current != model)
                return false;
            observed = response;
        }

        var reasoning = observed.Reasoning;
        var resetting = effort is null && _hasExplicitThoughtLevel && !changingModel;
        // Keep the reset requirement across a failed effort write.
        _hasExplicitThoughtLevel |= effort is not null;
        var target = effort ?? (resetting || _isResumed ? _advertisedEffortResetValue : reasoning?.Current);
        if (reasoning is null)
        {
            if (effort is not null)
                return false;
        }
        else if (target is not null && reasoning.Current != target)
        {
            var response = await SetAdvertisedModelOptionAsync(reasoning, target, ct).ConfigureAwait(false);
            if (response?.Model?.Current != model || response.Reasoning?.Id != reasoning.Id ||
                response.Reasoning.Current != target)
                return false;
            observed = response;
        }

        _modelChangeSupport = ConfigurationSupport.Supported;
        if (observed.Reasoning is not null)
            _thoughtChangeSupport = ConfigurationSupport.Supported;
        _hasExplicitModel = explicitModel;
        _hasExplicitThoughtLevel = effort is not null;
        _appliedThoughtLevel = observed.Reasoning?.Current;
        ConfirmContextBinding(model, _appliedThoughtLevel);
        ConfirmCompleteSelection();
        return true;
    }

    private async Task<AcpSessionOptions?> SetAdvertisedModelOptionAsync(AcpSessionOption option, string value, CancellationToken ct)
    {
        if (!option.Values.Any(candidate => candidate.Id == value))
            return null;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_handshakeTimeout);
        AcpSessionOptions? acknowledged = null;
        await _rpc.SendRequestAsync("session/set_config_option", new
        {
            sessionId = NativeThreadId, configId = option.Id, value
        }, timeout.Token, response =>
        {
            if (!IsPositiveAcknowledgement(response))
                return;
            _configurationHadEffect = true;
            RefreshPermissionModes(response);
            var options = ReadAdvertisedModelOptions(response);
            var changed = options?.Model?.Id == option.Id ? options.Model : options?.Reasoning;
            if (changed?.Id != option.Id || changed.Current != value)
                return;
            acknowledged = options;
            Volatile.Write(ref _advertisedOptions, options);
        }).ConfigureAwait(false);
        return acknowledged;
    }
}
