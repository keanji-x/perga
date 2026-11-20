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
          "properties": {
            "format": {
              "type": "string",
              "enum": ["full", "blocks", "markdown", "outline"],
              "description": "Response projection. Defaults to 'full'. Use 'blocks' or 'markdown' to avoid duplicating content, or 'outline' for a compact section/block index."
            },
            "sectionH1": {
              "type": "string",
              "description": "Optional H1 section title filter. Returns only blocks in that section with a scoped ReadAnchor (wholeDocumentRead=false)."
            },
            "blockIds": {
              "type": "array",
              "minItems": 1,
              "items": { "type": "string", "minLength": 1 },
              "description": "Optional non-empty block ID allowlist. Returns only matching blocks with a scoped ReadAnchor (wholeDocumentRead=false)."
            },
            "includeContract": {
              "type": "boolean",
              "description": "When true, includes concise authoring contracts and minimal examples for perga-block fences (task, table, view, html)."
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
              "description": "ReadAnchor returned by perga_report_read or the previous perga_report_commit."
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
                      "blockKind": { "type": "string", "enum": ["prose", "task", "html", "table", "view"] },
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

    internal static JsonElement FormatReportReadResponse(ReportReadResult res, JsonElement args)
    {
        var format = ParseReadFormat(args);
        var sectionFilter = ParseOptionalSectionFilter(args);
        var blockIdFilter = ParseOptionalBlockIdFilter(args);
        var includeContract = ParseOptionalBool(args, ["includeContract", "include_contract"]);

        var isScoped = sectionFilter is not null || blockIdFilter is not null;
        if (!isScoped && format == "full" && !includeContract)
        {
            return JsonHelpers.ToElement(res);
        }

        var blocks = FilterBlocks(res.Blocks, sectionFilter, blockIdFilter);
        var anchor = format == "outline"
            ? new ReadAnchor(res.DocRev, new Dictionary<string, int>(StringComparer.Ordinal), WholeDocumentRead: false)
            : isScoped
                ? new ReadAnchor(
                    res.DocRev,
                    blocks.ToDictionary(b => b.Id, b => b.Rev, StringComparer.Ordinal),
                    WholeDocumentRead: false)
                : res.Anchor;

        string? renderedMarkdown = format is "full" or "markdown"
            ? (isScoped
                ? ScopedRenderer.RenderMarkdown(new ReportDocument(res.DocRev, res.Summary, string.Empty, blocks), withMarkers: true)
                : res.RenderedMarkdown)
            : null;

        IReadOnlyList<string>? sections = format == "outline"
            ? CollectOrderedSections(blocks)
            : null;

        IReadOnlyList<ReportOutlineBlock>? outlineBlocks = format == "outline"
            ? blocks.Select(b => new ReportOutlineBlock(b.Id, b.Rev, b.Kind, b.SectionH1)).ToArray()
            : null;

        IReadOnlyList<ReportBlock>? fullBlocks = format is "full" or "blocks"
            ? blocks
            : null;

        return JsonHelpers.ToElement(new McpReportReadResponse(
            DocRev: res.DocRev,
            Summary: res.Summary,
            Sections: sections,
            RenderedMarkdown: renderedMarkdown,
            Blocks: fullBlocks,
            OutlineBlocks: outlineBlocks,
            Tasks: res.Tasks,
            Anchor: anchor,
            Diagnostics: res.Diagnostics,
            BlockContract: includeContract ? CachedBlockContract : null));
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
            Tasks: res.Tasks,
            Diagnostics: res.Diagnostics,
            RenderedMarkdown: includeDocument ? res.RenderedMarkdown : null,
            Blocks: includeDocument ? res.Blocks : null);
        return JsonHelpers.ToElement(receipt);
    }

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
        if (!JsonHelpers.TryGetPropertyIgnoreCase(args, ["sectionH1", "section_h1", "section"], out var elem) ||
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
        if (!JsonHelpers.TryGetPropertyIgnoreCase(args, ["blockIds", "block_ids"], out var elem) ||
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

    private static IReadOnlyList<ReportBlock> FilterBlocks(
        IReadOnlyList<ReportBlock> blocks,
        string? sectionFilter,
        HashSet<string>? blockIdFilter)
    {
        IEnumerable<ReportBlock> query = blocks;
        if (sectionFilter is not null)
        {
            query = query.Where(b => string.Equals(b.SectionH1, sectionFilter, StringComparison.OrdinalIgnoreCase));
        }

        if (blockIdFilter is not null)
        {
            query = query.Where(b => blockIdFilter.Contains(b.Id));
        }

        return query.ToArray();
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
          "fenceSyntax": "```perga-block <kind>\n<json or html>\n```",
          "ops": {
            "replace_section": { "op": "replace_section", "sectionH1": "Overview", "markdown": "Section body" },
            "upsert": { "op": "upsert", "id": "b_1234", "blockKind": "prose|task|table|view|html", "markdown": "...", "payload": {}, "position": 0 },
            "delete": { "op": "delete", "id": "b_1234" },
            "move": { "op": "move", "id": "b_1234", "toIndex": 0 }
          },
          "kinds": {
            "task": {
              "required": ["taskKey", "kind", "access", "ready", "goal"],
              "example": {
                "taskKey": "impl-1",
                "kind": "subagent",
                "access": "read_write",
                "ready": true,
                "dependsOn": [],
                "goal": "Implement change",
                "acceptance": "Tests pass"
              }
            },
            "table": {
              "required": ["columns", "rows"],
              "notes": "Each column requires unique key and label (align: left|right optional). Each row key must match a declared column key; cell values must be string, number, or null.",
              "example": {
                "caption": "Summary",
                "columns": [
                  { "key": "name", "label": "Name", "align": "left" },
                  { "key": "count", "label": "Count", "align": "right" }
                ],
                "rows": [
                  { "name": "Backend", "count": 9 }
                ]
              }
            },
            "view": {
              "required": ["version", "title", "description", "snapshot", "rows"],
              "notes": "version must be 1. snapshot is required ({id, observedAt, producedAt}) when any inline cell is used. row.layout (one|two|three|two-wide-start|two-wide-end) must match cells.length. Cell kinds: metrics, distribution, bars, time-series, table, meter, records, live.",
              "example": {
                "version": 1,
                "title": "Overview",
                "description": "Key metrics",
                "snapshot": { "id": "snap-1", "observedAt": 1700000000000, "producedAt": 1700000000000 },
                "rows": [
                  {
                    "id": "row-1",
                    "title": "Metrics",
                    "layout": "one",
                    "cells": [
                      {
                        "kind": "metrics",
                        "id": "m-1",
                        "title": "Totals",
                        "items": [
                          {
                            "id": "total",
                            "label": "Items",
                            "detail": "Active",
                            "emphasis": "primary",
                            "tone": "positive",
                            "value": { "state": "known", "amount": 42, "decimals": 0, "placement": "suffix", "signed": false, "unit": "" }
                          }
                        ]
                      }
                    ]
                  }
                ]
              }
            },
            "html": {
              "required": ["path"],
              "notes": "Workspace-relative .html/.htm file path rendered in a sandboxed frame (optional title and height).",
              "example": {
                "path": "docs/overview.html",
                "title": "Architecture diagram",
                "height": 480
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
        IReadOnlyList<TaskDto> Tasks,
        ReadAnchor Anchor,
        IReadOnlyList<ReportDiagnosticDto> Diagnostics,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonElement? BlockContract);

    private sealed record McpReportCommitReceipt(
        int DocRev,
        string Summary,
        ReadAnchor Anchor,
        IReadOnlyList<BlockChangeRecord> Changes,
        IReadOnlyList<TaskDto> Tasks,
        IReadOnlyList<ReportDiagnosticDto> Diagnostics,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? RenderedMarkdown,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<ReportBlock>? Blocks);
}
