using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Perga.Contracts;
using Perga.Host.IntegrationTests.Fixtures;
using Perga.Host.Serialization;
using Perga.Kernel;
using Perga.Kernel.Ports;
using Perga.Storage.Sqlite;

namespace Perga.Host.IntegrationTests;

// Keep real SQLite transactions; inject only at the store port after commit.
public class AnswerPublicationStoreProxy : DispatchProxy
{
    public ITrackStore Inner = null!;
    public Func<CancellationToken, Task>? NextEventRead;
    public Action? AfterNextAskWrite;
    public Action? BeforeNextApprovalAudit;

    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        if (method!.Name == nameof(ITrackStore.ListEventsSinceAsync) &&
            Interlocked.Exchange(ref NextEventRead, null) is { } readHook)
            return ReadAfterHook(method, args!, readHook);
        if (method.Name == nameof(ITrackStore.AppendEventAsync) && args?[0] is EventEnvelopeDto { Kind: "approval.decided" })
            Interlocked.Exchange(ref BeforeNextApprovalAudit, null)?.Invoke();
        var result = Forward(method, args);
        if (method.Name == nameof(ITrackStore.UpsertAskAsync) &&
            Interlocked.Exchange(ref AfterNextAskWrite, null) is { } writeHook)
            return AfterWrite((Task)result!, writeHook);
        return result;
    }

    private async Task<IReadOnlyList<EventEnvelopeDto>> ReadAfterHook(
        MethodInfo method, object?[] args, Func<CancellationToken, Task> hook)
    {
        await hook((CancellationToken)args[^1]!);
        return await (Task<IReadOnlyList<EventEnvelopeDto>>)Forward(method, args)!;
    }

    private static async Task AfterWrite(Task write, Action hook)
    {
        await write;
        hook();
    }

    private object? Forward(MethodInfo method, object?[]? args)
    {
        try { return method.Invoke(Inner, args); }
        catch (TargetInvocationException ex)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException!).Throw();
            throw;
        }
    }
}

public sealed class AnswerPublicationTests
{
    [Theory]
    [InlineData("read-failure")]
    [InlineData("cancel-after-commit")]
    [InlineData("cancel-during-sync")]
    public async Task CommittedDenialResumesOriginalWaiterExactlyOnceAndRejectsOppositeRetry(string fault)
    {
        AnswerPublicationStoreProxy proxy = null!;
        await using var host = new PergaHostTestCluster(configureExtraServices: services =>
        {
            services.RemoveAll<ITrackStore>();
            services.AddSingleton<ITrackStore>(sp =>
            {
                var store = DispatchProxy.Create<ITrackStore, AnswerPublicationStoreProxy>();
                proxy = (AnswerPublicationStoreProxy)store;
                var sqlite = sp.GetRequiredService<SqliteTrackStore>();
                sqlite.InitializeAsync().GetAwaiter().GetResult();
                proxy.Inner = sqlite;
                return store;
            });
        });

        var (trackId, sessionId) = await host.CreateTrackAsync();
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        var question = kernel.AskUserAsync(trackId, sessionId, [new AskQuestionDto("Continue?", ["Yes", "No"])]);

        AskDto? ask = null;
        for (var i = 0; i < 100 && ask is null; i++)
        {
            ask = (await Snapshot(host, trackId)).OpenAsks.SingleOrDefault();
            if (ask is null) await Task.Delay(10);
        }
        Assert.NotNull(ask);
        using var cancellation = new CancellationTokenSource();
        var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (fault == "read-failure")
            proxy.NextEventRead = _ => throw new IOException("injected post-commit event read failure");
        else if (fault == "cancel-after-commit")
            proxy.AfterNextAskWrite = cancellation.Cancel;
        else
            proxy.NextEventRead = async ct =>
            {
                reading.SetResult();
                await release.Task;
                ct.ThrowIfCancellationRequested();
            };

        var answer = kernel.RespondInteractionAsync(ask.SessionId!, ask.Id, new(Answers: JsonSerializer.SerializeToElement(false)), new InteropPrincipal.Human(), cancellation.Token);
        if (fault == "read-failure")
            await Assert.ThrowsAsync<IOException>(() => answer);
        else
        {
            if (fault == "cancel-during-sync")
            {
                await reading.Task.WaitAsync(TimeSpan.FromSeconds(5));
                cancellation.Cancel();
            }
            // Cancellation may race with a successfully returned committed answer.
            var error = await Record.ExceptionAsync(() => answer);
            Assert.True(error is null or OperationCanceledException, error?.ToString());
            release.TrySetResult();
        }

        // A snapshot queues behind the answer and observes its final actor state.
        Assert.DoesNotContain((await Snapshot(host, trackId)).OpenAsks, a => a.Id == ask.Id);
        Assert.False((await question.WaitAsync(TimeSpan.FromSeconds(5))).GetBoolean());
        using var retry = await host.Client.PostAsJsonAsync($"/api/v1/sessions/{ask.SessionId}/interactions/{ask.Id}/respond", new { answers = true });
        Assert.Equal(HttpStatusCode.Conflict, retry.StatusCode);

        await using var db = new SqliteConnection($"Data Source={Path.Combine(host.DataDir, "perga.db")};Pooling=False");
        await db.OpenAsync();
        Assert.True(await db.ExecuteScalarAsync<long>("""
            SELECT id FROM events WHERE kind='ask.changed'
            AND json_extract(payload_json,'$.id')=@Id AND json_extract(payload_json,'$.status')='answered'
            """, ask) > 0);
        Assert.Equal("false", await db.ExecuteScalarAsync<string>("SELECT answer_json FROM asks WHERE id=@Id", ask));
        Assert.Equal(1, await db.ExecuteScalarAsync<int>("""
            SELECT COUNT(*) FROM events WHERE kind='ask.changed'
            AND json_extract(payload_json,'$.id')=@Id AND json_extract(payload_json,'$.status')='answered'
            """, ask));
    }

    private static async Task<TrackAggregateSnapshot> Snapshot(PergaHostTestCluster host, string trackId) =>
        (await host.Client.GetFromJsonAsync<TrackAggregateSnapshot>($"/api/v1/tracks/{trackId}", PergaJsonDefaults.ApiOptions))!;

}
