using System.Text.Json;
using Perga.PolicyVm;

namespace Perga.PolicyVm.Tests;

public sealed class TemplateRoutingTests
{
    private const string OptionalRoutingTemplate = """
        export default {
          id: 'optional-routing', title: 'Optional Routing',
          inputs: {
            reviewProvider: { type: 'string', optional: true },
            reviewModel: { type: 'string', optional: true }
          },
          tasks: { routing: [{
            when: { role: 'review' }, provider: '${inputs.reviewProvider}', model: '${inputs.reviewModel}',
            access: 'read_only', enforce: true
          }] }
        };
        """;

    [Fact]
    public void Compile_PreservesEnforcedTaskRouting()
    {
        var vm = new PolicyVm();
        var compiled = vm.Compile("""
            export default defineTemplate({
              id: "delivery-test",
              title: "Delivery Test",
              inputs: { reviewModel: { type: "string", optional: true, default: "default-reviewer" } },
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
        Assert.Equal("default-reviewer", vm.RouteTaskDispatch(compiled, "review", "read_only", null, null,
            JsonSerializer.SerializeToElement(new { })).Model);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void OmittedOrBlankOptionalTargetsLeaveProviderAndModelUnconstrained(string? blank)
    {
        var vm = new PolicyVm();
        var policy = vm.Compile(OptionalRoutingTemplate);
        var inputs = blank is null
            ? JsonSerializer.SerializeToElement(new { })
            : JsonSerializer.SerializeToElement(new { reviewProvider = blank, reviewModel = blank });

        Assert.Equal(new PolicyTaskRoute(null, null, Access: "read_only"), Assert.Single(vm.ResolveRoutingTargets(policy, inputs)));
        Assert.Equal(new PolicyTaskRoute(null, null, Access: "read_only"),
            vm.RouteTaskDispatch(policy, "review", null, null, null, inputs));
        Assert.Equal(new PolicyTaskRoute("caller-provider", "caller-model", Access: "read_only"),
            vm.RouteTaskDispatch(policy, "review", "read_only", "caller-provider", "caller-model", inputs));
        Assert.Contains("requires access read_only",
            vm.RouteTaskDispatch(policy, "review", "read_write", "caller-provider", "caller-model", inputs).Denial);
    }

    [Fact]
    public void NonblankOptionalTargetsAreResolvedAndEnforced()
    {
        var vm = new PolicyVm();
        var policy = vm.Compile(OptionalRoutingTemplate);
        const string provider = "selected-provider";
        const string model = "selected-model";
        var inputs = JsonSerializer.SerializeToElement(new { reviewProvider = provider, reviewModel = model });

        Assert.Equal(new PolicyTaskRoute(provider, model, Access: "read_only"),
            vm.RouteTaskDispatch(policy, "review", null, null, null, inputs));
        Assert.Contains("requires provider",
            vm.RouteTaskDispatch(policy, "review", null, "other", model, inputs).Denial);
        Assert.Contains("requires model",
            vm.RouteTaskDispatch(policy, "review", null, provider, "other", inputs).Denial);
    }

    [Fact]
    public void BlankOptionalDefaultsLeaveRoutingTargetsUnset()
    {
        var vm = new PolicyVm();
        var policy = vm.Compile("""
            export default {
              id: 'blank-defaults', title: 'Blank Defaults',
              inputs: {
                reviewProvider: { type: 'string', optional: true, default: '' },
                reviewModel: { type: 'string', optional: true, default: '   ' }
              },
              tasks: { routing: [{ when: {}, provider: '${inputs.reviewProvider}', model: '${inputs.reviewModel}' }] }
            };
            """);
        var inputs = JsonSerializer.SerializeToElement(new { });

        Assert.Equal(new PolicyTaskRoute(null, null), Assert.Single(vm.ResolveRoutingTargets(policy, inputs)));
        Assert.Equal(new PolicyTaskRoute(null, null, Access: "read_write"),
            vm.RouteTaskDispatch(policy, null, null, null, null, inputs));
    }

    [Theory]
    [InlineData("${inputs.required}", "required")]
    [InlineData("${inputs.unknown}", "unknown")]
    [InlineData("prefix-${inputs.opt}", "opt")]
    [InlineData("${inputs.opt}\n", "opt")]
    public void MissingInterpolationInputsStillFailClosed(string target, string key)
    {
        var vm = new PolicyVm();
        var policy = vm.Compile($$"""
            export default {
              id: 'strict-routing', title: 'Strict Routing',
              inputs: { required: { type: 'string' }, opt: { type: 'string', optional: true } },
              tasks: { routing: [{ when: {}, model: {{JsonSerializer.Serialize(target)}} }] }
            };
            """);
        var inputs = JsonSerializer.SerializeToElement(new { });

        var error = Assert.Throws<InvalidOperationException>(() => vm.RouteTaskDispatch(policy, null, null, null, null, inputs));
        Assert.Contains($"Missing template input '{key}' for interpolation", error.Message);
    }

    [Theory]
    [InlineData("equity-research", "earnings", "earnings", "read_only")]
    [InlineData("equity-research", "event_review", "review", "read_only")]
    [InlineData("okr-loop", "triage", "triage", "read_only")]
    [InlineData("okr-loop", "optimize", "optimize", "read_write")]
    [InlineData("agents-delivery", "review", "review", "read_only")]
    public void BuiltInTemplatesRouteOptionalProviderAndModel(string template, string role, string inputPrefix, string access)
    {
        var vm = new PolicyVm();
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../templates", template));
        var policy = vm.Compile(TemplateBundle.PackDirectory(path));
        var omitted = JsonSerializer.SerializeToElement(new { });
        var supplied = JsonSerializer.SerializeToElement(new Dictionary<string, string>
        {
            [inputPrefix + "Provider"] = "selected-provider",
            [inputPrefix + "Model"] = "selected-model"
        });

        Assert.Equal(new PolicyTaskRoute(null, null, Access: access),
            vm.RouteTaskDispatch(policy, role, null, null, null, omitted));
        Assert.Equal(new PolicyTaskRoute("selected-provider", "selected-model", Access: access),
            vm.RouteTaskDispatch(policy, role, null, null, null, supplied));

        foreach (var blank in new[] { "", "   " })
        {
            var cleared = JsonSerializer.SerializeToElement(new Dictionary<string, string>
            {
                [inputPrefix + "Provider"] = blank,
                [inputPrefix + "Model"] = blank
            });

            Assert.Equal(new PolicyTaskRoute(null, null, Access: access),
                vm.RouteTaskDispatch(policy, role, null, null, null, cleared));
            Assert.All(vm.ResolveRoutingTargets(policy, cleared), target =>
            {
                Assert.Null(target.Provider);
                Assert.Null(target.Model);
            });
        }
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
