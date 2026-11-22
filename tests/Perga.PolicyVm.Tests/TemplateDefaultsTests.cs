using System.Text.Json;

namespace Perga.PolicyVm.Tests;

public sealed class TemplateDefaultsTests
{
    private readonly PolicyVm _vm = new();

    [Fact]
    public void InitData_FillsFalsyDefaultsInEitherChainingOrder()
    {
        var policy = _vm.Compile("""
            export default {
              id: 'defaults', title: 'Defaults',
              data: { schema: t => ({
                count: t.number().optional().default(0),
                enabled: t.boolean().default(false).optional(),
                note: t.string().default(''),
                explicitlyNull: t.string().default('fallback').optional()
              }), initial: { explicitlyNull: null } }
            };
            """);

        var data = _vm.InitData(policy, Json("{}"));

        Assert.Equal(0, data.GetProperty("count").GetInt32());
        Assert.False(data.GetProperty("enabled").GetBoolean());
        Assert.Equal("", data.GetProperty("note").GetString());
        Assert.Equal(JsonValueKind.Null, data.GetProperty("explicitlyNull").ValueKind);
        Assert.Equal("template:initial", data.GetProperty("_meta").GetProperty("source").GetString());
    }

    [Theory]
    [InlineData("t.number().default('wrong')", "expected finite number")]
    [InlineData("t.enum(['open']).default('closed')", "expected one of")]
    [InlineData("t.isoDate().default('2025-02-29')", "expected")]
    [InlineData("t.object({}).default({_meta: {source: 'forged'}})", "managed by the kernel")]
    [InlineData("t.string().optional().default(undefined)", "JSON")]
    [InlineData("t.object({}).default(new Date())", "plain JSON object")]
    [InlineData("t.object({ required: t.string() }).default({})", "required value is missing")]
    public void Compile_RejectsInvalidDefaultsWithoutInitialData(string node, string message)
    {
        var error = Assert.Throws<InvalidOperationException>(() => _vm.Compile($$"""
            export default { id: 'defaults', title: 'Defaults', data: { schema: t => ({ field: {{node}} }) } };
            """));

        Assert.Contains("field", error.Message);
        Assert.Contains(message, error.Message);
    }

    [Fact]
    public void Defaults_AreClonedForEachUpsertedObjectAndPreserveUnchangedMetadata()
    {
        var policy = _vm.Compile("""
            export default {
              id: 'defaults', title: 'Defaults',
              data: { schema: t => ({
                records: t.array(t.object({
                  id: t.id(),
                  config: t.object({ tags: t.array(t.string()).default(['new']) }).default({})
                })).default([])
              }) }
            };
            """);
        var inputs = Json("{}");
        var initial = _vm.InitData(policy, inputs);
        Assert.Empty(initial.GetProperty("records").EnumerateArray());

        var patch = Json("""{"records":{"$upsert":[{"id":"a"},{"id":"b"}]}}""");
        var updated = _vm.ValidateAndPatchData(policy, inputs, initial, patch, [], "first", "test");
        Assert.True(updated.Valid);
        foreach (var item in updated.NextData.GetProperty("records").EnumerateArray())
        {
            var config = item.GetProperty("config");
            Assert.Equal("new", config.GetProperty("tags")[0].GetString());
            Assert.Equal("test", config.GetProperty("_meta").GetProperty("source").GetString());
            Assert.Equal("first", item.GetProperty("_meta").GetProperty("updatedAt").GetString());
        }

        var retry = _vm.ValidateAndPatchData(policy, inputs, updated.NextData, patch, [], "second", "test");
        Assert.True(retry.Valid);
        Assert.Equal(updated.NextData.GetRawText(), retry.NextData.GetRawText());
    }

    [Fact]
    public void EmptyPatch_BackfillsDefaultsOnExistingDataAndPreservesExplicitValues()
    {
        var policy = _vm.Compile("""
            export default { id: 'defaults', title: 'Defaults', data: { schema: t => ({
              count: t.number().default(0),
              enabled: t.boolean().optional().default(true)
            }) } };
            """);
        var inputs = Json("{}");
        var current = Json("""{"count":42}""");
        var patched = _vm.ValidateAndPatchData(policy, inputs, current, Json("{}"), [], "now", "test");
        Assert.True(patched.Valid);
        Assert.Equal(42, patched.NextData.GetProperty("count").GetInt32());
        Assert.True(patched.NextData.GetProperty("enabled").GetBoolean());
        Assert.Equal("now", patched.NextData.GetProperty("_meta").GetProperty("updatedAt").GetString());
    }

    [Fact]
    public void SchemaOnlyTemplateWithMixedDefaultsAndRequiredFields_DefersInitUntilFirstPatch()
    {
        var policy = _vm.Compile("""
            export default {
              id: 'mixed', title: 'Mixed',
              data: { schema: t => ({
                title: t.string(),
                retries: t.number().default(0)
              }) }
            };
            """);
        var inputs = Json("{}");
        var initial = _vm.InitData(policy, inputs);
        Assert.Empty(initial.EnumerateObject());

        var patched = _vm.ValidateAndPatchData(policy, inputs, initial, Json("""{"title":"launch"}"""), [], "now", "test");
        Assert.True(patched.Valid);
        Assert.Equal("launch", patched.NextData.GetProperty("title").GetString());
        Assert.Equal(0, patched.NextData.GetProperty("retries").GetInt32());
    }

    private static JsonElement Json(string json) => JsonSerializer.Deserialize<JsonElement>(json);
}
