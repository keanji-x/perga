using System.Text.Json;
using Perga.Contracts;
using Perga.Kernel.Tests.Fakes;
using Perga.ReportDoc;

namespace Perga.Kernel.Tests;

[Collection("Template policy sandbox")]
public sealed class TemplateEntryOptimizationTests
{
    private const string SeededTemplateSource = """
        export default defineTemplate({
          id: "delivery-entry",
          title: "交付流程",
          description: "按计划与验收推进",
          report: { initialMarkdown: "# 交付概览\n\n目标与验收标准。\n" },
          data: {
            initial: { stage: "plan" }
          }
        });
        """;

    [Fact]
    public async Task ApplyTrackTemplateByHuman_SeedsBlankReportAndPreservesEditedReport()
    {
        await using var ctx = new KernelTestContext(policyVm: new global::Perga.PolicyVm.PolicyVm());
        ctx.Kernel.Templates.RegisterTemplate(SeededTemplateSource);

        var (_, blankSnap) = await ctx.CreateDefaultTrackAsync();
        var applied = await ctx.Kernel.ApplyTrackTemplateAsync(
            blankSnap.Track.Id,
            new ApplyTrackTemplateRequest(TemplateId: "delivery-entry"));

        Assert.Equal("delivery-entry", applied.TemplateId);
        Assert.Equal("plan", applied.Data.GetProperty("stage").GetString());
        var seededReport = await ctx.Kernel.ReadReportAsync(blankSnap.Track.Id);
        Assert.Equal(1, seededReport.DocRev);
        Assert.Contains("交付概览", seededReport.RenderedMarkdown, StringComparison.Ordinal);

        var (_, editedSnap) = await ctx.CreateDefaultTrackAsync();
        var initial = await ctx.Kernel.ReadReportAsync(editedSnap.Track.Id);
        await ctx.Kernel.CommitReportAsync(
            editedSnap.Track.Id,
            editedSnap.Track.LeadSessionId!,
            new ReportCommitRequest(
                Anchor: initial.Anchor,
                Ops: [new BlockOp.UpsertBlockOp(null, ReportBlockKind.Prose, "# 手动记录\n\n已写下的正文。\n", null, null)],
                Summary: "Manual edit"));

        var appliedEdited = await ctx.Kernel.ApplyTrackTemplateAsync(
            editedSnap.Track.Id,
            new ApplyTrackTemplateRequest(TemplateId: "delivery-entry"));

        Assert.Equal("delivery-entry", appliedEdited.TemplateId);
        var preservedReport = await ctx.Kernel.ReadReportAsync(editedSnap.Track.Id);
        Assert.Contains("手动记录", preservedReport.RenderedMarkdown, StringComparison.Ordinal);
        Assert.DoesNotContain("交付概览", preservedReport.RenderedMarkdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartTemplateSelector_SkipsWhenCatalogEmptyOrAlreadyTemplatedOrDisabled()
    {
        await using (var emptyCatalogCtx = new KernelTestContext(policyVm: new global::Perga.PolicyVm.PolicyVm()))
        {
            var (_, snap) = await emptyCatalogCtx.CreateDefaultTrackAsync();
            var skipped = await emptyCatalogCtx.Kernel.StartTemplateSelectorAsync(
                snap.Track.Id,
                new StartTemplateSelectorRequest("帮我规划一个迭代"));
            Assert.Null(skipped);

            var blankErr = await Assert.ThrowsAsync<KernelException>(() =>
                emptyCatalogCtx.Kernel.StartTemplateSelectorAsync(
                    snap.Track.Id,
                    new StartTemplateSelectorRequest("   ")));
            Assert.Equal(KernelErrorCode.InvalidArgument, blankErr.Code);
        }

        await using (var disabledCtx = new KernelTestContext(
            options: new KernelOptions { EnableTemplateSelectorSubagent = false },
            policyVm: new global::Perga.PolicyVm.PolicyVm()))
        {
            disabledCtx.Kernel.Templates.RegisterTemplate(SeededTemplateSource);
            var (_, snap) = await disabledCtx.CreateDefaultTrackAsync();
            var skipped = await disabledCtx.Kernel.StartTemplateSelectorAsync(
                snap.Track.Id,
                new StartTemplateSelectorRequest("帮我规划一个迭代"));
            Assert.Null(skipped);
        }

        await using (var templatedCtx = new KernelTestContext(policyVm: new global::Perga.PolicyVm.PolicyVm()))
        {
            templatedCtx.Kernel.Templates.RegisterTemplate(SeededTemplateSource);
            var (area, _) = await templatedCtx.CreateDefaultTrackAsync();
            var templated = await templatedCtx.Kernel.CreateTrackAsync(
                new CreateTrackRequest(area.Id, TemplateId: "delivery-entry"));
            var skipped = await templatedCtx.Kernel.StartTemplateSelectorAsync(
                templated.Track.Id,
                new StartTemplateSelectorRequest("帮我规划一个迭代"));
            Assert.Null(skipped);

            await templatedCtx.Kernel.CloseTrackAsync(templated.Track.Id, templated.Track.LeadSessionId!);
            var closedErr = await Assert.ThrowsAsync<KernelException>(() =>
                templatedCtx.Kernel.StartTemplateSelectorAsync(
                    templated.Track.Id,
                    new StartTemplateSelectorRequest("帮我规划一个迭代")));
            Assert.Equal(KernelErrorCode.Conflict, closedErr.Code);
        }
    }

    [Fact]
    public async Task LeadSystemPrompt_DescribesTrackSurfacesAndComposableTemplateModel()
    {
        await using var ctx = new KernelTestContext(policyVm: new global::Perga.PolicyVm.PolicyVm());
        var (_, snap) = await ctx.CreateDefaultTrackAsync();
        await ctx.SendAndWaitForTurnAsync(snap.Track.LeadSessionId!, new SendSessionMessageRequest("帮我做小米投研", "lead-prompt-check"));

        var leadPrompt = Assert.Single(ctx.AgentRuntime.OpenedSessions).LaunchSpec.SystemPrompt;
        Assert.Contains("## Track Model", leadPrompt, StringComparison.Ordinal);
        Assert.Contains("**Title**", leadPrompt, StringComparison.Ordinal);
        Assert.Contains("**Report**", leadPrompt, StringComparison.Ordinal);
        Assert.Contains("Templates are optional and implicit", leadPrompt, StringComparison.Ordinal);
        Assert.Contains("composable rather than all-or-nothing", leadPrompt, StringComparison.Ordinal);
        Assert.Contains("format: \"files\"", leadPrompt, StringComparison.Ordinal);
    }
}
