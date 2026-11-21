using System.Text.Json;
using Perga.Kernel;
using Perga.Kernel.Ports;
using Perga.VcsKit;

namespace Perga.Host.Mcp;

public static class VcsMcpToolNames
{
    public const string Status = "status";
    public const string Diff = "diff";
    public const string RestoreOp = "restore_op";
    public const string RebaseUpstream = "rebase_upstream";
    public const string PublishPr = "publish_pr";
    public const string PrChecks = "pr_checks";
    public const string CiEvidence = "ci_evidence";
    public const string PrMerge = "pr_merge";
    public const string IssueView = "issue_view";

    public static readonly IReadOnlyList<string> All =
    [
        Status,
        Diff,
        RestoreOp,
        RebaseUpstream,
        PublishPr,
        PrChecks,
        CiEvidence,
        PrMerge,
        IssueView
    ];
}

public sealed class VcsMcpBackend : IMcpToolBackend
{
    private static readonly IReadOnlyList<McpToolWireDescriptor> ToolDescriptors = BuildDescriptors();

    private readonly IPergaKernel _kernel;
    private readonly IVcsService _vcsService;
    private readonly IGitHubForgeService _forgeService;

    public VcsMcpBackend(
        IPergaKernel kernel,
        IVcsService vcsService,
        IGitHubForgeService forgeService)
    {
        _kernel = kernel ?? throw new ArgumentNullException(nameof(kernel));
        _vcsService = vcsService ?? throw new ArgumentNullException(nameof(vcsService));
        _forgeService = forgeService ?? throw new ArgumentNullException(nameof(forgeService));
    }

    public string ServerName => "vcs";

    public Task<IReadOnlyList<McpToolWireDescriptor>> ListToolsAsync(McpSessionPrincipal caller, CancellationToken ct)
    {
        _ = caller;
        _ = ct;
        return Task.FromResult(ToolDescriptors);
    }

    public async Task<McpCallToolResult> CallToolAsync(McpSessionPrincipal caller, string toolName,
        JsonElement arguments, CancellationToken ct)
    {
        var args = arguments.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
            ? McpToolResultFormatter.EmptyObject
            : arguments;
        var result = await CallToolCoreAsync(caller, toolName, args, ct).ConfigureAwait(false);
        if (!result.IsError)
            await _kernel.Actors.GetOrActivate(caller.TrackId).AfterMcpToolCallAsync(ServerName, toolName,
                JsonSerializer.SerializeToElement(new { mcpServer = ServerName, arguments = args, result }), ct).ConfigureAwait(false);
        return result;
    }

    private async Task<McpCallToolResult> CallToolCoreAsync(
        McpSessionPrincipal caller,
        string toolName,
        JsonElement args,
        CancellationToken ct)
    {
        try
        {
            if (!VcsMcpToolNames.All.Contains(toolName))
                throw KernelException.NotFound($"Unknown VCS MCP tool '{toolName}'.");

            await _kernel.GuardMcpToolCallAsync(caller.TrackId, caller.SessionId, ServerName, toolName, args, ct, caller.ExecutionOrigin)
                .ConfigureAwait(false);

            var workspaceCwd = await ResolveCallerWorkspaceCwdAsync(caller, ct).ConfigureAwait(false);

            switch (toolName)
            {
                case VcsMcpToolNames.Status:
                {
                    var commitMsg = McpToolResultFormatter.OptionalString(args, ["commitMessageIfGit", "commit_message"]);
                    var snapshot = await _vcsService.SnapshotWorkingCopyAsync(workspaceCwd, commitMsg, ct)
                        .ConfigureAwait(false);
                    return McpToolResultFormatter.Success(snapshot);
                }

                case VcsMcpToolNames.Diff:
                {
                    var fromRev = McpToolResultFormatter.OptionalString(args, ["fromRev", "from_rev", "from"]);
                    var toRev = McpToolResultFormatter.OptionalString(args, ["toRev", "to_rev", "to"]);
                    var diff = await _vcsService.DiffAsync(workspaceCwd, fromRev, toRev, ct).ConfigureAwait(false);
                    return McpToolResultFormatter.Success(diff);
                }

                case VcsMcpToolNames.RestoreOp:
                {
                    var opId = McpToolResultFormatter.RequireString(args, ["opId", "op_id"]);
                    await _vcsService.RestoreOperationAsync(workspaceCwd, opId, ct).ConfigureAwait(false);
                    var snapshot = await _vcsService.SnapshotWorkingCopyAsync(workspaceCwd, null, ct).ConfigureAwait(false);
                    return McpToolResultFormatter.Success(new { ok = true, opId, snapshot });
                }

                case VcsMcpToolNames.RebaseUpstream:
                {
                    var upstream = McpToolResultFormatter.OptionalString(args, ["upstreamBranch", "upstream_branch", "upstream"])
                                   ?? "main";
                    var snapshot = await _vcsService.RebaseOntoUpstreamAsync(workspaceCwd, upstream, ct)
                        .ConfigureAwait(false);
                    return McpToolResultFormatter.Success(snapshot);
                }

                case VcsMcpToolNames.PublishPr:
                {
                    var headBranch = McpToolResultFormatter.RequireString(args, ["headBranch", "head_branch", "head"]);
                    var baseBranch = McpToolResultFormatter.OptionalString(args, ["baseBranch", "base_branch", "base"])
                                     ?? "main";
                    var title = McpToolResultFormatter.RequireString(args, ["title"]);
                    var body = McpToolResultFormatter.OptionalString(args, ["body"]) ?? title;

                    if (McpToolResultFormatter.TryGetPropertyIgnoreCase(args, ["push", "pushBookmark", "push_bookmark"], out var pushElem) &&
                        pushElem.ValueKind == JsonValueKind.True)
                    {
                        var targetRev = McpToolResultFormatter.OptionalString(args, ["targetRev", "target_rev"]);
                        await _vcsService.PushBookmarkAsync(workspaceCwd, headBranch, targetRev, ct).ConfigureAwait(false);
                    }

                    var pr = await _forgeService.PublishPrAsync(
                        new PrPublishRequest(workspaceCwd, headBranch, baseBranch, title, body),
                        ct).ConfigureAwait(false);
                    return McpToolResultFormatter.Success(pr);
                }

                case VcsMcpToolNames.PrChecks:
                {
                    var prNumber = McpToolResultFormatter.RequireInt(args, ["prNumber", "pr_number", "number"]);
                    var repo = McpToolResultFormatter.OptionalString(args, ["repo", "repoOrPath"]) ?? workspaceCwd;
                    var checks = await _forgeService.GetPrChecksAsync(repo, prNumber, ct).ConfigureAwait(false);
                    return McpToolResultFormatter.Success(checks);
                }

                case VcsMcpToolNames.CiEvidence:
                {
                    var prNumber = McpToolResultFormatter.RequireInt(args, ["prNumber", "pr_number", "number"]);
                    var headSha = McpToolResultFormatter.RequireString(args, ["headSha", "head_sha", "sha"]);
                    var repo = McpToolResultFormatter.OptionalString(args, ["repo", "repoOrPath"]) ?? workspaceCwd;
                    var evidence = await _forgeService.GetCiFailureEvidenceAsync(repo, prNumber, headSha, ct)
                        .ConfigureAwait(false);
                    return McpToolResultFormatter.Success(evidence);
                }

                case VcsMcpToolNames.PrMerge:
                {
                    var prNumber = McpToolResultFormatter.RequireInt(args, ["prNumber", "pr_number", "number"]);
                    var expectedHeadSha = McpToolResultFormatter.RequireString(
                        args,
                        ["expectedHeadSha", "expected_head_sha", "headSha", "head_sha"]);
                    var repo = McpToolResultFormatter.OptionalString(args, ["repo", "repoOrPath"]) ?? workspaceCwd;
                    await _forgeService.MergePrAsync(repo, prNumber, expectedHeadSha, ct).ConfigureAwait(false);
                    return McpToolResultFormatter.Success(new { ok = true, prNumber, expectedHeadSha });
                }

                case VcsMcpToolNames.IssueView:
                {
                    var issueNumber = McpToolResultFormatter.RequireInt(args, ["issueNumber", "issue_number", "number"]);
                    var repo = McpToolResultFormatter.OptionalString(args, ["repo", "repoOrPath"]) ?? workspaceCwd;
                    var issue = await _forgeService.ViewIssueAsync(repo, issueNumber, ct).ConfigureAwait(false);
                    return McpToolResultFormatter.Success(issue);
                }

                default:
                    throw KernelException.NotFound($"Unknown VCS MCP tool '{toolName}'.");
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return McpToolResultFormatter.FromException(ex);
        }
    }

    private async Task<string> ResolveCallerWorkspaceCwdAsync(McpSessionPrincipal caller, CancellationToken ct)
    {
        var actor = _kernel.Actors.GetOrActivate(caller.TrackId);
        var mounted = await actor.EnsureMountedAsync(ct).ConfigureAwait(false);
        var effectiveCwd = mounted.MergedCwd;

        if (!string.IsNullOrEmpty(caller.BoundAttemptId) && !string.IsNullOrEmpty(caller.BoundTaskId))
        {
            try
            {
                var attempts = await _kernel.ListTaskAttemptsAsync(caller.TrackId, caller.BoundTaskId, ct)
                    .ConfigureAwait(false);
                var boundAttempt = attempts.FirstOrDefault(a =>
                    string.Equals(a.AttemptId, caller.BoundAttemptId, StringComparison.Ordinal));
                if (boundAttempt?.WorkspaceLease is { ValueKind: JsonValueKind.Object } leaseElem &&
                    McpToolResultFormatter.OptionalString(leaseElem, ["effectiveCwd", "effective_cwd"]) is { } leaseCwd &&
                    !string.IsNullOrWhiteSpace(leaseCwd))
                {
                    effectiveCwd = leaseCwd;
                }
            }
            catch (KernelException)
            {
                // Fall back to Track MergedCwd if task was removed.
            }
        }

        return effectiveCwd;
    }

    private static IReadOnlyList<McpToolWireDescriptor> BuildDescriptors()
    {
        static JsonElement Schema(string json) => McpToolResultFormatter.ParseClone(json);

        return
        [
            new(VcsMcpToolNames.Status, "Inspect current workspace VCS snapshot (JJ ChangeId/CommitId/OpId/conflicts or Git HEAD/branch).",
                Schema("""{"type":"object","properties":{"commitMessageIfGit":{"type":"string"}}}""")),
            new(VcsMcpToolNames.Diff, "Compute workspace diff summary and changed files between revisions.",
                Schema("""{"type":"object","properties":{"fromRev":{"type":"string"},"toRev":{"type":"string"}}}""")),
            new(VcsMcpToolNames.RestoreOp, "Restore Jujutsu operation log state (`jj op restore <opId>`).",
                Schema("""{"type":"object","required":["opId"],"properties":{"opId":{"type":"string"}}}""")),
            new(VcsMcpToolNames.RebaseUpstream, "Fetch and rebase working copy onto upstream branch.",
                Schema("""{"type":"object","properties":{"upstreamBranch":{"type":"string"}}}""")),
            new(VcsMcpToolNames.PublishPr, "Publish a pull request via GitHub Forge.",
                Schema("""{"type":"object","required":["headBranch","title"],"properties":{"headBranch":{"type":"string"},"baseBranch":{"type":"string"},"title":{"type":"string"},"body":{"type":"string"},"push":{"type":"boolean"}}}""")),
            new(VcsMcpToolNames.PrChecks, "Inspect CI check runs and completion status for a pull request.",
                Schema("""{"type":"object","required":["prNumber"],"properties":{"prNumber":{"type":"integer"},"repo":{"type":"string"}}}""")),
            new(VcsMcpToolNames.CiEvidence, "Fetch CI failure summary and log tail for a pull request SHA.",
                Schema("""{"type":"object","required":["prNumber","headSha"],"properties":{"prNumber":{"type":"integer"},"headSha":{"type":"string"},"repo":{"type":"string"}}}""")),
            new(VcsMcpToolNames.PrMerge, "Merge a pull request matching the expected head SHA.",
                Schema("""{"type":"object","required":["prNumber","expectedHeadSha"],"properties":{"prNumber":{"type":"integer"},"expectedHeadSha":{"type":"string"},"repo":{"type":"string"}}}""")),
            new(VcsMcpToolNames.IssueView, "Fetch issue metadata, body, and labels from GitHub Forge.",
                Schema("""{"type":"object","required":["issueNumber"],"properties":{"issueNumber":{"type":"integer"},"repo":{"type":"string"}}}"""))
        ];
    }
}
