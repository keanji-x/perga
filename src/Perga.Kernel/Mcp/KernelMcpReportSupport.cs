using System.Text.Json;
using System.Text.Json.Serialization;
using Perga.Contracts;
using Perga.Kernel.Internal;
using Perga.ReportDoc;

namespace Perga.Kernel.Mcp;

internal static class KernelMcpReportSupport
{
    private static readonly ReportDocEngine ScopedRenderer = new();
    private static readonly JsonElement CachedBlockContract = BuildBlockContract();

    internal const string ReportReadSchemaJson = """
        {
          "type": "object",
          "additionalProperties": false,
          "properties": {
            "trackId": {
              "type": "string",
              "minLength": 1,
              "description": "Omit for the current Track. A different Track ID returns a read-only snapshot without a read anchor."
            },
            "format": {
              "type": "string",
              "enum": ["full", "blocks", "markdown", "outline"],
              "description": "Response projection. Defaults to 'full'. Use 'blocks' or 'markdown' to avoid duplicating content, or 'outline' for a compact section/block index."
            },
            "sectionH1": {
              "type": "string",
              "description": "Optional H1 section title filter. Returns only blocks in that section. Reads of the current Track include a scoped ReadAnchor (wholeDocumentRead=false)."
            },
            "blockIds": {
              "type": "array",
              "minItems": 1,
              "items": { "type": "string", "minLength": 1 },
              "description": "Optional non-empty block ID allowlist. Returns only matching blocks. Reads of the current Track include a scoped ReadAnchor (wholeDocumentRead=false)."
            },
            "includeContract": {
              "type": "boolean",
              "description": "When true, includes the authoring contract and examples for Markdown prose and perga-block widgets."
            }
          }
        }
        """;

    internal const string ReportCommitSchemaJson = """
        {
          "type": "object",
          "required": ["anchor", "ops"],
          "properties": {
            "anchor": {
              "type": "object",
              "required": ["docRev", "knownBlockRevs", "wholeDocumentRead"],
              "properties": {
                "docRev": { "type": "integer", "minimum": 0 },
                "knownBlockRevs": {
                  "type": "object",
                  "additionalProperties": { "type": "integer", "minimum": 0 }
                },
                "wholeDocumentRead": { "type": "boolean" }
              },
              "description": "ReadAnchor returned by report_read or the previous report_commit."
            },
            "ops": {
              "type": "array",
              "minItems": 1,
              "items": {
                "oneOf": [
                  {
                    "type": "object",
                    "required": ["op", "sectionH1", "markdown"],
                    "properties": {
                      "op": { "type": "string", "const": "replace_section" },
                      "sectionH1": { "type": "string", "minLength": 1 },
                      "markdown": { "type": "string" }
                    }
                  },
                  {
                    "type": "object",
                    "required": ["op"],
                    "properties": {
                      "op": { "type": "string", "enum": ["upsert", "upsert_block"] },
                      "id": { "type": "string" },
                      "blockKind": { "type": "string", "enum": ["prose", "widget"] },
                      "markdown": { "type": "string" },
                      "payload": {},
                      "position": { "type": "integer", "minimum": 0 }
                    }
                  },
                  {
                    "type": "object",
                    "required": ["op", "id"],
                    "properties": {
                      "op": { "type": "string", "enum": ["delete", "delete_block"] },
                      "id": { "type": "string", "minLength": 1 }
                    }
                  },
                  {
                    "type": "object",
                    "required": ["op", "id", "toIndex"],
                    "properties": {
                      "op": { "type": "string", "enum": ["move", "move_block"] },
                      "id": { "type": "string", "minLength": 1 },
                      "toIndex": { "type": "integer", "minimum": 0 }
                    }
                  }
                ]
              }
            },
            "summary": { "type": "string" },
            "commitMessage": { "type": "string" },
            "includeDocument": {
              "type": "boolean",
              "description": "Defaults to false (returns a compact commit receipt with docRev, summary, anchor, changes, tasks, diagnostics). Set true to also include renderedMarkdown and blocks."
            }
          }
        }
        """;

    internal const string UserAskSchemaJson = """
        {
          "type": "object",
          "required": ["questions"],
          "properties": {
            "questions": {
              "type": "array",
              "minItems": 1,
              "items": {
                "type": "object",
                "required": ["question", "options"],
                "properties": {
                  "question": { "type": "string", "minLength": 1 },
                  "options": {
                    "type": "array",
                    "minItems": 1,
                    "items": { "type": "string", "minLength": 1 }
                  }
                }
              }
            }
          }
        }
        """;

    internal const string TerminalReadSchemaJson = """
        {
          "type": "object",
          "required": ["terminalId"],
          "properties": {
            "terminalId": { "type": "string", "minLength": 1 },
            "scrollOffset": { "type": "integer" },
            "waitFor": { "type": "string", "enum": ["none", "elapsed", "change", "text"] },
            "waitMs": { "type": "integer", "minimum": 0 },
            "settleMs": { "type": "integer", "minimum": 0 }
          }
        }
        """;

    internal const string TerminalInputSchemaJson = """
        {
          "type": "object",
          "required": ["terminalId", "kind", "payload"],
          "properties": {
            "terminalId": { "type": "string", "minLength": 1 },
            "kind": { "type": "string", "enum": ["text", "keys", "paste", "submit"] },
            "payload": {
              "description": "Action payload matching kind: string or {text} for text/paste/submit, or key sequence string/array for keys."
            }
          }
        }
        """;

    internal sealed record ReportReadOptions(string Format, string? SectionH1, HashSet<string>? BlockIds, bool IncludeContract)
    {
        public bool IsScoped => SectionH1 is not null || BlockIds is not null;
    }

    internal static ReportReadOptions ParseReadOptions(JsonElement args) => new(
        ParseReadFormat(args), ParseOptionalSectionFilter(args), ParseOptionalBlockIdFilter(args),
        ParseOptionalBool(args, ["includeContract"]));

    internal static JsonElement FormatReportReadResponse(
        ReportReadResult res,
        ReportReadOptions options,
        Perga.PolicyVm.TemplateViewsProjection? templateViews = null)
    {
        var blocks = ReportBlockSelection.FilterBlocks(res.Blocks, options.SectionH1, options.BlockIds);
        var markdown = options.IsScoped && options.Format is "full" or "markdown"
            ? ScopedRenderer.RenderMarkdown(new ReportDocument(res.DocRev, res.Summary, string.Empty, blocks), withMarkers: true)
            : res.RenderedMarkdown;
        var report = new McpReportReadResponse(
            DocRev: res.DocRev, Summary: res.Summary, Sections: null, RenderedMarkdown: markdown,
            Blocks: blocks, OutlineBlocks: null, Tasks: SummarizeTasks(res.Tasks), Anchor: res.Anchor,
            Diagnostics: res.Diagnostics, BlockContract: null,
            TemplateViews: templateViews is { Views.Count: > 0 } ? templateViews.Views : null,
            DataRev: templateViews?.DataRev ?? res.DataRev);
        return FormatReportProjection(report, options);
    }

    internal static JsonElement FormatReportSnapshotResponse(
        string trackId,
        ReportSnapshot snapshot,
        ReportReadOptions options)
    {
        var report = new McpReportReadResponse(
            DocRev: snapshot.DocRev, Summary: snapshot.Summary, Sections: null, RenderedMarkdown: snapshot.RenderedMarkdown,
            Blocks: snapshot.Blocks, OutlineBlocks: null, Tasks: null, Anchor: null,
            Diagnostics: [], BlockContract: null, TemplateViews: snapshot.TemplateViews,
            DataRev: snapshot.DataRev, TrackId: trackId, ReadOnly: true);
        return FormatReportProjection(report, options);
    }

    private static JsonElement FormatReportProjection(
        McpReportReadResponse report,
        ReportReadOptions options)
    {
        var format = options.Format;
        var blocks = report.Blocks!;
        var anchor = report.Anchor;
        if (anchor is not null && (format == "outline" || options.IsScoped))
        {
            var knownRevs = format == "outline"
                ? new Dictionary<string, int>(StringComparer.Ordinal)
                : blocks.ToDictionary(b => b.Id, b => b.Rev, StringComparer.Ordinal);
            anchor = new ReadAnchor(report.DocRev, knownRevs, WholeDocumentRead: false);
        }

        return JsonHelpers.ToElement(report with
        {
            Sections = format == "outline" ? CollectOrderedSections(blocks) : null,
            RenderedMarkdown = format is "full" or "markdown" ? report.RenderedMarkdown : null,
            Blocks = format is "full" or "blocks" ? blocks : null,
            OutlineBlocks = format == "outline"
                ? blocks.Select(b => new ReportOutlineBlock(b.Id, b.Rev, b.Kind, b.SectionH1)).ToArray()
                : null,
            Anchor = anchor,
            BlockContract = options.IncludeContract ? CachedBlockContract : null,
            TemplateViews = format == "outline" ? null : report.TemplateViews
        });
    }

    internal static bool ParseIncludeDocumentArg(JsonElement args) =>
        ParseOptionalBool(args, ["includeDocument", "include_document"]);

    internal static JsonElement FormatReportCommitResponse(ReportReadResult res, bool includeDocument)
    {
        var receipt = new McpReportCommitReceipt(
            DocRev: res.DocRev,
            Summary: res.Summary,
            Anchor: res.Anchor,
            Changes: res.Changes ?? [],
            Tasks: SummarizeTasks(res.Tasks),
            Diagnostics: res.Diagnostics,
            RenderedMarkdown: includeDocument ? res.RenderedMarkdown : null,
            Blocks: includeDocument ? res.Blocks : null);
        return JsonHelpers.ToElement(receipt);
    }

    internal const int McpSummaryMaxChars = 400;

    internal static string? BoundMcpSummary(string? text, int maxChars = McpSummaryMaxChars)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var trimmed = text.Trim();
        if (trimmed.Length <= maxChars)
            return trimmed;
        var cut = Math.Max(0, maxChars - 3);
        if (cut > 0 && char.IsHighSurrogate(trimmed[cut - 1]))
            cut--;
        return trimmed[..cut] + "...";
    }

    private static McpReportTaskSummary[] SummarizeTasks(IReadOnlyList<TaskDto> tasks) =>
        tasks.Select(task => new McpReportTaskSummary(
            task.Id, task.TaskKey, task.Kind, JsonHelpers.GetStringProperty(task.Context, "title"),
            task.Status, task.Outcome, BoundMcpSummary(task.OutcomeSummary ?? task.StatusDetail), task.RunCount,
            task.SessionId, task.DependsOn.Count > 0 ? task.DependsOn : null, task.ArchivedAtMs)).ToArray();

    private static string ParseReadFormat(JsonElement args)
    {
        if (!JsonHelpers.TryGetPropertyIgnoreCase(args, ["format"], out var elem) ||
            elem.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return "full";
        }

        if (elem.ValueKind != JsonValueKind.String)
        {
            throw KernelException.InvalidArgument("Argument 'format' must be one of: full, blocks, markdown, outline.");
        }

        var normalized = elem.GetString()?.Trim().ToLowerInvariant();
        return normalized switch
        {
            "full" or "blocks" or "markdown" or "outline" => normalized,
            _ => throw KernelException.InvalidArgument(
                $"Invalid report read format '{elem.GetString()}'. Expected one of: full, blocks, markdown, outline.")
        };
    }

    private static string? ParseOptionalSectionFilter(JsonElement args)
    {
        if (!JsonHelpers.TryGetPropertyIgnoreCase(args, ["sectionH1"], out var elem) ||
            elem.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        if (elem.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(elem.GetString()))
        {
            throw KernelException.InvalidArgument("Argument 'sectionH1' must be a non-empty string.");
        }

        var trimmed = elem.GetString()!.Trim();
        return trimmed.StartsWith("# ", StringComparison.Ordinal) ? trimmed[2..].Trim() : trimmed;
    }

    private static HashSet<string>? ParseOptionalBlockIdFilter(JsonElement args)
    {
        if (!JsonHelpers.TryGetPropertyIgnoreCase(args, ["blockIds"], out var elem) ||
            elem.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        if (elem.ValueKind != JsonValueKind.Array)
        {
            throw KernelException.InvalidArgument("Argument 'blockIds' must be an array of non-empty block ID strings.");
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in elem.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
            {
                throw KernelException.InvalidArgument("Each entry in 'blockIds' must be a non-empty string.");
            }

            ids.Add(item.GetString()!.Trim());
        }

        if (ids.Count == 0)
        {
            throw KernelException.InvalidArgument("Argument 'blockIds' must contain at least one block ID.");
        }

        return ids;
    }

    private static bool ParseOptionalBool(JsonElement args, ReadOnlySpan<string> names)
    {
        if (!JsonHelpers.TryGetPropertyIgnoreCase(args, names, out var elem) ||
            elem.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return false;
        }

        if (elem.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw KernelException.InvalidArgument($"Argument '{names[0]}' must be a boolean.");
        }

        return elem.GetBoolean();
    }

    private static IReadOnlyList<string> CollectOrderedSections(IReadOnlyList<ReportBlock> blocks)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var sections = new List<string>();
        foreach (var block in blocks)
        {
            if (!string.IsNullOrWhiteSpace(block.SectionH1) && seen.Add(block.SectionH1))
            {
                sections.Add(block.SectionH1);
            }
        }

        return sections;
    }

    private static JsonElement BuildBlockContract() => JsonHelpers.ParseClone("""
        {
          "fenceSyntax": "```perga-block widget\n<json>\n```",
          "ops": {
            "replaceSection": { "op": "replace_section", "sectionH1": "Progress", "markdown": "# Progress\n..." },
            "upsert": { "op": "upsert", "id": "b_1234", "blockKind": "prose", "markdown": "...", "position": 0 },
            "delete": { "op": "delete", "id": "b_1234" },
            "move": { "op": "move", "id": "b_1234", "toIndex": 0 }
          },
          "kinds": {
            "prose": {
              "notes": "Write standard Markdown, including native tables, code fences and links."
            },
            "widget": {
              "required": ["use", "view"],
              "notes": "Use builtin:flow, builtin:list, builtin:table, or a package-relative ./widgets/*.html path. Set view to flow for builtin:flow and to the template view name for other widgets. Optional fields include height, chrome (seamless or framed), and title.",
              "example": { "use": "./widgets/range-axis.html", "view": "valuationAxis", "height": 210, "chrome": "seamless" },
              "flowExample": { "use": "builtin:flow", "view": "flow" },
              "builtins": {
                "builtin:flow": { "view": "flow", "notes": "Displays the committed Track Flow and its execution state." },
                "builtin:table": {
                  "viewResult": { "columns": [{ "key": "cost", "label": "Cost", "align": "right" }], "rows": [{ "cost": { "text": "$24", "tone": "good", "check": true } }] },
                  "notes": "Required columns (1–12) and rows (0–100), optional nullable title and summary (strings up to 2048 Unicode code points). Columns have a unique key (1–64 characters), label (1–2048 Unicode code points), and optional align (left, right, center). Rows map keys to strings, null, or strict cells with required text and optional tone, code, tag, check, markdown. Tone is neutral, good, warn, bad, or accent. Code and tag are strings. Check and markdown are booleans. Missing and null cells display as an em dash. Undeclared row keys are ignored."
                },
                "builtin:list": {
                  "viewResult": { "items": [{ "id": "review", "title": "Review the change", "badge": "Ready", "tone": "good" }] },
                  "notes": "Required items (0–100), optional nullable title, summary, and groups (up to 100 strings). Item id (1–120 characters) and title (1–2048 Unicode code points) are required. Session and task IDs are 1–160 characters. Other strings are limited to 2048 Unicode code points. Status is done, running, pending, or blocked and defaults to pending. Optional nullable item fields are date, subtitle, badge, tone, meta, sessionId, taskKey, group, summary, bullets, note, table, checks. Tone uses the table cell values. Bullets are up to 12 strings. Table uses builtin:table's view result. Checks are up to 16 objects with label, status (pass, running, fail, pending), and optional nullable detail, command, output."
                }
              },
              "iframePostMessage": {
                "notes": "Send ready to request the current theme and view data. Resize height uses CSS pixels. File intents use workspace-relative paths. The theme scheme is light or dark. Theme tokens are a map of keys to string values that the iframe can apply to its CSS variables.",
                "cssThemeTokens": { "bg": "--bg", "paper": "--paper", "surface": "--paper", "text": "--text", "text2": "--text-2", "text3": "--text-3", "hairline": "--hairline", "accent": "--accent", "fontSans": "--font-sans", "fontSerif": "--font-serif", "fontMono": "--font-mono" },
                "widgetToHost": [
                  { "type": "perga:ready" },
                  { "type": "perga:resize", "height": 210 },
                  { "type": "perga:intent", "kind": "open_file", "path": "README.md" },
                  { "type": "perga:intent", "kind": "open_session", "sessionId": "ses_example" }
                ],
                "hostToWidget": [
                  { "type": "perga:theme", "scheme": "light", "tokens": { "bg": "#f4f2ec", "paper": "#fdfcfa", "text": "#1e2026" } },
                  { "type": "perga:data", "data": "Output of the selected template view" }
                ]
              }
            }
          }
        }
        """);

    private sealed record ReportOutlineBlock(
        string Id,
        int Rev,
        ReportBlockKind Kind,
        string? SectionH1);

    private sealed record McpReportReadResponse(
        int DocRev,
        string Summary,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Sections,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? RenderedMarkdown,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<ReportBlock>? Blocks,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<ReportOutlineBlock>? OutlineBlocks,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<McpReportTaskSummary>? Tasks,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ReadAnchor? Anchor,
        IReadOnlyList<ReportDiagnosticDto> Diagnostics,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonElement? BlockContract,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyDictionary<string, JsonElement>? TemplateViews = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? DataRev = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TrackId = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? ReadOnly = null);

    private sealed record McpReportCommitReceipt(
        int DocRev,
        string Summary,
        ReadAnchor Anchor,
        IReadOnlyList<BlockChangeRecord> Changes,
        IReadOnlyList<McpReportTaskSummary> Tasks,
        IReadOnlyList<ReportDiagnosticDto> Diagnostics,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? RenderedMarkdown,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<ReportBlock>? Blocks);

    private sealed record McpReportTaskSummary(
        string Id,
        string TaskKey,
        string Kind,
        string? Title,
        TaskRunStatus Status,
        FlowOutcome Outcome,
        string? OutcomeSummary,
        int RunCount,
        string? SessionId,
        IReadOnlyList<string>? DependsOn,
        long? ArchivedAtMs);
}
