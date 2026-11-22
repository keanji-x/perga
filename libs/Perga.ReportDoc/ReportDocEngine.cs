using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ZstdSharp;

namespace Perga.ReportDoc;

public sealed partial class ReportDocEngine : IReportDocEngine
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    [GeneratedRegex(@"^\s*<!--\s*perga:([A-Za-z0-9_-]+)(?:\s+rev=(\d+))?\s*-->\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex MarkerLineRegex();

    [GeneratedRegex(@"^#\s+(.+?)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex H1HeadingRegex();

    [GeneratedRegex(@"^\s*(`{3,})perga-block\s+(task|html|table|view|checkpoints|widget)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PergaBlockOpenRegex();

    [GeneratedRegex(@"^\s*(`{3,}|~{3,})", RegexOptions.CultureInvariant)]
    private static partial Regex GenericCodeFenceRegex();

    public ReportDocument Parse(string rawMarkdown, int docRev = 0, string summary = "")
    {
        ArgumentNullException.ThrowIfNull(rawMarkdown);
        ArgumentNullException.ThrowIfNull(summary);

        var normalized = rawMarkdown.Replace("\r\n", "\n").Replace('\r', '\n');
        var (leadingComment, remainder) = ExtractLeadingComment(normalized);

        var blocks = ParseBlocks(remainder, existingIds: null);
        return new ReportDocument(docRev, summary, leadingComment, blocks);
    }

    public string RenderMarkdown(ReportDocument doc, bool withMarkers = false)
    {
        ArgumentNullException.ThrowIfNull(doc);

        var chunks = new List<string>();
        if (!string.IsNullOrWhiteSpace(doc.LeadingComment))
        {
            chunks.Add(doc.LeadingComment.Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd('\n'));
        }

        string? emittedSection = null;
        foreach (var block in doc.Blocks)
        {
            if (!string.Equals(block.SectionH1, emittedSection, StringComparison.Ordinal))
            {
                if (block.SectionH1 is not null)
                {
                    chunks.Add($"# {block.SectionH1}");
                }

                emittedSection = block.SectionH1;
            }

            if (block.Kind == ReportBlockKind.Prose)
            {
                var body = NormalizeBlockText(block.Markdown);
                if (withMarkers)
                {
                    chunks.Add(string.IsNullOrEmpty(body)
                        ? $"<!-- perga:{block.Id} -->"
                        : $"<!-- perga:{block.Id} -->\n{body}");
                }
                else if (!string.IsNullOrEmpty(body))
                {
                    chunks.Add(body);
                }
            }
            else
            {
                var kindName = block.Kind.ToString().ToLowerInvariant();
                var body = block.Payload is { ValueKind: not JsonValueKind.Undefined and not JsonValueKind.Null } payload
                    ? payload.GetRawText()
                    : NormalizeBlockText(block.Markdown);

                var fence = BuildSafeBacktickFence(body);
                var fenced = string.IsNullOrEmpty(body)
                    ? $"{fence}perga-block {kindName}\n{fence}"
                    : $"{fence}perga-block {kindName}\n{body}\n{fence}";

                chunks.Add(withMarkers
                    ? $"<!-- perga:{block.Id} -->\n{fenced}"
                    : fenced);
            }
        }

        return string.Join("\n\n", chunks);
    }

    public ReportCommitResult ApplyCommit(
        ReportDocument current,
        ReadAnchor callerAnchor,
        IReadOnlyList<BlockOp> ops,
        string? newSummary = null)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(callerAnchor);
        ArgumentNullException.ThrowIfNull(ops);

        var originalById = new Dictionary<string, (ReportBlock Block, int Index)>(StringComparer.Ordinal);
        var usedIds = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < current.Blocks.Count; i++)
        {
            var b = current.Blocks[i];
            originalById[b.Id] = (b, i);
            usedIds.Add(b.Id);
        }

        var working = new List<ReportBlock>(current.Blocks);
        var explicitlyMovedIds = new HashSet<string>(StringComparer.Ordinal);
        var updatedIds = new HashSet<string>(StringComparer.Ordinal);
        var insertedIds = new HashSet<string>(StringComparer.Ordinal);
        var deletedIds = new HashSet<string>(StringComparer.Ordinal);

        var idSeed = 0;

        foreach (var op in ops)
        {
            switch (op)
            {
                case BlockOp.ReplaceSectionOp replaceOp:
                {
                    var targetSection = NormalizeSectionTitle(replaceOp.SectionH1);
                    if (string.IsNullOrEmpty(targetSection))
                    {
                        throw new ArgumentException("ReplaceSectionOp requires a non-empty SectionH1.", nameof(ops));
                    }

                    var existingIndices = new List<int>();
                    for (var i = 0; i < working.Count; i++)
                    {
                        if (string.Equals(working[i].SectionH1, targetSection, StringComparison.Ordinal))
                        {
                            existingIndices.Add(i);
                        }
                    }

                    foreach (var idx in existingIndices)
                    {
                        var existingBlock = working[idx];
                        if (originalById.TryGetValue(existingBlock.Id, out var orig))
                        {
                            EnsureBlockAuthorized(current, callerAnchor, orig.Block);
                        }
                    }

                    var insertPos = existingIndices.Count > 0 ? existingIndices[0] : working.Count;

                    // Remove old blocks in reverse index order so indices stay valid.
                    var removedInOp = new Dictionary<string, ReportBlock>(StringComparer.Ordinal);
                    for (var i = existingIndices.Count - 1; i >= 0; i--)
                    {
                        var removed = working[existingIndices[i]];
                        removedInOp[removed.Id] = removed;
                        working.RemoveAt(existingIndices[i]);
                    }

                    var parsedReplacement = ParseSectionReplacement(targetSection, replaceOp.Markdown, usedIds, removedInOp, ref idSeed);
                    working.InsertRange(insertPos, parsedReplacement);

                    var replacementIds = new HashSet<string>(parsedReplacement.Select(b => b.Id), StringComparer.Ordinal);
                    foreach (var (removedId, _) in removedInOp)
                    {
                        if (!replacementIds.Contains(removedId))
                        {
                            if (originalById.ContainsKey(removedId))
                            {
                                deletedIds.Add(removedId);
                            }

                            insertedIds.Remove(removedId);
                            updatedIds.Remove(removedId);
                        }
                    }

                    foreach (var replBlock in parsedReplacement)
                    {
                        deletedIds.Remove(replBlock.Id);
                        if (originalById.TryGetValue(replBlock.Id, out var original))
                        {
                            if (!ReferenceEquals(replBlock, original.Block))
                                updatedIds.Add(replBlock.Id);
                        }
                        else
                        {
                            insertedIds.Add(replBlock.Id);
                        }
                    }

                    break;
                }

                case BlockOp.UpsertBlockOp upsertOp:
                {
                    var existingIdx = !string.IsNullOrWhiteSpace(upsertOp.Id)
                        ? working.FindIndex(b => string.Equals(b.Id, upsertOp.Id, StringComparison.Ordinal))
                        : -1;

                    if (existingIdx >= 0)
                    {
                        var existing = working[existingIdx];
                        if (originalById.TryGetValue(existing.Id, out var orig))
                        {
                            EnsureBlockAuthorized(current, callerAnchor, orig.Block);
                        }

                        var (extractedSection, normalizedMarkdown) = ExtractOptionalLeadingH1(upsertOp.Kind, upsertOp.Markdown);
                        var nextSection = extractedSection ?? existing.SectionH1;
                        var nextPayload = CloneNullableElement(upsertOp.Payload);
                        var nextRev = originalById.TryGetValue(existing.Id, out var origEntry)
                            ? Math.Max(existing.Rev, origEntry.Block.Rev) + 1
                            : existing.Rev + 1;

                        var updated = new ReportBlock(
                            existing.Id,
                            nextRev,
                            upsertOp.Kind,
                            nextSection,
                            normalizedMarkdown,
                            nextPayload);

                        if (upsertOp.Position is { } requestedPos)
                        {
                            working.RemoveAt(existingIdx);
                            var clamped = Math.Clamp(requestedPos, 0, working.Count);
                            working.Insert(clamped, updated);
                            if (clamped != existingIdx)
                            {
                                explicitlyMovedIds.Add(existing.Id);
                            }
                        }
                        else
                        {
                            working[existingIdx] = updated;
                        }

                        if (originalById.ContainsKey(existing.Id))
                        {
                            updatedIds.Add(existing.Id);
                        }
                    }
                    else
                    {
                        // If caller specified an ID they previously knew about (deleted concurrently), surface a conflict.
                        if (!string.IsNullOrWhiteSpace(upsertOp.Id) &&
                            callerAnchor.KnownBlockRevs is not null &&
                            callerAnchor.KnownBlockRevs.ContainsKey(upsertOp.Id))
                        {
                            throw new ReportConflictException(
                                "block_deleted",
                                $"Block '{upsertOp.Id}' was deleted concurrently before docRev {current.DocRev}.",
                                "Call report_read to refresh the latest report blocks before retrying.");
                        }

                        var newId = !string.IsNullOrWhiteSpace(upsertOp.Id) && usedIds.Add(upsertOp.Id)
                            ? upsertOp.Id
                            : AllocateDeterministicId(
                                upsertOp.Kind,
                                null,
                                upsertOp.Markdown ?? (upsertOp.Payload?.GetRawText() ?? string.Empty),
                                ++idSeed + current.DocRev,
                                usedIds);

                        var insertIdx = upsertOp.Position is { } pos
                            ? Math.Clamp(pos, 0, working.Count)
                            : working.Count;

                        var (explicitH1, normalizedMarkdown) = ExtractOptionalLeadingH1(upsertOp.Kind, upsertOp.Markdown);
                        var inheritedSection = explicitH1 ?? InferSectionForInsert(working, insertIdx);
                        var clonedPayload = CloneNullableElement(upsertOp.Payload);

                        var inserted = new ReportBlock(
                            newId,
                            1,
                            upsertOp.Kind,
                            inheritedSection,
                            normalizedMarkdown,
                            clonedPayload);

                        working.Insert(insertIdx, inserted);
                        deletedIds.Remove(newId);
                        if (originalById.ContainsKey(newId))
                        {
                            updatedIds.Add(newId);
                        }
                        else
                        {
                            insertedIds.Add(newId);
                        }
                    }

                    break;
                }

                case BlockOp.DeleteBlockOp deleteOp:
                {
                    var idx = working.FindIndex(b => string.Equals(b.Id, deleteOp.Id, StringComparison.Ordinal));
                    if (idx < 0)
                    {
                        EnsureMissingBlockConflictOrNotFound(current, callerAnchor, deleteOp.Id);
                        throw new KeyNotFoundException($"Block '{deleteOp.Id}' does not exist.");
                    }

                    var target = working[idx];
                    if (originalById.TryGetValue(target.Id, out var orig))
                    {
                        EnsureBlockAuthorized(current, callerAnchor, orig.Block);
                        deletedIds.Add(target.Id);
                    }

                    working.RemoveAt(idx);
                    insertedIds.Remove(target.Id);
                    updatedIds.Remove(target.Id);
                    explicitlyMovedIds.Remove(target.Id);
                    break;
                }

                case BlockOp.MoveBlockOp moveOp:
                {
                    if (moveOp.ToIndex < 0)
                    {
                        throw new ArgumentOutOfRangeException(nameof(ops), "MoveBlockOp.ToIndex cannot be negative.");
                    }

                    var idx = working.FindIndex(b => string.Equals(b.Id, moveOp.Id, StringComparison.Ordinal));
                    if (idx < 0)
                    {
                        EnsureMissingBlockConflictOrNotFound(current, callerAnchor, moveOp.Id);
                        throw new KeyNotFoundException($"Block '{moveOp.Id}' does not exist.");
                    }

                    var moving = working[idx];
                    if (originalById.TryGetValue(moving.Id, out var orig))
                    {
                        EnsureBlockAuthorized(current, callerAnchor, orig.Block);
                    }

                    working.RemoveAt(idx);
                    var targetIdx = Math.Clamp(moveOp.ToIndex, 0, working.Count);
                    var adjustedSection = AdjustSectionOnMove(working, moving, targetIdx);
                    var placed = adjustedSection == moving.SectionH1
                        ? moving
                        : moving with { SectionH1 = adjustedSection };

                    working.Insert(targetIdx, placed);
                    explicitlyMovedIds.Add(moving.Id);
                    break;
                }

                default:
                    throw new NotSupportedException($"Unsupported BlockOp type: {op.GetType().Name}");
            }
        }

        var changes = BuildChangeRecords(
            originalById,
            working,
            insertedIds,
            updatedIds,
            deletedIds,
            explicitlyMovedIds);

        var nextDoc = new ReportDocument(
            current.DocRev + 1,
            newSummary ?? current.Summary,
            current.LeadingComment,
            working);

        return new ReportCommitResult(nextDoc, changes);
    }

    public byte[] CompressZstd(IReadOnlyList<ReportBlock> blocks)
    {
        ArgumentNullException.ThrowIfNull(blocks);

        var utf8Json = JsonSerializer.SerializeToUtf8Bytes(blocks, JsonOptions);
        using var compressor = new Compressor(level: 3);
        return compressor.Wrap(utf8Json).ToArray();
    }

    public IReadOnlyList<ReportBlock> DecompressZstd(byte[] compressed)
    {
        ArgumentNullException.ThrowIfNull(compressed);

        using var input = new MemoryStream(compressed, writable: false);
        using var decompressionStream = new DecompressionStream(input);
        using var output = new MemoryStream();
        decompressionStream.CopyTo(output);

        var deserialized = JsonSerializer.Deserialize<List<ReportBlock>>(output.GetBuffer().AsSpan(0, (int)output.Length), JsonOptions)
            ?? [];

        // Ensure every Payload JsonElement is independently cloned.
        for (var i = 0; i < deserialized.Count; i++)
        {
            if (deserialized[i].Payload is { } p)
            {
                deserialized[i] = deserialized[i] with { Payload = p.Clone() };
            }
        }

        return deserialized;
    }

    private static void EnsureBlockAuthorized(ReportDocument current, ReadAnchor callerAnchor, ReportBlock targetBlock)
    {
        if (callerAnchor.WholeDocumentRead && callerAnchor.DocRev == current.DocRev)
        {
            return;
        }

        if (callerAnchor.KnownBlockRevs is not null &&
            callerAnchor.KnownBlockRevs.TryGetValue(targetBlock.Id, out var knownRev))
        {
            if (knownRev == targetBlock.Rev)
            {
                return;
            }

            throw new ReportConflictException(
                "stale_block_rev",
                $"Conflict on block '{targetBlock.Id}': caller read rev {knownRev}, but current block rev is {targetBlock.Rev} (docRev {current.DocRev}).",
                $"Call report_read to inspect block '{targetBlock.Id}' at rev {targetBlock.Rev} before retrying.");
        }

        if (callerAnchor.WholeDocumentRead && callerAnchor.DocRev != current.DocRev)
        {
            throw new ReportConflictException(
                "stale_doc_rev",
                $"Conflict on block '{targetBlock.Id}': caller whole-document read anchor is at docRev {callerAnchor.DocRev}, but current docRev is {current.DocRev}.",
                "Call report_read to fetch the latest report revision before retrying.");
        }

        throw new ReportConflictException(
            "unread_block",
            $"Cannot modify or delete block '{targetBlock.Id}' (rev {targetBlock.Rev}) without reading it first.",
            $"Call report_read to obtain a valid ReadAnchor covering block '{targetBlock.Id}'.");
    }

    private static void EnsureMissingBlockConflictOrNotFound(ReportDocument current, ReadAnchor callerAnchor, string blockId)
    {
        if (callerAnchor.KnownBlockRevs is not null && callerAnchor.KnownBlockRevs.ContainsKey(blockId))
        {
            throw new ReportConflictException(
                "block_deleted",
                $"Conflict on block '{blockId}': block was deleted prior to docRev {current.DocRev}.",
                "Call report_read to refresh the current block list.");
        }

        if (!callerAnchor.WholeDocumentRead || callerAnchor.DocRev != current.DocRev)
        {
            throw new ReportConflictException(
                "stale_doc_rev",
                $"Block '{blockId}' not found; caller anchor (docRev {callerAnchor.DocRev}, wholeRead={callerAnchor.WholeDocumentRead}) is stale relative to docRev {current.DocRev}.",
                "Call report_read to fetch the latest report state.");
        }
    }

    private static List<BlockChangeRecord> BuildChangeRecords(
        Dictionary<string, (ReportBlock Block, int Index)> originalById,
        List<ReportBlock> finalBlocks,
        HashSet<string> insertedIds,
        HashSet<string> updatedIds,
        HashSet<string> deletedIds,
        HashSet<string> explicitlyMovedIds)
    {
        var changes = new List<BlockChangeRecord>();
        var finalIds = new HashSet<string>(StringComparer.Ordinal);

        for (var pos = 0; pos < finalBlocks.Count; pos++)
        {
            var block = finalBlocks[pos];
            finalIds.Add(block.Id);

            if (originalById.TryGetValue(block.Id, out var orig))
            {
                var moved = explicitlyMovedIds.Contains(block.Id) && orig.Index != pos;
                var wasUpdated = updatedIds.Contains(block.Id) || block.Rev != orig.Block.Rev;
                if (wasUpdated || explicitlyMovedIds.Contains(block.Id))
                {
                    changes.Add(new BlockChangeRecord(
                        block.Id,
                        block.Kind,
                        orig.Block.Rev,
                        block.Rev,
                        pos,
                        moved || explicitlyMovedIds.Contains(block.Id)));
                }
            }
            else if (insertedIds.Contains(block.Id))
            {
                changes.Add(new BlockChangeRecord(
                    block.Id,
                    block.Kind,
                    null,
                    block.Rev,
                    pos,
                    Moved: false));
            }
        }

        foreach (var (id, orig) in originalById)
        {
            if (!finalIds.Contains(id) || deletedIds.Contains(id))
            {
                changes.Add(new BlockChangeRecord(
                    id,
                    orig.Block.Kind,
                    orig.Block.Rev,
                    null,
                    null,
                    Moved: false));
            }
        }

        return changes;
    }

    private static List<ReportBlock> ParseSectionReplacement(
        string sectionH1,
        string rawSectionMarkdown,
        HashSet<string> usedIds,
        Dictionary<string, ReportBlock> removedInSection,
        ref int idSeed)
    {
        var normalized = (rawSectionMarkdown ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');

        // Allow callers to pass either body-only markdown or markdown starting with "# SectionH1".
        var lines = normalized.Split('\n');
        var startIdx = 0;
        while (startIdx < lines.Length && string.IsNullOrWhiteSpace(lines[startIdx]))
        {
            startIdx++;
        }

        if (startIdx < lines.Length)
        {
            var h1Match = H1HeadingRegex().Match(lines[startIdx]);
            if (h1Match.Success && string.Equals(h1Match.Groups[1].Value.Trim(), sectionH1, StringComparison.OrdinalIgnoreCase))
            {
                normalized = string.Join("\n", lines.Skip(startIdx + 1));
            }
        }

        var fullSectionSource = string.IsNullOrWhiteSpace(normalized)
            ? $"# {sectionH1}"
            : $"# {sectionH1}\n\n{normalized.Trim('\n')}";

        // Allow reusing IDs from blocks that belonged to the section being replaced.
        var reservedOutsideSection = new HashSet<string>(usedIds, StringComparer.Ordinal);
        foreach (var removedId in removedInSection.Keys)
        {
            reservedOutsideSection.Remove(removedId);
        }

        var parsed = ParseBlocks(fullSectionSource, reservedOutsideSection, seedOffset: ++idSeed * 997);
        var result = new List<ReportBlock>(parsed.Count);
        foreach (var parsedBlock in parsed)
        {
            var block = parsedBlock with { SectionH1 = sectionH1 };
            usedIds.Add(block.Id);
            if (removedInSection.TryGetValue(block.Id, out var previous))
            {
                result.Add(previous.HasSameContentAs(block) ? previous : block with { Rev = previous.Rev + 1 });
            }
            else
            {
                result.Add(block with { Rev = 1 });
            }
        }

        return result;
    }

    private static (string LeadingComment, string Remainder) ExtractLeadingComment(string normalized)
    {
        var idx = 0;
        var len = normalized.Length;

        while (idx < len && (normalized[idx] == ' ' || normalized[idx] == '\t' || normalized[idx] == '\n'))
        {
            idx++;
        }

        var comments = new List<string>();
        var cursor = idx;

        while (cursor < len && normalized.AsSpan(cursor).StartsWith("<!--", StringComparison.Ordinal))
        {
            var closeIdx = normalized.IndexOf("-->", cursor + 4, StringComparison.Ordinal);
            if (closeIdx < 0)
            {
                break;
            }

            var commentEnd = closeIdx + 3;
            var commentSlice = normalized[cursor..commentEnd];

            // Stop if this HTML comment is actually a perga block marker.
            var inner = normalized[(cursor + 4)..closeIdx].Trim();
            if (inner.StartsWith("perga:", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            comments.Add(commentSlice);
            cursor = commentEnd;
            while (cursor < len && (normalized[cursor] == ' ' || normalized[cursor] == '\t' || normalized[cursor] == '\n'))
            {
                cursor++;
            }
        }

        if (comments.Count == 0)
        {
            return (string.Empty, normalized);
        }

        return (string.Join("\n", comments), normalized[cursor..]);
    }

    private static List<ReportBlock> ParseBlocks(string markdown, HashSet<string>? existingIds, int seedOffset = 0)
    {
        var blocks = new List<ReportBlock>();
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return blocks;
        }

        var usedIds = existingIds is not null
            ? new HashSet<string>(existingIds, StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);

        var lines = markdown.Split('\n');
        string? currentSectionH1 = null;
        var sectionHasEmittedBlock = false;
        string? pendingMarkerId = null;
        int? pendingMarkerRev = null;
        var proseLines = new List<string>();

        var inProseFence = false;
        var proseFenceChar = '`';
        var proseFenceLen = 0;
        var occurrenceCounter = seedOffset;

        void EmitProse(string text, bool allowEmpty)
        {
            if (!allowEmpty && string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            occurrenceCounter++;
            var id = ResolveBlockId(
                pendingMarkerId,
                ReportBlockKind.Prose,
                currentSectionH1,
                text,
                occurrenceCounter,
                usedIds);
            var rev = pendingMarkerRev ?? 1;
            blocks.Add(new ReportBlock(id, rev, ReportBlockKind.Prose, currentSectionH1, text, null));
            sectionHasEmittedBlock = true;
            pendingMarkerId = null;
            pendingMarkerRev = null;
        }

        void FlushAccumulatedProse()
        {
            var trimmed = TrimBlankLines(proseLines);
            proseLines.Clear();
            if (!string.IsNullOrEmpty(trimmed))
            {
                EmitProse(trimmed, allowEmpty: false);
            }
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];

            if (inProseFence)
            {
                proseLines.Add(line);
                if (IsClosingFence(line, proseFenceChar, proseFenceLen))
                {
                    inProseFence = false;
                }

                continue;
            }

            var markerMatch = MarkerLineRegex().Match(line);
            if (markerMatch.Success)
            {
                var trimmedProse = TrimBlankLines(proseLines);
                proseLines.Clear();
                if (!string.IsNullOrEmpty(trimmedProse))
                {
                    EmitProse(trimmedProse, allowEmpty: false);
                }
                else if (pendingMarkerId is not null)
                {
                    EmitProse(string.Empty, allowEmpty: true);
                }

                pendingMarkerId = markerMatch.Groups[1].Value;
                pendingMarkerRev = markerMatch.Groups[2].Success && int.TryParse(markerMatch.Groups[2].Value, out var r)
                    ? r
                    : null;
                continue;
            }

            var h1Match = H1HeadingRegex().Match(line);
            if (h1Match.Success)
            {
                var trimmedProse = TrimBlankLines(proseLines);
                proseLines.Clear();
                if (!string.IsNullOrEmpty(trimmedProse))
                {
                    EmitProse(trimmedProse, allowEmpty: false);
                }
                else if (currentSectionH1 is not null && !sectionHasEmittedBlock)
                {
                    EmitProse(string.Empty, allowEmpty: true);
                }

                currentSectionH1 = h1Match.Groups[1].Value.Trim();
                sectionHasEmittedBlock = false;
                continue;
            }

            var pergaFenceMatch = PergaBlockOpenRegex().Match(line);
            if (pergaFenceMatch.Success)
            {
                FlushAccumulatedProse();

                var openBackticks = pergaFenceMatch.Groups[1].Value.Length;
                var kind = ParseBlockKind(pergaFenceMatch.Groups[2].Value);
                var fenceLines = new List<string>();
                i++;
                while (i < lines.Length && !IsClosingFence(lines[i], '`', openBackticks))
                {
                    fenceLines.Add(lines[i]);
                    i++;
                }

                var inner = string.Join("\n", fenceLines);
                var (blockMarkdown, blockPayload) = ParseStructuredBlockContent(inner);

                occurrenceCounter++;
                var id = ResolveBlockId(
                    pendingMarkerId,
                    kind,
                    currentSectionH1,
                    inner,
                    occurrenceCounter,
                    usedIds);
                var rev = pendingMarkerRev ?? 1;

                blocks.Add(new ReportBlock(id, rev, kind, currentSectionH1, blockMarkdown, blockPayload));
                sectionHasEmittedBlock = true;
                pendingMarkerId = null;
                pendingMarkerRev = null;
                continue;
            }

            var genericFenceMatch = GenericCodeFenceRegex().Match(line);
            if (genericFenceMatch.Success)
            {
                var fenceToken = genericFenceMatch.Groups[1].Value;
                inProseFence = true;
                proseFenceChar = fenceToken[0];
                proseFenceLen = fenceToken.Length;
            }

            proseLines.Add(line);
        }

        var trailingProse = TrimBlankLines(proseLines);
        proseLines.Clear();
        if (!string.IsNullOrEmpty(trailingProse))
        {
            EmitProse(trailingProse, allowEmpty: false);
        }
        else if (pendingMarkerId is not null || (currentSectionH1 is not null && !sectionHasEmittedBlock))
        {
            EmitProse(string.Empty, allowEmpty: true);
        }

        return blocks;
    }

    private static (string? Markdown, JsonElement? Payload) ParseStructuredBlockContent(string inner)
    {
        var trimmed = inner.Trim();
        if (trimmed.Length >= 2 &&
            ((trimmed[0] == '{' && trimmed[^1] == '}') || (trimmed[0] == '[' && trimmed[^1] == ']')))
        {
            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                if (doc.RootElement.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                {
                    return (null, doc.RootElement.Clone());
                }
            }
            catch (JsonException)
            {
                // Fall back to storing raw content in Markdown (e.g. non-JSON html/template content).
            }
        }

        return (inner, null);
    }

    private static ReportBlockKind ParseBlockKind(string rawKind) =>
        rawKind.ToLowerInvariant() switch
        {
            "task" => ReportBlockKind.Task,
            "html" => ReportBlockKind.Html,
            "table" => ReportBlockKind.Table,
            "view" => ReportBlockKind.View,
            "checkpoints" => ReportBlockKind.Checkpoints,
            "widget" => ReportBlockKind.Widget,
            _ => ReportBlockKind.Prose
        };

    private static string ResolveBlockId(
        string? explicitMarkerId,
        ReportBlockKind kind,
        string? sectionH1,
        string content,
        int occurrenceIndex,
        HashSet<string> usedIds)
    {
        if (!string.IsNullOrWhiteSpace(explicitMarkerId) && usedIds.Add(explicitMarkerId))
        {
            return explicitMarkerId;
        }

        return AllocateDeterministicId(kind, sectionH1, content, occurrenceIndex, usedIds);
    }

    private static string AllocateDeterministicId(
        ReportBlockKind kind,
        string? sectionH1,
        string content,
        int occurrenceIndex,
        HashSet<string> usedIds)
    {
        Span<byte> hash = stackalloc byte[32];
        var attempt = 0;
        while (true)
        {
            var material = $"{sectionH1 ?? string.Empty}\0{kind}\0{content}\0{occurrenceIndex}\0{attempt}";
            SHA256.HashData(Encoding.UTF8.GetBytes(material), hash);
            var candidate = $"b_{hash[0]:x2}{hash[1]:x2}";
            if (usedIds.Add(candidate))
            {
                return candidate;
            }

            attempt++;
        }
    }

    private static (string? SectionH1, string? Markdown) ExtractOptionalLeadingH1(ReportBlockKind kind, string? markdown)
    {
        if (markdown is null)
        {
            return (null, null);
        }

        var normalized = markdown.Replace("\r\n", "\n").Replace('\r', '\n');
        if (kind != ReportBlockKind.Prose)
        {
            return (null, normalized);
        }

        var lines = normalized.Split('\n');
        var idx = 0;
        while (idx < lines.Length && string.IsNullOrWhiteSpace(lines[idx]))
        {
            idx++;
        }

        if (idx < lines.Length)
        {
            var match = H1HeadingRegex().Match(lines[idx]);
            if (match.Success)
            {
                var h1 = match.Groups[1].Value.Trim();
                var rest = TrimBlankLines(lines.Skip(idx + 1).ToList());
                return (h1, rest);
            }
        }

        return (null, TrimBlankLines(lines.ToList()));
    }

    private static string? InferSectionForInsert(List<ReportBlock> blocks, int insertIdx)
    {
        if (blocks.Count == 0)
        {
            return null;
        }

        if (insertIdx > 0)
        {
            return blocks[insertIdx - 1].SectionH1;
        }

        return blocks[0].SectionH1;
    }

    private static string? AdjustSectionOnMove(List<ReportBlock> remainingBlocks, ReportBlock moving, int targetIdx)
    {
        if (remainingBlocks.Count == 0)
        {
            return moving.SectionH1;
        }

        var prevSection = targetIdx > 0 ? remainingBlocks[targetIdx - 1].SectionH1 : null;
        var nextSection = targetIdx < remainingBlocks.Count ? remainingBlocks[targetIdx].SectionH1 : null;

        // If moving into the middle of another contiguous section, adopt that section.
        if (prevSection is not null && string.Equals(prevSection, nextSection, StringComparison.Ordinal))
        {
            return prevSection;
        }

        // In Markdown, a null section cannot appear after a non-null H1 section.
        if (moving.SectionH1 is null && prevSection is not null)
        {
            return prevSection;
        }

        // If other blocks of moving.SectionH1 remain elsewhere and we moved away from them, adopt target neighbor section.
        var sameSectionExistsElsewhere = remainingBlocks.Any(b => string.Equals(b.SectionH1, moving.SectionH1, StringComparison.Ordinal));
        var adjacentMatchesMoving =
            string.Equals(prevSection, moving.SectionH1, StringComparison.Ordinal) ||
            string.Equals(nextSection, moving.SectionH1, StringComparison.Ordinal);

        if (sameSectionExistsElsewhere && !adjacentMatchesMoving)
        {
            return prevSection ?? nextSection;
        }

        return moving.SectionH1;
    }

    private static string NormalizeSectionTitle(string sectionH1)
    {
        if (string.IsNullOrWhiteSpace(sectionH1))
        {
            return string.Empty;
        }

        var trimmed = sectionH1.Trim();
        var match = H1HeadingRegex().Match(trimmed);
        return match.Success ? match.Groups[1].Value.Trim() : trimmed;
    }

    private static JsonElement? CloneNullableElement(JsonElement? element) =>
        element is { ValueKind: not JsonValueKind.Undefined } e ? e.Clone() : null;

    private static bool IsClosingFence(string line, char fenceChar, int minLength)
    {
        var span = line.AsSpan().TrimStart();
        var count = 0;
        while (count < span.Length && span[count] == fenceChar)
        {
            count++;
        }

        if (count < minLength)
        {
            return false;
        }

        return span[count..].IsWhiteSpace();
    }

    private static string BuildSafeBacktickFence(string body)
    {
        var maxRun = 0;
        var currentRun = 0;
        foreach (var ch in body)
        {
            if (ch == '`')
            {
                currentRun++;
                if (currentRun > maxRun)
                {
                    maxRun = currentRun;
                }
            }
            else
            {
                currentRun = 0;
            }
        }

        return new string('`', Math.Max(3, maxRun + 1));
    }

    private static string NormalizeBlockText(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        return TrimBlankLines(text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').ToList());
    }

    private static string TrimBlankLines(List<string> lines)
    {
        var start = 0;
        var end = lines.Count - 1;

        while (start <= end && string.IsNullOrWhiteSpace(lines[start]))
        {
            start++;
        }

        while (end >= start && string.IsNullOrWhiteSpace(lines[end]))
        {
            end--;
        }

        if (start > end)
        {
            return string.Empty;
        }

        return string.Join("\n", lines.GetRange(start, end - start + 1));
    }
}
