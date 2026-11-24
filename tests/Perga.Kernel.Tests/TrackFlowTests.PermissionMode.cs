using System.Text.Json;
using Perga.AgentKit;
using Perga.Contracts;
using Perga.Kernel.Flow;
using Perga.Kernel.Mcp;
using Perga.Kernel.Ports;
using Perga.Kernel.Tests.Fakes;

namespace Perga.Kernel.Tests;

public sealed partial class TrackFlowTests
{
    [Theory]
    [InlineData("""{"kind":"watch","cmd":"poll","permissionMode":"full"}""", "permissionMode")]
    [InlineData("""{"kind":"wake","session":"lead","prompt":"Review","permissionMode":"full"}""", "permissionMode")]
    [InlineData("""{"kind":"run","cmd":"poll","permissionMode":"full"}""", "permissionMode")]
    [InlineData("""{"kind":"run","prompt":"Review","permissionMode":" "}""", "permissionMode")]
    [InlineData("""{"kind":"run","prompt":"Review","permissionMode":null}""", "permissionMode")]
    public void ParserRejectsPermissionModeOnUnsupportedOrBlankActions(string action, string field)
    {
        var (spec, errors) = TrackFlowParser.Parse($$"""
            {"rev":0,"nodes":[{"id":"node","do":{{action}}}]}
            """);
        Assert.Null(spec);
        Assert.Contains(errors, e => e.Path == $"$.nodes[0].do.{field}");
    }

    [Fact]
    public void SerializerPreservesExplicitPermissionModeRoundTrip()
    {
        var (spec, errors) = TrackFlowParser.Parse("""
            {"rev":0,"nodes":[{"id":"impl","do":{"kind":"run","provider":"codex","permissionMode":"full","prompt":"Run tests"}}]}
            """);
        Assert.Empty(errors);
        Assert.Equal("full", Assert.Single(spec!.Nodes).Do.PermissionMode);

        var canonical = TrackFlowSerializer.SerializeCanonical(spec);
        using var doc = JsonDocument.Parse(canonical);
        Assert.Equal("full", doc.RootElement.GetProperty("nodes")[0].GetProperty("do").GetProperty("permissionMode").GetString());
    }

    [Fact]
    public async Task FlowCommitValidatesExplicitPermissionModeAndReusedSessionMatch()
    {
        await using var ctx = new KernelTestContext();
        ctx.AgentRuntime.Providers =
        [
            new("codex", true, "1", null, ["gpt-5"]),
            new("claude", true, "1", null, ["sonnet"])
        ];
        var (_, snap) = await ctx.CreateDefaultTrackAsync();
        var trackId = snap.Track.Id;

        var badStatic = await ctx.Kernel.CommitTrackFlowAsync(trackId, dryRun: true, expectedFlowRev: 0, Human, content: """
            {"rev":0,"nodes":[{"id":"bad","do":{"kind":"run","provider":"codex","permissionMode":"bypassPermissions","prompt":"Run"}}]}
            """);
        Assert.False(badStatic.Ok);
        Assert.Contains(badStatic.Diagnostics, d => d.Path == "$.nodes[0].do.permissionMode");

        var sub = (await ctx.Kernel.CreateSessionAsync(trackId,
            new CreateSessionRequest(Title: "Helper", Provider: "codex", PermissionMode: "never"),
            "req-reuse-perm", Human, default)).Receipt.Session;

        var mismatch = await ctx.Kernel.CommitTrackFlowAsync(trackId, dryRun: true, expectedFlowRev: 0, Human, content: $$$"""
            {"rev":0,"nodes":[{"id":"reuse","do":{"kind":"run","session":"{{{sub.Id}}}","permissionMode":"full","prompt":"Run"}}]}
            """);
        Assert.False(mismatch.Ok);
        Assert.Contains(mismatch.Diagnostics, d => d.Path == "$.nodes[0].do.permissionMode");

        var match = await ctx.Kernel.CommitTrackFlowAsync(trackId, dryRun: true, expectedFlowRev: 0, Human, content: $$$"""
            {"rev":0,"nodes":[{"id":"reuse","do":{"kind":"run","session":"{{{sub.Id}}}","permissionMode":"never","prompt":"Run"}}]}
            """);
        Assert.True(match.Ok, JsonSerializer.Serialize(match.Diagnostics));
    }

    [Fact]
    public async Task ExplicitFlowPermissionModeAppliesToWorkerLaunchAndProjection()
    {
        await using var ctx = new KernelTestContext();
        ctx.AgentRuntime.Providers = [new("codex", true, "1", null, ["gpt-5"])];
        var (_, snap) = await ctx.CreateDefaultTrackAsync();
        var trackId = snap.Track.Id;

        var commit = await ctx.Kernel.CommitTrackFlowAsync(trackId, dryRun: false, expectedFlowRev: 0, Human, content: """
            {"rev":0,"nodes":[{"id":"explicit-full","do":{"kind":"run","access":"read_only","provider":"codex","permissionMode":"full","prompt":"Run checks"}}]}
            """);
        Assert.True(commit.Ok, JsonSerializer.Serialize(commit.Diagnostics));
        await ctx.Kernel.WaitForTrackIdleAsync(trackId);

        var workerLaunch = Assert.Single(ctx.AgentRuntime.OpenedSessions, s =>
            s.LaunchSpec.SystemPrompt?.Contains("BoundTaskId=", StringComparison.Ordinal) == true);
        Assert.Equal("full", workerLaunch.LaunchSpec.PermissionMode);

        var flow = await ctx.Kernel.GetTrackFlowAsync(trackId);
        var projected = Assert.Single(flow.Projection.Nodes, n => n.Id == "explicit-full");
        Assert.Equal("full", projected.PermissionMode);
    }

    [Theory]
    [InlineData("acp:antigravity", null, "codex", "read_write", "full")]
    [InlineData("acp:antigravity", null, "codex", "read_only", "never")]
    [InlineData("acp:antigravity", null, "claude", "read_write", "bypassPermissions")]
    [InlineData("acp:antigravity", null, "claude", "read_only", "dontAsk")]
    [InlineData("acp:opencode", null, "codex", "read_write", "never")]
    [InlineData("claude", "bypassPermissions", "codex", "read_write", "full")]
    [InlineData("claude", "bypassPermissions", "codex", "read_only", "never")]
    [InlineData("claude", "acceptEdits", "codex", "read_write", "never")]
    [InlineData("claude", "plan", "claude", "read_write", "dontAsk")]
    [InlineData("codex", "full", "claude", "read_write", "bypassPermissions")]
    [InlineData("codex", "never", "codex", "read_write", "never")]
    public async Task OmittedFlowPermissionModeInheritsFromLeadByAccessAndProviderPolicy(
        string leadProvider,
        string? leadPermissionMode,
        string workerProvider,
        string access,
        string? expectedWorkerMode)
    {
        await using var ctx = new KernelTestContext();
        ctx.AgentRuntime.Providers =
        [
            new("codex", true, "1", null, ["gpt-5"]),
            new("claude", true, "1", null, ["sonnet"]),
            new("acp:antigravity", true, "1", null, ["gemini-4-neon"]),
            new("acp:opencode", true, "1", null, ["opencode/default"])
        ];

        var area = await ctx.Kernel.CreateAreaAsync(new CreateAreaRequest("Flow Permission Inheritance", DefaultCwd: "/workspace/demo"));
        var snap = await ctx.Kernel.CreateTrackAsync(new CreateTrackRequest(
            AreaId: area.Id,
            Title: "Inheritance Test",
            Cwd: "/workspace/demo",
            Provider: leadProvider,
            PermissionMode: leadPermissionMode));
        var trackId = snap.Track.Id;
        await ctx.Kernel.Actors.GetOrActivate(trackId).EnsureMountedAsync();

        var commit = await ctx.Kernel.CommitTrackFlowAsync(trackId, dryRun: false, expectedFlowRev: 0, Human, content: $$$"""
            {"rev":0,"nodes":[{"id":"worker","do":{"kind":"run","access":"{{{access}}}","provider":"{{{workerProvider}}}","prompt":"Execute"}}]}
            """);
        Assert.True(commit.Ok, JsonSerializer.Serialize(commit.Diagnostics));
        await ctx.Kernel.WaitForTrackIdleAsync(trackId);

        var workerLaunch = Assert.Single(ctx.AgentRuntime.OpenedSessions, s =>
            s.LaunchSpec.SystemPrompt?.Contains("BoundTaskId=", StringComparison.Ordinal) == true);
        Assert.Equal(expectedWorkerMode, workerLaunch.LaunchSpec.PermissionMode);
    }

    [Fact]
    public async Task SessionCreateInheritsCrossProviderFullAccessFromAgyLeadAndFailsClosedFromAcceptEdits()
    {
        await using var ctx = new KernelTestContext();
        ctx.AgentRuntime.Providers =
        [
            new("codex", true, "1", null, ["gpt-5"]),
            new("claude", true, "1", null, ["sonnet"]),
            new("acp:antigravity", true, "1", null, ["gemini-4-neon"])
        ];

        var area = await ctx.Kernel.CreateAreaAsync(new CreateAreaRequest("Collaborator Inheritance", DefaultCwd: "/workspace/demo"));
        var agyTrack = await ctx.Kernel.CreateTrackAsync(new CreateTrackRequest(
            AreaId: area.Id, Title: "AGY Lead", Cwd: "/workspace/demo", Provider: "acp:antigravity"));
        await ctx.Kernel.Actors.GetOrActivate(agyTrack.Track.Id).EnsureMountedAsync();
        var agyCaller = await ctx.OpenMcpCallerAsync(agyTrack.Track.LeadSessionId!);
        var agyResult = await ctx.Kernel.InvokeKernelMcpToolAsync(agyCaller, KernelMcpToolNames.SessionCreate,
            TestJson.Parse("""{"title":"Codex Helper","provider":"codex","requestKey":"req-agy-codex"}"""));
        Assert.Equal("full", agyResult.GetProperty("session").GetProperty("permissionMode").GetString());

        var claudeTrack = await ctx.Kernel.CreateTrackAsync(new CreateTrackRequest(
            AreaId: area.Id, Title: "Claude Lead", Cwd: "/workspace/demo", Provider: "claude", PermissionMode: "acceptEdits"));
        await ctx.Kernel.Actors.GetOrActivate(claudeTrack.Track.Id).EnsureMountedAsync();
        var claudeCaller = await ctx.OpenMcpCallerAsync(claudeTrack.Track.LeadSessionId!);
        var claudeResult = await ctx.Kernel.InvokeKernelMcpToolAsync(claudeCaller, KernelMcpToolNames.SessionCreate,
            TestJson.Parse("""{"title":"Codex Helper","provider":"codex","requestKey":"req-claude-codex"}"""));
        Assert.Equal("never", claudeResult.GetProperty("session").GetProperty("permissionMode").GetString());
    }

    [Fact]
    public async Task LaterWaveNonReuseWorkerAppliesClearedPermissionMode()
    {
        var clock = new ControllableTimeProvider();
        await using var ctx = new KernelTestContext(timeProvider: clock);
        ctx.AgentRuntime.Providers = [new("codex", true, "1", null, ["gpt-5"])];
        var trackId = (await ctx.CreateDefaultTrackAsync()).TrackSnap.Track.Id;
        var probeHits = 0;
        ctx.CommandRunner.Handler = (_, _) =>
        {
            var n = Interlocked.Increment(ref probeHits);
            return Task.FromResult(new ShellCommandResult(n <= 2 ? 0 : 75, $"wave {n}", "", false, 1));
        };

        var commit1 = await ctx.Kernel.CommitTrackFlowAsync(trackId, false, 0, Human, content: """
            {
              "rev": 0,
              "nodes": [
                {"id": "watch", "do": {"kind": "watch", "cmd": "probe", "repeat": true, "interval": "5s"}},
                {"id": "worker", "when": {"after": ["watch"]}, "do": {"kind": "run", "access": "read_only", "provider": "codex", "permissionMode": "full", "prompt": "Wave 1"}}
              ]
            }
            """);
        Assert.True(commit1.Ok, JsonSerializer.Serialize(commit1.Diagnostics));
        await WaitUntilAsync(() => Task.FromResult(ctx.AgentRuntime.OpenedSessions.Any(s => s.LaunchSpec.PermissionMode == "full")));
        var wave1Task = Assert.Single((await ctx.Store.LoadTrackAggregateAsync(trackId, default))!.Tasks, t => t.TaskKey == "worker");
        await ctx.Kernel.CompleteTaskDoneAsync(trackId, wave1Task.SessionId!, outcome: FlowOutcome.Ok, summary: "done 1");
        await WaitUntilAsync(async () =>
            Assert.Single((await ctx.Store.LoadTrackAggregateAsync(trackId, default))!.Tasks, t => t.TaskKey == "worker").Status == TaskRunStatus.Done);

        var rev = (await ctx.Kernel.GetTrackFlowAsync(trackId)).FlowRev;
        var commit2 = await ctx.Kernel.CommitTrackFlowAsync(trackId, false, rev, Human, content: $$$"""
            {
              "rev": {{{rev}}},
              "nodes": [
                {"id": "watch", "do": {"kind": "watch", "cmd": "probe", "repeat": true, "interval": "5s"}},
                {"id": "worker", "when": {"after": ["watch"]}, "do": {"kind": "run", "access": "read_only", "provider": "codex", "prompt": "Wave 2"}}
              ]
            }
            """);
        Assert.True(commit2.Ok, JsonSerializer.Serialize(commit2.Diagnostics));
        await WaitUntilAsync(() => Task.FromResult(clock.HasTimerDueAt(clock.GetUtcNow() + TimeSpan.FromSeconds(5))));
        clock.Advance(TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => Task.FromResult(ctx.AgentRuntime.OpenedSessions.Count(s => s.LaunchSpec.StateKey == wave1Task.SessionId) >= 2));

        var wave2Launch = ctx.AgentRuntime.OpenedSessions.Last(s => s.LaunchSpec.StateKey == wave1Task.SessionId).LaunchSpec;
        Assert.Equal("never", wave2Launch.PermissionMode);
    }

    [Fact]
    public async Task TrackCreationRejectsInvalidTemplateSeededPermissionModeAgainstDefaultProvider()
    {
        await using var ctx = new KernelTestContext(policyVm: new Perga.PolicyVm.PolicyVm());
        ctx.AgentRuntime.Providers = [new("codex", true, "1", null, ["gpt-5"])];
        ctx.Kernel.Templates.RegisterTemplate("""
            export default {
              id: "bad-perm-seed",
              title: "Bad Perm Seed",
              flow: [{ id: "seed-node", do: { kind: "run", permissionMode: "bypassPermissions", prompt: "Run" } }]
            };
            """);

        var area = await ctx.Kernel.CreateAreaAsync(new CreateAreaRequest("Seed Validation", DefaultCwd: "/workspace/demo"));
        var ex = await Assert.ThrowsAsync<KernelException>(() => ctx.Kernel.CreateTrackAsync(new CreateTrackRequest(
            AreaId: area.Id,
            Title: "Seeded Invalid Perm",
            Cwd: "/workspace/demo",
            Provider: "codex",
            TemplateId: "bad-perm-seed")));
        Assert.Equal(KernelErrorCode.InvalidArgument, ex.Code);
        Assert.Contains("permissionMode", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FlowProjectionPreservesBoundAcpSessionNullPermissionModeAfterReset()
    {
        await using var ctx = new KernelTestContext();
        ctx.AgentRuntime.Providers =
        [
            new("acp:custom", true, "1", null, ["m1"], PermissionModes:
            [
                new("inspect", "Inspect", "", "neutral", true),
                new("act", "Act", "", "warning")
            ])
        ];
        var area = await ctx.Kernel.CreateAreaAsync(new CreateAreaRequest("Projection Null Perm", DefaultCwd: "/workspace/demo"));
        var snap = await ctx.Kernel.CreateTrackAsync(new CreateTrackRequest(
            AreaId: area.Id, Title: "Projection Null", Cwd: "/workspace/demo", Provider: "acp:custom"));
        var trackId = snap.Track.Id;
        await ctx.Kernel.Actors.GetOrActivate(trackId).EnsureMountedAsync();

        var commit = await ctx.Kernel.CommitTrackFlowAsync(trackId, dryRun: false, expectedFlowRev: 0, Human, content: """
            {"rev":0,"nodes":[{"id":"acp-node","do":{"kind":"run","provider":"acp:custom","permissionMode":"act","prompt":"Inspect"}}]}
            """);
        Assert.True(commit.Ok, JsonSerializer.Serialize(commit.Diagnostics));
        await ctx.Kernel.WaitForTrackIdleAsync(trackId);

        var task = Assert.Single(await ctx.Kernel.ListTasksAsync(trackId), t => t.TaskKey == "acp-node");
        Assert.Equal("act", Assert.Single((await ctx.Kernel.GetTrackFlowAsync(trackId)).Projection.Nodes).PermissionMode);

        // Reset the bound ACP session's permissionMode to null; projection must report null, not the stale declaration.
        await ctx.Kernel.UpdateSessionAsync(task.SessionId!, new UpdateSessionRequest(PermissionMode: new(null)), Human, default);
        var afterReset = Assert.Single((await ctx.Kernel.GetTrackFlowAsync(trackId)).Projection.Nodes, n => n.Id == "acp-node");
        Assert.Null(afterReset.PermissionMode);
    }

    [Fact]
    public async Task FlowCommitValidatesDynamicAcpPermissionCatalogAndDispatchRejectsPostCommitReuseDrift()
    {
        await using var ctx = new KernelTestContext();
        ctx.AgentRuntime.Providers =
        [
            new("codex", true, "1", null, ["gpt-5"]),
            new("acp:custom", true, "1", null, ["m1"], PermissionModes:
            [
                new("inspect", "Inspect", "", "neutral", true),
                new("act", "Act", "", "warning")
            ])
        ];
        var (_, snap) = await ctx.CreateDefaultTrackAsync();
        var trackId = snap.Track.Id;

        var badDynamic = await ctx.Kernel.CommitTrackFlowAsync(trackId, dryRun: true, expectedFlowRev: 0, Human, content: """
            {"rev":0,"nodes":[{"id":"dyn-bad","do":{"kind":"run","provider":"acp:custom","permissionMode":"unknown","prompt":"Run"}}]}
            """);
        Assert.False(badDynamic.Ok);
        Assert.Contains(badDynamic.Diagnostics, d => d.Path == "$.nodes[0].do.permissionMode");

        var sub = (await ctx.Kernel.CreateSessionAsync(trackId,
            new CreateSessionRequest(Title: "Reusable", Provider: "codex", PermissionMode: "never"),
            "req-drift-sub", Human, default)).Receipt.Session;

        var commit = await ctx.Kernel.CommitTrackFlowAsync(trackId, dryRun: false, expectedFlowRev: 0, Human, content: $$$"""
            {
              "rev": 0,
              "nodes": [
                {"id": "gate-step", "do": {"kind": "run", "access": "read_only", "prompt": "Wait"}},
                {"id": "reuse-step", "when": {"after": ["gate-step"]}, "do": {"kind": "run", "session": "{{{sub.Id}}}", "permissionMode": "never", "prompt": "Reuse"}}
              ]
            }
            """);
        Assert.True(commit.Ok, JsonSerializer.Serialize(commit.Diagnostics));
        await WaitUntilAsync(async () => (await ctx.Kernel.ListTasksAsync(trackId)).Any(t => t.TaskKey == "gate-step" && t.SessionId is not null));
        var gateTask = Assert.Single(await ctx.Kernel.ListTasksAsync(trackId), t => t.TaskKey == "gate-step");

        // Mutate reused session's permissionMode before reuse-step is dispatched.
        await ctx.Kernel.UpdateSessionAsync(sub.Id, new UpdateSessionRequest(PermissionMode: new("full")), Human, default);
        await ctx.Kernel.CompleteTaskDoneAsync(trackId, gateTask.SessionId!, outcome: FlowOutcome.Ok);

        await WaitUntilAsync(async () =>
            Assert.Single(await ctx.Kernel.ListTasksAsync(trackId), t => t.TaskKey == "reuse-step").Status == TaskRunStatus.Failed);
        var failed = Assert.Single(await ctx.Kernel.ListTasksAsync(trackId), t => t.TaskKey == "reuse-step");
        Assert.Contains("permissionMode", failed.StatusDetail ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DefaultProviderStaticExplicitPermissionModeDispatchesWithoutProviderProbe()
    {
        await using var ctx = new KernelTestContext();
        ctx.AgentRuntime.Providers = [new("codex", true, "1", null, ["gpt-5"])];
        var (_, snap) = await ctx.CreateDefaultTrackAsync();
        var trackId = snap.Track.Id;

        var commit = await ctx.Kernel.CommitTrackFlowAsync(trackId, dryRun: false, expectedFlowRev: 0, Human, content: """
            {
              "rev": 0,
              "nodes": [
                {"id": "first", "do": {"kind": "run", "access": "read_only", "prompt": "Hold"}},
                {"id": "second", "when": {"after": ["first"]}, "do": {"kind": "run", "permissionMode": "full", "prompt": "Run"}}
              ]
            }
            """);
        Assert.True(commit.Ok, JsonSerializer.Serialize(commit.Diagnostics));
        await WaitUntilAsync(async () => (await ctx.Kernel.ListTasksAsync(trackId)).Any(t => t.TaskKey == "first" && t.SessionId is not null));
        var first = Assert.Single(await ctx.Kernel.ListTasksAsync(trackId), t => t.TaskKey == "first");

        // Simulate provider probe outage before dispatching 'second' (default provider + static permissionMode).
        ctx.AgentRuntime.ProbeScript = _ => throw new InvalidOperationException("probe unavailable");
        await ctx.Kernel.CompleteTaskDoneAsync(trackId, first.SessionId!, outcome: FlowOutcome.Ok);
        await WaitUntilAsync(() => Task.FromResult(ctx.AgentRuntime.OpenedSessions.Any(s => s.LaunchSpec.PermissionMode == "full")));

        var secondLaunch = ctx.AgentRuntime.OpenedSessions.Last(s => s.LaunchSpec.PermissionMode == "full").LaunchSpec;
        Assert.Equal("full", secondLaunch.PermissionMode);
    }

    [Fact]
    public async Task StaticPermissionModeFlowCommitAndDryRunSucceedDuringProviderProbeOutage()
    {
        await using var ctx = new KernelTestContext();
        var (_, snap) = await ctx.CreateDefaultTrackAsync();
        var trackId = snap.Track.Id;

        var probeCalls = 0;
        ctx.AgentRuntime.ProbeScript = _ =>
        {
            probeCalls++;
            throw new InvalidOperationException("probe unavailable");
        };

        var invalid = await ctx.Kernel.CommitTrackFlowAsync(trackId, dryRun: true, expectedFlowRev: 0, Human, content: """
            {"rev":0,"nodes":[{"id":"bad","do":{"kind":"run","permissionMode":"bogus","prompt":"Run"}}]}
            """);
        Assert.False(invalid.Ok);
        Assert.Contains(invalid.Diagnostics, d => d.Path == "$.nodes[0].do.permissionMode");
        Assert.Equal(0, probeCalls);

        var dryRun = await ctx.Kernel.CommitTrackFlowAsync(trackId, dryRun: true, expectedFlowRev: 0, Human, content: """
            {"rev":0,"nodes":[{"id":"good","do":{"kind":"run","permissionMode":"full","prompt":"Run"}}]}
            """);
        Assert.True(dryRun.Ok, JsonSerializer.Serialize(dryRun.Diagnostics));
        Assert.Equal(0, probeCalls);

        var commit = await ctx.Kernel.CommitTrackFlowAsync(trackId, dryRun: false, expectedFlowRev: 0, Human, content: """
            {"rev":0,"nodes":[{"id":"good","do":{"kind":"run","permissionMode":"full","prompt":"Run"}}]}
            """);
        Assert.True(commit.Ok, JsonSerializer.Serialize(commit.Diagnostics));
        await WaitUntilAsync(() => Task.FromResult(ctx.AgentRuntime.OpenedSessions.Any(s => s.LaunchSpec.PermissionMode == "full")));
        Assert.Equal(0, probeCalls);
    }

    [Fact]
    public async Task ReadOnlyFlowReuse_RejectsImplicitElevatedSessionAtCommitAndDispatch()
    {
        await using var ctx = new KernelTestContext();
        var (_, snap) = await ctx.CreateDefaultTrackAsync();
        var trackId = snap.Track.Id;

        var fullSub = (await ctx.Kernel.CreateSessionAsync(trackId,
            new CreateSessionRequest(Title: "FullSub", Provider: "codex", PermissionMode: "full"),
            "req-ro-full-sub", Human, default)).Receipt.Session;

        var implicitRejected = await ctx.Kernel.CommitTrackFlowAsync(trackId, dryRun: true, expectedFlowRev: 0, Human, content: $$$"""
            {"rev":0,"nodes":[{"id":"ro-reuse","do":{"kind":"run","access":"read_only","session":"{{{fullSub.Id}}}","prompt":"Inspect"}}]}
            """);
        Assert.False(implicitRejected.Ok);
        Assert.Contains(implicitRejected.Diagnostics, d => d.Path == "$.nodes[0].do.permissionMode");

        var explicitAllowed = await ctx.Kernel.CommitTrackFlowAsync(trackId, dryRun: true, expectedFlowRev: 0, Human, content: $$$"""
            {"rev":0,"nodes":[{"id":"ro-reuse","do":{"kind":"run","access":"read_only","session":"{{{fullSub.Id}}}","permissionMode":"full","prompt":"Inspect"}}]}
            """);
        Assert.True(explicitAllowed.Ok, JsonSerializer.Serialize(explicitAllowed.Diagnostics));

        var safeSub = (await ctx.Kernel.CreateSessionAsync(trackId,
            new CreateSessionRequest(Title: "SafeSub", Provider: "codex", PermissionMode: "never"),
            "req-ro-safe-sub", Human, default)).Receipt.Session;

        var commit = await ctx.Kernel.CommitTrackFlowAsync(trackId, dryRun: false, expectedFlowRev: 0, Human, content: $$$"""
            {
              "rev": 0,
              "nodes": [
                {"id": "hold", "do": {"kind": "run", "access": "read_only", "prompt": "Wait"}},
                {"id": "ro-drift", "when": {"after": ["hold"]}, "do": {"kind": "run", "access": "read_only", "session": "{{{safeSub.Id}}}", "prompt": "Inspect"}}
              ]
            }
            """);
        Assert.True(commit.Ok, JsonSerializer.Serialize(commit.Diagnostics));
        await WaitUntilAsync(async () => (await ctx.Kernel.ListTasksAsync(trackId)).Any(t => t.TaskKey == "hold" && t.SessionId is not null));
        var hold = Assert.Single(await ctx.Kernel.ListTasksAsync(trackId), t => t.TaskKey == "hold");

        await ctx.Kernel.UpdateSessionAsync(safeSub.Id, new UpdateSessionRequest(PermissionMode: new("full")), Human, default);
        await ctx.Kernel.CompleteTaskDoneAsync(trackId, hold.SessionId!, outcome: FlowOutcome.Ok);

        await WaitUntilAsync(async () =>
            Assert.Single(await ctx.Kernel.ListTasksAsync(trackId), t => t.TaskKey == "ro-drift").Status == TaskRunStatus.Failed);
        var failed = Assert.Single(await ctx.Kernel.ListTasksAsync(trackId), t => t.TaskKey == "ro-drift");
        Assert.Contains("elevated permission mode", failed.StatusDetail ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(ctx.AgentRuntime.OpenedSessions, s => s.LaunchSpec.PermissionMode == "full");
    }

    [Fact]
    public async Task DispatchApprovalDrift_ReevaluatesWhenLeadPermissionModeEscalatesDuringPendingApproval()
    {
        await using var ctx = new KernelTestContext(policyVm: new global::Perga.PolicyVm.PolicyVm());
        var (_, snap) = await ctx.CreateDefaultTrackAsync();
        var trackId = snap.Track.Id;
        var leadId = snap.Track.LeadSessionId!;

        var caller = await ctx.OpenMcpCallerAsync(leadId);
        await KernelMcpDispatcher.ExecuteGuardedAsync(
            ctx.Kernel,
            caller,
            KernelMcpToolNames.TrackSet,
            JsonSerializer.SerializeToElement(new
            {
                source = """
                    export default {
                      id: 'perm-drift-guard',
                      title: 'Permission Drift Guard',
                      before(action) {
                        if (action.type !== 'task.dispatch') return Allow();
                        if (action.permissionMode === 'full') return Deny('Full access blocked by policy');
                        return AskHuman('Approve dispatch?', { approve: 'Approve', deny: 'Reject' });
                      }
                    };
                    """
            }));

        var commit = await ctx.Kernel.CommitTrackFlowAsync(trackId, dryRun: false, expectedFlowRev: 0, Human, content: """
            {"rev":0,"nodes":[{"id":"guarded-rw","do":{"kind":"run","access":"read_write","prompt":"Edit"}}]}
            """);
        Assert.True(commit.Ok, JsonSerializer.Serialize(commit.Diagnostics));

        await WaitUntilAsync(async () => (await ctx.Kernel.GetTrackAggregateAsync(trackId)).PendingApprovals.Count == 1);
        var approval = Assert.Single((await ctx.Kernel.GetTrackAggregateAsync(trackId)).PendingApprovals);

        // Escalate Lead permissionMode from "never" to "full" while human approval is pending.
        await ctx.Kernel.UpdateSessionAsync(leadId, new UpdateSessionRequest(PermissionMode: new("full")), Human, default);

        // Approve the stale ask that was requested under "never".
        await ctx.Kernel.RespondInteractionAsync(
            approval.SessionId,
            approval.Id,
            new(ExecutionId: approval.ExecutionId, OptionId: approval.Options.Single(o => o.IsApproval).Id),
            Human,
            default);

        await WaitUntilAsync(async () =>
            Assert.Single(await ctx.Kernel.ListTasksAsync(trackId), t => t.TaskKey == "guarded-rw").Status == TaskRunStatus.Failed);
        var failed = Assert.Single(await ctx.Kernel.ListTasksAsync(trackId), t => t.TaskKey == "guarded-rw");
        Assert.Contains("Full access blocked by policy", failed.StatusDetail ?? "", StringComparison.Ordinal);
        Assert.Null(failed.SessionId);
        Assert.Single(await ctx.Kernel.ListSessionsAsync(trackId, new(IncludeArchived: true), Human, default));
    }

    [Fact]
    public async Task DispatchApprovalDrift_ReevaluatesWhenLeadModelChangesDuringPendingApproval()
    {
        await using var ctx = new KernelTestContext(policyVm: new global::Perga.PolicyVm.PolicyVm());
        ctx.AgentRuntime.Providers = [new("codex", true, "1", null, ["gpt-5", "gpt-6"])];
        var (_, snap) = await ctx.CreateDefaultTrackAsync();
        var trackId = snap.Track.Id;
        var leadId = snap.Track.LeadSessionId!;

        await ctx.Kernel.UpdateSessionAsync(leadId, new UpdateSessionRequest(Model: new("gpt-5")), Human, default);
        var caller = await ctx.OpenMcpCallerAsync(leadId);
        await KernelMcpDispatcher.ExecuteGuardedAsync(
            ctx.Kernel,
            caller,
            KernelMcpToolNames.TrackSet,
            JsonSerializer.SerializeToElement(new
            {
                source = """
                    export default {
                      id: 'model-drift-guard',
                      title: 'Model Drift Guard',
                      before(action) {
                        if (action.type !== 'task.dispatch') return Allow();
                        return AskHuman('Approve dispatch?', { approve: 'Approve', deny: 'Reject' });
                      }
                    };
                    """
            }));

        var commit = await ctx.Kernel.CommitTrackFlowAsync(trackId, dryRun: false, expectedFlowRev: 0, Human, content: """
            {"rev":0,"nodes":[{"id":"model-drift","do":{"kind":"run","access":"read_write","prompt":"Edit"}}]}
            """);
        Assert.True(commit.Ok, JsonSerializer.Serialize(commit.Diagnostics));

        await WaitUntilAsync(async () => (await ctx.Kernel.GetTrackAggregateAsync(trackId)).PendingApprovals.Count == 1);
        var firstApproval = Assert.Single((await ctx.Kernel.GetTrackAggregateAsync(trackId)).PendingApprovals);

        // Change Lead model while approval is pending; the worker would inherit the new model.
        await ctx.Kernel.UpdateSessionAsync(leadId, new UpdateSessionRequest(Model: new("gpt-6")), Human, default);

        // Approving the stale gpt-5 ask must NOT launch a gpt-6 worker; it must request a fresh approval.
        await ctx.Kernel.RespondInteractionAsync(
            firstApproval.SessionId,
            firstApproval.Id,
            new(ExecutionId: firstApproval.ExecutionId, OptionId: firstApproval.Options.Single(o => o.IsApproval).Id),
            Human,
            default);

        await WaitUntilAsync(async () =>
            (await ctx.Kernel.GetTrackAggregateAsync(trackId)).PendingApprovals.Any(a => a.Id != firstApproval.Id));
        var secondApproval = Assert.Single((await ctx.Kernel.GetTrackAggregateAsync(trackId)).PendingApprovals);
        Assert.NotEqual(firstApproval.Id, secondApproval.Id);
        Assert.Equal(TaskRunStatus.Pending, Assert.Single(await ctx.Kernel.ListTasksAsync(trackId), t => t.TaskKey == "model-drift").Status);
    }

    [Fact]
    public async Task HostCommandDispatch_ReportsNullPermissionModeEvenWithFullLead()
    {
        await using var ctx = new KernelTestContext(policyVm: new global::Perga.PolicyVm.PolicyVm());
        var (_, snap) = await ctx.CreateDefaultTrackAsync();
        var trackId = snap.Track.Id;
        var leadId = snap.Track.LeadSessionId!;

        await ctx.Kernel.UpdateSessionAsync(leadId, new UpdateSessionRequest(PermissionMode: new("full")), Human, default);
        var caller = await ctx.OpenMcpCallerAsync(leadId);
        await KernelMcpDispatcher.ExecuteGuardedAsync(
            ctx.Kernel,
            caller,
            KernelMcpToolNames.TrackSet,
            JsonSerializer.SerializeToElement(new
            {
                source = """
                    export default {
                      id: 'deny-full-agent',
                      title: 'Deny Full Agent Only',
                      before(action) {
                        if (action.type === 'task.dispatch' && action.permissionMode != null) {
                          return Deny('Unexpected permissionMode on host command: ' + action.permissionMode);
                        }
                        return Allow();
                      }
                    };
                    """
            }));

        var commit = await ctx.Kernel.CommitTrackFlowAsync(trackId, dryRun: false, expectedFlowRev: 0, Human, content: """
            {
              "rev": 0,
              "nodes": [
                {"id":"shell-step","do":{"kind":"run","cmd":"echo ok","access":"read_write"}},
                {"id":"watch-step","do":{"kind":"watch","cmd":"echo ok","interval":"5s"}}
              ]
            }
            """);
        Assert.True(commit.Ok, JsonSerializer.Serialize(commit.Diagnostics));

        await WaitUntilAsync(async () =>
        {
            var tasks = await ctx.Kernel.ListTasksAsync(trackId);
            return tasks.Single(t => t.TaskKey == "shell-step").Status == TaskRunStatus.Done &&
                   tasks.Single(t => t.TaskKey == "watch-step").Status == TaskRunStatus.Done;
        });
        var tasks = await ctx.Kernel.ListTasksAsync(trackId);
        Assert.Equal(FlowOutcome.Ok, tasks.Single(t => t.TaskKey == "shell-step").Outcome);
        Assert.Equal(FlowOutcome.Ok, tasks.Single(t => t.TaskKey == "watch-step").Outcome);
    }

    [Fact]
    public async Task DispatchApprovalDrift_ReevaluatesWhenReusedSessionModelResetsToNull()
    {
        await using var ctx = new KernelTestContext(policyVm: new global::Perga.PolicyVm.PolicyVm());
        ctx.AgentRuntime.Providers = [new("codex", true, "1", null, ["gpt-5", "gpt-6"])];
        var (_, snap) = await ctx.CreateDefaultTrackAsync();
        var trackId = snap.Track.Id;
        var leadId = snap.Track.LeadSessionId!;

        await ctx.Kernel.UpdateSessionAsync(leadId, new UpdateSessionRequest(Model: new("gpt-5")), Human, default);
        var reusable = (await ctx.Kernel.CreateSessionAsync(trackId,
            new CreateSessionRequest(Title: "Reusable", Provider: "codex", Model: "gpt-5", PermissionMode: "never"),
            "req-reuse-null-model", Human, default)).Receipt.Session;

        var caller = await ctx.OpenMcpCallerAsync(leadId);
        await KernelMcpDispatcher.ExecuteGuardedAsync(
            ctx.Kernel,
            caller,
            KernelMcpToolNames.TrackSet,
            JsonSerializer.SerializeToElement(new
            {
                source = """
                    export default {
                      id: 'reuse-null-model-guard',
                      title: 'Reuse Null Model Guard',
                      before(action) {
                        if (action.type !== 'task.dispatch') return Allow();
                        if (action.model == null) return Deny('Provider-default null model blocked');
                        return AskHuman('Approve dispatch?', { approve: 'Approve', deny: 'Reject' });
                      }
                    };
                    """
            }));

        var commit = await ctx.Kernel.CommitTrackFlowAsync(trackId, dryRun: false, expectedFlowRev: 0, Human, content: $$$"""
            {"rev":0,"nodes":[{"id":"reuse-model-drift","do":{"kind":"run","session":"{{{reusable.Id}}}","access":"read_write","prompt":"Edit"}}]}
            """);
        Assert.True(commit.Ok, JsonSerializer.Serialize(commit.Diagnostics));

        await WaitUntilAsync(async () => (await ctx.Kernel.GetTrackAggregateAsync(trackId)).PendingApprovals.Count == 1);
        var firstApproval = Assert.Single((await ctx.Kernel.GetTrackAggregateAsync(trackId)).PendingApprovals);

        // Reset reused session's model from "gpt-5" (matching Lead) to null (provider default) while approval is pending.
        await ctx.Kernel.UpdateSessionAsync(reusable.Id, new UpdateSessionRequest(Model: new((string?)null)), Human, default);

        // Approving the stale gpt-5 ask must NOT launch under model=null; policy must re-evaluate and deny it.
        await ctx.Kernel.RespondInteractionAsync(
            firstApproval.SessionId,
            firstApproval.Id,
            new(ExecutionId: firstApproval.ExecutionId, OptionId: firstApproval.Options.Single(o => o.IsApproval).Id),
            Human,
            default);

        await WaitUntilAsync(async () =>
            Assert.Single(await ctx.Kernel.ListTasksAsync(trackId), t => t.TaskKey == "reuse-model-drift").Status == TaskRunStatus.Failed);
        var failed = Assert.Single(await ctx.Kernel.ListTasksAsync(trackId), t => t.TaskKey == "reuse-model-drift");
        Assert.Contains("Provider-default null model blocked", failed.StatusDetail ?? "", StringComparison.Ordinal);
        Assert.DoesNotContain(ctx.AgentRuntime.OpenedSessions, s => s.LaunchSpec.Model is null);
    }
}
