using System.Text.Json;
using Perga.Kernel;
using Perga.Kernel.Mcp;
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

    public VcsMcpBackend(IPergaKernel kernel)
    {
        _kernel = kernel ?? throw new ArgumentNullException(nameof(kernel));
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
        try
        {
            if (!VcsMcpToolNames.All.Contains(toolName))
                throw KernelException.NotFound($"Unknown VCS MCP tool '{toolName}'.");

            var generation = await _kernel.GuardMcpToolCallAsync(caller, ServerName, toolName, args, ct)
                .ConfigureAwait(false);

            var (workspaceCwd, vcsService, forgeService) = await ResolveCallerWorkspaceAsync(caller, ct).ConfigureAwait(false);
            await AuthorizeMutationAsync(ct).ConfigureAwait(false);

            Task AuthorizeMutationAsync(CancellationToken token) =>
                _kernel.EnsureMcpAuthorizationAsync(caller, generation, ServerName, toolName, token);

            if (toolName is VcsMcpToolNames.PublishPr or VcsMcpToolNames.PrChecks or VcsMcpToolNames.CiEvidence
                or VcsMcpToolNames.PrMerge or VcsMcpToolNames.IssueView && forgeService is null)
            {
                throw KernelException.PreconditionFailed("Forge is not available on this execution target.");
            }

            switch (toolName)
            {
                case VcsMcpToolNames.Status:
                {
                    var commitMsg = McpToolResultFormatter.OptionalString(args, ["commitMessageIfGit", "commit_message"]);
                    var snapshot = await vcsService.SnapshotWorkingCopyAsync(workspaceCwd, commitMsg, ct, AuthorizeMutationAsync)
                        .ConfigureAwait(false);
                    return McpToolResultFormatter.Success(snapshot);
                }

                case VcsMcpToolNames.Diff:
                {
                    var fromRev = McpToolResultFormatter.OptionalString(args, ["fromRev", "from_rev", "from"]);
                    var toRev = McpToolResultFormatter.OptionalString(args, ["toRev", "to_rev", "to"]);
                    var diff = await vcsService.DiffAsync(workspaceCwd, fromRev, toRev, ct, AuthorizeMutationAsync).ConfigureAwait(false);
                    return McpToolResultFormatter.Success(diff);
                }

                case VcsMcpToolNames.RestoreOp:
                {
                    var opId = McpToolResultFormatter.RequireString(args, ["opId", "op_id"]);
                    await vcsService.RestoreOperationAsync(workspaceCwd, opId, ct, AuthorizeMutationAsync).ConfigureAwait(false);
                    var snapshot = await vcsService.SnapshotWorkingCopyAsync(workspaceCwd, null, ct, AuthorizeMutationAsync).ConfigureAwait(false);
                    return McpToolResultFormatter.Success(new { ok = true, opId, snapshot });
                }

                case VcsMcpToolNames.RebaseUpstream:
                {
                    var upstream = McpToolResultFormatter.OptionalString(args, ["upstreamBranch", "upstream_branch", "upstream"])
                                   ?? "main";
                    var snapshot = await vcsService.RebaseOntoUpstreamAsync(workspaceCwd, upstream, ct, AuthorizeMutationAsync)
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
                        await vcsService.PushBookmarkAsync(workspaceCwd, headBranch, targetRev, ct, AuthorizeMutationAsync).ConfigureAwait(false);
                    }

                    await AuthorizeMutationAsync(ct).ConfigureAwait(false);
                    var pr = await forgeService!.PublishPrAsync(
                        new PrPublishRequest(workspaceCwd, headBranch, baseBranch, title, body),
                        ct).ConfigureAwait(false);
                    return McpToolResultFormatter.Success(pr);
                }

                case VcsMcpToolNames.PrChecks:
                {
                    var prNumber = McpToolResultFormatter.RequireInt(args, ["prNumber", "pr_number", "number"]);
                    var repo = McpToolResultFormatter.OptionalString(args, ["repo", "repoOrPath"]) ?? workspaceCwd;
                    var checks = await forgeService!.GetPrChecksAsync(repo, prNumber, ct).ConfigureAwait(false);
                    return McpToolResultFormatter.Success(checks);
                }

                case VcsMcpToolNames.CiEvidence:
                {
                    var prNumber = McpToolResultFormatter.RequireInt(args, ["prNumber", "pr_number", "number"]);
                    var headSha = McpToolResultFormatter.RequireString(args, ["headSha", "head_sha", "sha"]);
                    var repo = McpToolResultFormatter.OptionalString(args, ["repo", "repoOrPath"]) ?? workspaceCwd;
                    var evidence = await forgeService!.GetCiFailureEvidenceAsync(repo, prNumber, headSha, ct)
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
                    await AuthorizeMutationAsync(ct).ConfigureAwait(false);
                    await forgeService!.MergePrAsync(repo, prNumber, expectedHeadSha, ct).ConfigureAwait(false);
                    return McpToolResultFormatter.Success(new { ok = true, prNumber, expectedHeadSha });
                }

                case VcsMcpToolNames.IssueView:
                {
                    var issueNumber = McpToolResultFormatter.RequireInt(args, ["issueNumber", "issue_number", "number"]);
                    var repo = McpToolResultFormatter.OptionalString(args, ["repo", "repoOrPath"]) ?? workspaceCwd;
                    var issue = await forgeService!.ViewIssueAsync(repo, issueNumber, ct).ConfigureAwait(false);
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

    private async Task<(string WorkspaceCwd, IVcsService VcsService, IGitHubForgeService? ForgeService)> ResolveCallerWorkspaceAsync(
        McpSessionPrincipal caller,
        CancellationToken ct)
    {
        var actor = _kernel.Actors.GetOrActivate(caller.TrackId);
        var mounted = await actor.EnsureMountedAsync(ct).ConfigureAwait(false);
        var targetId = await actor.GetExecutionTargetIdAsync(ct).ConfigureAwait(false);
        var env = _kernel.ExecutionEnvironments.GetRequired(targetId);
        return (mounted.MergedCwd, env.Vcs, env.Forge);
    }

    private static IReadOnlyList<McpToolWireDescriptor> BuildDescriptors()
    {
        static JsonElement Schema(string json) => McpToolResultFormatter.ParseClone(json);

        var readOnlyOpenWorld = new McpToolAnnotations(ReadOnlyHint: true, DestructiveHint: false, IdempotentHint: true, OpenWorldHint: true);
        var localWrite = new McpToolAnnotations(ReadOnlyHint: false, DestructiveHint: false, IdempotentHint: false, OpenWorldHint: false);
        var localDestructive = new McpToolAnnotations(ReadOnlyHint: false, DestructiveHint: true, IdempotentHint: false, OpenWorldHint: false);
        var openWorldDestructive = new McpToolAnnotations(ReadOnlyHint: false, DestructiveHint: true, IdempotentHint: false, OpenWorldHint: true);

        return
        [
            new(VcsMcpToolNames.Status, "Inspect current workspace VCS snapshot (JJ ChangeId/CommitId/OpId/conflicts or Git HEAD/branch).",
                Schema("""{"type":"object","properties":{"commitMessageIfGit":{"type":"string"}}}"""), localWrite),
            new(VcsMcpToolNames.Diff, "Compute workspace diff summary and changed files between revisions.",
                Schema("""{"type":"object","properties":{"fromRev":{"type":"string"},"toRev":{"type":"string"}}}"""), localWrite),
            new(VcsMcpToolNames.RestoreOp, "Restore Jujutsu operation log state (`jj op restore <opId>`).",
                Schema("""{"type":"object","required":["opId"],"properties":{"opId":{"type":"string"}}}"""), localDestructive),
            new(VcsMcpToolNames.RebaseUpstream, "Fetch and rebase working copy onto upstream branch.",
                Schema("""{"type":"object","properties":{"upstreamBranch":{"type":"string"}}}"""), openWorldDestructive),
            new(VcsMcpToolNames.PublishPr, "Publish a pull request via GitHub Forge.",
                Schema("""{"type":"object","required":["headBranch","title"],"properties":{"headBranch":{"type":"string"},"baseBranch":{"type":"string"},"title":{"type":"string"},"body":{"type":"string"},"push":{"type":"boolean"}}}"""), openWorldDestructive),
            new(VcsMcpToolNames.PrChecks, "Inspect CI check runs and completion status for a pull request.",
                Schema("""{"type":"object","required":["prNumber"],"properties":{"prNumber":{"type":"integer"},"repo":{"type":"string"}}}"""), readOnlyOpenWorld),
            new(VcsMcpToolNames.CiEvidence, "Fetch CI failure summary and log tail for a pull request SHA.",
                Schema("""{"type":"object","required":["prNumber","headSha"],"properties":{"prNumber":{"type":"integer"},"headSha":{"type":"string"},"repo":{"type":"string"}}}"""), readOnlyOpenWorld),
            new(VcsMcpToolNames.PrMerge, "Merge a pull request matching the expected head SHA.",
                Schema("""{"type":"object","required":["prNumber","expectedHeadSha"],"properties":{"prNumber":{"type":"integer"},"expectedHeadSha":{"type":"string"},"repo":{"type":"string"}}}"""), openWorldDestructive),
            new(VcsMcpToolNames.IssueView, "Fetch issue metadata, body, and labels from GitHub Forge.",
                Schema("""{"type":"object","required":["issueNumber"],"properties":{"issueNumber":{"type":"integer"},"repo":{"type":"string"}}}"""), readOnlyOpenWorld)
        ];
    }
}
