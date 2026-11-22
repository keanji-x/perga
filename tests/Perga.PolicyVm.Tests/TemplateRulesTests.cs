using System.Text.Json;

namespace Perga.PolicyVm.Tests;

public sealed class TemplateRulesTests
{
    private readonly PolicyVm _vm = new();
    private static JsonElement Json(string text = "{}") => JsonSerializer.Deserialize<JsonElement>(text);

    [Theory]
    [InlineData("{}")]
    [InlineData("[null]")]
    [InlineData("[{when:[]}]")]
    [InlineData("[{when:{unknown:'x'}}]")]
    [InlineData("[{when:{role:1}}]")]
    [InlineData("[{when:{access:'write'}}]")]
    [InlineData("[{when:{},provider:''}]")]
    [InlineData("[{when:{},model:false}]")]
    [InlineData("[{when:{},enforce:'true'}]")]
    [InlineData("[{when:{},extra:true}]")]
    public void RoutingRejectsMalformedDeclarations(string routing) =>
        Assert.ThrowsAny<Exception>(() => _vm.Compile("export default {id:'bad',title:'Bad',tasks:{routing:" + routing + "}};"));

    [Fact]
    public void RoutingTargetsShareInputResolutionAndOnlyMatchingRulesApply()
    {
        var policy = _vm.Compile("""
            export default {id:'typed',title:'Typed',inputs:{model:{type:'string',default:'default'}},tasks:{routing:[
                {when:{role:'review'},provider:'${inputs.provider}',model:'${inputs.model}'}]}};
            """);
        var rule = Assert.Single(policy.Manifest.Routing);
        Assert.Equal("review", rule.When.Role);
        var inputs = Json("{\"provider\":\"codex\"}");
        Assert.Equal(new PolicyTaskRoute("codex", "default"), Assert.Single(_vm.ResolveRoutingTargets(policy, inputs)));
        Assert.Equal(Assert.Single(_vm.ResolveRoutingTargets(policy, inputs)),
            _vm.RouteTaskDispatch(policy, "review", "read_only", null, null, inputs));
        Assert.Equal(new PolicyTaskRoute(null, null), _vm.RouteTaskDispatch(policy, "other", "read_only", null, null, Json()));
    }

    [Fact]
    public void JsonSpec_RoutesOmittedFieldsAndDeniesOnlyExplicitEnforcedConflicts()
    {
        var policy = _vm.Compile("""
            {"id":"json","title":"JSON","inputs":{"model":{"type":"string","default":"gpt-5"}},
             "tasks":{"routing":[{"when":{"role":"review","access":"read_only"},"provider":"codex","model":"${inputs.model}","enforce":true}]}}
            """);
        var route = _vm.RouteTaskDispatch(policy,"review","read_only",null,null,Json());
        Assert.Equal(new PolicyTaskRoute("codex","gpt-5"),route);
        Assert.Equal(new PolicyTaskRoute(null,null),_vm.RouteTaskDispatch(policy,"other","read_only",null,null,Json()));
        Assert.NotNull(_vm.RouteTaskDispatch(policy,"review","read_only",null,"wrong",Json()).Denial);
        Assert.Equal("custom",_vm.RouteTaskDispatch(policy,"review","read_only",null,null,Json("""{"model":"custom"}""")).Model);
        var defaults = _vm.Compile("export default {id:'defaults',title:'Defaults',tasks:{routing:[{when:{},model:'default'}]}};");
        Assert.Equal("explicit",_vm.RouteTaskDispatch(defaults,null,"read_only",null,"explicit",Json()).Model);
    }

    [Theory]
    [InlineData("Rewrite({provider:'codex'})")]
    [InlineData("({kind:'rewrite',patch:{}})")]
    [InlineData("({...Allow(),patch:{arguments:{}}})")]
    [InlineData("({...AskHuman('Approve?'),patch:{provider:'codex'}})")]
    [InlineData("({kind:'typo'})")]
    public void Before_RejectsUnsupportedDecisions(string decision)
    {
        var policy = _vm.Compile("export default {id:'test',title:'Test',before:()=>"+decision+"};");
        Assert.Throws<InvalidOperationException>(() => _vm.Before(policy,"task.dispatch","track",Json(),Json(),Json()));
    }

    [Fact]
    public void FourHooks_HaveIndependentSlotsAndShareExplicitState()
    {
        var policy = _vm.Compile("""
            export default {id:'hooks',title:'Hooks',
              turnContext:ctx=>ctx.session.isLead?'lead context':'worker context',
              before(a,ctx){ctx.state.before=a.type;return a.type==='report.commit'?Deny('report rule'):Allow();},
              after(e,ctx){ctx.state.after=e.type;return 'wake';},
              headerView:ctx=>({badges:[{label:ctx.state.after}]})};
            """);
        var turn = _vm.PrepareTurn(policy,new("track","session",true,null,0,Json()),Json());
        Assert.Equal("lead context",turn.Output.Context);
        Assert.Equal("turn.before",turn.NextState.GetProperty("before").GetString());
        Assert.IsType<GuardDecision.Deny>(_vm.Before(policy,"report.commit","track",Json(),Json(),Json()).Output);
        var after = _vm.After(policy,"task.settled","track",Json(),Json(),turn.NextState);
        Assert.Equal("wake",after.Output);
        Assert.Equal("task.settled",Assert.Single(_vm.EvaluateHeaderView(policy,new("track",Json()),after.NextState)!.Badges!).Label);
    }

    [Fact]
    public void Inheritance_MergesDeclarationsAndReplacesProvidedHookSlots()
    {
        var parent = _vm.Compile("""
            export default {id:'base',title:'Base',plugins:['a','b'],inputs:{one:{type:'string',default:'base'}},
              flow:[{id:'pulse',when:{cron:'0 9 * * *'},do:{kind:'wake',session:'lead',prompt:'base'}}],
              report:{maxProseChars:100,sections:[{h1:'Keep'},{h1:'Override',editableBy:['human']}]},
              tasks:{routing:[{when:{role:'review'},model:'base'},{when:{},provider:'codex'}]},
              before(){throw Error('parent before replaced');},after(){throw Error('parent after replaced');},
              turnContext(){throw Error('parent context replaced');},headerView(){throw Error('parent header replaced');}};
            """);
        var policy = _vm.Compile(PolicyVm.Inherit(parent,"""
            export default {id:'child',title:'Child',plugins:['b','c'],inputs:{one:{type:'string',default:'child'},two:{type:'boolean'}},
              flow:[{id:'pulse',when:{cron:'0 10 * * *'},do:{kind:'wake',session:'lead',prompt:'child'}}],
              report:{sections:[{h1:'Override',editableBy:['lead']}]},
              tasks:{routing:[{when:{role:'review'},model:'child'}]},
              before(a,ctx){ctx.state.order=['child'];return Allow();},after(e,ctx){ctx.state.after=['child'];},
              turnContext:()=> 'child context',headerView:()=>({badges:[{label:'child'}]})};
            """));
        Assert.Equal(["a","b","c"],policy.Manifest.DefaultPlugins);
        Assert.Equal("child",policy.Manifest.InputsSchema.GetProperty("one").GetProperty("default").GetString());
        Assert.Equal(100,policy.Manifest.MaxProseChars);
        Assert.Equal(2,policy.Manifest.Sections.Count);
        Assert.Equal("lead",Assert.Single(policy.Manifest.Sections[1].EditableBy!));
        Assert.Equal("0 10 * * *",Assert.Single(_vm.ResolveFlow(policy, Json())).When?.Cron);
        Assert.Equal(new PolicyTaskRoute("codex","child"),_vm.RouteTaskDispatch(policy,"review","read_only",null,null,Json()));
        Assert.Equal("[\"child\"]",_vm.Before(policy,"task.dispatch","track",Json(),Json(),Json()).NextState.GetProperty("order").GetRawText());
        Assert.Equal("[\"child\"]",_vm.After(policy,"event","track",Json(),Json(),Json()).NextState.GetProperty("after").GetRawText());
        Assert.Equal("child context",_vm.PrepareTurn(policy,new("track","session",true,null,0,Json()),Json()).Output.Context);
        Assert.Equal("child",Assert.Single(_vm.EvaluateHeaderView(policy,new("track",Json()),Json())!.Badges!).Label);
    }

    [Fact]
    public void Inheritance_InheritsEachOmittedHook()
    {
        var parent = _vm.Compile("""
            export default {id:'base',title:'Base',before:()=>Deny('parent denied'),after:()=> 'parent note',
              turnContext:()=> 'parent context',headerView:()=>({badges:[{label:'parent'}]})};
            """);
        var policy = _vm.Compile(PolicyVm.Inherit(parent,"export default {id:'child',title:'Child'};"));
        Assert.Equal("parent denied",Assert.IsType<GuardDecision.Deny>(_vm.Before(policy,"task.dispatch","track",Json(),Json(),Json()).Output).Reason);
        Assert.Equal("parent note",_vm.After(policy,"event","track",Json(),Json(),Json()).Output);
        Assert.Equal("parent context",_vm.PrepareTurn(policy,new("track","session",true,null,0,Json()),Json()).Output.Context);
        Assert.Equal("parent",Assert.Single(_vm.EvaluateHeaderView(policy,new("track",Json()),Json())!.Badges!).Label);
    }
}
