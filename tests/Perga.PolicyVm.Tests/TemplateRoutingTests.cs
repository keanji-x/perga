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
    public void OmittedAccessIsFilledThenUsedByLaterAccessRules()
    {
        IPolicyVm vm = new PolicyVm();
        var policy = vm.Compile("""
            export default {
              id: 'routed', title: 'Routed',
              tasks: { routing: [
                { when: { role: 'review' }, access: 'read_only', enforce: true },
                { when: { access: 'read_only' }, model: 'reviewer' },
                { when: {}, access: 'read_write', enforce: true }
              ] }
            };
            """);
        var inputs = JsonSerializer.SerializeToElement(new { });

        var omitted = vm.RouteTaskDispatch(policy, "review", null, null, null, inputs);
        Assert.Equal(new PolicyTaskRoute(null, "reviewer", Access: "read_only"), omitted);
        Assert.Contains("requires access read_only", vm.RouteTaskDispatch(policy, "review", "read_write", null, null, inputs).Denial);
        Assert.Equal("read_write", vm.RouteTaskDispatch(policy, "other", null, null, null, inputs).Access);
    }

    [Fact]
    public void AccessOnlyReadWriteRuleMatchesOmittedAccessEvenBeforeRoleRule()
    {
        IPolicyVm vm = new PolicyVm();
        var policy = vm.Compile("""
            export default {
              id: 'access-first', title: 'Access First',
              tasks: { routing: [
                { when: { access: 'read_write' }, model: 'writer-model', enforce: true },
                { when: { role: 'review' }, access: 'read_only', model: 'review-model', enforce: true }
              ] }
            };
            """);
        var inputs = JsonSerializer.SerializeToElement(new { });

        var defaultWrite = vm.RouteTaskDispatch(policy, null, null, null, null, inputs);
        Assert.Equal(new PolicyTaskRoute(null, "writer-model", Access: "read_write"), defaultWrite);
        Assert.Contains("requires model writer-model", vm.RouteTaskDispatch(policy, null, null, null, "other-model", inputs).Denial);

        var reviewOmitted = vm.RouteTaskDispatch(policy, "review", null, null, null, inputs);
        Assert.Equal(new PolicyTaskRoute(null, "review-model", Access: "read_only"), reviewOmitted);
    }
}
