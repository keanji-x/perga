using Perga.AgentKit;
using Perga.Contracts;
using Perga.Kernel.Tests.Fakes;
using Xunit;

namespace Perga.Kernel.Tests;

public sealed class OarKernelIntegrationTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _workspaceDir;
    private readonly string _mockBridgePath;
    private readonly string _logPath;

    public OarKernelIntegrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"perga-oar-kernel-{Guid.NewGuid():N}");
        _workspaceDir = Path.Combine(_tempDir, "workspace");
        Directory.CreateDirectory(_workspaceDir);
        _logPath = Path.Combine(_tempDir, "bridge.jsonl");
        _mockBridgePath = Path.Combine(_tempDir, "mock-oar-bridge.mjs");
        File.WriteAllText(_mockBridgePath, MockBridgeScript);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task KernelDefaultPermissionModesOpenAndCompleteTurnsAcrossAllFourOarProviders()
    {
        await using var runtime = new AgentRuntime(new AgentRuntimeOptions
        {
            BridgeScriptPath = _mockBridgePath,
            ProviderOverrides = new Dictionary<string, ProviderCommandOverride>(StringComparer.OrdinalIgnoreCase)
            {
                ["codex"] = new("fake-codex", Env: new Dictionary<string, string> { ["MOCK_OAR_LOG"] = _logPath }),
                ["claude"] = new("fake-claude", Env: new Dictionary<string, string> { ["MOCK_OAR_LOG"] = _logPath }),
                ["acp:antigravity"] = new("fake-agy", Env: new Dictionary<string, string> { ["MOCK_OAR_LOG"] = _logPath }),
                ["acp:opencode"] = new("fake-opencode", Env: new Dictionary<string, string> { ["MOCK_OAR_LOG"] = _logPath })
            }
        });

        await using var ctx = new KernelTestContext(agentRuntime: runtime);
        var area = await ctx.Kernel.CreateAreaAsync(new CreateAreaRequest("OAR Defaults", DefaultCwd: _workspaceDir));

        foreach (var provider in new[] { "codex", "claude", "acp:antigravity", "acp:opencode" })
        {
            var snap = await ctx.Kernel.CreateTrackAsync(new CreateTrackRequest(
                AreaId: area.Id,
                Title: $"Track {provider}",
                Cwd: _workspaceDir,
                Provider: provider));
            await ctx.Kernel.Actors.GetOrActivate(snap.Track.Id).EnsureMountedAsync();

            var turn = await ctx.SendAndWaitForTurnAsync(
                snap.Track.LeadSessionId!,
                new SendSessionMessageRequest($"Hello {provider}", $"corr-{provider}"));
            Assert.Equal("completed", turn.Status);
        }

        var logLines = await File.ReadAllLinesAsync(_logPath);
        Assert.Contains(logLines, l => l.Contains("\"runtime\":\"codex\"", StringComparison.Ordinal) &&
                                       l.Contains("\"OAR_CODEX_SANDBOX\":\"workspace-write\"", StringComparison.Ordinal));
        Assert.Contains(logLines, l => l.Contains("\"runtime\":\"claude\"", StringComparison.Ordinal) &&
                                       l.Contains("\"--permission-mode\",\"dontAsk\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CanceledCompactionPoisonsOarHandleAndNextKernelTurnRecoversViaReopen()
    {
        await using var runtime = new AgentRuntime(new AgentRuntimeOptions
        {
            BridgeScriptPath = _mockBridgePath,
            ProviderOverrides = new Dictionary<string, ProviderCommandOverride>(StringComparer.OrdinalIgnoreCase)
            {
                ["codex"] = new("fake-codex", Env: new Dictionary<string, string>
                {
                    ["MOCK_OAR_LOG"] = _logPath,
                    ["MOCK_ABORT_COMPACT"] = "1"
                })
            }
        });

        await using var ctx = new KernelTestContext(agentRuntime: runtime);
        var area = await ctx.Kernel.CreateAreaAsync(new CreateAreaRequest("OAR Compact Recovery", DefaultCwd: _workspaceDir));
        var snap = await ctx.Kernel.CreateTrackAsync(new CreateTrackRequest(
            AreaId: area.Id,
            Title: "Compact Recovery",
            Cwd: _workspaceDir,
            Provider: "codex"));
        await ctx.Kernel.Actors.GetOrActivate(snap.Track.Id).EnsureMountedAsync();
        var leadId = snap.Track.LeadSessionId!;

        var firstTurn = await ctx.SendAndWaitForTurnAsync(leadId, new SendSessionMessageRequest("turn 1", "turn-1"));
        Assert.Equal("completed", firstTurn.Status);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ctx.Kernel.ControlSessionAsync(leadId, new("compact"), new InteropPrincipal.Human(), default));

        var recoveredTurn = await ctx.SendAndWaitForTurnAsync(leadId, new SendSessionMessageRequest("turn 2 after cancel", "turn-2"));
        Assert.Equal("completed", recoveredTurn.Status);

        var openLines = (await File.ReadAllLinesAsync(_logPath))
            .Where(l => l.Contains("\"method\":\"session/open\"", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(2, openLines.Length);
        Assert.Contains("\"resume\":\"codex-sess-1\"", openLines[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task WorkerModelAndEffortSettingsUpdateReopensOarSessionAndRestoresDefaults()
    {
        await using var runtime = new AgentRuntime(new AgentRuntimeOptions
        {
            BridgeScriptPath = _mockBridgePath,
            ProviderOverrides = new Dictionary<string, ProviderCommandOverride>(StringComparer.OrdinalIgnoreCase)
            {
                ["codex"] = new("fake-codex", Env: new Dictionary<string, string> { ["MOCK_OAR_LOG"] = _logPath })
            }
        });

        await using var ctx = new KernelTestContext(agentRuntime: runtime);
        ctx.DirectDriver.CreateLeaseDirectories = true;
        ctx.CowOverlayDriver.CreateLeaseDirectories = true;
        var area = await ctx.Kernel.CreateAreaAsync(new CreateAreaRequest("OAR Worker Settings", DefaultCwd: _workspaceDir));
        var snap = await ctx.Kernel.CreateTrackAsync(new CreateTrackRequest(
            AreaId: area.Id,
            Title: "Worker Settings",
            Cwd: _workspaceDir,
            Provider: "codex"));
        await ctx.Kernel.Actors.GetOrActivate(snap.Track.Id).EnsureMountedAsync();

        var dispatched = await ctx.Kernel.CreateSessionAsync(snap.Track.Id, new(Prompt: "Run initial worker turn", Task: new(), Provider: "codex"), "worker-settings-dispatch", new InteropPrincipal.Human(), CancellationToken.None);
        await ctx.Kernel.WaitForTrackIdleAsync(snap.Track.Id);

        var worker = Assert.Single((await ctx.Kernel.ListSessionsAsync(snap.Track.Id, new(IncludeArchived: true), new InteropPrincipal.Human(), default)).Select(summary => summary.Session).ToArray(), s => s.BoundTaskId == dispatched.Receipt.Task!.Id);
        var attemptId = worker.BoundAttemptId!;

        var itemsPage = await ctx.Kernel.GetSessionItemsAsync(worker.Id, new(), new InteropPrincipal.Human(), default);
        var workerConfig = Assert.IsType<SessionCapabilitiesDto>(itemsPage.Capabilities);
        Assert.Equal("gpt-5.4", workerConfig.DefaultModel);
        Assert.Equal("low", workerConfig.DefaultReasoningEffort);
        Assert.Contains(workerConfig.Models, m => m.Id == "gpt-5.4-mini");

        // Switch Worker to gpt-5.4-mini / low and verify next turn reopens OAR session with those exact selections.
        var updated = await ctx.Kernel.UpdateSessionAsync(worker.Id, new(ExpectedConfigVersion: 0, Model: new("gpt-5.4-mini"), ReasoningEffort: new("low"), ExpectedAttemptId: attemptId), new InteropPrincipal.Human(), CancellationToken.None);
        Assert.Equal("gpt-5.4-mini", updated.Model);
        Assert.Equal("low", updated.ReasoningEffort);

        await ctx.Kernel.SendSessionMessageAsync(worker.Id, new(Content: "Second worker turn", RequestKey: "worker-turn-2", Mode: "default", TurnId: null, ExpectedAttemptId: attemptId), new InteropPrincipal.Human(), CancellationToken.None);
        await ctx.Kernel.WaitForTrackIdleAsync(snap.Track.Id);
        var turnsAfterSwitch = ctx.Store.GetTurnsForSession(worker.Id);
        Assert.Equal(2, turnsAfterSwitch.Count);
        Assert.All(turnsAfterSwitch, t => Assert.Equal("completed", t.Status));

        // Reset Worker back to provider defaults (null/null) and verify OAR session reopens on gpt-5.4 / low.
        var restored = await ctx.Kernel.UpdateSessionAsync(worker.Id, new(ExpectedConfigVersion: 1, Model: new(null), ReasoningEffort: new(null), ExpectedAttemptId: attemptId), new InteropPrincipal.Human(), CancellationToken.None);
        Assert.Null(restored.Model);
        Assert.Null(restored.ReasoningEffort);

        await ctx.Kernel.SendSessionMessageAsync(worker.Id, new(Content: "Third worker turn", RequestKey: "worker-turn-3", Mode: "default", TurnId: null, ExpectedAttemptId: attemptId), new InteropPrincipal.Human(), CancellationToken.None);
        await ctx.Kernel.WaitForTrackIdleAsync(snap.Track.Id);
        var turnsAfterRestore = ctx.Store.GetTurnsForSession(worker.Id);
        Assert.Equal(3, turnsAfterRestore.Count);
        Assert.All(turnsAfterRestore, t => Assert.Equal("completed", t.Status));

        var openLines = (await File.ReadAllLinesAsync(_logPath))
            .Where(l => l.Contains("\"method\":\"session/open\"", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(3, openLines.Length);
        Assert.DoesNotContain("configureWorker", openLines[0], StringComparison.Ordinal);
        Assert.Contains("\"resume\":\"codex-sess-1\"", openLines[1], StringComparison.Ordinal);
        Assert.Contains("\"model\":\"gpt-5.4-mini\"", openLines[1], StringComparison.Ordinal);
        Assert.Contains("\"effort\":\"low\"", openLines[1], StringComparison.Ordinal);
        Assert.Contains("\"resume\":\"codex-sess-1\"", openLines[2], StringComparison.Ordinal);
        Assert.Contains("\"model\":null", openLines[2], StringComparison.Ordinal);
        Assert.Contains("\"effort\":null", openLines[2], StringComparison.Ordinal);
    }

    [Fact]
    public async Task WorkerFirstTurnUsesTheSharedProviderDefaultConfiguration()
    {
        await using var runtime = new AgentRuntime(new AgentRuntimeOptions
        {
            BridgeScriptPath = _mockBridgePath,
            ProviderOverrides = new Dictionary<string, ProviderCommandOverride>(StringComparer.OrdinalIgnoreCase)
            {
                ["codex"] = new("fake-codex", Env: new Dictionary<string, string>
                {
                    ["MOCK_OAR_LOG"] = _logPath,
                    ["MOCK_NULL_INITIAL_EFFORT"] = "1"
                })
            }
        });

        await using var ctx = new KernelTestContext(agentRuntime: runtime);
        ctx.DirectDriver.CreateLeaseDirectories = true;
        ctx.CowOverlayDriver.CreateLeaseDirectories = true;
        var area = await ctx.Kernel.CreateAreaAsync(new CreateAreaRequest("OAR Null Effort Reconcile", DefaultCwd: _workspaceDir));
        var snap = await ctx.Kernel.CreateTrackAsync(new CreateTrackRequest(
            AreaId: area.Id,
            Title: "Null Effort Reconcile",
            Cwd: _workspaceDir,
            Provider: "codex"));
        await ctx.Kernel.Actors.GetOrActivate(snap.Track.Id).EnsureMountedAsync();

        var dispatched = await ctx.Kernel.CreateSessionAsync(snap.Track.Id, new(Prompt: "Run turn with null native effort", Task: new(), Provider: "codex"), "worker-null-effort-dispatch", new InteropPrincipal.Human(), CancellationToken.None);
        await ctx.Kernel.WaitForTrackIdleAsync(snap.Track.Id);

        var worker = Assert.Single((await ctx.Kernel.ListSessionsAsync(snap.Track.Id, new(IncludeArchived: true), new InteropPrincipal.Human(), default)).Select(summary => summary.Session).ToArray(), s => s.BoundTaskId == dispatched.Receipt.Task!.Id);
        var turn = Assert.Single(ctx.Store.GetTurnsForSession(worker.Id));
        Assert.Equal("completed", turn.Status);

        var logLines = await File.ReadAllLinesAsync(_logPath);
        var openLines = logLines.Where(l => l.Contains("\"method\":\"session/open\"", StringComparison.Ordinal)).ToArray();
        var promptLines = logLines.Where(l => l.Contains("\"method\":\"session/prompt\"", StringComparison.Ordinal)).ToArray();
        Assert.Single(openLines);
        Assert.Single(promptLines);
        Assert.Contains("\"effort\":null", openLines[0], StringComparison.Ordinal);
    }

    private const string MockBridgeScript = """
import fs from 'node:fs';
import readline from 'node:readline';

const logPath = process.env.MOCK_OAR_LOG;
const log = (entry) => { if (logPath) fs.appendFileSync(logPath, JSON.stringify(entry) + '\n'); };
const send = (msg) => process.stdout.write(JSON.stringify(msg) + '\n');

let openRuntime = null;
readline.createInterface({ input: process.stdin, crlfDelay: Infinity }).on('line', (line) => {
  const trimmed = line.trim();
  if (!trimmed) return;
  const req = JSON.parse(trimmed);
  log({ method: req.method, params: req.params });

  if (req.method === 'probe') {
    const rt = req.params.runtime;
    const models = rt === 'codex'
      ? [
          { id: 'gpt-5.4', displayName: 'GPT 5.4', description: '', isDefault: true, defaultEffort: 'low', effortLevels: ['low', 'high'] },
          { id: 'gpt-5.4-mini', displayName: 'GPT 5.4 Mini', description: '', isDefault: false, defaultEffort: 'low', effortLevels: ['low'] }
        ]
      : [{ id: `${rt}-default`, displayName: `${rt}-default`, description: '', isDefault: true, defaultEffort: null, effortLevels: [] }];
    send({
      jsonrpc: '2.0',
      id: req.id,
      result: { runtime: rt, available: true, version: 'mock-1.0.0', reason: null, supportsNativeSteer: true, supportsManualCompaction: true, models }
    });
    return;
  }
  if (req.method === 'session/open') {
    openRuntime = req.params.runtime;
    const sessionId = req.params.resume ?? `${openRuntime}-sess-1`;
    const resolvedModel = req.params.model ?? (openRuntime === 'codex' ? 'gpt-5.4' : `${openRuntime}-default`);
    const resolvedEffort = req.params.effort ?? (openRuntime === 'codex' && process.env.MOCK_NULL_INITIAL_EFFORT !== '1' ? 'low' : null);
    const sessionConfiguration = {
      models: [
        { id: 'gpt-5.4', name: 'GPT 5.4', reasoningEfforts: ['low', 'high'], defaultReasoningEffort: 'low' },
        { id: 'gpt-5.4-mini', name: 'GPT 5.4 Mini', reasoningEfforts: ['low'], defaultReasoningEffort: 'low' }
      ],
      defaultModel: 'gpt-5.4',
      defaultReasoningEffort: 'low'
    };
    send({
      jsonrpc: '2.0',
      id: req.id,
      result: {
        sessionId,
        model: resolvedModel,
        effort: resolvedEffort,
        supportsNativeSteer: true,
        supportsNativeCompact: true,
        supportsSessionResume: true,
        contextUsage: { inputTokens: 0, outputTokens: 0, cachedInputTokens: 0, contextWindowTokens: 200000, usedTokens: 0 },
        sessionConfiguration
      }
    });
    return;
  }
  if (req.method === 'session/prompt') {
    send({ jsonrpc: '2.0', method: 'oar/event', params: { kind: 'text_delta', delta: `OK:${req.params.prompt}` } });
    send({
      jsonrpc: '2.0',
      id: req.id,
      result: {
        status: 'completed',
        usage: { inputTokens: 10, outputTokens: 5, cachedInputTokens: 0, contextWindowTokens: 200000, usedTokens: 15 }
      }
    });
    return;
  }
  if (req.method === 'session/compact_support') {
    send({ jsonrpc: '2.0', id: req.id, result: { supported: true, mode: 'native' } });
    return;
  }
  if (req.method === 'session/compact') {
    if (process.env.MOCK_ABORT_COMPACT === '1') {
      send({ jsonrpc: '2.0', id: req.id, error: { code: -32011, message: 'Slash compaction timed out.' } });
      return;
    }
    send({ jsonrpc: '2.0', id: req.id, result: { completed: true, mode: 'native', summary: 'Compacted', usage: null } });
    return;
  }
  if (req.method === 'session/close') {
    send({ jsonrpc: '2.0', id: req.id, result: { closed: true } });
    return;
  }
  send({ jsonrpc: '2.0', id: req.id, error: { code: -32601, message: 'unknown method' } });
});
""";
}
