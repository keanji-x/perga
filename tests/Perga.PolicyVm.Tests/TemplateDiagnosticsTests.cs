using System.Text.Json;

namespace Perga.PolicyVm.Tests;

public sealed class TemplateDiagnosticsTests
{
    private readonly PolicyVm _vm = new();
    private static JsonElement Json(string text = "{}") => JsonSerializer.Deserialize<JsonElement>(text);

    [Theory]
    [InlineData("null", "headerView", "expected an object")]
    [InlineData("{ metrics: {} }", "headerView.metrics", "expected an array")]
    [InlineData("{ metrics: [{ label: 'x', value: '1', tone: 'purple' }] }", "headerView.metrics[0].tone", "expected neutral")]
    [InlineData("{ metrics: [{ label: 'x', value: '1', spark: ['wrong'] }] }", "headerView.metrics[0].spark[0]", "a finite number")]
    [InlineData("{ rules: [{ label: 'x' }] }", "headerView.rules[0].text", "required field is missing")]
    [InlineData("{ notice: { text: 'x', extra: true } }", "headerView.notice.extra", "unknown field")]
    public void HeaderView_ReportsInvalidFieldAndKeepsLegacyReadSafe(string result, string path, string reason)
    {
        IPolicyVm vm = _vm;
        var policy = vm.Compile($"export default {{ id: 'test', title: 'Test', headerView: () => ({result}) }};");
        var context = new PolicyHeaderViewContext("track", Json());

        var diagnostic = vm.EvaluateHeaderViewWithDiagnostic(policy, context, Json());
        Assert.Null(diagnostic.View);
        Assert.Contains(path, diagnostic.Error);
        Assert.Contains(reason, diagnostic.Error);
        Assert.Null(vm.EvaluateHeaderView(policy, context, Json()));
    }

    [Fact]
    public void HeaderView_RuntimeErrorContainsSourceLocationAndStackFrame()
    {
        var policy = _vm.Compile("""
            function loadBadge() {
              throw new Error('badge provider unavailable');
            }
            export default { id: 'test', title: 'Test', headerView: () => loadBadge() };
            """);

        var result = _vm.EvaluateHeaderViewWithDiagnostic(policy, new("track", Json()), Json());
        Assert.Null(result.View);
        Assert.Contains("Template JavaScript error during HeaderView: badge provider unavailable", result.Error);
        Assert.Matches(@"template-0\.perga\.js:2:\d+", result.Error!);
        Assert.Contains("at loadBadge", result.Error);
    }

    [Fact]
    public void HeaderView_AbsentHookAndValidHookHaveNoDiagnostic()
    {
        var context = new PolicyHeaderViewContext("track", Json());
        var noHook = _vm.Compile("export default { id: 'test', title: 'Test' };");
        Assert.Equal(new HeaderViewEvaluation(null, null), _vm.EvaluateHeaderViewWithDiagnostic(noHook, context, Json()));

        var valid = _vm.Compile("""
            export default { id: 'test', title: 'Test', headerView: () => ({ notice: { text: 'OK', tone: 'info' } }) };
            """);
        var result = _vm.EvaluateHeaderViewWithDiagnostic(valid, context, Json());
        Assert.Equal("OK", result.View?.Notice?.Text);
        Assert.Null(result.Error);
    }

    [Fact]
    public void Compile_SyntaxErrorContainsSourceLocation()
    {
        var error = Assert.Throws<InvalidOperationException>(() => _vm.Compile("""
            export default {
              id: ,
              title: 'Test'
            };
            """));
        Assert.StartsWith("Template JavaScript", error.Message);
        Assert.Matches(@"template-0\.perga\.js:2:\d+", error.Message);
    }

    [Fact]
    public void Compile_ReportsSyntaxErrorsInImportedModules()
    {
        var package = Directory.CreateTempSubdirectory("perga-template-diagnostics-");
        try
        {
            File.WriteAllText(Path.Combine(package.FullName, "template.perga.js"), """
                import "./helper.js";
                export default { id: 'test', title: 'Test' };
                """);
            File.WriteAllText(Path.Combine(package.FullName, "helper.js"), """
                export const valid = 1;
                export const broken = ;
                """);
            var source = TemplateBundle.PackDirectory(package.FullName, "template.perga.js");

            var error = Assert.Throws<InvalidOperationException>(() => _vm.Compile(source));
            Assert.Matches(@"helper\.js:2:\d+", error.Message);
        }
        finally
        {
            package.Delete(recursive: true);
        }
    }

    [Fact]
    public void Compile_PublishesInlineReportMarkdownFromSingleModule()
    {
        var compiled = _vm.Compile("""
            export default {
              id: 'test', title: 'Test',
              report: { initialMarkdown: '# Summary for ${inputs.subject}' }
            };
            """);

        Assert.Equal("# Summary for ${inputs.subject}", compiled.Manifest.InitialReportMarkdown);
    }

    [Fact]
    public void ValidateInputs_RejectsUnknownKeysAndListsAllowedKeys()
    {
        var policy = _vm.Compile("""
            export default { id: 'test', title: 'Test', inputs: {
              user: { type: 'string', optional: true },
              count: { type: 'number', default: 1 }
            } };
            """);

        var error = Assert.Throws<InvalidOperationException>(() => _vm.ValidateInputs(policy, Json("""{"User":"name","typo":true}""")));
        Assert.Contains("Unknown template input keys: [User, typo]", error.Message);
        Assert.Contains("Allowed input keys: [count, user]", error.Message);
        _vm.ValidateInputs(policy, Json("""{"user":"name"}"""));
    }

    [Fact]
    public void Before_ExposesSuppliedSessionAndActionTypeAndKeepsSessionOptional()
    {
        IPolicyVm vm = _vm;
        var policy = vm.Compile("""
            export default { id: 'test', title: 'Test',
              before(action, ctx) {
                if (action.type !== 'tool.report_commit') return Deny('wrong action');
                if (!ctx.session) return Deny('missing session');
                return ctx.session.sessionId === action.sessionId && ctx.session.isLead
                  ? Allow() : Deny('wrong session');
              }
            };
            """);
        var action = Json("""{"type":"spoofed","sessionId":"lead"}""");
        var session = new PolicySessionContext("lead", true, 0.25);

        Assert.IsType<GuardDecision.Allow>(vm.Before(policy, "tool.report_commit", "track", action, Json(), Json(), session));
        Assert.Equal("missing session",
            Assert.IsType<GuardDecision.Deny>(vm.Before(policy, "tool.report_commit", "track", action, Json(), Json())).Reason);
    }

    [Fact]
    public void Report_RejectsBothTemplateAndInitialMarkdownAndAllowsChildOverride()
    {
        var err = Assert.Throws<InvalidOperationException>(() => _vm.Compile("""
            export default {
              id: 'dual', title: 'Dual',
              report: { template: './report.md', initialMarkdown: '# Inline' }
            };
            """));
        Assert.Contains("cannot declare both", err.Message);

        var parentDir = Directory.CreateTempSubdirectory("perga-report-parent-");
        try
        {
            File.WriteAllText(Path.Combine(parentDir.FullName, "template.perga.js"), """
                export default { id: 'base', title: 'Base', report: { template: './report.md' } };
                """);
            File.WriteAllText(Path.Combine(parentDir.FullName, "report.md"), "# Parent");
            var parent = _vm.Compile(TemplateBundle.PackDirectory(parentDir.FullName, "template.perga.js"));

            var childSource = PolicyVm.Inherit(parent, """
                export default { id: 'child', title: 'Child', report: { initialMarkdown: '# Child Inline' } };
                """);
            var child = _vm.Compile(childSource);
            Assert.Equal("# Child Inline", child.Manifest.InitialReportMarkdown);
        }
        finally
        {
            parentDir.Delete(recursive: true);
        }
    }

    [Fact]
    public void HeaderView_NonFunctionFromDynamicSetupReportsDiagnostic()
    {
        var policy = _vm.Compile("""
            export default {
              id: 'dyn', title: 'Dyn',
              inputs: { bad: { type: 'boolean', default: false } },
              setup(inputs) {
                return inputs.bad ? { headerView: 'not-a-function' } : {};
              }
            };
            """);

        var result = _vm.EvaluateHeaderViewWithDiagnostic(policy, new("track", Json("""{"bad":true}""")), Json());
        Assert.Null(result.View);
        Assert.Contains("headerView", result.Error);
    }
}
