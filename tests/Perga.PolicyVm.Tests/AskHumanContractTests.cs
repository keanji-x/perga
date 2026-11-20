using System.Text.Json;

namespace Perga.PolicyVm.Tests;

public sealed class AskHumanContractTests
{
    [Theory]
    [InlineData("AskHuman('Proceed?')", "Approve", "Deny")]
    [InlineData("AskHuman('Proceed?', {})", "Approve", "Deny")]
    [InlineData("AskHuman('Proceed?', {approve:'继续', deny:'停止'})", "继续", "停止")]
    [InlineData("AskHuman('Proceed?', {approve:'Publish'})", "Publish", "Deny")]
    [InlineData("AskHuman('Proceed?', {deny:'Cancel'})", "Approve", "Cancel")]
    [InlineData("({kind:'ask_human',title:'Proceed?'})", "Approve", "Deny")]
    [InlineData("({kind:'ask_human',title:'Proceed?',approveLabel:'Ship',denyLabel:'Wait'})", "Ship", "Wait")]
    public void Before_UsesExplicitBinaryLabels(string expression, string approve, string deny)
    {
        var ask = Assert.IsType<GuardDecision.AskHuman>(Evaluate(expression));
        Assert.Equal("Proceed?", ask.Title);
        Assert.Equal(approve, ask.ApproveLabel);
        Assert.Equal(deny, ask.DenyLabel);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("['Approve', 'Deny']")]
    [InlineData("null")]
    [InlineData("42")]
    [InlineData("'Approve'")]
    [InlineData("true")]
    [InlineData("(()=>{})")]
    [InlineData("{approve:''}")]
    [InlineData("{deny:'   '}")]
    [InlineData("{approve:42}")]
    [InlineData("{deny:null}")]
    public void Helper_RejectsInvalidLabelsWithTypeError(string labels)
    {
        var decision = Assert.IsType<GuardDecision.Deny>(Evaluate(
            "(()=>{try{return AskHuman('Proceed?'," + labels + ");}catch(e){return Deny(e.name);}})()"));
        Assert.Equal("TypeError", decision.Reason);
    }

    [Theory]
    [InlineData("options:[]")]
    [InlineData("options:undefined")]
    [InlineData("options:null")]
    [InlineData("approveLabel:''")]
    [InlineData("denyLabel:'   '")]
    [InlineData("approveLabel:42")]
    [InlineData("denyLabel:null")]
    public void Parser_RejectsLegacyOptionsAndInvalidLabels(string properties)
    {
        var error = Assert.Throws<InvalidOperationException>(() => Evaluate(
            "({kind:'ask_human',title:'Proceed?'," + properties + "})"));
        Assert.Contains(properties.Split(':')[0], error.Message);
    }

    private static GuardDecision Evaluate(string expression)
    {
        var vm = new PolicyVm();
        var policy = vm.Compile("export default {id:'ask',title:'Ask',before:()=>" + expression + "};");
        var json = JsonSerializer.SerializeToElement(new { });
        return vm.Before(policy, "task.dispatch", "track", json, json, json).Output;
    }
}
