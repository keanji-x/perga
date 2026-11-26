using System.Text.Json;

namespace Perga.ReportDoc;

public sealed partial class ReportDocEngine
{
    private const string OperationsParameterName = "ops";

    public ReportCommitResult ApplyCommit(
        ReportDocument current,
        ReadAnchor callerAnchor,
        IReadOnlyList<BlockOp> ops,
        string? newSummary = null)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(callerAnchor);
        ArgumentNullException.ThrowIfNull(ops);

        var state = new CommitState(current, callerAnchor);
        foreach (var op in ops)
        {
            switch (op)
            {
                case BlockOp.ReplaceSectionOp replace: ApplyReplaceSection(state, replace); break;
                case BlockOp.EditBlockOp edit: ApplyEditBlock(state, edit); break;
                case BlockOp.UpsertBlockOp upsert: ApplyUpsert(state, upsert); break;
                case BlockOp.DeleteBlockOp delete: ApplyDelete(state, delete); break;
                case BlockOp.MoveBlockOp move: ApplyMove(state, move); break;
                default: throw new NotSupportedException($"Unsupported BlockOp type: {op.GetType().Name}");
            }
        }

        var next = current with
        {
            DocRev = current.DocRev + 1,
            Summary = newSummary ?? current.Summary,
            Blocks = state.Blocks
        };
        return new ReportCommitResult(next, BuildChangeRecords(state));
    }

    private static void ApplyReplaceSection(CommitState state, BlockOp.ReplaceSectionOp op)
    {
        var targetSection = NormalizeSectionTitle(op.SectionH1, "sectionH1");
        var finalSection = op.NewSectionH1 is { } newTitle ? NormalizeSectionTitle(newTitle, "newSectionH1") : targetSection;
        var placementId = op.BeforeBlockId ?? op.AfterBlockId;
        if (op.BeforeBlockId is not null && op.AfterBlockId is not null)
        {
            throw new ArgumentException("Specify only one of 'beforeBlockId' and 'afterBlockId'.", OperationsParameterName);
        }
        if (placementId is not null && string.IsNullOrWhiteSpace(placementId))
        {
            throw new ArgumentException("Section placement requires a non-blank block ID.", OperationsParameterName);
        }
        if (op.Markdown is null && op.NewSectionH1 is null)
        {
            throw new ArgumentException("ReplaceSectionOp requires 'markdown' or 'newSectionH1'.", OperationsParameterName);
        }

        var blocks = state.Blocks;
        var existingIndices = new List<int>();
        var sectionBlocks = new Dictionary<string, (ReportBlock Block, int Index)>(StringComparer.Ordinal);
        for (var i = 0; i < blocks.Count; i++)
        {
            var block = blocks[i];
            if (block.SectionH1 == targetSection)
            {
                state.AuthorizeOriginalBlock(block.Id);
                existingIndices.Add(i);
                sectionBlocks.Add(block.Id, (block, i));
            }
        }

        if (op.Markdown is null && existingIndices.Count == 0)
        {
            if (state.Anchor.WholeDocumentRead && state.Anchor.DocRev != state.Current.DocRev)
            {
                throw new ReportConflictException(
                    "stale_doc_rev",
                    $"Section '{targetSection}' was not found at docRev {state.Current.DocRev}. Caller anchor is at docRev {state.Anchor.DocRev}.",
                    "Call report_read to fetch the latest report revision before retrying.");
            }

            throw new ArgumentException($"Section '{targetSection}' does not exist. Provide 'markdown' to create it.", OperationsParameterName);
        }

        var replacement = op.Markdown is { } markdown
            ? ParseSectionReplacement(state, targetSection, finalSection, markdown, sectionBlocks)
            : existingIndices.Select(index =>
            {
                var block = blocks[index];
                return targetSection == finalSection ? block : block with { SectionH1 = finalSection, Rev = block.Rev + 1 };
            }).ToList();

        if (op.Markdown is null && placementId is null)
        {
            for (var i = 0; i < existingIndices.Count; i++)
            {
                blocks[existingIndices[i]] = replacement[i];
            }
        }
        else
        {
            var insertIdx = existingIndices.Count > 0 ? existingIndices[0] : blocks.Count;
            for (var i = existingIndices.Count - 1; i >= 0; i--)
            {
                blocks.RemoveAt(existingIndices[i]);
            }

            if (placementId is not null)
            {
                if (sectionBlocks.ContainsKey(placementId))
                {
                    throw new ArgumentException($"Section placement cannot target block '{placementId}' in section '{targetSection}'.", OperationsParameterName);
                }

                insertIdx = state.GetBlockIndex(placementId) + (op.AfterBlockId is null ? 0 : 1);
                for (var i = 0; i < replacement.Count; i++)
                {
                    var id = replacement[i].Id;
                    if (sectionBlocks.TryGetValue(id, out var previous) && previous.Index != insertIdx + i)
                    {
                        state.ExplicitlyMovedIds.Add(id);
                    }
                }
            }

            EnsureValidSectionBoundary(blocks, insertIdx, finalSection);
            blocks.InsertRange(insertIdx, replacement);
        }
    }

    private static void ApplyEditBlock(CommitState state, BlockOp.EditBlockOp op)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(op.Id);
        ArgumentException.ThrowIfNullOrEmpty(op.Target);
        ArgumentNullException.ThrowIfNull(op.Replacement);
        var target = NormalizeLineEndings(op.Target);

        var index = state.GetBlockIndex(op.Id);
        var existing = state.Blocks[index];
        state.AuthorizeOriginalBlock(existing.Id);
        if (existing.Kind != ReportBlockKind.Prose)
        {
            throw new ArgumentException($"Block '{existing.Id}' has kind '{existing.Kind}'. edit_block requires a prose block.", OperationsParameterName);
        }

        var markdown = NormalizeLineEndings(existing.Markdown ?? string.Empty);
        var firstIdx = markdown.IndexOf(target, StringComparison.Ordinal);
        if (firstIdx < 0)
        {
            throw new ArgumentException($"Target text was not found in block '{existing.Id}'.", OperationsParameterName);
        }
        if (markdown.IndexOf(target, firstIdx + 1, StringComparison.Ordinal) >= 0)
        {
            throw new ArgumentException($"Target text matched multiple locations in block '{existing.Id}'; provide a more specific target.", OperationsParameterName);
        }

        var nextMarkdown = string.Concat(
            markdown.AsSpan(0, firstIdx),
            NormalizeLineEndings(op.Replacement),
            markdown.AsSpan(firstIdx + target.Length));
        state.Blocks[index] = existing with { Markdown = NormalizeBlockText(nextMarkdown), Rev = existing.Rev + 1 };
    }

    private static void ApplyUpsert(CommitState state, BlockOp.UpsertBlockOp op)
    {
        var explicitSection = op.SectionH1 is { } title ? NormalizeSectionTitle(title, "sectionH1") : null;
        var blocks = state.Blocks;
        var requestedId = string.IsNullOrWhiteSpace(op.Id) ? null : op.Id;
        var existingIdx = requestedId is null ? -1 : blocks.FindIndex(block => block.Id == requestedId);
        var existing = existingIdx < 0 ? null : blocks[existingIdx];
        if (existing is not null)
        {
            state.AuthorizeOriginalBlock(existing.Id);
            blocks.RemoveAt(existingIdx);
        }
        else if (requestedId is not null && state.Anchor.KnownBlockRevs?.ContainsKey(requestedId) == true)
        {
            throw new ReportConflictException(
                "block_deleted",
                $"Block '{requestedId}' was deleted concurrently before docRev {state.Current.DocRev}.",
                "Call report_read to refresh the latest report blocks before retrying.");
        }

        var insertIdx = op.Position is { } position
            ? Math.Clamp(position, 0, blocks.Count)
            : existing is null ? blocks.Count : existingIdx;
        var kind = op.Kind ?? existing?.Kind ?? ReportBlockKind.Prose;
        var (extractedSection, markdown) = ExtractOptionalLeadingH1(kind, op.Markdown);
        var section = explicitSection ?? extractedSection ??
            (existing is null ? InferSectionForInsert(blocks, insertIdx) : existing.SectionH1);
        if (existing is null || section != existing.SectionH1 || insertIdx != existingIdx)
        {
            EnsureValidSectionBoundary(blocks, insertIdx, section);
        }

        var payload = op.Payload;
        if (existing is not null && op.Markdown is null && payload is null && kind == existing.Kind)
        {
            markdown = existing.Markdown;
            payload = existing.Payload;
        }

        var id = existing?.Id ?? (requestedId is not null && state.UsedIds.Add(requestedId)
            ? requestedId
            : AllocateDeterministicId(kind, null, op.Markdown ?? op.Payload?.GetRawText() ?? string.Empty,
                ++state.IdSeed + state.Current.DocRev, state.UsedIds));
        blocks.Insert(insertIdx, new ReportBlock(id, existing is null ? 1 : existing.Rev + 1, kind, section, markdown, CloneNullableElement(payload)));
        if (existing is not null && insertIdx != existingIdx)
        {
            state.ExplicitlyMovedIds.Add(id);
        }
    }

    private static void ApplyDelete(CommitState state, BlockOp.DeleteBlockOp op)
    {
        var index = state.GetBlockIndex(op.Id);
        state.AuthorizeOriginalBlock(op.Id);
        state.Blocks.RemoveAt(index);
        state.ExplicitlyMovedIds.Remove(op.Id);
    }

    private static void ApplyMove(CommitState state, BlockOp.MoveBlockOp op)
    {
        if (op.ToIndex < 0)
        {
            throw new ArgumentOutOfRangeException(OperationsParameterName, "MoveBlockOp.ToIndex cannot be negative.");
        }

        var index = state.GetBlockIndex(op.Id);
        state.AuthorizeOriginalBlock(op.Id);
        var blocks = state.Blocks;
        var moving = blocks[index];
        blocks.RemoveAt(index);
        var targetIdx = Math.Clamp(op.ToIndex, 0, blocks.Count);
        var adjustedSection = AdjustSectionOnMove(blocks, moving, targetIdx);
        blocks.Insert(targetIdx, adjustedSection == moving.SectionH1
            ? moving
            : moving with { SectionH1 = adjustedSection, Rev = moving.Rev + 1 });
        state.ExplicitlyMovedIds.Add(moving.Id);
    }

    private static void EnsureValidSectionBoundary(List<ReportBlock> blocks, int insertIdx, string? sectionH1)
    {
        if (sectionH1 is not null && insertIdx < blocks.Count && blocks[insertIdx].SectionH1 is null)
        {
            throw new ArgumentException("Cannot place an H1 section before an untitled preamble block.", OperationsParameterName);
        }
        if (sectionH1 is null && insertIdx > 0 && blocks[insertIdx - 1].SectionH1 is not null)
        {
            throw new ArgumentException("Cannot place an untitled preamble block after an H1 section.", OperationsParameterName);
        }
    }

    private sealed class CommitState
    {
        public CommitState(ReportDocument current, ReadAnchor anchor)
        {
            Current = current;
            Anchor = anchor;
            Blocks = new List<ReportBlock>(current.Blocks);
            foreach (var block in Blocks)
            {
                OriginalById[block.Id] = block;
                UsedIds.Add(block.Id);
            }
        }

        public ReportDocument Current { get; }
        public ReadAnchor Anchor { get; }
        public List<ReportBlock> Blocks { get; }
        public Dictionary<string, ReportBlock> OriginalById { get; } = new(StringComparer.Ordinal);
        public HashSet<string> UsedIds { get; } = new(StringComparer.Ordinal);
        public HashSet<string> ExplicitlyMovedIds { get; } = new(StringComparer.Ordinal);
        public int IdSeed { get; set; }

        public void AuthorizeOriginalBlock(string id)
        {
            if (OriginalById.TryGetValue(id, out var original))
            {
                EnsureBlockAuthorized(Current, Anchor, original);
            }
        }

        public int GetBlockIndex(string id)
        {
            var index = Blocks.FindIndex(block => block.Id == id);
            if (index < 0)
            {
                EnsureMissingBlockConflictOrNotFound(Current, Anchor, id);
                throw new KeyNotFoundException($"Block '{id}' does not exist.");
            }

            return index;
        }
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

    private static List<BlockChangeRecord> BuildChangeRecords(CommitState state)
    {
        var changes = new List<BlockChangeRecord>();
        var finalIds = new HashSet<string>(StringComparer.Ordinal);

        for (var pos = 0; pos < state.Blocks.Count; pos++)
        {
            var block = state.Blocks[pos];
            finalIds.Add(block.Id);

            if (state.OriginalById.TryGetValue(block.Id, out var original))
            {
                var moved = state.ExplicitlyMovedIds.Contains(block.Id);
                if (block.Rev != original.Rev || moved)
                {
                    changes.Add(new BlockChangeRecord(
                        block.Id,
                        block.Kind,
                        original.Rev,
                        block.Rev,
                        pos,
                        moved));
                }
            }
            else
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

        foreach (var (id, original) in state.OriginalById)
        {
            if (!finalIds.Contains(id))
            {
                changes.Add(new BlockChangeRecord(
                    id,
                    original.Kind,
                    original.Rev,
                    null,
                    null,
                    Moved: false));
            }
        }

        return changes;
    }

    private static List<ReportBlock> ParseSectionReplacement(
        CommitState state,
        string targetSection,
        string finalSection,
        string rawSectionMarkdown,
        Dictionary<string, (ReportBlock Block, int Index)> removedInSection)
    {
        var normalized = NormalizeLineEndings(rawSectionMarkdown);

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
            var heading = h1Match.Groups[1].Value.Trim();
            if (h1Match.Success &&
                (string.Equals(heading, finalSection, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(heading, targetSection, StringComparison.OrdinalIgnoreCase)))
            {
                normalized = string.Join("\n", lines.Skip(startIdx + 1));
            }
        }

        var fullSectionSource = string.IsNullOrWhiteSpace(normalized)
            ? $"# {finalSection}"
            : $"# {finalSection}\n\n{normalized.Trim('\n')}";

        // Allow reusing IDs from blocks that belonged to the section being replaced.
        var reservedOutsideSection = new HashSet<string>(state.UsedIds, StringComparer.Ordinal);
        foreach (var removedId in removedInSection.Keys)
        {
            reservedOutsideSection.Remove(removedId);
        }

        var parsed = ParseBlocks(fullSectionSource, reservedOutsideSection, seedOffset: ++state.IdSeed * 997);
        var result = new List<ReportBlock>(parsed.Count);
        foreach (var parsedBlock in parsed)
        {
            var block = parsedBlock with { SectionH1 = finalSection };
            state.UsedIds.Add(block.Id);
            if (removedInSection.TryGetValue(block.Id, out var previous))
            {
                result.Add(previous.Block.HasSameContentAs(block) ? previous.Block : block with { Rev = previous.Block.Rev + 1 });
            }
            else
            {
                result.Add(block with { Rev = 1 });
            }
        }

        return result;
    }

    private static (string? SectionH1, string? Markdown) ExtractOptionalLeadingH1(ReportBlockKind kind, string? markdown)
    {
        if (markdown is null)
        {
            return (null, null);
        }

        var normalized = NormalizeLineEndings(markdown);
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

    private static string NormalizeSectionTitle(string sectionH1, string fieldName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sectionH1, fieldName);
        var trimmed = sectionH1.Trim();
        if (trimmed.AsSpan().IndexOfAny('\r', '\n') >= 0)
        {
            throw new ArgumentException($"'{fieldName}' must be a single line.", fieldName);
        }

        var match = H1HeadingRegex().Match(trimmed);
        return match.Success ? match.Groups[1].Value.Trim() : trimmed;
    }

    private static JsonElement? CloneNullableElement(JsonElement? element) =>
        element is { ValueKind: not JsonValueKind.Undefined } e ? e.Clone() : null;
}
