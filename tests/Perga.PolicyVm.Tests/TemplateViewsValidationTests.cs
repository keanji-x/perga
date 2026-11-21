using Perga.PolicyVm;

namespace Perga.PolicyVm.Tests;

public sealed class TemplateViewsValidationTests
{
    private readonly PolicyVm _vm = new();

    [Fact]
    public void Compile_BuiltinFlowWithKernelFlowView_SucceedsWithoutDeclaringReportView()
    {
        const string source = """
            export default defineTemplate({
              id: "flow-only",
              title: "Flow Only",
              report: {
                initialMarkdown: "# Flow\n\n```perga-block widget\n{\"use\":\"builtin:flow\",\"view\":\"flow\"}\n```"
              }
            });
            """;

        var compiled = _vm.Compile(source);
        Assert.Equal("flow-only", compiled.TemplateId);
    }

    [Fact]
    public void Compile_ValidBuiltinViews_PassContractValidation()
    {
        const string source = """
            export default defineTemplate({
              id: "valid-builtins",
              title: "Valid Builtins",
              data: {
                initial: () => ({ ok: true })
              },
              report: {
                initialMarkdown: [
                  "```perga-block widget",
                  '{"use":"builtin:stepper","view":"steps"}',
                  "```",
                  "```perga-block widget",
                  '{"use":"builtin:list","view":"events"}',
                  "```",
                  "```perga-block widget",
                  '{"use":"builtin:table","view":"krs"}',
                  "```",
                  "```perga-block widget",
                  '{"use":"builtin:detail","view":"steps"}',
                  "```"
                ].join("\n"),
                views: {
                  steps: (_data, kernel) => ({
                    items: [{ id: "s1", title: "Step 1", status: kernel.flow.nodes.length === 0 ? "pending" : "done" }]
                  }),
                  events: () => ({ items: [] }),
                  krs: () => ({ metrics: [{ label: "P95", current: "120ms", status: "on_track", direction: "lte" }] })
                }
              }
            });
            """;

        var compiled = _vm.Compile(source);
        Assert.Equal("valid-builtins", compiled.TemplateId);
    }

    [Theory]
    [InlineData("builtin:stepper", "() => ({ wrong: [] })", "items")]
    [InlineData("builtin:list", "() => ({ items: [{ id: 'a', title: 'A', status: 'bogus' }] })", "status")]
    [InlineData("builtin:table", "() => ({ rows: [] })", "metrics")]
    [InlineData("builtin:table", "() => ({ metrics: [{ label: 'KR', direction: 'sideways' }] })", "direction")]
    [InlineData("builtin:detail", "() => ({ items: [{ id: 'a', status: 'done' }] })", "title")]
    [InlineData("builtin:flow", "() => ({ items: [] })", "nodes")]
    [InlineData("builtin:unknown", "() => ({})", "Unknown builtin widget")]
    [InlineData("builtin:list", "() => ({ items: [{ id: 'a', title: 'A', status: 'done', summary: 42 }] })", "summary")]
    [InlineData("builtin:table", "() => ({ metrics: [{ label: 'KR', target: 'invalid' }] })", "target")]
    [InlineData("builtin:flow", "() => ({ nodes: [{ id: 'a', title: 'A', status: 'invalid' }] })", "status")]
    public void Compile_BrokenBuiltinViewContract_FailsFastWithActionableError(
        string widgetUse,
        string viewFnJs,
        string expectedFragment)
    {
        var source = $$"""
            export default defineTemplate({
              id: "bad-view",
              title: "Bad View",
              report: {
                initialMarkdown: "```perga-block widget\n{\"use\":\"{{widgetUse}}\",\"view\":\"v\"}\n```",
                views: {
                  v: {{viewFnJs}}
                }
              }
            });
            """;

        var ex = Assert.Throws<InvalidOperationException>(() => _vm.Compile(source));
        Assert.Contains(expectedFragment, ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
