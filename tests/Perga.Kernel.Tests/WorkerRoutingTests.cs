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
    public async Task Flow_AppliesEnforcedRoutingModel_AndBlocksConflicts()
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

        var denied = await FlowFixture.AppendAsync(ctx.Kernel, track.Track.Id,
            FlowFixture.Worker("denied", "Run L2 review", model: "gpt-5", role: "review"));
        Assert.False(denied.Ok);
        Assert.Contains(denied.Diagnostics, diagnostic => diagnostic.Message.Contains("gpt-6.2-soar", StringComparison.Ordinal));

        var receipt = await FlowFixture.StartWorkerAsync(ctx.Kernel, track.Track.Id,
            FlowFixture.Worker("review", "Run L2 review", role: "review"));

        Assert.Equal("codex", receipt.Session.Provider);
        Assert.Equal("gpt-6.2-soar", receipt.Session.Model);
    }

    [Fact]
    public async Task TemplateV2_DataPatch_Views_And_SetupInputsRouting_EnforcedEndToEnd()
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
                        template: "./report.md",
                        views: {
                          summary: (data) => ({ currentPrice: data.price, items: [] }),
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

        var deniedAccess = await FlowFixture.AppendAsync(ctx.Kernel, track.Track.Id,
            FlowFixture.Worker("bad-access", "Review", "read_write", model: "o3-pro", role: "review"));
        Assert.False(deniedAccess.Ok);
        var deniedModel = await FlowFixture.AppendAsync(ctx.Kernel, track.Track.Id,
            FlowFixture.Worker("bad-model", "Review", model: "gpt-5", role: "review"));
        Assert.False(deniedModel.Ok);
        var okReceipt = await FlowFixture.StartWorkerAsync(ctx.Kernel, track.Track.Id,
            FlowFixture.Worker("review", "Review with custom model", role: "review"));
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
        Assert.Equal(148.5, views.Views["summary"].GetProperty("currentPrice").GetDouble());

        // 3b. MCP perga_report_read automatically includes projected templateViews for widget tracks
        var leadSession = Assert.Single(track.Sessions, s => s.IsLead);
        var mcpReport = await KernelMcpDispatcher.ExecuteGuardedAsync(
            ctx.Kernel,
            new McpSessionPrincipal("tok", track.Track.Id, leadSession.Id, IsLead: true, BoundTaskId: null, TaskRunCount: null, EnabledPlugins: []),
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
