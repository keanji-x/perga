using Perga.Contracts;

namespace Perga.Kernel.Tests;

public sealed partial class TrackFlowTests
{
    [Fact]
    public void ParserAcceptsMinimalTrackAction()
    {
        var (spec, errors) = TrackFlowParser.Parse("""
            {"rev":0,"nodes":[{"id":"spawn","do":{"kind":"track","prompt":"Investigate caching"}}]}
            """);
        Assert.Empty(errors);
        Assert.Equal(new FlowDoSpecDto("track", Prompt: "Investigate caching"), Assert.Single(spec!.Nodes).Do);
    }

    [Fact]
    public void ParserAcceptsFullTrackActionAndPreservesTemplateInputs()
    {
        var (spec, errors) = TrackFlowParser.Parse("""
            {
              "rev": 0,
              "nodes": [{
                "id": "spawn",
                "do": {
                  "kind": "track",
                  "prompt": "Investigate ${inputs.feature}",
                  "title": "Cache research",
                  "area": "Engineering",
                  "template": "agents-delivery",
                  "templateInputs": { "targets": ["web", "desktop"] },
                  "cwd": "${inputs.cwd}",
                  "workspaceDriver": "vcs-workspace",
                  "executionTargetId": "local",
                  "provider": "codex",
                  "model": "gpt-5",
                  "reasoningEffort": " high ",
                  "permissionMode": " full "
                }
              }]
            }
            """);
        Assert.Empty(errors);
        var action = Assert.Single(spec!.Nodes).Do;
        var expected = new FlowDoSpecDto("track", Prompt: "Investigate ${inputs.feature}", Title: "Cache research",
            Area: "Engineering", Template: "agents-delivery", Cwd: "${inputs.cwd}", WorkspaceDriver: "vcs-workspace",
            ExecutionTargetId: "local", Provider: "codex", Model: "gpt-5", ReasoningEffort: "high", PermissionMode: "full");
        Assert.Equal(expected, action with { TemplateInputs = null });
        Assert.Equal("desktop", action.TemplateInputs!.Value.GetProperty("targets")[1].GetString());
    }

    [Theory]
    [InlineData("", "prompt")]
    [InlineData(""","prompt":"" """, "prompt")]
    [InlineData(""","prompt":" \t\n " """, "prompt")]
    [InlineData(""","prompt":null """, "prompt")]
    [InlineData(""","prompt":42 """, "prompt")]
    [InlineData(""","prompt":"Start","templateInputs":{} """, "templateInputs")]
    [InlineData(""","prompt":"Start","template":" ","templateInputs":{} """, "templateInputs")]
    [InlineData(""","prompt":"Start","template":"dev","templateInputs":[] """, "templateInputs")]
    [InlineData(""","prompt":"Start","template":"dev","templateInputs":null """, "templateInputs")]
    [InlineData(""","prompt":"Start","reasoningEffort":" " """, "reasoningEffort")]
    [InlineData(""","prompt":"Start","permissionMode":" " """, "permissionMode")]
    public void ParserRejectsInvalidTrackOptions(string options, string field)
    {
        var (spec, errors) = TrackFlowParser.Parse($$"""
            {"rev":0,"nodes":[{"id":"spawn","do":{"kind":"track"{{options}}} }]}
            """);
        Assert.Null(spec);
        Assert.Contains(errors, error => error.Path == $"$.nodes[0].do.{field}");
    }

    [Theory]
    [InlineData("session")]
    [InlineData("role")]
    [InlineData("access")]
    [InlineData("gate")]
    [InlineData("cmd")]
    [InlineData("acceptance")]
    [InlineData("prChecks")]
    [InlineData("repo")]
    [InlineData("headSha")]
    [InlineData("interval")]
    [InlineData("timeout")]
    [InlineData("repeat")]
    [InlineData("unknown")]
    public void ParserRejectsFieldsOutsideTrackActionEvenWhenNull(string field)
    {
        var (spec, errors) = TrackFlowParser.Parse($$"""
            {"rev":0,"nodes":[{"id":"spawn","do":{"kind":"track","prompt":"Start","{{field}}":null} }]}
            """);
        Assert.Null(spec);
        Assert.Equal($"$.nodes[0].do.{field}", Assert.Single(errors).Path);
    }
}
