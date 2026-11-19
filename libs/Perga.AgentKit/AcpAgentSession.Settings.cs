using System.Text.Json;
using Perga.AgentKit.Internal;

namespace Perga.AgentKit;

public sealed partial class AcpAgentSession
{
    private SessionRuntimeSettings _appliedSettings = new(null, null, null);
    private AcpPermissionModes _permissionModes = new([], null);
    private IReadOnlyList<PermissionModeOption> _extraPermissionModes = [];
    private IReadOnlySet<string> _launchArgumentModes = new HashSet<string>(StringComparer.Ordinal);

    private void RefreshPermissionModes(JsonElement response)
    {
        // Empty success responses do not advertise a replacement catalogue.
        if (response.ValueKind != JsonValueKind.Object ||
            (!response.TryGetProperty("configOptions", out _) && !response.TryGetProperty("modes", out _)))
            return;

        var modes = AcpPermissionModes.Read(response);
        var merged = modes.Options.ToList();
        foreach (var extra in _extraPermissionModes)
            if (!merged.Any(option => string.Equals(option.Id, extra.Id, StringComparison.Ordinal)))
                merged.Add(extra);

        // Publish options and their transport together; readers retain one immutable snapshot.
        Volatile.Write(ref _permissionModes, new(Array.AsReadOnly(merged.ToArray()), modes.ConfigId));
    }

    public async Task<bool> TryReconcileSettingsAsync(SessionRuntimeSettings settings, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var permissionModes = Volatile.Read(ref _permissionModes);
        ValidatePermissionMode(settings.PermissionMode, permissionModes);
        if (settings.PermissionMode != _appliedSettings.PermissionMode)
        {
            if (settings.PermissionMode is null ||
                _launchArgumentModes.Contains(settings.PermissionMode) ||
                (_appliedSettings.PermissionMode is { } current && _launchArgumentModes.Contains(current)))
                return false;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(_handshakeTimeout);
            try
            {
                await SetPermissionModeAsync(settings.PermissionMode, NativeThreadId!, permissionModes, timeout.Token).ConfigureAwait(false);
            }
            catch (JsonRpcRemoteException ex) when (ex.Code == -32601) { return false; }
            _appliedSettings = _appliedSettings with { PermissionMode = settings.PermissionMode };
        }
        if ((settings.Model != _appliedSettings.Model || settings.ReasoningEffort != _appliedSettings.ReasoningEffort) &&
            !await TrySetModelAsync(settings.Model, settings.ReasoningEffort, ct).ConfigureAwait(false))
            return false;
        _appliedSettings = settings;
        return true;
    }

    private static void ValidatePermissionMode(string? mode, AcpPermissionModes modes)
    {
        if (mode is not null && !modes.Options.Any(option => option.Id == mode))
            throw new ArgumentException($"Unsupported ACP permission mode '{mode}'.", nameof(mode));
    }

    private Task<JsonElement> SetPermissionModeAsync(string mode, string sessionId, AcpPermissionModes modes, CancellationToken ct)
    {
        var parameters = new Dictionary<string, object?> { ["sessionId"] = sessionId };
        if (modes.ConfigId is { } configId)
        {
            parameters["configId"] = configId;
            parameters["value"] = mode;
        }
        else parameters["modeId"] = mode;
        return _rpc.SendRequestAsync(modes.ConfigId is null ? "session/set_mode" : "session/set_config_option", parameters, ct, RefreshPermissionModes);
    }

    public async Task<bool> TrySetModelAsync(string? model, string? reasoningEffort = null, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var hasModel = !string.IsNullOrWhiteSpace(model);
        var effort = string.IsNullOrWhiteSpace(reasoningEffort) ? null : reasoningEffort.Trim();
        if (!hasModel && effort is null) return false;

        // A blank model keeps the group advertised as the session's initial default.
        var requestedModel = hasModel ? model!.Trim() : _defaultModel;
        var entry = _modelCatalog.Models.FirstOrDefault(row => row.Model == requestedModel);
        var reasoningOption = _modelCatalog.ThoughtLevel;
        var useThoughtLevel = _modelCatalog.UsesThoughtLevel(requestedModel);
        if (!hasModel && !useThoughtLevel &&
            entry?.SupportedReasoningEfforts.Any(value => string.Equals(value.ReasoningEffort, effort, StringComparison.OrdinalIgnoreCase)) != true)
            return false;
        if (useThoughtLevel && effort is not null && !reasoningOption!.Values.Any(value => value.Id == effort))
            return false;

        var thoughtLevel = useThoughtLevel ? effort ?? _defaultThoughtLevel : null;
        // A loaded effort is not evidence of the provider's initial default to restore.
        if (useThoughtLevel && thoughtLevel is null && _hasExplicitThoughtLevel) return false;

        if (hasModel || !useThoughtLevel || _hasExplicitModel)
        {
            if (requestedModel is null || !await TrySetAcpModelAsync(requestedModel, reasoningEffort, ct).ConfigureAwait(false))
                return false;
            _hasExplicitModel = hasModel;
        }

        if (thoughtLevel is not null && thoughtLevel != _appliedThoughtLevel)
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(_handshakeTimeout);
            try
            {
                await _rpc.SendRequestAsync("session/set_config_option", new
                {
                    sessionId = NativeThreadId,
                    configId = reasoningOption!.Id,
                    value = thoughtLevel
                }, timeoutCts.Token, response =>
                {
                    RefreshPermissionModes(response);
                    _appliedThoughtLevel = thoughtLevel;
                    _hasExplicitThoughtLevel = effort is not null;
                    ConfirmContextBinding(requestedModel is null ? null : _modelCatalog.Resolve(requestedModel, reasoningEffort), thoughtLevel);
                }).ConfigureAwait(false);
            }
            catch (JsonRpcRemoteException error) when (error.Code == -32601)
            {
                return false;
            }
        }
        else if (thoughtLevel is not null)
        {
            // An explicit selection still counts when it matches the loaded value.
            _hasExplicitThoughtLevel = effort is not null;
        }
        return true;
    }

    private async Task<bool> TrySetAcpModelAsync(string requestedModel, string? reasoningEffort, CancellationToken ct)
    {
        var resolvedModelId = _modelCatalog.Resolve(requestedModel, reasoningEffort);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_handshakeTimeout);

        try
        {
            var result = await _rpc.SendRequestAsync("session/set_model", new
            {
                sessionId = NativeThreadId,
                modelId = resolvedModelId
            }, timeoutCts.Token, res => ConfirmModelResult(res, resolvedModelId)).ConfigureAwait(false);
            return ConfirmsModel(result, resolvedModelId);
        }
        catch (JsonRpcRemoteException ex) when (ex.Code == -32601)
        {
            try
            {
                var result = await _rpc.SendRequestAsync("session/set_config_option", new
                {
                    sessionId = NativeThreadId,
                    configId = "model",
                    value = resolvedModelId
                }, timeoutCts.Token, res => ConfirmModelResult(res, resolvedModelId)).ConfigureAwait(false);
                return ConfirmsModel(result, resolvedModelId);
            }
            catch (JsonRpcRemoteException configEx) when (configEx.Code == -32601)
            {
                return false;
            }
        }
    }

    private void ConfirmModelResult(JsonElement result, string modelId)
    {
        RefreshPermissionModes(result);
        if (ConfirmsModel(result, modelId))
        {
            // Providers may reset thought_level on a model change. Missing options mean unknown state.
            _appliedThoughtLevel = AcpSessionOptions.Read(result)?.Reasoning?.Current;
            ConfirmContextBinding(modelId, _appliedThoughtLevel);
        }
    }

    private static bool ConfirmsModel(JsonElement result, string modelId)
    {
        if (result.ValueKind != JsonValueKind.Object || TryGetBool(result, "success") == false)
            return false;
        var (_, currentModelId) = ExtractAvailableModels(result);
        return currentModelId is null || string.Equals(currentModelId, modelId, StringComparison.Ordinal);
    }

}
