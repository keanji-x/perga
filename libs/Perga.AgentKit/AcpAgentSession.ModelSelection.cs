using System.Text.Json;
using Perga.AgentKit.Internal;

namespace Perga.AgentKit;

public sealed partial class AcpAgentSession
{
    private string? _defaultModel;
    private bool _hasExplicitModel;
    private ConfigurationSupport _modelChangeSupport = ConfigurationSupport.Unknown;
    private ConfigurationSupport _thoughtChangeSupport = ConfigurationSupport.Unknown;
    private bool _configurationHadEffect;
    private bool _hasExplicitSuffixEffort;
    private bool _isResumed;
    private volatile bool _configurationUncertain;
    private SessionConfiguration? _pendingConfiguration;
    private string? _defaultThoughtLevel;
    private string? _appliedThoughtLevel;
    private bool _hasExplicitThoughtLevel;
    private AcpModelCatalog _modelCatalog = AcpModelCatalog.Build([], null);


    public ConfirmedConfiguration? ConfigurationState { get; private set; }
    private SessionSelection _permission = new SessionSelection.ProviderDefault();
    public SessionConfigurationCapabilities ConfigurationCapabilities => new(
        new(_modelChangeSupport, _modelChangeSupport, _modelChangeSupport,
            _defaultModel is null ? ConfigurationSupport.Unknown : _modelChangeSupport,
            AvailableModels, _defaultModel),
        new(EffortChangeSupport, EffortChangeSupport, EffortChangeSupport,
            _defaultThoughtLevel is null ? ConfigurationSupport.Unknown : _thoughtChangeSupport,
            AvailableModelCatalog.SelectMany(model => model.SupportedReasoningEfforts.Select(effort => effort.ReasoningEffort))
                .Concat((Volatile.Read(ref _advertisedOptions)?.Reasoning ?? _modelCatalog.ThoughtLevel)?.Values.Select(value => value.Id) ?? []).Distinct().ToArray(), _defaultThoughtLevel),
        new(ConfigurationSupport.Supported, _permissionChangeSupport, ConfigurationSupport.Supported,
            ConfigurationSupport.Unsupported, AvailablePermissionModes.Select(mode => mode.Id).ToArray()));
    private ConfigurationSupport EffortChangeSupport => UsesAdvertisedModelOptions || _modelCatalog.ThoughtLevel is not null
        ? _thoughtChangeSupport : _modelCatalog.Models.Any(model => model.SupportedReasoningEfforts.Count > 0)
            ? _modelChangeSupport : ConfigurationSupport.Unsupported;

    public async Task<SessionConfigurationOutcome> ConfigureAsync(SessionConfiguration configuration, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ct.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var permissionCatalog = Volatile.Read(ref _permissionCatalog);
        var targetPermission = ValidatePermissionMode(configuration.Permission.ValueOrNull, permissionCatalog);
        var matchesConfirmedModelOptions = !UsesAdvertisedModelOptions || MatchesConfirmedModelOptions();
        if (ConfigurationState?.Selection == configuration && matchesConfirmedModelOptions)
            return new SessionConfigurationOutcome.Applied(ConfigurationState);
        if (configuration.Model is SessionSelection.ProviderDefault && _hasExplicitModel && _defaultModel is null)
            return new SessionConfigurationOutcome.Unsupported(ConfigurationDimension.Model, "The original default model is unknown.");
        var targetModel = configuration.Model.ValueOrNull ?? _defaultModel;
        var usesThought = !UsesAdvertisedModelOptions && _modelCatalog.UsesThoughtLevel(targetModel);
        if (!UsesAdvertisedModelOptions && ValidateLegacyEffort(configuration.Effort, targetModel, usesThought) is { } invalidEffort)
            return invalidEffort;
        if (configuration.Permission != _permission &&
            (targetPermission is null ||
             targetPermission.Transport is AcpPermissionTransport.LaunchArgument ||
             permissionCatalog.IsLaunchArgument(_permission.ValueOrNull)))
        {
            if (configuration.Model is SessionSelection.ProviderDefault)
                return new SessionConfigurationOutcome.Unsupported(ConfigurationDimension.Model,
                    "ACP cannot establish the original default model on a reopened native session.");
            if (usesThought && configuration.Effort is SessionSelection.ProviderDefault)
                return new SessionConfigurationOutcome.Unsupported(ConfigurationDimension.Effort,
                    "ACP cannot establish the original default effort on a reopened native session.");
            return new SessionConfigurationOutcome.RequiresReopen("Permission changes require a new process.");
        }
        var previousSelection = ConfigurationState?.Selection;
        ConfigurationState = null;
        _pendingConfiguration = configuration;
        _configurationHadEffect = false;
        // Existing model-only evidence remains valid while reasserting the same wire ID.
        var targetNativeId = UsesAdvertisedModelOptions || targetModel is null ? targetModel
            : _modelCatalog.Resolve(targetModel, configuration.Effort.ValueOrNull);
        _configurationUncertain = UsesAdvertisedModelOptions || usesThought || targetNativeId is null ||
            _confirmedConfiguration.ModelId != targetNativeId;
        lock (_idleUsageLock)
        {
            if (targetNativeId is null || _confirmedConfiguration.ModelId != targetNativeId ||
                ((UsesAdvertisedModelOptions || usesThought) && configuration.Effort.ValueOrNull != _appliedThoughtLevel))
            {
                _pendingIdleUsage = null;
                _pendingIdleUsageWithWindow = null;
            }
        }
        try
        {
            if (configuration.Permission != _permission)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(_handshakeTimeout);
                try
                {
                    var result = await SetPermissionModeAsync(targetPermission!.Option.Id, NativeThreadId!, targetPermission.Transport, timeout.Token).ConfigureAwait(false);
                    if (!IsPositiveAcknowledgement(result))
                        return RejectConfiguration(ConfigurationFailureCode.InvalidAcknowledgement, "The provider rejected the permission mode.");
                    _permission = configuration.Permission;
                    _configurationHadEffect = true;
                }
                catch (JsonRpcRemoteException error) when (error.Code == -32601)
                {
                    _permissionChangeSupport = ConfigurationSupport.Unsupported;
                    return new SessionConfigurationOutcome.RequiresReopen("Permission operation is unavailable.");
                }
            }
            if ((previousSelection?.Model != configuration.Model || previousSelection?.Effort != configuration.Effort || !matchesConfirmedModelOptions) &&
                !await ApplySelectionAsync(configuration.Model.ValueOrNull, configuration.Effort.ValueOrNull, ct).ConfigureAwait(false))
            {
                var rejected = RejectConfiguration(ConfigurationFailureCode.InvalidAcknowledgement, "The provider did not confirm the complete selection.");
                if (!_configurationHadEffect && (_modelChangeSupport == ConfigurationSupport.Unsupported || _thoughtChangeSupport == ConfigurationSupport.Unsupported))
                    return new SessionConfigurationOutcome.Unsupported(_modelChangeSupport == ConfigurationSupport.Unsupported
                        ? ConfigurationDimension.Model : ConfigurationDimension.Effort, "The provider does not implement the required configuration operation.");
                return rejected;
            }
            ConfigurationState = new(configuration, _confirmedConfiguration.ModelId, _appliedThoughtLevel,
                Interlocked.Read(ref _contextUsageRevision));
            _configurationUncertain = false;
            return new SessionConfigurationOutcome.Applied(ConfigurationState);
        }
        catch (JsonRpcRemoteException error)
        {
            return RejectConfiguration(ConfigurationFailureCode.OperationFailed, error.Message);
        }
        catch
        {
            RejectConfiguration(ConfigurationFailureCode.OperationFailed, "Configuration transport failed.");
            throw;
        }
    }

    private SessionConfigurationOutcome? ValidateLegacyEffort(SessionSelection effort, string? targetModel, bool usesThought)
    {
        if (effort is SessionSelection.ProviderDefault && _hasExplicitThoughtLevel && _defaultThoughtLevel is null)
            return new SessionConfigurationOutcome.Unsupported(ConfigurationDimension.Effort, "The original default effort is unknown.");
        if (effort is SessionSelection.ProviderDefault && _isResumed &&
            (usesThought || !_modelCatalog.HasDefaultEffortEvidence(targetModel)))
            return new SessionConfigurationOutcome.Unsupported(ConfigurationDimension.Effort, "The original default effort is unknown after resume.");
        if (effort is SessionSelection.Explicit thoughtEffort && usesThought &&
            !_modelCatalog.ThoughtLevel!.Values.Any(value => value.Id == thoughtEffort.Value))
            return new SessionConfigurationOutcome.Rejected(ConfigurationFailureCode.InvalidSelection,
                "The requested effort is not advertised.", new(_confirmedConfiguration.ModelId, _appliedThoughtLevel, ConfigurationState is not null));
        if (effort is SessionSelection.Explicit explicitEffort && !usesThought)
        {
            var model = _modelCatalog.Models.FirstOrDefault(row => row.Model == targetModel);
            if (model?.SupportedReasoningEfforts.Any(value => string.Equals(value.ReasoningEffort, explicitEffort.Value, StringComparison.OrdinalIgnoreCase)) != true)
                return new SessionConfigurationOutcome.Unsupported(ConfigurationDimension.Effort, "No advertised operation expresses this model and effort selection.");
        }
        return null;
    }

    private SessionConfigurationOutcome.Rejected RejectConfiguration(ConfigurationFailureCode code, string reason)
    {
        lock (_idleUsageLock)
        {
            ConfigurationState = null;
            _configurationUncertain = true;
            _pendingIdleUsage = null;
            _pendingIdleUsageWithWindow = null;
            _lastCompletedContextPrompt = null;
            Interlocked.Increment(ref _contextUsageRevision);
        }
        return new(code, reason, new(_confirmedConfiguration.ModelId, _appliedThoughtLevel, false));
    }

    private async Task InitializeConfigurationAsync(JsonElement response, HarnessLaunchSpec spec, bool isResume, CancellationToken ct)
    {
        _isResumed = isResume;
        _permission = SessionSelection.FromNullable(spec.PermissionMode);
        var (rawModels, currentModelId) = ExtractAvailableModels(response);
        AvailableModels = rawModels.Select(m => m.ModelId).ToArray();
        var observedThought = AcpSessionOptions.Read(response)?.Reasoning;
        if (_advertisedEffortResetValue is not null)
            RefreshAdvertisedModelOptions(response);
        if (UsesAdvertisedModelOptions)
        {
            _defaultModel = isResume ? null : _advertisedOptions!.Model!.Current;
        }
        else
        {
            _modelCatalog = AcpModelCatalog.Build(rawModels, currentModelId, observedThought, currentSelectionIsDefault: !isResume);
            _defaultModel = isResume || currentModelId is null ? null : _modelCatalog.Models.FirstOrDefault(model => model.IsDefault)?.Model;
        }
        // A resumed current selection is not evidence of the original default.
        _appliedThoughtLevel = observedThought?.Current;
        if (!isResume) _defaultThoughtLevel = _appliedThoughtLevel;
        var configuration = SessionConfiguration.FromLaunch(spec);
        if (isResume && configuration.Model is SessionSelection.ProviderDefault)
            throw new SessionConfigurationException(new SessionConfigurationOutcome.Unsupported(
                ConfigurationDimension.Model, "The resumed session does not advertise its original default model."));
        if (configuration.Model is SessionSelection.ProviderDefault && configuration.Effort is SessionSelection.ProviderDefault)
        {
            if (UsesAdvertisedModelOptions)
                ConfirmContextBinding(currentModelId, _appliedThoughtLevel);
            ConfigurationState = new(configuration, currentModelId, _appliedThoughtLevel, ContextUsageRevision);
            return;
        }
        var outcome = await ConfigureAsync(configuration, ct).ConfigureAwait(false);
        if (outcome is not SessionConfigurationOutcome.Applied)
            throw new SessionConfigurationException(outcome);
    }

    private static bool IsPositiveAcknowledgement(JsonElement result) =>
        result.ValueKind == JsonValueKind.Object &&
        (!result.TryGetProperty("success", out var success) || success.ValueKind == JsonValueKind.True);

    private static bool ConfirmsThoughtLevel(JsonElement result, string optionId, string requested)
    {
        if (!IsPositiveAcknowledgement(result))
            return false;
        if (!result.TryGetProperty("configOptions", out var options)) return true;
        if (options.ValueKind != JsonValueKind.Array) return false;
        var foundRequestedOption = false;
        foreach (var option in options.EnumerateArray())
        {
            if (option.ValueKind != JsonValueKind.Object) return false;
            var id = TryGetString(option, "id");
            if (id != optionId && TryGetString(option, "category") != "thought_level") continue;
            if (id != optionId || TryGetString(option, "type") != "select" ||
                TryGetString(option, "currentValue") != requested) return false;
            // Inspect the unfiltered entry: malformed relevant options must not look absent.
            var parsed = AcpSessionOptions.Read(JsonSerializer.SerializeToElement(new { configOptions = new[] { option } }));
            if (parsed?.Reasoning?.Current != requested) return false;
            foundRequestedOption = true;
        }
        return foundRequestedOption;
    }

    private async Task<bool> ApplySelectionAsync(string? model, string? reasoningEffort = null, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var hasModel = !string.IsNullOrWhiteSpace(model);
        var effort = string.IsNullOrWhiteSpace(reasoningEffort) ? null : reasoningEffort.Trim();
        // A blank model keeps the group advertised as the session's initial default.
        var requestedModel = hasModel ? model!.Trim() : _defaultModel;
        if (UsesAdvertisedModelOptions)
            return requestedModel is not null && await ApplyAdvertisedModelOptionsAsync(requestedModel, effort, hasModel, ct).ConfigureAwait(false);
        if (!hasModel && effort is null && !_hasExplicitModel && !_hasExplicitThoughtLevel && !_hasExplicitSuffixEffort) return true;
        var entry = _modelCatalog.Models.FirstOrDefault(row => row.Model == requestedModel);
        var reasoningOption = _modelCatalog.ThoughtLevel;
        var useThoughtLevel = _modelCatalog.UsesThoughtLevel(requestedModel);
        if (!hasModel && effort is not null && !useThoughtLevel &&
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
            _hasExplicitSuffixEffort = !useThoughtLevel && effort is not null;
        }

        if (thoughtLevel is not null && thoughtLevel != _appliedThoughtLevel)
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(_handshakeTimeout);
            try
            {
                var result = await _rpc.SendRequestAsync("session/set_config_option", new
                {
                    sessionId = NativeThreadId,
                    configId = reasoningOption!.Id,
                    value = thoughtLevel
                }, timeoutCts.Token, result =>
                {
                    RefreshPermissionModes(result);
                    if (!ConfirmsThoughtLevel(result, reasoningOption!.Id, thoughtLevel)) return;
                    _thoughtChangeSupport = ConfigurationSupport.Supported;
                    _configurationHadEffect = true;
                    _appliedThoughtLevel = thoughtLevel;
                    _hasExplicitThoughtLevel = effort is not null;
                    ConfirmContextBinding(requestedModel is null ? null : _modelCatalog.Resolve(requestedModel, reasoningEffort), thoughtLevel);
                    ConfirmCompleteSelection();
                }).ConfigureAwait(false);
                if (!ConfirmsThoughtLevel(result, reasoningOption!.Id, thoughtLevel)) return false;
            }
            catch (JsonRpcRemoteException error) when (error.Code == -32601)
            {
                _thoughtChangeSupport = ConfigurationSupport.Unsupported;
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
                _modelChangeSupport = ConfigurationSupport.Unsupported;
                return false;
            }
        }
    }

    private void ConfirmModelResult(JsonElement result, string modelId)
    {
        RefreshPermissionModes(result);
        if (ConfirmsModel(result, modelId))
        {
            _modelChangeSupport = ConfigurationSupport.Supported;
            _configurationHadEffect = true;
            // Providers may reset thought_level on a model change. Missing options mean unknown state.
            _appliedThoughtLevel = AcpSessionOptions.Read(result)?.Reasoning?.Current;
            ConfirmContextBinding(modelId, _appliedThoughtLevel);
            var target = _pendingConfiguration;
            if (target is not null && (!_modelCatalog.UsesThoughtLevel(target.Model.ValueOrNull ?? _defaultModel) ||
                (target.Effort.ValueOrNull ?? _defaultThoughtLevel) is { } expected && expected == _appliedThoughtLevel))
                ConfirmCompleteSelection();
        }
    }

    private void ConfirmCompleteSelection()
    {
        if (_pendingConfiguration is not { } selection) return;
        ConfigurationState = new(selection, _confirmedConfiguration.ModelId, _appliedThoughtLevel,
            Interlocked.Read(ref _contextUsageRevision));
        _configurationUncertain = false;
    }

    private static bool ConfirmsModel(JsonElement result, string modelId)
    {
        if (!IsPositiveAcknowledgement(result))
            return false;
        var (_, currentModelId) = ExtractAvailableModels(result);
        return currentModelId is null || string.Equals(currentModelId, modelId, StringComparison.Ordinal);
    }

    private static (IReadOnlyList<AcpRawModel> Models, string? CurrentModelId) ExtractAvailableModels(JsonElement response)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<AcpRawModel>();
        string? currentModelId = null;
        void Add(string? id, string? name, string? description)
        {
            if (!string.IsNullOrWhiteSpace(id) && ids.Add(id))
            {
                result.Add(new AcpRawModel(id, name, description));
            }
        }

        if (response.ValueKind != JsonValueKind.Object)
        {
            return (result, currentModelId);
        }

        if (response.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Object)
        {
            currentModelId ??= TryGetString(models, "currentModelId");
            if (models.TryGetProperty("availableModels", out var available) && available.ValueKind == JsonValueKind.Array)
            {
                foreach (var model in available.EnumerateArray())
                {
                    Add(TryGetString(model, "modelId"), TryGetString(model, "name"), TryGetString(model, "description"));
                }
            }
        }

        if (response.TryGetProperty("configOptions", out var config) && config.ValueKind == JsonValueKind.Array)
        {
            foreach (var option in config.EnumerateArray())
            {
                if (option.ValueKind != JsonValueKind.Object ||
                    !(TryGetString(option, "category") == "model" || TryGetString(option, "id") == "model") ||
                    !option.TryGetProperty("options", out var values) || values.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                currentModelId ??= TryGetString(option, "currentValue");
                foreach (var value in values.EnumerateArray())
                {
                    if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("options", out var group) &&
                        group.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in group.EnumerateArray())
                        {
                            Add(TryGetString(item, "value"), TryGetString(item, "name"), TryGetString(item, "description"));
                        }
                    }
                    else
                    {
                        Add(TryGetString(value, "value"), TryGetString(value, "name"), TryGetString(value, "description"));
                    }
                }
            }
        }

        return (result, currentModelId);
    }

}
