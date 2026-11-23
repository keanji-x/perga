using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Perga.Host.IntegrationTests.Fixtures;
using Perga.Kernel;
using Perga.Kernel.Mcp;
using Perga.Kernel.Ports;
using Perga.Storage.Sqlite;

namespace Perga.Host.IntegrationTests;

public sealed class McpQueuedWritesIntegrationTests
{
    [Theory]
    [InlineData(KernelMcpToolNames.TrackSet, nameof(ITrackStore.UpdateTrackTemplateAndEmitAsync), false)]
    [InlineData(KernelMcpToolNames.TrackSet, nameof(ITrackStore.UpdateTrackTemplateAndEmitAsync), true)]
    [InlineData(KernelMcpToolNames.TemplateUpdateInputs, nameof(ITrackStore.UpdateTrackTemplateAndEmitAsync))]
    [InlineData(KernelMcpToolNames.FlowCommit, nameof(ITrackStore.CommitTrackFlowAsync))]
    [InlineData(KernelMcpToolNames.ReportCommit, nameof(ITrackStore.SaveReportAsync))]
    [InlineData(KernelMcpToolNames.DataPatch, nameof(ITrackStore.UpdateTrackAndEmitAsync))]
    public async Task RevokedRuntimeCannotCommitAnMcpWriteQueuedAtSqlite(string tool, string storeMethod, bool clear = false)
    {
        AnswerPublicationStoreProxy proxy = null!;
        await using var host = new PergaHostTestCluster(configureHostOptions: options =>
        {
            options.EnableScheduleService = false;
            options.EnableMaintenanceService = false;
        }, configureExtraServices: services =>
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

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = deadline.Token;
        var (trackId, leadId) = await host.CreateTrackAsync();
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        var caller = await kernel.OpenMcpCallerAsync(leadId, ct);
        await kernel.InvokeKernelMcpToolAsync(caller, KernelMcpToolNames.TrackSet, JsonSerializer.SerializeToElement(new
        {
            source = """
                export default { id: "initial", title: "Initial",
                    inputs: { label: { type: "string", default: "initial" } },
                    data: { initial: { label: "initial" } } };
                """
        }), ct);
        var before = (await proxy.Inner.GetTrackAsync(trackId, ct))!;
        var report = await kernel.ReadReportAsync(trackId, ct: ct);
        object args = tool switch
        {
            KernelMcpToolNames.TrackSet when clear => new { clear = true, resetData = true },
            KernelMcpToolNames.TrackSet => new { source = """export default { id: "replacement", title: "Replacement" };""" },
            KernelMcpToolNames.TemplateUpdateInputs => new { inputs = new { label = "changed" } },
            KernelMcpToolNames.FlowCommit => new { content = $$$"""
                {"rev":{{{before.FlowRev}}},"nodes":[{"id":"queued","when":{"at":"2099-01-01T00:00:00Z"},
                  "do":{"kind":"run","cmd":"true","access":"read_only"}}]}
                """ },
            KernelMcpToolNames.ReportCommit => new
            {
                anchor = report.Anchor,
                ops = new[] { new { op = "replace_section", sectionH1 = "Queued", markdown = "Must not persist" } }
            },
            KernelMcpToolNames.DataPatch => new { patch = new { label = "changed" } },
            _ => throw new ArgumentOutOfRangeException(nameof(tool))
        };

        const BindingFlags privateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        var context = typeof(SqliteTrackStore).GetField("_context", privateInstance)!.GetValue(proxy.Inner)!;
        var writer = (SqliteSingleWriter)context.GetType().GetProperty("Writer", privateInstance)!.GetValue(context)!;
        var writerBlocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseWriter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hold = writer.EnqueueAsync(async (_, _, token) =>
        {
            writerBlocked.TrySetResult();
            await releaseWriter.Task.WaitAsync(token);
        }, ct);
        var queued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        proxy.AfterStoreCall = name =>
        {
            if (name == storeMethod) queued.TrySetResult();
        };
        Task<JsonElement> write;
        try
        {
            await writerBlocked.Task.WaitAsync(ct);
            write = kernel.InvokeKernelMcpToolAsync(caller, tool, JsonSerializer.SerializeToElement(args), ct);
            // Surface malformed requests immediately if they never enter the writer queue.
            if (await Task.WhenAny(queued.Task, write).WaitAsync(ct) == write)
                await write;
            Assert.True(queued.Task.IsCompleted);
            Assert.False(write.IsCompleted);
            kernel.McpSessionBridge.RevokeSession(leadId, caller.RuntimeBindingId);
        }
        finally { releaseWriter.TrySetResult(); }

        await hold.WaitAsync(ct);
        var error = await Assert.ThrowsAsync<KernelException>(() => write.WaitAsync(ct));
        Assert.Equal(KernelErrorCode.Conflict, error.Code);
        var after = (await proxy.Inner.GetTrackAsync(trackId, ct))!;
        Assert.Equal(before.TemplateId, after.TemplateId);
        Assert.True(JsonElement.DeepEquals(before.TemplateInputs, after.TemplateInputs));
        Assert.Equal(before.DataRev, after.DataRev);
        Assert.Equal(before.FlowRev, after.FlowRev);
        Assert.Equal(report.DocRev, (await kernel.ReadReportAsync(trackId, ct: ct)).DocRev);
    }
}
