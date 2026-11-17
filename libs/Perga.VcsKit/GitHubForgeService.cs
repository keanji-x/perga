using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Perga.VcsKit;

public enum PrMergeStrategy
{
    Squash,
    Merge,
    Rebase,
}

public sealed record GitHubForgeOptions
{
    public string GhExecutablePath { get; init; } = "gh";
    public PrMergeStrategy MergeStrategy { get; init; } = PrMergeStrategy.Squash;
    public int MaxCiLogTailChars { get; init; } = 16_384;
}

public sealed partial class GitHubForgeService : IGitHubForgeService
{
    private readonly IProcessRunner _processRunner;
    private readonly GitHubForgeOptions _options;

    public GitHubForgeService(IProcessRunner? processRunner = null, GitHubForgeOptions? options = null)
    {
        _processRunner = processRunner ?? new ProcessRunner();
        _options = options ?? new GitHubForgeOptions();

        if (_options.MaxCiLogTailChars <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MaxCiLogTailChars must be positive.");
        }
    }

    public async Task<PrInfo> PublishPrAsync(PrPublishRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);
        ArgumentException.ThrowIfNullOrWhiteSpace(req.WorkspacePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(req.HeadBranch);
        ArgumentException.ThrowIfNullOrWhiteSpace(req.BaseBranch);
        ArgumentException.ThrowIfNullOrWhiteSpace(req.Title);
        ArgumentNullException.ThrowIfNullOrEmpty(req.Body);

        var workingDir = Path.GetFullPath(req.WorkspacePath);
        var createArgs = new List<string>
        {
            "pr",
            "create",
            "--head",
            req.HeadBranch,
            "--base",
            req.BaseBranch,
            "--title",
            req.Title,
            "--body",
            req.Body,
        };

        var createRes = await RunGhCheckedAsync(workingDir, createArgs, ct).ConfigureAwait(false);
        var createOutput = createRes.StandardOutput.Trim();

        if (TryParsePrInfoJson(createOutput, out var directPrInfo))
        {
            return directPrInfo;
        }

        var selector = ExtractFirstUrlOrDefault(createOutput) ?? req.HeadBranch;
        var viewRes = await RunGhCheckedAsync(
                workingDir,
                ["pr", "view", selector, "--json", "number,url,state,headRefOid"],
                ct)
            .ConfigureAwait(false);

        if (TryParsePrInfoJson(viewRes.StandardOutput, out var viewedPrInfo))
        {
            return viewedPrInfo;
        }

        throw new InvalidOperationException(
            $"Failed to parse pull request JSON returned by 'gh pr view': {viewRes.StandardOutput}");
    }

    public async Task<PrChecksStatus> GetPrChecksAsync(string repoOrPath, int prNumber, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoOrPath);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(prNumber);

        var (workingDir, repoFlags) = ResolveRepoTarget(repoOrPath);
        var prStr = prNumber.ToString(CultureInfo.InvariantCulture);

        var viewArgs = BuildGhArgs(repoFlags, "pr", "view", prStr, "--json", "number,headRefOid,statusCheckRollup");
        var viewRes = await RunGhCheckedAsync(workingDir, viewArgs, ct).ConfigureAwait(false);

        using var viewDoc = JsonDocument.Parse(viewRes.StandardOutput);
        var root = viewDoc.RootElement;
        var resolvedNumber = GetIntOrDefault(root, "number", prNumber);
        var headSha = GetStringOrDefault(root, "headRefOid", string.Empty);

        var checksArgs = BuildGhArgs(repoFlags, "pr", "checks", prStr, "--json", "name,state,bucket,link");
        var checksRes = await RunGhAsync(workingDir, checksArgs, ct).ConfigureAwait(false);

        IReadOnlyList<CheckRunSummary> checks;

        // `gh pr checks` returns exit code 8 when checks are pending and 1 when checks fail.
        if (checksRes.ExitCode is 0 or 1 or 8 && TryParsePrChecksJson(checksRes.StandardOutput, out var parsedChecks))
        {
            checks = parsedChecks;
        }
        else if (root.TryGetProperty("statusCheckRollup", out var rollupElement) &&
                 rollupElement.ValueKind == JsonValueKind.Array)
        {
            checks = ParseStatusCheckRollup(rollupElement);
        }
        else if (checksRes.ExitCode is not (0 or 1 or 8))
        {
            throw new VcsCommandException(
                $"{_options.GhExecutablePath} {string.Join(' ', checksArgs)}",
                checksRes.ExitCode,
                checksRes.StandardOutput,
                checksRes.StandardError);
        }
        else
        {
            checks = [];
        }

        var allCompleted = checks.All(IsCheckCompleted);
        var allGreen = allCompleted && checks.All(IsCheckPassing);

        return new PrChecksStatus(
            PrNumber: resolvedNumber,
            HeadSha: headSha,
            AllCompleted: allCompleted,
            AllGreen: allGreen,
            Checks: checks);
    }

    public async Task<CiFailureEvidence> GetCiFailureEvidenceAsync(
        string repoOrPath,
        int prNumber,
        string headSha,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoOrPath);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(prNumber);
        ArgumentException.ThrowIfNullOrWhiteSpace(headSha);

        var (workingDir, repoFlags) = ResolveRepoTarget(repoOrPath);
        var checksStatus = await GetPrChecksAsync(repoOrPath, prNumber, ct).ConfigureAwait(false);
        var effectiveSha = !string.IsNullOrWhiteSpace(headSha) ? headSha : checksStatus.HeadSha;

        var failedChecks = checksStatus.Checks
            .Where(c => IsCheckCompleted(c) && !IsCheckPassing(c))
            .ToArray();

        var summaryBuilder = new StringBuilder();
        var runIds = new List<string>();
        var seenRunIds = new HashSet<string>(StringComparer.Ordinal);

        if (failedChecks.Length > 0)
        {
            summaryBuilder.AppendLine(
                CultureInfo.InvariantCulture,
                $"PR #{prNumber} ({effectiveSha}) has {failedChecks.Length} failing check(s):");

            foreach (var check in failedChecks)
            {
                var conclusionText = check.Conclusion ?? check.Status;
                if (!string.IsNullOrWhiteSpace(check.DetailsUrl))
                {
                    summaryBuilder.AppendLine(
                        CultureInfo.InvariantCulture,
                        $"- {check.Name}: {conclusionText} ({check.DetailsUrl})");

                    var extractedRunId = TryExtractActionsRunId(check.DetailsUrl);
                    if (extractedRunId is not null && seenRunIds.Add(extractedRunId))
                    {
                        runIds.Add(extractedRunId);
                    }
                }
                else
                {
                    summaryBuilder.AppendLine(
                        CultureInfo.InvariantCulture,
                        $"- {check.Name}: {conclusionText}");
                }
            }
        }

        if (runIds.Count == 0)
        {
            var runListArgs = BuildGhArgs(
                repoFlags,
                "run",
                "list",
                "--commit",
                effectiveSha,
                "--json",
                "databaseId,name,status,conclusion,url");

            var runListRes = await RunGhAsync(workingDir, runListArgs, ct).ConfigureAwait(false);
            if (runListRes.ExitCode == 0 && !string.IsNullOrWhiteSpace(runListRes.StandardOutput))
            {
                CollectFailedRunsFromListJson(runListRes.StandardOutput, summaryBuilder, runIds, seenRunIds);
            }
        }

        var logsBuilder = new StringBuilder();
        foreach (var runId in runIds)
        {
            var logArgs = BuildGhArgs(repoFlags, "run", "view", runId, "--log-failed");
            var logRes = await RunGhAsync(workingDir, logArgs, ct).ConfigureAwait(false);
            var logContent = !string.IsNullOrWhiteSpace(logRes.StandardOutput)
                ? logRes.StandardOutput
                : logRes.StandardError;

            if (!string.IsNullOrWhiteSpace(logContent))
            {
                if (logsBuilder.Length > 0)
                {
                    logsBuilder.AppendLine();
                }

                logsBuilder.Append(logContent.TrimEnd());
            }
        }

        var summaryText = summaryBuilder.Length > 0
            ? summaryBuilder.ToString().TrimEnd()
            : $"No failed CI checks found for PR #{prNumber} ({effectiveSha}).";

        var fullLogs = logsBuilder.ToString();
        var logTail = fullLogs.Length <= _options.MaxCiLogTailChars
            ? fullLogs
            : fullLogs[^_options.MaxCiLogTailChars..];

        return new CiFailureEvidence(
            PrNumber: prNumber,
            HeadSha: effectiveSha,
            SummaryText: summaryText,
            LogTail: logTail);
    }

    public async Task MergePrAsync(string repoOrPath, int prNumber, string expectedHeadSha, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoOrPath);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(prNumber);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedHeadSha);

        var (workingDir, repoFlags) = ResolveRepoTarget(repoOrPath);
        var prStr = prNumber.ToString(CultureInfo.InvariantCulture);
        var strategyFlag = _options.MergeStrategy switch
        {
            PrMergeStrategy.Merge => "--merge",
            PrMergeStrategy.Rebase => "--rebase",
            _ => "--squash",
        };

        var args = BuildGhArgs(
            repoFlags,
            "pr",
            "merge",
            prStr,
            strategyFlag,
            "--match-head-commit",
            expectedHeadSha);

        await RunGhCheckedAsync(workingDir, args, ct).ConfigureAwait(false);
    }

    public async Task<IssueInfo> ViewIssueAsync(string repoOrPath, int issueNumber, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoOrPath);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(issueNumber);

        var (workingDir, repoFlags) = ResolveRepoTarget(repoOrPath);
        var issueStr = issueNumber.ToString(CultureInfo.InvariantCulture);

        var args = BuildGhArgs(
            repoFlags,
            "issue",
            "view",
            issueStr,
            "--json",
            "number,url,state,title,body,labels");

        var res = await RunGhCheckedAsync(workingDir, args, ct).ConfigureAwait(false);

        using var doc = JsonDocument.Parse(res.StandardOutput);
        var root = doc.RootElement;

        var number = GetIntOrDefault(root, "number", issueNumber);
        var url = GetStringOrDefault(root, "url", string.Empty);
        var state = GetStringOrDefault(root, "state", string.Empty);
        var title = GetStringOrDefault(root, "title", string.Empty);
        var body = GetStringOrDefault(root, "body", string.Empty);
        var labels = ParseIssueLabels(root);

        return new IssueInfo(number, url, state, title, body, labels);
    }

    private static (string WorkingDirectory, IReadOnlyList<string> RepoFlags) ResolveRepoTarget(string repoOrPath)
    {
        if (IsRepositorySlug(repoOrPath))
        {
            return (Environment.CurrentDirectory, ["-R", repoOrPath]);
        }

        return (Path.GetFullPath(repoOrPath), []);
    }

    private static bool IsRepositorySlug(string repoOrPath)
    {
        if (Path.IsPathRooted(repoOrPath) ||
            repoOrPath.StartsWith("./", StringComparison.Ordinal) ||
            repoOrPath.StartsWith("../", StringComparison.Ordinal) ||
            repoOrPath.StartsWith(".\\", StringComparison.Ordinal) ||
            repoOrPath.StartsWith("..\\", StringComparison.Ordinal) ||
            Directory.Exists(repoOrPath))
        {
            return false;
        }

        var parts = repoOrPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length is 2 or 3 && !repoOrPath.Contains('\\', StringComparison.Ordinal);
    }

    private static List<string> BuildGhArgs(IReadOnlyList<string> repoFlags, params ReadOnlySpan<string> commandArgs)
    {
        var list = new List<string>(repoFlags.Count + commandArgs.Length);
        list.AddRange(repoFlags);
        foreach (var arg in commandArgs)
        {
            list.Add(arg);
        }

        return list;
    }

    private static bool TryParsePrInfoJson(string rawOutput, out PrInfo prInfo)
    {
        prInfo = default!;
        if (string.IsNullOrWhiteSpace(rawOutput) || !rawOutput.TrimStart().StartsWith('{'))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(rawOutput);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("number", out var numProp))
            {
                return false;
            }

            var number = numProp.GetInt32();
            var url = GetStringOrDefault(root, "url", string.Empty);
            var state = GetStringOrDefault(root, "state", "OPEN");
            var headSha = GetStringOrDefault(root, "headRefOid", GetStringOrDefault(root, "headSha", string.Empty));
            prInfo = new PrInfo(number, url, state, headSha);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryParsePrChecksJson(string rawOutput, out IReadOnlyList<CheckRunSummary> checks)
    {
        checks = [];
        if (string.IsNullOrWhiteSpace(rawOutput) || !rawOutput.TrimStart().StartsWith('['))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(rawOutput);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            var list = new List<CheckRunSummary>();
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var name = GetStringOrDefault(item, "name", "unnamed-check");
                var bucket = GetStringOrDefault(item, "bucket", string.Empty).ToLowerInvariant();
                var rawState = GetStringOrDefault(item, "state", string.Empty).ToUpperInvariant();
                var link = GetNullableString(item, "link") ?? GetNullableString(item, "detailsUrl");

                var (status, conclusion) = NormalizeCheckBucketAndState(bucket, rawState);
                list.Add(new CheckRunSummary(name, status, conclusion, link));
            }

            checks = list;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static IReadOnlyList<CheckRunSummary> ParseStatusCheckRollup(JsonElement rollupArray)
    {
        var list = new List<CheckRunSummary>();
        foreach (var item in rollupArray.EnumerateArray())
        {
            var name = GetNullableString(item, "name")
                       ?? GetNullableString(item, "context")
                       ?? "unnamed-check";

            var detailsUrl = GetNullableString(item, "detailsUrl")
                             ?? GetNullableString(item, "targetUrl");

            var rawStatus = GetNullableString(item, "status")?.ToUpperInvariant();
            var rawConclusion = GetNullableString(item, "conclusion")?.ToUpperInvariant();
            var rawState = GetNullableString(item, "state")?.ToUpperInvariant();

            if (!string.IsNullOrEmpty(rawStatus))
            {
                var conclusion = string.IsNullOrEmpty(rawConclusion) ? null : rawConclusion;
                list.Add(new CheckRunSummary(name, rawStatus, conclusion, detailsUrl));
            }
            else if (!string.IsNullOrEmpty(rawState))
            {
                var (status, conclusion) = NormalizeCheckBucketAndState(string.Empty, rawState);
                list.Add(new CheckRunSummary(name, status, conclusion, detailsUrl));
            }
            else
            {
                list.Add(new CheckRunSummary(name, "UNKNOWN", null, detailsUrl));
            }
        }

        return list;
    }

    private static (string Status, string? Conclusion) NormalizeCheckBucketAndState(string bucket, string rawState)
    {
        return bucket switch
        {
            "pending" => ("IN_PROGRESS", null),
            "pass" => ("COMPLETED", string.IsNullOrEmpty(rawState) ? "SUCCESS" : NormalizePassingConclusion(rawState)),
            "skipping" => ("COMPLETED", "SKIPPED"),
            "cancel" => ("COMPLETED", "CANCELLED"),
            "fail" => ("COMPLETED", string.IsNullOrEmpty(rawState) ? "FAILURE" : NormalizeFailingConclusion(rawState)),
            _ => rawState switch
            {
                "PENDING" or "QUEUED" or "IN_PROGRESS" or "WAITING" or "REQUESTED" => ("IN_PROGRESS", null),
                "SUCCESS" or "PASS" => ("COMPLETED", "SUCCESS"),
                "NEUTRAL" => ("COMPLETED", "NEUTRAL"),
                "SKIPPED" => ("COMPLETED", "SKIPPED"),
                "CANCELLED" or "CANCELED" => ("COMPLETED", "CANCELLED"),
                "TIMED_OUT" => ("COMPLETED", "TIMED_OUT"),
                "ACTION_REQUIRED" => ("COMPLETED", "ACTION_REQUIRED"),
                "" => ("UNKNOWN", null),
                _ => ("COMPLETED", "FAILURE"),
            },
        };
    }

    private static string NormalizePassingConclusion(string rawState) =>
        rawState is "NEUTRAL" or "SKIPPED" ? rawState : "SUCCESS";

    private static string NormalizeFailingConclusion(string rawState) =>
        rawState is "TIMED_OUT" or "ACTION_REQUIRED" or "STARTUP_FAILURE" or "STALE" or "ERROR"
            ? rawState
            : "FAILURE";

    private static bool IsCheckCompleted(CheckRunSummary check) =>
        string.Equals(check.Status, "COMPLETED", StringComparison.OrdinalIgnoreCase);

    private static bool IsCheckPassing(CheckRunSummary check) =>
        IsCheckCompleted(check) &&
        check.Conclusion is not null &&
        (string.Equals(check.Conclusion, "SUCCESS", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(check.Conclusion, "NEUTRAL", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(check.Conclusion, "SKIPPED", StringComparison.OrdinalIgnoreCase));

    private static void CollectFailedRunsFromListJson(
        string rawJson,
        StringBuilder summaryBuilder,
        List<string> runIds,
        HashSet<string> seenRunIds)
    {
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var conclusion = GetStringOrDefault(item, "conclusion", string.Empty).ToUpperInvariant();
                if (conclusion is "SUCCESS" or "NEUTRAL" or "SKIPPED" or "")
                {
                    continue;
                }

                var runId = TryGetIdString(item, "databaseId");
                var name = GetStringOrDefault(item, "name", "workflow");
                var url = GetNullableString(item, "url");

                if (summaryBuilder.Length == 0)
                {
                    summaryBuilder.AppendLine("Failing workflow run(s):");
                }

                summaryBuilder.AppendLine(
                    url is not null
                        ? $"- {name}: {conclusion} ({url})"
                        : $"- {name}: {conclusion}");

                if (runId is not null && seenRunIds.Add(runId))
                {
                    runIds.Add(runId);
                }
            }
        }
        catch (JsonException)
        {
            // Ignore malformed fallback output.
        }
    }

    private static IReadOnlyList<string> ParseIssueLabels(JsonElement root)
    {
        if (!root.TryGetProperty("labels", out var labelsProp) || labelsProp.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var labels = new List<string>();
        foreach (var item in labelsProp.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var str = item.GetString();
                if (!string.IsNullOrWhiteSpace(str))
                {
                    labels.Add(str);
                }
            }
            else if (item.ValueKind == JsonValueKind.Object)
            {
                var name = GetNullableString(item, "name");
                if (!string.IsNullOrWhiteSpace(name))
                {
                    labels.Add(name);
                }
            }
        }

        return labels;
    }

    private static string? ExtractFirstUrlOrDefault(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var match = UrlRegex().Match(text);
        return match.Success ? match.Value.Trim() : null;
    }

    private static string? TryExtractActionsRunId(string detailsUrl)
    {
        var match = ActionsRunIdRegex().Match(detailsUrl);
        return match.Success ? match.Groups["runId"].Value : null;
    }

    private static int GetIntOrDefault(JsonElement obj, string propName, int fallback) =>
        obj.TryGetProperty(propName, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var v)
            ? v
            : fallback;

    private static string GetStringOrDefault(JsonElement obj, string propName, string fallback) =>
        GetNullableString(obj, propName) ?? fallback;

    private static string? GetNullableString(JsonElement obj, string propName) =>
        obj.TryGetProperty(propName, out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString()
            : null;

    private static string? TryGetIdString(JsonElement obj, string propName)
    {
        if (!obj.TryGetProperty(propName, out var p))
        {
            return null;
        }

        return p.ValueKind switch
        {
            JsonValueKind.Number when p.TryGetInt64(out var n) => n.ToString(CultureInfo.InvariantCulture),
            JsonValueKind.String => p.GetString(),
            _ => null,
        };
    }

    private Task<ProcessRunResult> RunGhAsync(
        string workingDirectory,
        IReadOnlyList<string> args,
        CancellationToken ct) =>
        _processRunner.RunAsync(
            new ProcessRunRequest(_options.GhExecutablePath, args, workingDirectory),
            ct);

    private async Task<ProcessRunResult> RunGhCheckedAsync(
        string workingDirectory,
        IReadOnlyList<string> args,
        CancellationToken ct)
    {
        var result = await RunGhAsync(workingDirectory, args, ct).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new VcsCommandException(
                $"{_options.GhExecutablePath} {string.Join(' ', args)}",
                result.ExitCode,
                result.StandardOutput,
                result.StandardError);
        }

        return result;
    }

    [GeneratedRegex(@"https?://\S+", RegexOptions.CultureInvariant)]
    private static partial Regex UrlRegex();

    [GeneratedRegex(@"/actions/runs/(?<runId>\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex ActionsRunIdRegex();
}
