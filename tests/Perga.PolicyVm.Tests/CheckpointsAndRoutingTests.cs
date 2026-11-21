using Perga.PolicyVm;

namespace Perga.PolicyVm.Tests;

public sealed class CheckpointsAndRoutingTests
{
    [Fact]
    public void Compile_ParsesCheckpointsAndEnforcedTaskRouting()
    {
        var vm = new PolicyVm();
        var js = """
            export default defineTemplate({
              id: "agents-delivery-test",
              title: "Agents Delivery Test",
              inputs: {
                reviewModel: { type: "string", default: "gpt-6.2-soar" }
              },
              checkpoints: [
                { id: "design", title: "设计", role: "design", access: "read_only", docPath: "docs/design/checkpoint-native-view.md" },
                { id: "review-l2", title: "L2 Review", role: "review", access: "read_only", dependsOn: ["design"] }
              ],
              tasks: {
                routing: [
                  { when: { role: "review" }, model: "${inputs.reviewModel}", enforce: true }
                ]
              }
            });
            """;

        var compiled = vm.Compile(js);
        Assert.NotNull(compiled.Manifest.Checkpoints);
        Assert.Equal(2, compiled.Manifest.Checkpoints!.Count);
        Assert.Equal("design", compiled.Manifest.Checkpoints[0].Id);
        Assert.Equal("docs/design/checkpoint-native-view.md", compiled.Manifest.Checkpoints[0].DocPath);
        Assert.Equal(["design"], compiled.Manifest.Checkpoints[1].DependsOn);

        Assert.NotNull(compiled.Manifest.Routing);
        Assert.Single(compiled.Manifest.Routing!);
        var rule = compiled.Manifest.Routing[0];
        Assert.Equal("review", rule.When.Role);
        Assert.Equal("${inputs.reviewModel}", rule.Model);
        Assert.True(rule.Enforce);
    }

    [Fact]
    public void Compile_RejectsDuplicateCheckpointIds()
    {
        var vm = new PolicyVm();
        var js = """
            export default defineTemplate({
              id: "dup-cp",
              title: "Dup",
              checkpoints: [
                { id: "design", title: "设计" },
                { id: "design", title: "重复设计" }
              ]
            });
            """;

        Assert.Throws<InvalidOperationException>(() => vm.Compile(js));
    }
}
