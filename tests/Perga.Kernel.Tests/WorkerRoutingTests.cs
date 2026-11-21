using Perga.AgentKit;
using Perga.Contracts;
using Perga.Kernel.Events;
using Perga.Kernel.Mcp;
using Perga.Kernel.Ports;
using Perga.Kernel.Tests.Fakes;

namespace Perga.Kernel.Tests;

public sealed class WorkerRoutingTests
{
    private static readonly InteropPrincipal Human = new InteropPrincipal.Human();

    private const string RoutingTemplateSource = """
        export default defineTemplate({
          id: "agents-delivery-routing",
          title: "Agents Delivery Routing",
          inputs: {
            reviewModel: { type: "string", default: "gpt-6.2-soar" }
          },
          checkpoints: [
            { id: "design", title: "设计", role: "design", access: "read_only" },
            { id: "review-l2", title: "L2 Review", role: "review", access: "read_only", dependsOn: ["design"] }
          ],
          tasks: {
            routing: [
              { when: { role: "review" }, provider: "codex", model: "${inputs.reviewModel}", enforce: true }
            ]
          }
        });
        """;

    [Fact]
    public async Task CreateSession_AppliesEnforcedRoutingModel_AndBlocksConflicts()
    {
        await using var ctx = new KernelTestContext(policyVm: new Perga.PolicyVm.PolicyVm());
        ctx.AgentRuntime.Providers = [new ProviderStatus("codex", true, "1.0.0", null, ["gpt-6.2-soar", "gpt-5"])];
        ctx.Kernel.Templates.RegisterTemplate(RoutingTemplateSource);

        var area = await ctx.Kernel.CreateAreaAsync(new CreateAreaRequest("Area"));
        var track = await ctx.Kernel.CreateTrackAsync(new CreateTrackRequest(
            AreaId: area.Id,
            Title: "Routing Test",
            Cwd: "/repo",
            TemplateId: "agents-delivery-routing"));

        // Conflicting model for review role must be blocked with PolicyDenied.
        var denied = await Assert.ThrowsAsync<KernelException>(() =>
            ctx.Kernel.CreateSessionAsync(
                track.Track.Id,
                new CreateSessionRequest(
                    Prompt: "Run L2 review",
                    Model: "gpt-5",
                    Role: "review",
                    Task: new SessionTaskRequest(Access: "read_only")),
                requestKey: "rk-denied",
                caller: Human,
                ct: CancellationToken.None));
        Assert.Equal(KernelErrorCode.PolicyDenied, denied.Code);
        Assert.Contains("gpt-6.2-soar", denied.Message, StringComparison.Ordinal);

        // Omitting model auto-fills from template routing.
        var (receipt, _) = await ctx.Kernel.CreateSessionAsync(
            track.Track.Id,
            new CreateSessionRequest(
                Prompt: "Run L2 review",
                Role: "review",
                Task: new SessionTaskRequest(Access: "read_only")),
            requestKey: "rk-ok",
            caller: Human,
            ct: CancellationToken.None);

        Assert.Equal("codex", receipt.Session.Provider);
        Assert.Equal("gpt-6.2-soar", receipt.Session.Model);
    }

    [Fact]
    public async Task TemplateV2_DataPatch_Views_HumanReadOnly_And_SetupInputsRouting_EnforcedEndToEnd()
    {
        await using var ctx = new KernelTestContext(policyVm: new Perga.PolicyVm.PolicyVm());
        ctx.AgentRuntime.Providers = [new ProviderStatus("codex", true, "1.0.0", null, ["o3-pro", "gpt-5"])];

        var files = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["template.perga.js"] = """
                export default defineTemplate({
                  id: "v2-kernel-e2e",
                  title: "V2 Kernel E2E",
                  inputs: { reviewModel: { type: "string", default: "gpt-5" } },
                  setup(inputs) {
                    return {
                      data: {
                        schema: (t) => ({
                          price: t.number(),
                        }),
                        initial: () => ({ price: 100 }),
                      },
                      report: {
                        humanReadOnly: true,
                        template: "./report.md",
                        views: {
                          summary: (data) => ({ currentPrice: data.price }),
                        },
                      },
                      tasks: {
                        routing: [
                          { when: { role: "review" }, provider: "codex", model: inputs.reviewModel, access: "read_only", enforce: true },
                        ],
                      },
                    };
                  },
                });
                """,
            ["report.md"] = "# Target\n\nTarget prose.\n\n# Live\n\n```perga-block widget\n{\"use\":\"builtin:list\",\"view\":\"summary\"}\n```\n",
        };
        var bundleSource = Perga.PolicyVm.TemplateBundle.BundleHeader
            + System.Text.Json.JsonSerializer.Serialize(new { entry = "template.perga.js", files });
        ctx.Kernel.Templates.RegisterTemplate(bundleSource);

        var area = await ctx.Kernel.CreateAreaAsync(new CreateAreaRequest("Area"));
        var track = await ctx.Kernel.CreateTrackAsync(new CreateTrackRequest(
            AreaId: area.Id,
            Title: "V2 E2E",
            Cwd: "/repo",
            TemplateId: "v2-kernel-e2e",
            TemplateInputs: TestJson.Parse("""{"reviewModel":"o3-pro"}""")));

        // 1. setup(inputs) dynamic routing enforces custom reviewModel AND TargetAccess=read_only
        var deniedAccess = await Assert.ThrowsAsync<KernelException>(() =>
            ctx.Kernel.CreateSessionAsync(
                track.Track.Id,
                new CreateSessionRequest(
                    Prompt: "Review",
                    Role: "review",
                    Model: "o3-pro",
                    Task: new SessionTaskRequest(Access: "read_write")),
                requestKey: "rk-access-denied",
                caller: Human,
                ct: CancellationToken.None));
        Assert.Equal(KernelErrorCode.PolicyDenied, deniedAccess.Code);

        var deniedModel = await Assert.ThrowsAsync<KernelException>(() =>
            ctx.Kernel.CreateSessionAsync(
                track.Track.Id,
                new CreateSessionRequest(
                    Prompt: "Review",
                    Role: "review",
                    Model: "gpt-5",
                    Task: new SessionTaskRequest(Access: "read_only")),
                requestKey: "rk-model-denied",
                caller: Human,
                ct: CancellationToken.None));
        Assert.Equal(KernelErrorCode.PolicyDenied, deniedModel.Code);

        // 2. HumanReadOnly blocks PUT report block edit while allowing routed CreateSessionAsync
        var proseBlock = track.Report.Blocks.First(b => b.Kind == Perga.ReportDoc.ReportBlockKind.Prose);
        var humanEditDenied = await Assert.ThrowsAsync<KernelException>(() =>
            ctx.Kernel.UpdateReportBlockByHumanAsync(
                track.Track.Id,
                proseBlock.Id,
                new HumanBlockUpdateRequest(track.Report.DocRev, proseBlock.Rev, Markdown: "Hacked by human")));
        Assert.Equal(KernelErrorCode.PolicyDenied, humanEditDenied.Code);

        var (okReceipt, _) = await ctx.Kernel.CreateSessionAsync(
            track.Track.Id,
            new CreateSessionRequest(
                Prompt: "Review with custom model",
                Role: "review",
                Task: new SessionTaskRequest(Access: "read_only")),
            requestKey: "rk-ok-v2",
            caller: Human,
            ct: CancellationToken.None);
        Assert.Equal("o3-pro", okReceipt.Session.Model);
        Assert.Equal(AccessMode.ReadOnly, okReceipt.Task!.Access);

        // 3. PatchTrackDataAsync validates Fail-Closed, updates views, and emits TrackUpdated
        var invalidPatch = await Assert.ThrowsAsync<KernelException>(() =>
            ctx.Kernel.PatchTrackDataAsync(track.Track.Id, TestJson.Parse("""{"price":"not-a-number"}"""), source: "api"));
        Assert.Equal(KernelErrorCode.InvalidArgument, invalidPatch.Code);

        var beforeTip = await ctx.Store.GetEventTipIdAsync(CancellationToken.None);
        var patched = await ctx.Kernel.PatchTrackDataAsync(track.Track.Id, TestJson.Parse("""{"price":148.5}"""), source: "api");
        Assert.Equal(148.5, patched.Data.GetProperty("price").GetDouble());

        var events = await ctx.Store.ListEventsSinceAsync(beforeTip, track.Track.Id, 50, CancellationToken.None);
        Assert.Contains(events, e => e.Kind == KernelEventKinds.TrackUpdated);

        var views = await ctx.Kernel.GetTrackViewsAsync(track.Track.Id);
        Assert.Equal(1, views.DataRev);
        Assert.True(views.HumanReadOnly);
        Assert.Equal(148.5, views.Views["summary"].GetProperty("currentPrice").GetDouble());

        // 3b. MCP perga_report_read automatically includes projected templateViews for widget tracks
        var leadSession = Assert.Single(track.Sessions, s => s.IsLead);
        var mcpReport = await KernelMcpDispatcher.ExecuteGuardedAsync(
            ctx.Kernel,
            new McpSessionPrincipal("tok", track.Track.Id, leadSession.Id, IsLead: true, BoundTaskId: null, BoundAttemptId: null, EnabledPlugins: []),
            KernelMcpToolNames.ReportRead,
            TestJson.Parse("""{"format":"full"}"""),
            CancellationToken.None);
        Assert.Equal(148.5, mcpReport.GetProperty("templateViews").GetProperty("summary").GetProperty("currentPrice").GetDouble());

        // 4. Closed track rejects PatchTrackDataAsync with Conflict
        await ctx.Kernel.CloseTrackAsync(track.Track.Id, leadSession.Id);
        var closedErr = await Assert.ThrowsAsync<KernelException>(() =>
            ctx.Kernel.PatchTrackDataAsync(track.Track.Id, TestJson.Parse("""{"price":200}"""), source: "api"));
        Assert.Equal(KernelErrorCode.Conflict, closedErr.Code);
    }
}
