using System.Text.Json;

namespace Perga.PolicyVm.Tests;

public sealed class TemplateFlowTests
{
    private readonly PolicyVm _vm = new();

    [Theory]
    [InlineData("headerView: 42")]
    [InlineData("headerView: null")]
    [InlineData("before: 42")]
    [InlineData("flow: {}")]
    [InlineData("flow: [{id:'x', do:{kind:'wake',session:'lead',prompt:()=> 'p'}}]")]
    [InlineData("flow: [{id:'x', do:{kind:'run',cmd:'echo hi',prompt:'p'}}]")]
    [InlineData("flow: [{id:'x', do:{kind:'wake',prompt:'p'}}]")]
    [InlineData("flow: [{id:'x', do:{kind:'run',prompt:'p'}},{id:'x', do:{kind:'run',prompt:'q'}}]")]
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

    [Fact]
    public void SetupResolvesBlueprintUsingSuppliedInputsAndValidatesAtApplication()
    {
        var policy = _vm.Compile("""
            export default { id:'flow', title:'Flow',
              inputs: { mode: {type:'string',default:'default'} },
              setup(inputs) {
                return {flow: [{id:inputs.mode, do:{kind:'wake',session:'lead',prompt:'Run ${inputs.mode}'}}]};
              }
            };
            """);
        var node = Assert.Single(_vm.ResolveFlow(policy, JsonSerializer.Deserialize<JsonElement>("""{"mode":"custom"}""")));
        Assert.Equal("custom", node.Id);
        Assert.Equal("Run ${inputs.mode}", node.Do.Prompt);
        Assert.Throws<InvalidOperationException>(() =>
            _vm.ResolveFlow(policy, JsonSerializer.Deserialize<JsonElement>("""{"mode":"invalid id"}""")));
    }

    private CompiledPolicy Policy() => _vm.Compile("""
        export default {id:'inputs', title:'Inputs', inputs: {
          name:{type:'string'}, count:{type:'number',default:2}, enabled:{type:'boolean',default:true},
          choice:{type:'enum',values:['a','b'],default:'a'}, extra:{type:'string',optional:true}
        }};
        """);
}
