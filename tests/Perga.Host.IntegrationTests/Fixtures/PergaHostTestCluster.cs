using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Perga.AgentKit;
using Perga.Contracts;
using Perga.Host.Configuration;
using Perga.Host.Serialization;
using Perga.Kernel;
using Perga.VcsKit;

namespace Perga.Host.IntegrationTests.Fixtures;

public sealed class AdvanceableTimeProvider : TimeProvider
{
    private long _utcNowMs;

    public AdvanceableTimeProvider(DateTimeOffset? initialUtc = null)
    {
        var start = initialUtc ?? new DateTimeOffset(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);
        _utcNowMs = start.ToUnixTimeMilliseconds();
    }

    public override DateTimeOffset GetUtcNow() =>
        DateTimeOffset.FromUnixTimeMilliseconds(Interlocked.Read(ref _utcNowMs));

    public void Advance(TimeSpan delta)
    {
        Interlocked.Add(ref _utcNowMs, (long)delta.TotalMilliseconds);
    }

    public void SetUtcNow(DateTimeOffset value)
    {
        Interlocked.Exchange(ref _utcNowMs, value.ToUnixTimeMilliseconds());
    }
}

public sealed record RecordedSessionOpen(
    HarnessLaunchSpec Spec,
    DateTimeOffset OpenedAt,
    string? NativeThreadId);

public sealed record RecordedTurnExecution(
    HarnessLaunchSpec Spec,
    TurnInput Input);

public sealed class ScriptableAgentRuntime : IAgentRuntime
{
    public bool CanResumeSession(string provider) => true;
    private readonly ConcurrentQueue<RecordedSessionOpen> _openedSessions = new();
    private readonly ConcurrentQueue<RecordedTurnExecution> _executedTurns = new();
    private Func<HarnessLaunchSpec, TurnInput, CancellationToken, IAsyncEnumerable<UnifiedAgentEvent>>? _turnHandler;
    private Func<string, Task<bool>>? _steerHandler;
    public Func<bool>? ApprovalClaimHandler { get; set; }
    public Action? ApprovalHandler { get; set; }
    public bool RequiresTerminalStop { get; set; }
    public Func<CancellationToken, Task>? CancelHandler { get; set; }
    public Func<CancellationToken, Task<bool>>? CloseHandler { get; set; }
    public Func<HarnessLaunchSpec, CancellationToken, Task>? BeforeOpenAsync { get; set; }
    public bool MismatchNativeThreadId { get; set; }
    public Func<HarnessLaunchSpec, Exception?>? OpenError { get; set; }
    public bool ShutdownConfirmed { get; set; } = true;
    private int _probeCount;
    private int _forceRefreshProbeCount;

    public ConcurrentQueue<(string RequestId, bool Approved)> ApprovalResponses { get; } = new();

    public bool SupportsSteering { get; set; } = true;
    public Func<SessionConfiguration, SessionConfigurationOutcome>? ConfigureHandler { get; set; }
    public Func<HarnessLaunchSpec, ConfirmedConfiguration?>? LaunchConfirmationHandler { get; set; }
    public ConcurrentQueue<string?> DisposedSessions { get; } = new();
    public ConcurrentQueue<SessionConfiguration> Configurations { get; } = new();
    public bool SupportsModelSwitch { get; set; }
    public Func<CancellationToken, Task<NativeCompactResult>>? CompactHandler { get; set; }

    public IReadOnlyCollection<RecordedSessionOpen> OpenedSessions => _openedSessions.ToArray();
    public IReadOnlyCollection<RecordedTurnExecution> ExecutedTurns => _executedTurns.ToArray();
    public IReadOnlyList<PermissionModeOption>? AcpPermissionModes { get; set; }

    public IReadOnlyList<ProviderStatus>? ProviderStatuses { get; set; }

    public int ProbeCount => Volatile.Read(ref _probeCount);
    public int ForceRefreshProbeCount => Volatile.Read(ref _forceRefreshProbeCount);

    public Func<TurnInput, CancellationToken, Task>? BeforeTurn { get; set; }

    public void SetTurnHandler(
        Func<HarnessLaunchSpec, TurnInput, CancellationToken, IAsyncEnumerable<UnifiedAgentEvent>> handler)
    {
        _turnHandler = handler;
    }

    public void SetSteerHandler(Func<string, Task<bool>> handler)
    {
        _steerHandler = handler;
    }

    public Task<IReadOnlyList<ProviderStatus>> ProbeProvidersAsync(CancellationToken ct)
    {
        _ = ct;
        Interlocked.Increment(ref _probeCount);
        IReadOnlyList<ProviderStatus> providers = ProviderStatuses ??
        [
            new("codex", Available: true, Version: "0.10.0-mock", Reason: null,
                Models: ["gpt-5.4", "gpt-5.4-mini", "post-compaction-model", "next-model", "new-model", "A", "B", "chosen", "chosen-model"]),
            new("claude", Available: true, Version: "1.0.0-mock", Reason: null, Models: ["claude-sonnet-4-6"]),
            new("acp:antigravity", Available: true, Version: "2.0.0-mock", Reason: null, Models: ["gemini-3.1-pro"], PermissionModes: AcpPermissionModes)
        ];
        return Task.FromResult(providers);
    }

    public Task<IReadOnlyList<ProviderStatus>> ProbeProvidersAsync(bool forceRefresh, CancellationToken ct = default)
    {
        if (forceRefresh)
        {
            Interlocked.Increment(ref _forceRefreshProbeCount);
        }

        return ProbeProvidersAsync(ct);
    }

    public IAgentSession CreateSession(HarnessLaunchSpec spec) => new ScriptableAgentSession(this, spec);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private async IAsyncEnumerable<UnifiedAgentEvent> DefaultStreamAsync(
        HarnessLaunchSpec spec,
        TurnInput input,
        [EnumeratorCancellation] CancellationToken ct)
    {
        _executedTurns.Enqueue(new RecordedTurnExecution(spec, input));
        if (BeforeTurn is { } before) await before(input, ct);

        if (_turnHandler is not null)
        {
            await foreach (var evt in _turnHandler(spec, input, ct).ConfigureAwait(false))
            {
                yield return evt;
            }

            yield break;
        }

        yield return new UnifiedAgentEvent.ThreadBound($"thr_{Guid.NewGuid():N}");
        yield return new UnifiedAgentEvent.ThinkingDelta("Analyzing track state...");
        yield return new UnifiedAgentEvent.TextDelta($"Ack: {input.Prompt[..Math.Min(40, input.Prompt.Length)]}");
        yield return new UnifiedAgentEvent.TurnCompleted(new TokenUsage(120, 45, 0, 100_000));
    }

    private sealed class ScriptableAgentSession : IAgentSession
    {
        private readonly ScriptableAgentRuntime _owner;
        private HarnessLaunchSpec _spec;
        private CancellationTokenSource? _activeTurnCts;
        private readonly object _approvalLock = new();
        private readonly HashSet<string> _seenApprovalRequests = new(StringComparer.Ordinal);
        private readonly HashSet<string> _pendingApprovalRequests = new(StringComparer.Ordinal);
        private bool _approvalsActive;
        private bool _disposed;

        public ScriptableAgentSession(ScriptableAgentRuntime owner, HarnessLaunchSpec spec)
        {
            _owner = owner;
            _spec = spec;
            ConfigurationState = owner.LaunchConfirmationHandler is { } confirm
                ? confirm(spec) : new(SessionConfiguration.FromLaunch(spec));
            NativeThreadId = _owner.MismatchNativeThreadId ? "wrong-native-id" : spec.ResumeNativeThreadId ?? $"thr_{Guid.NewGuid():N}";
        }

        public async Task InitializeAsync(CancellationToken ct)
        {
            if (_owner.BeforeOpenAsync is { } before) await before(_spec, ct);
            if (_owner.OpenError?.Invoke(_spec) is { } error) throw error;
            _owner._openedSessions.Enqueue(new RecordedSessionOpen(_spec, DateTimeOffset.UtcNow, NativeThreadId));
        }

        public string? NativeThreadId { get; }
        public bool SupportsNativeSteer => _owner.SupportsSteering;
        public bool SupportsNativeCompact => _owner.CompactHandler is not null;
        public Task<NativeCompactResult> CompactAsync(CancellationToken ct) => _owner.CompactHandler!(ct);
        public IReadOnlyList<PermissionModeOption> AvailablePermissionModes =>
            _spec.Provider.StartsWith("acp:", StringComparison.Ordinal) ? _owner.AcpPermissionModes ?? [] : [];
        public WorkerProviderConfiguration? WorkerConfiguration => _spec.Provider == "codex"
            ? new([
                new WorkerModelOption("gpt-5.4", "GPT 5.4", ["low", "high"], "low"),
                new WorkerModelOption("gpt-5.4-mini", "GPT 5.4 Mini", ["low"], "low"),
            ], "gpt-5.4", "low")
            : null;
        public bool SupportsSessionResume => true;
        public bool ShutdownConfirmed => _disposed && _owner.ShutdownConfirmed;
        public bool CanReuseContextWindow => true;
        public bool SupportsScopedClose => _owner.CloseHandler is not null;

        public Task<bool> TryCloseSessionAsync(CancellationToken ct) =>
            _owner.CloseHandler?.Invoke(ct) ?? Task.FromResult(false);

        public async IAsyncEnumerable<UnifiedAgentEvent> StreamTurnAsync(
            TurnInput input,
            [EnumeratorCancellation] CancellationToken ct)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            lock (_approvalLock)
            {
                _activeTurnCts = linked;
                _approvalsActive = !_disposed;
            }
            try
            {
                await foreach (var evt in _owner.DefaultStreamAsync(_spec, input, linked.Token).ConfigureAwait(false))
                {
                    lock (_approvalLock)
                    {
                        if (evt is UnifiedAgentEvent.ApprovalRequested request
                            && _seenApprovalRequests.Add(request.RequestId) && _approvalsActive)
                            _pendingApprovalRequests.Add(request.RequestId);
                        if (evt is UnifiedAgentEvent.TurnCompleted or UnifiedAgentEvent.TurnInterrupted)
                            ExpireApprovals();
                    }
                    yield return evt;
                }
            }
            finally
            {
                lock (_approvalLock)
                {
                    _activeTurnCts = null;
                    ExpireApprovals();
                }
            }
        }

        // Called under _approvalLock, shared by stream completion, cancellation and disposal.
        private void ExpireApprovals()
        {
            _approvalsActive = false;
            _pendingApprovalRequests.Clear();
        }

        public bool RequiresTerminalStop => _owner.RequiresTerminalStop;

        public Task CancelTurnAsync(CancellationToken ct)
        {
            lock (_approvalLock)
                ExpireApprovals();
            if (_owner.CancelHandler is { } handler)
                return handler(ct);
            _activeTurnCts?.Cancel();
            return Task.CompletedTask;
        }

        public Func<Task>? TryClaimApprovalResponse(
            string requestId, bool approved, string? selectedOptionId, string? reason, CancellationToken ct)
        {
            lock (_approvalLock)
            {
                if (_disposed || !_approvalsActive || !_pendingApprovalRequests.Remove(requestId)
                    || _owner.ApprovalClaimHandler?.Invoke() == false)
                    return null;
            }

            return () =>
            {
                _owner.ApprovalResponses.Enqueue((requestId, approved));
                _owner.ApprovalHandler?.Invoke();
                return Task.CompletedTask;
            };
        }

        public SessionConfigurationCapabilities ConfigurationCapabilities => SessionConfigurationCapabilities.LaunchOnly with
        {
            Model = SessionConfigurationCapabilities.LaunchDimension() with { LiveChange = _owner.SupportsModelSwitch ? ConfigurationSupport.Supported : ConfigurationSupport.Unsupported },
            Effort = SessionConfigurationCapabilities.LaunchDimension() with { LiveChange = _owner.SupportsModelSwitch ? ConfigurationSupport.Supported : ConfigurationSupport.Unsupported }
        };
        public ConfirmedConfiguration? ConfigurationState { get; private set; }
        public Task<SessionConfigurationOutcome> ConfigureAsync(SessionConfiguration configuration, CancellationToken ct = default)
        {
            _owner.Configurations.Enqueue(configuration);
            var outcome = _owner.ConfigureHandler?.Invoke(configuration) ?? ((_owner.SupportsModelSwitch && ConfigurationState?.Selection.Permission == configuration.Permission) || ConfigurationState?.Selection == configuration
                ? (SessionConfigurationOutcome)new SessionConfigurationOutcome.Applied(new(configuration))
                : new SessionConfigurationOutcome.RequiresReopen("Scripted launch-only configuration."));
            ConfigurationState = outcome is SessionConfigurationOutcome.Applied applied ? applied.Configuration : null;
            if (ConfigurationState is { } state)
                _spec = _spec with { Model = state.Selection.Model.ValueOrNull, ReasoningEffort = state.Selection.Effort.ValueOrNull,
                    PermissionMode = state.Selection.Permission.ValueOrNull };
            return Task.FromResult(outcome);
        }

        public async Task<SteerDelivery> SteerAsync(string turnCorrelationId, string message, CancellationToken ct)
        {
            var accepted = _owner._steerHandler is null || await _owner._steerHandler(message).WaitAsync(ct);
            return accepted ? SteerDelivery.Accepted : SteerDelivery.Declined;
        }

        public ValueTask DisposeAsync()
        {
            lock (_approvalLock)
            {
                _disposed = true;
                ExpireApprovals();
            }
            _owner.DisposedSessions.Enqueue(NativeThreadId);
            return ValueTask.CompletedTask;
        }
    }
}

public sealed class FakeGitHubForgeService : IGitHubForgeService
{
    public ConcurrentQueue<(int PrNumber, string ExpectedHeadSha)> MergedPrs { get; } = new();

    public Task<PrInfo> PublishPrAsync(PrPublishRequest req, CancellationToken ct)
    {
        _ = ct;
        return Task.FromResult(new PrInfo(
            Number: 42,
            Url: $"https://github.com/example/repo/pull/42",
            State: "OPEN",
            HeadSha: "abc1234def5678"));
    }

    public Task<PrChecksStatus> GetPrChecksAsync(string repoOrPath, int prNumber, CancellationToken ct)
    {
        _ = repoOrPath;
        _ = ct;
        return Task.FromResult(new PrChecksStatus(
            PrNumber: prNumber,
            HeadSha: "abc1234def5678",
            AllCompleted: true,
            AllGreen: true,
            Checks: [new CheckRunSummary("ci/test", "COMPLETED", "SUCCESS", "https://github.com/example/repo/runs/1")]));
    }

    public Task<CiFailureEvidence> GetCiFailureEvidenceAsync(string repoOrPath, int prNumber, string headSha, CancellationToken ct)
    {
        _ = repoOrPath;
        _ = ct;
        return Task.FromResult(new CiFailureEvidence(
            PrNumber: prNumber,
            HeadSha: headSha,
            SummaryText: "1 check failed: lint",
            LogTail: "error: unexpected console.log"));
    }

    public Task MergePrAsync(string repoOrPath, int prNumber, string expectedHeadSha, CancellationToken ct)
    {
        _ = repoOrPath;
        _ = ct;
        MergedPrs.Enqueue((prNumber, expectedHeadSha));
        return Task.CompletedTask;
    }

    public Task<IssueInfo> ViewIssueAsync(string repoOrPath, int issueNumber, CancellationToken ct)
    {
        _ = repoOrPath;
        _ = ct;
        return Task.FromResult(new IssueInfo(
            Number: issueNumber,
            Url: $"https://github.com/example/repo/issues/{issueNumber}",
            State: "OPEN",
            Title: $"Issue #{issueNumber}",
            Body: "Detailed issue description.",
            Labels: ["bug", "p1"]));
    }
}

public sealed class PergaHostTestCluster : IAsyncDisposable
{
    private readonly bool _ownsTempDir;

    public PergaHostTestCluster(
        string? explicitDataDir = null,
        Action<PergaHostOptions>? configureHostOptions = null,
        Action<IServiceCollection>? configureExtraServices = null)
    {
        if (string.IsNullOrWhiteSpace(explicitDataDir))
        {
            DataDir = Directory.CreateTempSubdirectory("perga-host-it-").FullName;
            _ownsTempDir = true;
        }
        else
        {
            DataDir = Path.GetFullPath(explicitDataDir);
            Directory.CreateDirectory(DataDir);
            _ownsTempDir = false;
        }

        TimeProvider = new AdvanceableTimeProvider();
        AgentRuntime = new ScriptableAgentRuntime();
        ForgeService = new FakeGitHubForgeService();

        Factory = new IsolatedPergaHostFactory(DataDir, configureHostOptions, services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(TimeProvider);

            services.RemoveAll<IAgentRuntime>();
            services.AddSingleton<IAgentRuntime>(AgentRuntime);

            services.RemoveAll<IGitHubForgeService>();
            services.AddSingleton<IGitHubForgeService>(ForgeService);

            configureExtraServices?.Invoke(services);
        });

        Client = Factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        var management = Factory.Services.GetRequiredService<Perga.Host.Http.SystemManagementAuthorization>();
        Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", File.ReadAllText(management.TokenFilePath).Trim());
    }

    public string DataDir { get; }
    public AdvanceableTimeProvider TimeProvider { get; }
    public ScriptableAgentRuntime AgentRuntime { get; }
    public FakeGitHubForgeService ForgeService { get; }
    public WebApplicationFactory<Program> Factory { get; }
    public HttpClient Client { get; }
    public IServiceProvider Services => Factory.Services;

    public async Task<(string TrackId, string SessionId)> CreateTrackAsync()
    {
        using var areaResponse = await Client.PostAsJsonAsync("/api/v1/areas",
            new { name = "Test area", defaultCwd = DataDir });
        Assert.True(areaResponse.IsSuccessStatusCode, await areaResponse.Content.ReadAsStringAsync());
        var area = (await areaResponse.Content.ReadFromJsonAsync<AreaDto>(PergaJsonDefaults.ApiOptions))!;
        using var trackResponse = await Client.PostAsJsonAsync("/api/v1/tracks",
            new { areaId = area.Id, cwd = DataDir });
        Assert.True(trackResponse.IsSuccessStatusCode, await trackResponse.Content.ReadAsStringAsync());
        using var snapshot = JsonDocument.Parse(await trackResponse.Content.ReadAsStringAsync());
        var track = snapshot.RootElement.GetProperty("track");
        var trackId = track.GetProperty("id").GetString()!;
        await Services.GetRequiredService<IPergaKernel>().WaitForTrackIdleAsync(trackId);
        return (trackId, track.GetProperty("leadSessionId").GetString()!);
    }

    public async Task<WebSocket> ConnectEventsWebSocketAsync(string query = "", CancellationToken ct = default)
    {
        var wsClient = Factory.Server.CreateWebSocketClient();
        var uri = new Uri($"ws://localhost/api/v1/events{query}");
        return await wsClient.ConnectAsync(uri, ct);
    }

    public async Task<WebSocket> ConnectTerminalWebSocketAsync(string termId, CancellationToken ct = default)
    {
        var wsClient = Factory.Server.CreateWebSocketClient();
        var uri = new Uri($"ws://localhost/api/v1/terminals/{Uri.EscapeDataString(termId)}/ws");
        return await wsClient.ConnectAsync(uri, ct);
    }

    public static async Task<JsonDocument> ReceiveTextJsonFrameAsync(WebSocket ws, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var res = await ws.ReceiveAsync(buffer.AsMemory(), ct);
            if (res.MessageType == WebSocketMessageType.Close)
            {
                throw new EndOfStreamException("WebSocket closed before frame arrived.");
            }

            ms.Write(buffer, 0, res.Count);
            if (res.EndOfMessage)
            {
                break;
            }
        }

        ms.Position = 0;
        return await JsonDocument.ParseAsync(ms, cancellationToken: ct);
    }

    public static async Task<(WebSocketMessageType Type, byte[] Payload)> ReceiveBinaryFrameAsync(WebSocket ws, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        var buffer = new byte[32 * 1024];
        WebSocketMessageType msgType = WebSocketMessageType.Binary;
        while (true)
        {
            var res = await ws.ReceiveAsync(buffer.AsMemory(), ct);
            msgType = res.MessageType;
            if (res.MessageType == WebSocketMessageType.Close)
            {
                return (WebSocketMessageType.Close, []);
            }

            ms.Write(buffer, 0, res.Count);
            if (res.EndOfMessage)
            {
                break;
            }
        }

        return (msgType, ms.ToArray());
    }

    public async Task<HttpResponseMessage> PostMcpAsync(
        string mcpPath,
        string? bearerToken,
        object payload,
        string? mcpSessionId = null,
        string? acceptHeader = null,
        CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(payload, PergaJsonDefaults.McpOptions);
        var req = new HttpRequestMessage(HttpMethod.Post, mcpPath)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        if (!string.IsNullOrWhiteSpace(bearerToken))
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        }

        if (!string.IsNullOrWhiteSpace(mcpSessionId))
        {
            req.Headers.TryAddWithoutValidation("Mcp-Session-Id", mcpSessionId);
        }

        if (!string.IsNullOrWhiteSpace(acceptHeader))
        {
            req.Headers.TryAddWithoutValidation("Accept", acceptHeader);
        }

        return await Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await Factory.DisposeAsync();

        if (_ownsTempDir && Directory.Exists(DataDir))
        {
            try
            {
                Directory.Delete(DataDir, recursive: true);
            }
            catch
            {
            }
        }
    }
}
