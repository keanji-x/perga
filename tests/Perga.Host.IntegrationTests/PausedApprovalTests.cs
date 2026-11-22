using Dapper;
using Microsoft.Data.Sqlite;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Perga.AgentKit;
using Perga.Contracts;
using Perga.Host.IntegrationTests.Fixtures;
using Perga.Host.Serialization;
using Perga.Kernel;
using Perga.Kernel.Ports;
using Perga.Storage.Sqlite;

namespace Perga.Host.IntegrationTests;

public sealed class PausedApprovalTests
{
    [Theory]
    [InlineData("approve", true)]
    [InlineData("deny", false)]
    public async Task ProviderApprovalPublishesIdentityAndOnlyResumesItsOriginalTurn(string optionId, bool approved)
    {
        await using var host = new PergaHostTestCluster();
        WaitForProviderDecision(host);
        var (trackId, sessionId) = await host.CreateTrackAsync();
        await StartTurn(host, sessionId);
        var ask = await WaitForApproval(host, trackId);
        Assert.Equal(sessionId, ask.SessionId);
        Assert.Null(ask.DelegateSessionId);
        Assert.Equal("provider", ask.Source);
        Assert.Equal("shell", ask.ToolName);
        Assert.NotNull(ask.TurnId);
        Assert.Equal("Approve tool 'shell'?", ask.Title);
        Assert.Equal(["approve", "deny"], ask.Options.Select(o => o.Id));
        var page = await host.AuthenticatedClient.GetFromJsonAsync<SessionItemsPageDto>($"/api/v1/sessions/{sessionId}/items", PergaJsonDefaults.ApiOptions);
        Assert.Equal(page!.ActiveTurn!.Id, ask.TurnId);
        Assert.Empty(host.AgentRuntime.ApprovalResponses);

        var (otherTrack, otherSession) = await host.CreateTrackAsync();
        using var crossTrack = await host.AuthenticatedClient.PostAsJsonAsync($"/api/v1/sessions/{otherSession}/interactions/{ask.Id}/respond", new { executionId = ask.ExecutionId, optionId });
        Assert.Equal(HttpStatusCode.Conflict, crossTrack.StatusCode);
        Assert.Empty(host.AgentRuntime.ApprovalResponses);

        using var blank = await host.AuthenticatedClient.PostAsJsonAsync(ApprovalPath(trackId, ask), new { executionId = ask.ExecutionId, optionId = " " });
        Assert.Equal(HttpStatusCode.BadRequest, blank.StatusCode);
        using var unknown = await host.AuthenticatedClient.PostAsJsonAsync(ApprovalPath(trackId, ask), new { executionId = ask.ExecutionId, optionId = "invented" });
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.Empty(host.AgentRuntime.ApprovalResponses);
        Assert.Equal(ask.Id, Assert.Single((await Snapshot(host, trackId)).PendingApprovals).Id);

        await using var connection = new SqliteConnection($"Data Source={Path.Combine(host.DataDir, "perga.db")};Pooling=False");
        await connection.OpenAsync();
        Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM events WHERE kind = 'approval.decided'"));

        using var response = await host.AuthenticatedClient.PostAsJsonAsync(ApprovalPath(trackId, ask), new { executionId = ask.ExecutionId, optionId });
        response.EnsureSuccessStatusCode();
        var answered = (await response.Content.ReadFromJsonAsync<SessionInteractionResult>(PergaJsonDefaults.ApiOptions))!;
        Assert.Equal(ask.Id, answered.Id);
        Assert.Equal("approval", answered.Kind);
        for (var i = 0; i < 100 && host.AgentRuntime.ApprovalResponses.IsEmpty; i++)
            await Task.Delay(10);
        Assert.Equal(("approval-request", approved), Assert.Single(host.AgentRuntime.ApprovalResponses));
        using var duplicate = await host.AuthenticatedClient.PostAsJsonAsync(ApprovalPath(trackId, ask), new { executionId = ask.ExecutionId, optionId });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Single(host.AgentRuntime.ExecutedTurns);
    }

    [Fact]
    public async Task FailedAnswerEventWriteDoesNotConsumeApprovalAndCanRetry()
    {
        await using var host = new PergaHostTestCluster();
        WaitForProviderDecision(host);
        var (trackId, sessionId) = await host.CreateTrackAsync();
        await StartTurn(host, sessionId);
        var ask = await WaitForApproval(host, trackId);
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        await using var connection = new SqliteConnection($"Data Source={Path.Combine(host.DataDir, "perga.db")};Pooling=False");
        await connection.OpenAsync();
        await connection.ExecuteAsync("""
            CREATE TRIGGER reject_answer_event BEFORE INSERT ON events
            WHEN NEW.kind = 'approval.decided'
            BEGIN SELECT RAISE(ABORT, 'injected answer event failure'); END;
            """);
        await Assert.ThrowsAsync<SqliteException>(() => kernel.RespondInteractionAsync(ask.SessionId, ask.Id, new(ExecutionId: ask.ExecutionId, OptionId: "approve"), new InteropPrincipal.Human(), default));
        Assert.Equal(ask.Id, Assert.Single((await Snapshot(host, trackId)).PendingApprovals).Id);
        Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM asks WHERE id = @Id", ask));
        Assert.Empty(host.AgentRuntime.ApprovalResponses);
        await connection.ExecuteAsync("DROP TRIGGER reject_answer_event;");
        using var response = await host.AuthenticatedClient.PostAsJsonAsync(ApprovalPath(trackId, ask), new { executionId = ask.ExecutionId, optionId = "approve" });
        response.EnsureSuccessStatusCode();
        for (var i = 0; i < 100 && host.AgentRuntime.ApprovalResponses.IsEmpty; i++) await Task.Delay(10);
        Assert.True(Assert.Single(host.AgentRuntime.ApprovalResponses).Approved);
    }

    [Fact]
    public async Task RestartDiscardsLiveApprovalWithoutWakingAnotherTurn()
    {
        var directory = Directory.CreateTempSubdirectory("perga-approval-");
        try
        {
            ApprovalDto ask;
            string trackId;
            await using (var host = new PergaHostTestCluster(directory.FullName))
            {
                WaitForProviderDecision(host);
                var created = await host.CreateTrackAsync();
                trackId = created.TrackId;
                await StartTurn(host, created.SessionId);
                ask = await WaitForApproval(host, trackId);
            }
            await using var reopened = new PergaHostTestCluster(directory.FullName);
            var snapshot = await Snapshot(reopened, trackId);
            Assert.Empty(snapshot.OpenAsks);
            Assert.Empty(snapshot.PendingApprovals);
            using var response = await reopened.AuthenticatedClient.PostAsJsonAsync(ApprovalPath(trackId, ask), new { executionId = ask.ExecutionId, optionId = "approve" });
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            using var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("conflict", error.RootElement.GetProperty("code").GetString());
            Assert.Empty(reopened.AgentRuntime.ExecutedTurns);
            Assert.Empty(reopened.AgentRuntime.ApprovalResponses);

        }
        finally { directory.Delete(recursive: true); }
    }

    [Fact]
    public async Task InterruptedTurnExpiresApprovalAndAnonymousQuestionCannotImpersonateIt()
    {
        await using var host = new PergaHostTestCluster();
        WaitForProviderDecision(host);
        var (trackId, sessionId) = await host.CreateTrackAsync();
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        await StartTurn(host, sessionId);
        var hold = await WaitForApproval(host, trackId);
        using var askCancellation = new CancellationTokenSource();
        var questionTask = kernel.AskUserAsync(trackId, null,
            [new AskQuestionDto("Approve tool 'shell'?", ["Approve", "Deny"])], askCancellation.Token);
        AskDto? question = null;
        for (var i = 0; i < 100 && question is null; i++)
        {
            question = (await Snapshot(host, trackId)).OpenAsks.FirstOrDefault(a => a.Id != hold.Id);
            if (question is null) await Task.Delay(10);
        }
        Assert.NotNull(question);
        Assert.Equal(AskDelivery.Wake, question.Delivery);
        Assert.Null(question.SessionId);
        Assert.Null(question.TurnId);
        askCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => questionTask);

        using var interrupted = await host.AuthenticatedClient.PostAsJsonAsync($"/api/v1/sessions/{sessionId}/control", new { action = "stop", turnId = hold.TurnId });
        interrupted.EnsureSuccessStatusCode();
        using var expired = await host.AuthenticatedClient.PostAsJsonAsync(ApprovalPath(trackId, hold), new { executionId = hold.ExecutionId, optionId = "approve" });
        Assert.Equal(HttpStatusCode.Conflict, expired.StatusCode);
        Assert.Empty(host.AgentRuntime.ApprovalResponses);
        Assert.Single(host.AgentRuntime.ExecutedTurns);
    }

    [Fact]
    public async Task TerminalStopDuringApprovalSettlesTurn()
    {
        await using var host = new PergaHostTestCluster();
        host.AgentRuntime.RequiresTerminalStop = true;
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.AgentRuntime.CancelHandler = ct =>
        {
            ct.ThrowIfCancellationRequested();
            stopped.TrySetResult();
            return Task.CompletedTask;
        };
        host.AgentRuntime.SetTurnHandler((_, _, ct) => TerminalApprovalTurn(stopped.Task, ct));
        var (trackId, sessionId) = await host.CreateTrackAsync();
        await StartTurn(host, sessionId);
        var approval = await WaitForApproval(host, trackId);

        using var stop = await host.AuthenticatedClient.PostAsJsonAsync($"/api/v1/sessions/{sessionId}/control", new { action = "stop", turnId = approval.TurnId });
        stop.EnsureSuccessStatusCode();

        for (var i = 0; i < 100; i++)
        {
            var snapshot = await Snapshot(host, trackId);
            if (snapshot.Sessions.Single(s => s.Id == sessionId).Status == "idle")
            {
                Assert.DoesNotContain(snapshot.PendingApprovals, ask => ask.Id == approval.Id);
                return;
            }
            await Task.Delay(25);
        }
        Assert.Equal("idle", (await Snapshot(host, trackId)).Sessions.Single(s => s.Id == sessionId).Status);
    }

    [Fact]
    public async Task StopWinsWhileApprovalAuditWaitsForSQLite()
    {
        var auditStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var host = new PergaHostTestCluster(configureExtraServices: services =>
        {
            services.RemoveAll<ITrackStore>();
            services.AddSingleton<ITrackStore>(sp =>
            {
                var store = DispatchProxy.Create<ITrackStore, AnswerPublicationStoreProxy>();
                var proxy = (AnswerPublicationStoreProxy)store;
                var sqlite = sp.GetRequiredService<SqliteTrackStore>();
                sqlite.InitializeAsync().GetAwaiter().GetResult();
                proxy.Inner = sqlite;
                proxy.BeforeNextApprovalAudit = () => auditStarted.TrySetResult();
                return store;
            });
        });
        host.AgentRuntime.RequiresTerminalStop = true;
        var stopObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.AgentRuntime.CancelHandler = _ =>
        {
            stopObserved.TrySetResult();
            return Task.CompletedTask;
        };
        host.AgentRuntime.SetTurnHandler((_, _, ct) => TerminalApprovalTurn(stopObserved.Task, ct));
        var (trackId, sessionId) = await host.CreateTrackAsync();
        await StartTurn(host, sessionId);
        var approval = await WaitForApproval(host, trackId);

        await using var db = new SqliteConnection($"Data Source={Path.Combine(host.DataDir, "perga.db")};Pooling=False");
        await db.OpenAsync();
        await db.ExecuteAsync("BEGIN IMMEDIATE;");
        var released = false;
        try
        {
            var answer = host.AuthenticatedClient.PostAsJsonAsync(ApprovalPath(trackId, approval), new { executionId = approval.ExecutionId, optionId = "approve" });
            await auditStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var stop = await host.AuthenticatedClient.PostAsJsonAsync($"/api/v1/sessions/{sessionId}/control", new { action = "stop", turnId = approval.TurnId })
                .WaitAsync(TimeSpan.FromSeconds(5));
            stop.EnsureSuccessStatusCode();
            var stopping = await stop.Content.ReadFromJsonAsync<AgentSessionDto>(PergaJsonDefaults.ApiOptions);
            Assert.Equal(sessionId, stopping!.Id);
            Assert.Equal("stopping", stopping.Status);
            Assert.True(stopObserved.Task.IsCompletedSuccessfully);
            Assert.False(answer.IsCompleted);
            await db.ExecuteAsync("ROLLBACK;");
            released = true;
            using var replied = await answer;
            Assert.Equal(HttpStatusCode.Conflict, replied.StatusCode);
        }
        finally
        {
            if (!released)
                await db.ExecuteAsync("ROLLBACK;");
        }
        Assert.Empty(host.AgentRuntime.ApprovalResponses);
        Assert.Empty((await Snapshot(host, trackId)).PendingApprovals);
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        await kernel.WaitForTrackIdleAsync(trackId).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("expired", await db.ExecuteScalarAsync<string>("""
            SELECT json_extract(payload_json, '$.detail.delivery') FROM events
            WHERE kind = 'approval.delivery' AND json_extract(payload_json, '$.approval.id') = @Id;
            """, approval));
    }

    [Fact]
    public async Task ClosingTrackRevokesExistingApproval()
    {
        await using var host = new PergaHostTestCluster();
        WaitForProviderDecision(host);
        var (trackId, sessionId) = await host.CreateTrackAsync();
        await StartTurn(host, sessionId);
        var approval = await WaitForApproval(host, trackId);

        using var closed = await host.AuthenticatedClient.PatchAsJsonAsync($"/api/v1/tracks/{trackId}", new { closed = true });
        closed.EnsureSuccessStatusCode();
        using var late = await host.AuthenticatedClient.PostAsJsonAsync(ApprovalPath(trackId, approval), new { executionId = approval.ExecutionId, optionId = "approve" });
        Assert.Equal(HttpStatusCode.Conflict, late.StatusCode);
        Assert.Empty((await Snapshot(host, trackId)).PendingApprovals);
        Assert.Empty(host.AgentRuntime.ApprovalResponses);
    }

    [Fact]
    public async Task StopWithoutCurrentTurnIdentityCannotCancelAnotherTurn()
    {
        await using var host = new PergaHostTestCluster();
        WaitForProviderDecision(host);
        var (trackId, sessionId) = await host.CreateTrackAsync();
        var stopPath = $"/api/v1/sessions/{sessionId}/control";
        await StartTurn(host, sessionId);
        var first = await WaitForApproval(host, trackId);
        using (var decided = await host.AuthenticatedClient.PostAsJsonAsync(ApprovalPath(trackId, first), new { executionId = first.ExecutionId, optionId = "approve" }))
            decided.EnsureSuccessStatusCode();
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        await kernel.WaitForTrackIdleAsync(trackId).WaitAsync(TimeSpan.FromSeconds(5));

        WaitForProviderDecision(host);
        await StartTurn(host, sessionId, "second-approval");
        var second = await WaitForApproval(host, trackId);
        using var missing = await host.AuthenticatedClient.PostAsync(stopPath, null);
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        using var stale = await host.AuthenticatedClient.PostAsJsonAsync(stopPath, new { action = "stop", turnId = first.TurnId });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal(second.Id, Assert.Single((await Snapshot(host, trackId)).PendingApprovals).Id);
    }

    [Fact]
    public async Task TerminalEvidenceDoesNotReleaseAnInFlightStop()
    {
        await using var host = new PergaHostTestCluster();
        host.AgentRuntime.RequiresTerminalStop = true;
        var cancelEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelReleased = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.AgentRuntime.CancelHandler = async ct =>
        {
            cancelEntered.TrySetResult();
            await cancelReleased.Task.WaitAsync(ct);
        };
        host.AgentRuntime.SetTurnHandler((_, _, ct) => Stream(ct));
        async IAsyncEnumerable<UnifiedAgentEvent> Stream([EnumeratorCancellation] CancellationToken ct)
        {
            yield return new UnifiedAgentEvent.ApprovalRequested("blocked-cancel", "shell", JsonSerializer.SerializeToElement(new { }));
            await cancelEntered.Task.WaitAsync(ct);
            yield return new UnifiedAgentEvent.TurnInterrupted();
            drained.TrySetResult();
        }

        var (trackId, sessionId) = await host.CreateTrackAsync();
        await StartTurn(host, sessionId);
        var approval = await WaitForApproval(host, trackId);
        var stopPath = $"/api/v1/sessions/{sessionId}/control";
        var first = host.AuthenticatedClient.PostAsJsonAsync(stopPath, new { action = "stop", turnId = approval.TurnId });
        await drained.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = host.AuthenticatedClient.PostAsJsonAsync(stopPath, new { action = "stop", turnId = approval.TurnId });
        try
        {
            Assert.NotSame(second, await Task.WhenAny(second, Task.Delay(100)));
        }
        finally
        {
            cancelReleased.TrySetResult();
        }
        using var firstResponse = await first;
        using var secondResponse = await second;
        firstResponse.EnsureSuccessStatusCode();
        secondResponse.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task TerminalStopWaitsPastCancelDispatchGraceForSlowProviderUnwind()
    {
        await using var host = new PergaHostTestCluster(configureHostOptions: opts =>
        {
            opts.ProviderCancelDispatchGrace = TimeSpan.FromMilliseconds(150);
            opts.ProviderTerminalStopGrace = TimeSpan.FromSeconds(3);
        });
        host.AgentRuntime.RequiresTerminalStop = true;
        var cancelSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.AgentRuntime.CancelHandler = _ =>
        {
            cancelSent.TrySetResult();
            return Task.CompletedTask;
        };
        host.AgentRuntime.SetTurnHandler((_, _, ct) => DelayedTerminalStopTurn(cancelSent.Task, TimeSpan.FromMilliseconds(350), ct));

        var (trackId, sessionId) = await host.CreateTrackAsync();
        await StartTurn(host, sessionId);
        var approval = await WaitForApproval(host, trackId);

        using var stop = await host.AuthenticatedClient.PostAsJsonAsync($"/api/v1/sessions/{sessionId}/control", new { action = "stop", turnId = approval.TurnId });
        stop.EnsureSuccessStatusCode();

        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        await kernel.WaitForTrackIdleAsync(trackId).WaitAsync(TimeSpan.FromSeconds(5));
        var snapshot = await Snapshot(host, trackId);
        Assert.Equal("idle", snapshot.Sessions.Single(s => s.Id == sessionId).Status);
        Assert.Empty(snapshot.PendingApprovals);
    }

    [Fact]
    public async Task TerminalStopExceedingTerminalGraceAbortsAndFencesSession()
    {
        await using var host = new PergaHostTestCluster(configureHostOptions: opts =>
        {
            opts.ProviderCancelDispatchGrace = TimeSpan.FromMilliseconds(150);
            opts.ProviderTerminalStopGrace = TimeSpan.FromMilliseconds(250);
        });
        host.AgentRuntime.RequiresTerminalStop = true;
        var cancelSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.AgentRuntime.CancelHandler = _ =>
        {
            cancelSent.TrySetResult();
            return Task.CompletedTask;
        };
        host.AgentRuntime.SetTurnHandler((_, _, ct) => WedgedApprovalTurn(ct));

        var (trackId, sessionId) = await host.CreateTrackAsync();
        await StartTurn(host, sessionId);
        var approval = await WaitForApproval(host, trackId);

        using var stop = await host.AuthenticatedClient.PostAsJsonAsync(
            $"/api/v1/sessions/{sessionId}/control", new { action = "stop", turnId = approval.TurnId })
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(HttpStatusCode.Conflict, stop.StatusCode);
        await cancelSent.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        await kernel.WaitForTrackIdleAsync(trackId).WaitAsync(TimeSpan.FromSeconds(5));
        var snapshot = await Snapshot(host, trackId);
        Assert.Equal("unknown", snapshot.Sessions.Single(s => s.Id == sessionId).Status);
        Assert.Empty(snapshot.PendingApprovals);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OutOfRangeStopGraceUsesDefault(bool invalidCancelGrace)
    {
        await using var host = new PergaHostTestCluster(configureHostOptions: opts =>
        {
            if (invalidCancelGrace)
                opts.ProviderCancelDispatchGrace = TimeSpan.FromDays(60);
            else
                opts.ProviderTerminalStopGrace = TimeSpan.FromDays(60);
        });
        host.AgentRuntime.RequiresTerminalStop = true;
        var cancelSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.AgentRuntime.CancelHandler = _ =>
        {
            cancelSent.TrySetResult();
            return Task.CompletedTask;
        };
        host.AgentRuntime.SetTurnHandler((_, _, ct) => TerminalApprovalTurn(cancelSent.Task, ct));

        var (trackId, sessionId) = await host.CreateTrackAsync();
        await StartTurn(host, sessionId);
        var approval = await WaitForApproval(host, trackId);
        using var stop = await host.AuthenticatedClient.PostAsJsonAsync(
            $"/api/v1/sessions/{sessionId}/control", new { action = "stop", turnId = approval.TurnId })
            .WaitAsync(TimeSpan.FromSeconds(5));

        stop.EnsureSuccessStatusCode();
        Assert.True(cancelSent.Task.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task ApprovalDeliveryRunsOutsideAdmissionAndControlLocks()
    {
        await using var host = new PergaHostTestCluster();
        var deliveryEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDelivery = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var turnFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.AgentRuntime.ApprovalHandler = () =>
        {
            deliveryEntered.TrySetResult();
            // Simulate a synchronous provider write that must never hold either lock.
            releaseDelivery.Task.GetAwaiter().GetResult();
            turnFinished.TrySetResult();
        };
        host.AgentRuntime.SetTurnHandler((_, _, ct) => ApprovalTurn(turnFinished.Task, ct));

        var (trackId, sessionId) = await host.CreateTrackAsync();
        await StartTurn(host, sessionId);
        var approval = await WaitForApproval(host, trackId);

        var decideTask = host.AuthenticatedClient.PostAsJsonAsync(ApprovalPath(trackId, approval), new { executionId = approval.ExecutionId, optionId = "approve" });
        await deliveryEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            // Stop enters the control lock before Close makes the session unwritable.
            using var stop = await host.AuthenticatedClient.PostAsJsonAsync(
                $"/api/v1/sessions/{sessionId}/control", new { action = "stop", turnId = approval.TurnId })
                .WaitAsync(TimeSpan.FromSeconds(5));
            stop.EnsureSuccessStatusCode();
            // Close enters approval admission while provider delivery is still blocked.
            using var closeResp = await host.AuthenticatedClient.PatchAsJsonAsync($"/api/v1/tracks/{trackId}", new { closed = true })
                .WaitAsync(TimeSpan.FromSeconds(5));
            closeResp.EnsureSuccessStatusCode();
        }
        finally
        {
            releaseDelivery.TrySetResult();
        }

        using var decided = await decideTask.WaitAsync(TimeSpan.FromSeconds(5));
        decided.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task StopCannotTakeProviderRequestAfterApprovalClaimsDelivery()
    {
        var clock = new ApprovalNotificationClock();
        await using var host = new PergaHostTestCluster(configureExtraServices: services =>
        {
            services.Replace(ServiceDescriptor.Singleton<TimeProvider>(clock));
            services.Replace(ServiceDescriptor.Singleton<ITrackStore>(sp =>
            {
                var store = DispatchProxy.Create<ITrackStore, AnswerPublicationStoreProxy>();
                var proxy = (AnswerPublicationStoreProxy)store;
                var sqlite = sp.GetRequiredService<SqliteTrackStore>();
                sqlite.InitializeAsync().GetAwaiter().GetResult();
                proxy.Inner = sqlite;
                proxy.BeforeNextApprovalAudit = clock.PauseNextRead;
                return store;
            }));
        });

        var requestPending = 1;
        var cancelTookRequest = false;
        var cancelSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.AgentRuntime.RequiresTerminalStop = true;
        host.AgentRuntime.ApprovalClaimHandler = () => Interlocked.Exchange(ref requestPending, 0) == 1;
        host.AgentRuntime.CancelHandler = _ =>
        {
            cancelTookRequest = Interlocked.Exchange(ref requestPending, 0) == 1;
            cancelSent.TrySetResult();
            return Task.CompletedTask;
        };
        host.AgentRuntime.SetTurnHandler((_, _, ct) => TerminalApprovalTurn(cancelSent.Task, ct));
        var (trackId, sessionId) = await host.CreateTrackAsync();
        await StartTurn(host, sessionId);
        var approval = await WaitForApproval(host, trackId);

        var answer = host.AuthenticatedClient.PostAsJsonAsync(ApprovalPath(trackId, approval), new { executionId = approval.ExecutionId, optionId = "approve" });
        try
        {
            // Hold the answer at its approval-change notification, between the claim and write.
            await clock.NotificationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var stop = await host.AuthenticatedClient.PostAsJsonAsync(
                $"/api/v1/sessions/{sessionId}/control", new { action = "stop", turnId = approval.TurnId })
                .WaitAsync(TimeSpan.FromSeconds(5));
            stop.EnsureSuccessStatusCode();
            Assert.Empty(host.AgentRuntime.ApprovalResponses);
        }
        finally
        {
            clock.ReleaseNotification.TrySetResult();
        }

        using var response = await answer.WaitAsync(TimeSpan.FromSeconds(5));
        response.EnsureSuccessStatusCode();
        Assert.False(cancelTookRequest);
        Assert.Equal(("terminal-approval", true), Assert.Single(host.AgentRuntime.ApprovalResponses));
    }

    private sealed class ApprovalNotificationClock : TimeProvider
    {
        private readonly AsyncLocal<bool> _pauseNextRead = new();
        public TaskCompletionSource NotificationStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseNotification { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void PauseNextRead() => _pauseNextRead.Value = true;

        public override DateTimeOffset GetUtcNow()
        {
            if (_pauseNextRead.Value)
            {
                _pauseNextRead.Value = false;
                NotificationStarted.TrySetResult();
                ReleaseNotification.Task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
            }
            return TimeProvider.System.GetUtcNow();
        }
    }

    private static async IAsyncEnumerable<UnifiedAgentEvent> DelayedTerminalStopTurn(
        Task cancelSent, TimeSpan unwindDelay, [EnumeratorCancellation] CancellationToken ct)
    {
        yield return new UnifiedAgentEvent.ApprovalRequested("delayed-terminal-approval", "run_command", JsonSerializer.Deserialize<JsonElement>("{}"));
        await cancelSent.WaitAsync(ct);
        await Task.Delay(unwindDelay, ct);
        yield return new UnifiedAgentEvent.TurnInterrupted();
    }

    private static async IAsyncEnumerable<UnifiedAgentEvent> WedgedApprovalTurn([EnumeratorCancellation] CancellationToken ct)
    {
        yield return new UnifiedAgentEvent.ApprovalRequested("wedged-terminal-approval", "run_command", JsonSerializer.Deserialize<JsonElement>("{}"));
        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
    }

    private static async IAsyncEnumerable<UnifiedAgentEvent> TerminalApprovalTurn(
        Task stopped, [EnumeratorCancellation] CancellationToken ct)
    {
        yield return new UnifiedAgentEvent.ApprovalRequested("terminal-approval", "run_command", JsonSerializer.Deserialize<JsonElement>("{}"));
        await stopped.WaitAsync(ct);
        yield return new UnifiedAgentEvent.TurnInterrupted();
    }

    private static async Task StartTurn(PergaHostTestCluster host, string sessionId, string key = "approval")
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/sessions/{sessionId}/messages")
        {
            Content = JsonContent.Create(new { content = "run" })
        };
        request.Headers.Add("Idempotency-Key", key);
        using var response = await host.AuthenticatedClient.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task<TrackAggregateSnapshot> Snapshot(PergaHostTestCluster host, string trackId) =>
        (await host.AuthenticatedClient.GetFromJsonAsync<TrackAggregateSnapshot>($"/api/v1/tracks/{trackId}", PergaJsonDefaults.ApiOptions))!;

    private static async Task<ApprovalDto> WaitForApproval(PergaHostTestCluster host, string trackId)
    {
        for (var i = 0; i < 100; i++)
        {
            var ask = (await Snapshot(host, trackId)).PendingApprovals.FirstOrDefault();
            if (ask is not null) return ask;
            await Task.Delay(10);
        }
        throw new TimeoutException("Provider approval was not published.");
    }

    private static string ApprovalPath(string trackId, ApprovalDto approval) =>
        $"/api/v1/sessions/{approval.SessionId}/interactions/{approval.Id}/respond";

    private static void WaitForProviderDecision(PergaHostTestCluster host)
    {
        var answered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.AgentRuntime.ApprovalHandler = () => answered.TrySetResult();
        host.AgentRuntime.SetTurnHandler((_, _, ct) => ApprovalTurn(answered.Task, ct));
    }

    private static async IAsyncEnumerable<UnifiedAgentEvent> ApprovalTurn(
        Task answered, [EnumeratorCancellation] CancellationToken ct)
    {
        yield return new UnifiedAgentEvent.ApprovalRequested("approval-request", "shell", JsonSerializer.SerializeToElement(new { }));
        await answered.WaitAsync(ct);
        yield return new UnifiedAgentEvent.TurnCompleted(new TokenUsage(1, 1, 0, 1000));
    }
}
