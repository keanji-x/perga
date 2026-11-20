using System.Text.Json;

namespace Perga.PolicyVm.Tests;

public sealed class DeclarativeScheduleTests
{
    private readonly PolicyVm _vm = new();

    [Theory]
    [InlineData("headerView: 42")]
    [InlineData("headerView: null")]
    [InlineData("initialMarkdown: 'removed'")]
    [InlineData("schedules: [{id:'x', cron:'bad', prompt:'p'}]")]
    [InlineData("schedules: [{id:'x', cron:'* * * * *', prompt:()=> 'p'}]")]
    [InlineData("schedules: [{id:'x', cron:'* * * * *', wakePrompt:'p'}]")]
    [InlineData("schedules: [{id:'x', cron:'* * * * *', wakePrompt:()=> 'p', prompt:'p'}]")]
    [InlineData("schedules: [{id:'x', cron:'* * * * *', timezone:'Invalid/Zone', prompt:'p'}]")]
    [InlineData("schedules: [{id:'x', cron:'* * * * *', prompt:'p'},{id:'x', cron:'* * * * *', prompt:'p'}]")]
    [InlineData("inputs: {choice: {type:'enum'}}")]
    public void CompileRejectsInvalidDeclarations(string field) => Assert.Throws<InvalidOperationException>(() =>
        _vm.Compile("export default { id:'test', title:'Test', " + field + "};"));

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"name\":2}")]
    [InlineData("{\"name\":\"ok\",\"count\":\"2\"}")]
    [InlineData("{\"name\":\"ok\",\"enabled\":1}")]
    [InlineData("{\"name\":\"ok\",\"choice\":\"unknown\"}")]
    [InlineData("[]")]
    public void ValidateInputsChecksRequiredValuesAndTypes(string json)
    {
        var policy = Policy();
        Assert.Throws<InvalidOperationException>(() => _vm.ValidateInputs(policy, JsonSerializer.Deserialize<JsonElement>(json)));
    }

    [Fact]
    public void DefaultsAndSuppliedInputsShareInterpolation()
    {
        var policy = Policy();
        var inputs = JsonSerializer.Deserialize<JsonElement>("{\"name\":\"Ada\"}");
        _vm.ValidateInputs(policy, inputs);
        Assert.Equal("Ada:2:a", _vm.InterpolateInputs(policy, "${inputs.name}:${inputs.count}:${inputs.choice}", inputs));
        Assert.Equal("true", _vm.InterpolateInputs(policy, "${inputs.enabled}", inputs));
        Assert.Throws<InvalidOperationException>(() => _vm.InterpolateInputs(policy, "${inputs.missing}", inputs));
    }

    private CompiledPolicy Policy() => _vm.Compile("""
        export default {id:'inputs', title:'Inputs', inputs: {
          name:{type:'string'}, count:{type:'number',default:2}, enabled:{type:'boolean',default:true},
          choice:{type:'enum',values:['a','b'],default:'a'}, extra:{type:'string',optional:true}
        }};
        """);
}
