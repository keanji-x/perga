using System.Text.Json;
using Perga.PolicyVm;

namespace Perga.PolicyVm.Tests;

public sealed class TemplateRoutingTests
{
    [Fact]
    public void Compile_PreservesEnforcedTaskRouting()
    {
        var vm = new PolicyVm();
        var compiled = vm.Compile("""
            export default defineTemplate({
              id: "delivery-test",
              title: "Delivery Test",
              inputs: { reviewModel: { type: "string", default: "default-reviewer" } },
              tasks: {
                routing: [
                  { when: { role: "review" }, model: "${inputs.reviewModel}", enforce: true }
                ]
              }
            });
            """);

        var rule = Assert.Single(compiled.Manifest.Routing);
        Assert.Equal("review", rule.When.Role);
        Assert.Equal("${inputs.reviewModel}", rule.Model);
        Assert.True(rule.Enforce);

        var inputs = JsonSerializer.SerializeToElement(new { reviewModel = "selected-reviewer" });
        var route = vm.RouteTaskDispatch(compiled, "review", "read_only", null, null, inputs);
        Assert.Equal("selected-reviewer", route.Model);
        Assert.Null(route.Denial);
        Assert.NotNull(vm.RouteTaskDispatch(compiled, "review", "read_only", null, "other", inputs).Denial);
    }

    [Fact]
    public void Compile_IgnoresRetiredManifestFieldsInFrozenSnapshot()
    {
        var vm = new PolicyVm();
        var snapshot = PolicyVm.WithToolNameVersion("""
            export default defineTemplate({
              id: "legacy",
              title: "Legacy Template",
              checkpoints: [{ id: "design", title: "Design" }, { id: "design", title: "Duplicate" }],
              report: { humanReadOnly: "ignored", views: { example: () => ({ count: 1 }) } },
              initialState: () => ({ visits: 3 })
            });
            """, legacy: true);

        var compiled = vm.Compile(snapshot);
        Assert.True(compiled.UsesLegacyBeforeToolNames);
        Assert.Equal(3, vm.InitState(compiled, default).Output.GetProperty("visits").GetInt32());
        var projection = vm.ProjectViews(compiled, default, default, default);
        Assert.Empty(projection.Diagnostics);
        Assert.Equal(1, projection.Views["example"].GetProperty("count").GetInt32());
    }
}
