using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Perga.AgentKit;
using Perga.Contracts;
using Perga.Host.IntegrationTests.Fixtures;
using Perga.Host.Serialization;
using Perga.Host.SystemUpgrade;
using Perga.Kernel;
using Perga.Kernel.Mcp;
using Perga.Kernel.Ports;

namespace Perga.Host.IntegrationTests;

public sealed class SystemUpgradeTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DrainRejectsAcpPermissionProbes(bool createTrack)
    {
        await using var host = new PergaHostTestCluster();
        var (track, lead) = await host.CreateTrackAsync();
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        await kernel.PatchSessionAsync(lead, new(Provider: "acp:antigravity"));
        var areaId = (await kernel.ListTracksAsync()).Single(t => t.Id == track).AreaId;
        var probes = 0;
        host.AgentRuntime.PermissionProbeHandler = _ =>
        {
            Interlocked.Increment(ref probes);
            return Task.FromResult<IReadOnlyList<PermissionModeOption>>([new("act", "Act", "", "warning")]);
        };
        kernel.DispatchAdmission.SetDraining(true);
        var error = await Assert.ThrowsAsync<KernelException>(async () =>
        {
            if (createTrack)
                await kernel.CreateTrackAsync(new(areaId, Cwd: host.DataDir, Provider: "acp:antigravity", PermissionMode: "act")).WaitAsync(Deadline);
            else
                await kernel.SetSessionPermissionModeAsync(lead, "act").WaitAsync(Deadline);
        });
        Assert.Equal(KernelErrorCode.Conflict, error.Code);
        Assert.Equal(0, probes);
        Assert.Equal(0, kernel.DispatchAdmission.ActiveDispatches);
        Assert.Empty(host.AgentRuntime.OpenedSessions);
        Assert.Null(Assert.Single(await kernel.ListSessionsAsync(track).WaitAsync(Deadline)).PermissionMode);
        Assert.Single(await kernel.ListTracksAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AcpPermissionProbeCountsUntilItSettlesAcrossDrain(bool createTrack)
    {
        await using var host = new PergaHostTestCluster();
        var (track, lead) = await host.CreateTrackAsync();
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        await kernel.PatchSessionAsync(lead, new(Provider: "acp:antigravity"));
        var areaId = (await kernel.ListTracksAsync()).Single(t => t.Id == track).AreaId;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.AgentRuntime.PermissionProbeHandler = async ct =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(ct);
            return [new("act", "Act", "", "warning")];
        };
        Task operation = createTrack
            ? kernel.CreateTrackAsync(new(areaId, Cwd: host.DataDir, Provider: "acp:antigravity", PermissionMode: "act"))
            : kernel.SetSessionPermissionModeAsync(lead, "act");
        try
        {
            await entered.Task.WaitAsync(Deadline);
            Assert.Equal(1, kernel.DispatchAdmission.ActiveDispatches);
            kernel.DispatchAdmission.SetDraining(true);
            Assert.Equal(1, (await Info(host).WaitAsync(Deadline)).ActiveDispatches);
            Assert.False(operation.IsCompleted);
        }
        finally { release.TrySetResult(); }
        await operation.WaitAsync(Deadline);
        Assert.Equal(0, kernel.DispatchAdmission.ActiveDispatches);
    }

    [Fact]
    public async Task IdleModelChangeDuringDrainDefersProviderRecreation()
    {
        await using var host = new PergaHostTestCluster();
        var (track, lead) = await host.CreateTrackAsync();
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        await kernel.AcceptMessageAsync(lead, new SendMessageRequest("prime cache", "model-cache"), default);
        await kernel.WaitForTrackIdleAsync(track).WaitAsync(Deadline);
        var opened = host.AgentRuntime.OpenedSessions.Count;
        Assert.True(opened > 0);
        kernel.DispatchAdmission.SetDraining(true);
        var updated = await kernel.UpdateSessionModelAsync(lead, new("new-model", "high")).WaitAsync(Deadline);
        Assert.Equal("new-model", updated.Model);
        Assert.Equal(0, kernel.DispatchAdmission.ActiveDispatches);
        Assert.Equal(opened, host.AgentRuntime.OpenedSessions.Count);
        kernel.DispatchAdmission.SetDraining(false);
        await kernel.AcceptMessageAsync(lead, new SendMessageRequest("reuse cache", "model-reuse"), default);
        await kernel.WaitForTrackIdleAsync(track).WaitAsync(Deadline);
        Assert.Equal(opened + 1, host.AgentRuntime.OpenedSessions.Count);
        Assert.Equal("new-model", host.AgentRuntime.OpenedSessions.Last().Spec.Model);
    }

    [Fact]
    public async Task ReceiptWakeRetriesAfterStreamFailsBeforeAcknowledgement()
    {
        var source = new MutableReceipt();
        await using var host = new PergaHostTestCluster(configureExtraServices: services =>
            services.AddSingleton<ISystemUpgradeReceiptSource>(source));
        var (track, _) = await host.CreateTrackAsync();
        var calls = 0;
        host.AgentRuntime.SetTurnHandler(FailFirst);
        async IAsyncEnumerable<UnifiedAgentEvent> FailFirst(HarnessLaunchSpec spec, TurnInput input, [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield();
            if (Interlocked.Increment(ref calls) == 1) throw new IOException("before first acknowledgement");
            yield return new UnifiedAgentEvent.TurnCompleted(new TokenUsage(1, 1, 0, 100000));
        }
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        source.Value = new("retry-receipt", "old", "new", "succeeded", null, null, track, 1795080000.125, []);
        await kernel.UpgradeReceipts.ImportAsync(default);
        await using var db = Open(host.DataDir);
        await Until(async () => (await db.ExecuteScalarAsync<string>("SELECT notification_status FROM system_upgrade_receipts WHERE job_id = 'retry-receipt';"))?.StartsWith("delivery_failed:", StringComparison.Ordinal) == true);
        Assert.Equal(1, await db.ExecuteScalarAsync<int>("SELECT wake_pending FROM mails WHERE id = 'system-upgrade:retry-receipt';"));
        await Until(async () =>
        {
            await kernel.UpgradeReceipts.ImportAsync(default);
            return await db.ExecuteScalarAsync<int>("SELECT wake_pending FROM mails WHERE id = 'system-upgrade:retry-receipt';") == 0;
        });
        await kernel.WaitForTrackIdleAsync(track).WaitAsync(Deadline);
        Assert.Equal(2, Volatile.Read(ref calls));
        Assert.Single(await kernel.ListMailsAsync(track));
    }

    [Fact]
    public async Task InfoDrainAndResumePreserveAcceptedMessages()
    {
        await using var host = new PergaHostTestCluster();
        var (track, lead) = await host.CreateTrackAsync();
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        using var sub = kernel.EventBus.Subscribe();
        using var infoResponse = await host.Client.GetAsync("/api/v1/system/info");
        Assert.Equal("no-store", infoResponse.Headers.CacheControl!.ToString());
        var info = (await infoResponse.Content.ReadFromJsonAsync<SystemInfoDto>(PergaJsonDefaults.ApiOptions))!;
        Assert.Equal(11, info.SchemaVersion);
        Assert.False(info.Draining);
        using var draining = await host.Client.PostAsJsonAsync("/api/v1/system/drain", new { draining = true });
        Assert.Equal(HttpStatusCode.Accepted, draining.StatusCode);
        var accepted = await kernel.AcceptMessageAsync(lead, new SendMessageRequest("during drain", IdempotencyKey: "drain-input"), default);
        Assert.Equal("accepted", accepted.Receipt!.DeliveryState);
        Assert.Empty(host.AgentRuntime.OpenedSessions);
        Assert.Equal(0, (await Info(host)).ActiveDispatches);
        Assert.True((await Info(host)).Draining);
        using var resumed = await host.Client.PostAsJsonAsync("/api/v1/system/drain", new { draining = false });
        Assert.Equal(HttpStatusCode.Accepted, resumed.StatusCode);
        await kernel.WaitForTrackIdleAsync(track).WaitAsync(Deadline);
        Assert.Single(host.AgentRuntime.ExecutedTurns);
        Assert.Equal(0, (await Info(host)).ActiveDispatches);
        Assert.True(sub.Reader.TryRead(out var evt));
        Assert.Equal("system.upgrade_status", evt.Kind);
        Assert.True(evt.Payload.GetProperty("draining").GetBoolean());
        using var missing = await host.Client.PostAsJsonAsync("/api/v1/system/drain", new { });
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
    }

    [Fact]
    public async Task DrainReturnsWhileProviderTurnIsBusyAndCountsUntilItSettles()
    {
        await using var host = new PergaHostTestCluster();
        var (track, lead) = await host.CreateTrackAsync();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.AgentRuntime.SetTurnHandler(Wait);
        async IAsyncEnumerable<UnifiedAgentEvent> Wait(HarnessLaunchSpec spec, TurnInput input, [EnumeratorCancellation] CancellationToken ct)
        {
            started.TrySetResult();
            yield return new UnifiedAgentEvent.TextDelta("working");
            await finish.Task.WaitAsync(ct);
            yield return new UnifiedAgentEvent.TurnCompleted(new TokenUsage(1, 1, 0, 100000));
        }
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        await kernel.AcceptMessageAsync(lead, new SendMessageRequest("work", IdempotencyKey: "busy"), default);
        await started.Task.WaitAsync(Deadline);
        using var response = await host.Client.PostAsJsonAsync("/api/v1/system/drain", new { draining = true }).WaitAsync(Deadline);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(1, (await Info(host)).ActiveDispatches);
        finish.SetResult();
        await kernel.WaitForTrackIdleAsync(track).WaitAsync(Deadline);
        Assert.Equal(0, (await Info(host)).ActiveDispatches);
        Assert.True((await Info(host)).Draining);
    }

    [Fact]
    public async Task AcceptedDuringDrainSurvivesRestart()
    {
        var directory = Directory.CreateTempSubdirectory("perga-drain-restart-");
        try
        {
            string track, lead;
            await using (var host = new PergaHostTestCluster(directory.FullName))
            {
                (track, lead) = await host.CreateTrackAsync();
                var kernel = host.Services.GetRequiredService<IPergaKernel>();
                kernel.DispatchAdmission.SetDraining(true);
                await kernel.AcceptMessageAsync(lead, new SendMessageRequest("durable", IdempotencyKey: "restart-drain"), default);
                Assert.Empty(host.AgentRuntime.ExecutedTurns);
            }
            await using var restarted = new PergaHostTestCluster(directory.FullName);
            await restarted.Services.GetRequiredService<IPergaKernel>().WaitForTrackIdleAsync(track).WaitAsync(Deadline);
            Assert.False((await Info(restarted)).Draining);
            Assert.Single(restarted.AgentRuntime.ExecutedTurns);
        }
        finally { directory.Delete(true); }
    }

    [Fact]
    public async Task ManagementWritesDenyCrossOriginAndUntrustedHost()
    {
        await using var host = new PergaHostTestCluster();
        using var csrf = new HttpRequestMessage(HttpMethod.Post, "/api/v1/system/drain") { Content = JsonContent.Create(new { draining = true }) };
        csrf.Headers.Add("Origin", "https://attacker.example");
        using var denied = await host.Client.SendAsync(csrf);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using var noToken = new HttpRequestMessage(HttpMethod.Post, "/api/v1/system/drain") { Content = JsonContent.Create(new { draining = true }) };
        noToken.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "wrong");
        using var noTokenResponse = await host.Client.SendAsync(noToken);
        Assert.Equal(HttpStatusCode.Forbidden, noTokenResponse.StatusCode);
        using var remote = await host.Client.PostAsJsonAsync("http://untrusted.example/api/v1/system/drain", new { draining = true });
        Assert.Equal(HttpStatusCode.Forbidden, remote.StatusCode);
        Assert.False((await Info(host)).Draining);
    }

    [Fact]
    public async Task LeadOnlyToolsUsePersistedIdentityAndRejectArbitraryOptions()
    {
        var runner = new RecordingRunner();
        await using var host = new PergaHostTestCluster(configureHostOptions: opts =>
        {
            // Configure after startup via the shared options object below, avoiding managed release requirements.
            opts.UpgradeSources["trusted"] = new() { Kind = UpgradeSourceKind.Tarball, Location = "/owner/release.tar" };
        }, configureExtraServices: services => services.AddSingleton<IUpgradeProcessRunner>(runner));
        var (track, lead) = await host.CreateTrackAsync();
        host.Services.GetRequiredService<Perga.Host.Configuration.PergaHostOptions>().InstallationRoot = host.DataDir;
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        var principal = new McpSessionPrincipal("test", track, lead, false, null, null, []);
        var subagent = await kernel.CreateSubagentSessionAsync(track, new CreateSessionRequest(), default);
        foreach (var name in new[] { KernelMcpToolNames.SystemUpgradeCheck, KernelMcpToolNames.SystemUpgradeApply, KernelMcpToolNames.SystemUpgradeRollback })
        {
            var args = JsonSerializer.SerializeToElement(name == KernelMcpToolNames.SystemUpgradeApply ? new { sourceId = "trusted" } : (object)new { });
            foreach (var forbidden in new[] { principal with { SessionId = subagent.Id, IsLead = true }, principal with { BoundAttemptId = "worker" } })
            {
                var error = await Assert.ThrowsAsync<KernelException>(() => kernel.InvokeKernelMcpToolAsync(forbidden, name, args));
                Assert.Equal(KernelErrorCode.SystemUpgradeLeadRequired, error.Code);
            }
        }
        var backend = host.Services.GetRequiredService<Perga.Host.Mcp.KernelMcpBackend>();
        var denied = await backend.CallToolAsync(principal with { SessionId = subagent.Id, IsLead = true },
            KernelMcpToolNames.SystemUpgradeCheck, JsonSerializer.SerializeToElement(new { }), default);
        Assert.True(denied.IsError);
        using var denial = JsonDocument.Parse(Assert.Single(denied.Content).Text);
        Assert.Equal("system_upgrade_lead_required", denial.RootElement.GetProperty("code").GetString());
        Assert.DoesNotContain(KernelMcpCatalog.GetToolsForSession(null, false), x => x.Name == KernelMcpToolNames.SystemUpgradeApply);
        Assert.Contains(KernelMcpCatalog.GetToolsForSession(null, true), x => x.Name == KernelMcpToolNames.SystemUpgradeApply);
        var invalid = await Assert.ThrowsAsync<KernelException>(() => kernel.InvokeKernelMcpToolAsync(principal,
            KernelMcpToolNames.SystemUpgradeApply, JsonSerializer.SerializeToElement(new { sourceId = "trusted", force = true })));
        Assert.Equal(KernelErrorCode.InvalidArgument, invalid.Code);
        await Assert.ThrowsAsync<KernelException>(() => kernel.InvokeKernelMcpToolAsync(principal,
            KernelMcpToolNames.SystemUpgradeApply, JsonSerializer.SerializeToElement(new { sourceId = "/tmp/arbitrary" })));
        Assert.Empty(runner.Commands);
        var check = await kernel.InvokeKernelMcpToolAsync(principal, KernelMcpToolNames.SystemUpgradeCheck, JsonSerializer.SerializeToElement(new { sourceId = "trusted" }));
        Assert.True(check.GetProperty("verified").GetBoolean());
        var apply = await kernel.InvokeKernelMcpToolAsync(principal, KernelMcpToolNames.SystemUpgradeApply, JsonSerializer.SerializeToElement(new { sourceId = "trusted" })).WaitAsync(Deadline);
        Assert.Equal("detached-job", apply.GetProperty("jobId").GetString());
        Assert.Contains("upgrade", runner.Commands[1]);
        Assert.Contains("--notify-track-id", runner.Commands[1]);
        Assert.DoesNotContain("--wait", runner.Commands[1]);
        await kernel.InvokeKernelMcpToolAsync(principal, KernelMcpToolNames.SystemUpgradeRollback, JsonSerializer.SerializeToElement(new { })).WaitAsync(Deadline);
        var rollback = runner.Commands[2];
        Assert.Equal("/usr/bin/python3", rollback[0]);
        Assert.DoesNotContain("--wait", rollback);
        Assert.DoesNotContain("/usr/bin/systemd-run", rollback);
        Assert.Contains("rollback", rollback);
        Assert.All(runner.Commands, command => Assert.DoesNotContain("--force", command));
        var store = host.Services.GetRequiredService<ITrackStore>();
        var persisted = (await store.GetTrackAsync(track, default))!;
        await store.UpdateTrackAndEmitAsync(persisted with { LeadSessionId = subagent.Id }, null, default);
        var stale = await Assert.ThrowsAsync<KernelException>(() => kernel.InvokeKernelMcpToolAsync(principal with { IsLead = true }, KernelMcpToolNames.SystemUpgradeCheck, JsonSerializer.SerializeToElement(new { })));
        Assert.Equal(KernelErrorCode.SystemUpgradeLeadRequired, stale.Code);
    }

    [Fact]
    public async Task UpgradeCallFromProviderReturnsBeforeDrainWaitsForItsTurn()
    {
        var runner = new RecordingRunner();
        await using var host = new PergaHostTestCluster(configureHostOptions: options =>
            options.UpgradeSources["trusted"] = new() { Kind = UpgradeSourceKind.Tarball, Location = "/owner/release.tar" },
            configureExtraServices: services => services.AddSingleton<IUpgradeProcessRunner>(runner));
        var (track, lead) = await host.CreateTrackAsync();
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        host.Services.GetRequiredService<Perga.Host.Configuration.PergaHostOptions>().InstallationRoot = host.DataDir;
        runner.OnRun = args => { if (args.Contains("upgrade")) kernel.DispatchAdmission.SetDraining(true); };
        var returned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.AgentRuntime.SetTurnHandler(Upgrade);
        async IAsyncEnumerable<UnifiedAgentEvent> Upgrade(HarnessLaunchSpec spec, TurnInput input, [EnumeratorCancellation] CancellationToken ct)
        {
            var principal = new McpSessionPrincipal("test", track, lead, true, null, null, []);
            var result = await kernel.InvokeKernelMcpToolAsync(principal, KernelMcpToolNames.SystemUpgradeApply,
                JsonSerializer.SerializeToElement(new { sourceId = "trusted" }), ct);
            Assert.Equal("queued", result.GetProperty("status").GetString());
            Assert.Equal(1, kernel.DispatchAdmission.ActiveDispatches);
            returned.SetResult();
            yield return new UnifiedAgentEvent.TurnCompleted(new TokenUsage(1, 1, 0, 100000));
        }
        await kernel.AcceptMessageAsync(lead, new SendMessageRequest("upgrade", "upgrade-from-provider"), default);
        await returned.Task.WaitAsync(Deadline);
        await kernel.WaitForTrackIdleAsync(track).WaitAsync(Deadline);
        Assert.True(kernel.DispatchAdmission.Draining);
        Assert.Equal(0, kernel.DispatchAdmission.ActiveDispatches);
    }

    [Fact]
    public async Task WorkerDispatchStaysPendingUntilDrainIsReopened()
    {
        await using var host = new PergaHostTestCluster();
        var (track, _) = await host.CreateTrackAsync();
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        kernel.DispatchAdmission.SetDraining(true);
        var dispatched = await kernel.DispatchWorkerAsync(track, new("Inspect the report"), "drained-worker", null, default);
        Assert.Equal(TaskRunStatus.Pending, dispatched.Task.Status);
        Assert.Empty(host.AgentRuntime.OpenedSessions);
        Assert.Equal(0, kernel.DispatchAdmission.ActiveDispatches);
        kernel.DispatchAdmission.SetDraining(false);
        await Until(() => Task.FromResult(host.AgentRuntime.ExecutedTurns.Count == 1));
        await kernel.WaitForTrackIdleAsync(track).WaitAsync(Deadline);
        Assert.Equal(0, kernel.DispatchAdmission.ActiveDispatches);
    }

    [Fact]
    public async Task LateReceiptIsConsumedOnceAndNotificationSurvivesRestart()
    {
        var directory = Directory.CreateTempSubdirectory("perga-upgrade-receipt-");
        try
        {
            string track;
            await using (var host = new PergaHostTestCluster(Path.Combine(directory.FullName, "data")))
            {
                (track, _) = await host.CreateTrackAsync();
                var options = host.Services.GetRequiredService<Perga.Host.Configuration.PergaHostOptions>();
                options.InstallationRoot = directory.FullName;
                Directory.CreateDirectory(Path.Combine(directory.FullName, "runtime"));
                var receipt = new SystemUpgradeReceipt("receipt-job", "old", "new", "succeeded", null, null, track, 1234, ["web"]);
                await File.WriteAllTextAsync(Path.Combine(directory.FullName, "runtime", "upgrade-result.json"), JsonSerializer.Serialize(receipt, PergaJsonDefaults.ApiOptions));
                await Until(() => Task.FromResult(host.AgentRuntime.ExecutedTurns.Count == 1));
                var kernel = host.Services.GetRequiredService<IPergaKernel>();
                await kernel.WaitForTrackIdleAsync(track).WaitAsync(Deadline);
                await kernel.UpgradeReceipts.ImportAsync(default);
                Assert.Single(host.AgentRuntime.ExecutedTurns);
                Assert.Single(await kernel.ListMailsAsync(track));
                await using var db = Open(host.DataDir);
                Assert.Equal(1, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM system_upgrade_receipts;"));
                Assert.Equal(0, await db.ExecuteScalarAsync<int>("SELECT SUM(wake_pending) FROM mails;"));
                var turn = Assert.Single(await host.Services.GetRequiredService<ITrackStore>().ListSessionTurnsAsync((await kernel.GetTrackAggregateAsync(track)).Track.LeadSessionId!, default));
                Assert.Equal("system", turn.TriggerKind);
            }
            // Boot reads the same receipt (configure root after startup would miss this path).
            await using var restarted = new PergaHostTestCluster(Path.Combine(directory.FullName, "data"), configureExtraServices: services =>
                services.AddSingleton<ISystemUpgradeReceiptSource>(new FixedReceipt(new("receipt-job", "old", "new", "succeeded", null, null, track, 1234, ["web"]))));
            await restarted.Services.GetRequiredService<IPergaKernel>().WaitForTrackIdleAsync(track).WaitAsync(Deadline);
            Assert.Empty(restarted.AgentRuntime.ExecutedTurns);
            Assert.Single(await restarted.Services.GetRequiredService<IPergaKernel>().ListMailsAsync(track));
        }
        finally { directory.Delete(true); }
    }

    [Fact]
    public async Task ReceiptPollingDoesNotDuplicateWakeWhileLeadIsBusy()
    {
        var source = new MutableReceipt();
        await using var host = new PergaHostTestCluster(configureExtraServices: services =>
            services.AddSingleton<ISystemUpgradeReceiptSource>(source));
        var (track, lead) = await host.CreateTrackAsync();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = 0;
        host.AgentRuntime.SetTurnHandler(Wait);
        async IAsyncEnumerable<UnifiedAgentEvent> Wait(HarnessLaunchSpec spec, TurnInput input, [EnumeratorCancellation] CancellationToken ct)
        {
            if (Interlocked.Increment(ref first) == 1)
            {
                started.SetResult();
                yield return new UnifiedAgentEvent.TextDelta("busy");
                await finish.Task.WaitAsync(ct);
            }
            yield return new UnifiedAgentEvent.TurnCompleted(new TokenUsage(1, 1, 0, 100000));
        }
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        await kernel.AcceptMessageAsync(lead, new SendMessageRequest("busy", "receipt-busy"), default);
        await started.Task.WaitAsync(Deadline);
        source.Value = new("busy-receipt", "old", "new", "succeeded", null, null, track, 1234, ["templates"]);
        for (var i = 0; i < 5; i++) await kernel.UpgradeReceipts.ImportAsync(default);
        finish.SetResult();
        await kernel.WaitForTrackIdleAsync(track).WaitAsync(Deadline);
        Assert.Equal(2, host.AgentRuntime.ExecutedTurns.Count);
        Assert.Single(await kernel.ListMailsAsync(track));
    }

    [Fact]
    public async Task ReceiptAndWakeCommitTogetherAndUnavailableTargetIsRecorded()
    {
        await using var host = new PergaHostTestCluster();
        var (track, _) = await host.CreateTrackAsync();
        var store = host.Services.GetRequiredService<ISystemUpgradeStore>();
        var receipt = new SystemUpgradeReceipt("atomic-job", "old", "new", "failed", "error", null, track, 1234, []);
        await using var db = Open(host.DataDir);
        await db.ExecuteAsync("CREATE TRIGGER fail_upgrade_mail BEFORE INSERT ON mails BEGIN SELECT RAISE(ABORT, 'injected mail failure'); END;");
        await Assert.ThrowsAnyAsync<Exception>(() => store.ConsumeUpgradeReceiptAsync(receipt, default));
        Assert.Equal(0, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM system_upgrade_receipts;"));
        await db.ExecuteAsync("DROP TRIGGER fail_upgrade_mail;");
        Assert.True(await store.ConsumeUpgradeReceiptAsync(receipt, default));
        Assert.False(await store.ConsumeUpgradeReceiptAsync(receipt, default));
        Assert.Single(await host.Services.GetRequiredService<IPergaKernel>().ListMailsAsync(track));
        Assert.True(await store.ConsumeUpgradeReceiptAsync(receipt with { JobId = "missing", NotifyTrackId = "missing-track" }, default));
        Assert.Equal("target_unavailable", await db.ExecuteScalarAsync<string>("SELECT notification_status FROM system_upgrade_receipts WHERE job_id = 'missing';"));
    }

    private static async Task<SystemInfoDto> Info(PergaHostTestCluster host) =>
        (await host.Client.GetFromJsonAsync<SystemInfoDto>("/api/v1/system/info", PergaJsonDefaults.ApiOptions))!;
    private static SqliteConnection Open(string dataDir) => new(new SqliteConnectionStringBuilder { DataSource = Path.Combine(dataDir, "perga.db"), Pooling = false }.ToString());
    private static async Task Until(Func<Task<bool>> condition)
    {
        using var timeout = new CancellationTokenSource(Deadline);
        while (!await condition()) await Task.Delay(20, timeout.Token);
    }
    private sealed class RecordingRunner : IUpgradeProcessRunner
    {
        public List<string[]> Commands { get; } = [];
        public Action<IReadOnlyList<string>>? OnRun { get; set; }
        public Task<string> RunAsync(IReadOnlyList<string> arguments, CancellationToken ct)
        {
            Commands.Add(arguments.ToArray());
            OnRun?.Invoke(arguments);
            return Task.FromResult(arguments.Contains("check") ? "{\"verified\":true}" : "{\"jobId\":\"detached-job\"}");
        }
    }
    private sealed class MutableReceipt : ISystemUpgradeReceiptSource
    {
        public SystemUpgradeReceipt? Value { get; set; }
        public Task<SystemUpgradeReceipt?> ReadAsync(CancellationToken ct) => Task.FromResult(Value);
    }
    private sealed class FixedReceipt(SystemUpgradeReceipt receipt) : ISystemUpgradeReceiptSource
    {
        public Task<SystemUpgradeReceipt?> ReadAsync(CancellationToken ct) => Task.FromResult<SystemUpgradeReceipt?>(receipt);
    }
}
