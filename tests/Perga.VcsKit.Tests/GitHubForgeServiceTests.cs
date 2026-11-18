namespace Perga.VcsKit.Tests;

public sealed class GitHubForgeServiceTests
{
    [Fact]
    public async Task PublishPrAsync_CreatesAndViewsPullRequest()
    {
        var runner = new RecordingProcessRunner(req =>
        {
            if (req.Arguments is ["pr", "create", ..])
            {
                return new ProcessRunResult(0, "https://github.com/acme/perga/pull/42\n", string.Empty);
            }

            if (req.Arguments is ["pr", "view", "https://github.com/acme/perga/pull/42", "--json", "number,url,state,headRefOid"])
            {
                return new ProcessRunResult(
                    0,
                    """{"number":42,"url":"https://github.com/acme/perga/pull/42","state":"OPEN","headRefOid":"deadbeef1234"}""",
                    string.Empty);
            }

            throw new InvalidOperationException($"Unexpected gh invocation: {string.Join(' ', req.Arguments)}");
        });

        var forge = new GitHubForgeService(runner);
        var pr = await forge.PublishPrAsync(
            new PrPublishRequest(
                WorkspacePath: "/tmp",
                HeadBranch: "feat/vcskit",
                BaseBranch: "main",
                Title: "feat: implement VcsKit",
                Body: "Implements JJ + Git + GH integration."),
            CancellationToken.None);

        Assert.Equal(42, pr.Number);
        Assert.Equal("https://github.com/acme/perga/pull/42", pr.Url);
        Assert.Equal("OPEN", pr.State);
        Assert.Equal("deadbeef1234", pr.HeadSha);
        Assert.Equal(2, runner.Requests.Count);
        Assert.Equal(
            ["pr", "create", "--head", "feat/vcskit", "--base", "main", "--title", "feat: implement VcsKit", "--body", "Implements JJ + Git + GH integration."],
            runner.Requests[0].Arguments);
    }

    [Fact]
    public async Task GetPrChecksAsync_HandlesPendingPassAndFailureExitCodes()
    {
        var mode = "pending";
        var runner = new RecordingProcessRunner(req =>
        {
            if (req.Arguments is ["-R", "acme/perga", "pr", "view", "17", "--json", "number,headRefOid,statusCheckRollup"])
            {
                return new ProcessRunResult(
                    0,
                    """{"number":17,"headRefOid":"sha_17","statusCheckRollup":[]}""",
                    string.Empty);
            }

            if (req.Arguments is ["-R", "acme/perga", "pr", "checks", "17", "--json", "name,state,bucket,link"])
            {
                return mode switch
                {
                    "pending" => new ProcessRunResult(
                        8,
                        """[{"name":"build","state":"IN_PROGRESS","bucket":"pending","link":"https://github.com/acme/perga/actions/runs/100"}]""",
                        string.Empty),
                    "pass" => new ProcessRunResult(
                        0,
                        """
                        [
                          {"name":"build","state":"SUCCESS","bucket":"pass","link":"https://github.com/acme/perga/actions/runs/100"},
                          {"name":"optional-lint","state":"SKIPPED","bucket":"skipping","link":null}
                        ]
                        """,
                        string.Empty),
                    _ => new ProcessRunResult(
                        1,
                        """[{"name":"unit-tests","state":"FAILURE","bucket":"fail","link":"https://github.com/acme/perga/actions/runs/101/job/55"}]""",
                        string.Empty),
                };
            }

            throw new InvalidOperationException($"Unexpected args: {string.Join(' ', req.Arguments)}");
        });

        var forge = new GitHubForgeService(runner);

        var pending = await forge.GetPrChecksAsync("acme/perga", 17, CancellationToken.None);
        Assert.False(pending.AllCompleted);
        Assert.False(pending.AllGreen);
        Assert.Single(pending.Checks);
        Assert.Equal("IN_PROGRESS", pending.Checks[0].Status);

        mode = "pass";
        var green = await forge.GetPrChecksAsync("acme/perga", 17, CancellationToken.None);
        Assert.True(green.AllCompleted);
        Assert.True(green.AllGreen);
        Assert.Equal(2, green.Checks.Count);

        mode = "fail";
        var failed = await forge.GetPrChecksAsync("acme/perga", 17, CancellationToken.None);
        Assert.True(failed.AllCompleted);
        Assert.False(failed.AllGreen);
        Assert.Equal("FAILURE", failed.Checks[0].Conclusion);
    }

    [Fact]
    public async Task GetCiFailureEvidenceAsync_ExtractsRunIdFetchesFailedLogsAndTruncatesTail()
    {
        var runner = new RecordingProcessRunner(req =>
        {
            if (req.Arguments is ["pr", "view", "99", "--json", "number,headRefOid,statusCheckRollup"])
            {
                return new ProcessRunResult(0, """{"number":99,"headRefOid":"abc999","statusCheckRollup":[]}""", string.Empty);
            }

            if (req.Arguments is ["pr", "checks", "99", "--json", "name,state,bucket,link"])
            {
                return new ProcessRunResult(
                    1,
                    """[{"name":"linux-test","state":"FAILURE","bucket":"fail","link":"https://github.com/acme/perga/actions/runs/778899/job/1234"}]""",
                    string.Empty);
            }

            if (req.Arguments is ["run", "view", "778899", "--log-failed"])
            {
                return new ProcessRunResult(
                    0,
                    "0123456789ABCDEF_FAIL_TAIL_MARKER",
                    string.Empty);
            }

            throw new InvalidOperationException($"Unexpected args: {string.Join(' ', req.Arguments)}");
        });

        var forge = new GitHubForgeService(runner, new GitHubForgeOptions { MaxCiLogTailChars = 16 });
        var evidence = await forge.GetCiFailureEvidenceAsync("/tmp", 99, "abc999", CancellationToken.None);

        Assert.Equal(99, evidence.PrNumber);
        Assert.Equal("abc999", evidence.HeadSha);
        Assert.Contains("linux-test", evidence.SummaryText);
        Assert.Contains("778899", evidence.SummaryText);
        Assert.Equal("FAIL_TAIL_MARKER", evidence.LogTail);
    }

    [Fact]
    public async Task MergePrAsync_PassesMatchHeadCommitAndThrowsOnMismatch()
    {
        var shouldFail = false;
        var runner = new RecordingProcessRunner(req =>
        {
            if (shouldFail)
            {
                return new ProcessRunResult(1, string.Empty, "Pull request head commit does not match expected SHA");
            }

            return new ProcessRunResult(0, string.Empty, string.Empty);
        });

        var forge = new GitHubForgeService(runner);
        await forge.MergePrAsync("acme/perga", 55, "expected_sha_123", CancellationToken.None);

        Assert.Single(runner.Requests);
        Assert.Equal(
            ["-R", "acme/perga", "pr", "merge", "55", "--squash", "--match-head-commit", "expected_sha_123"],
            runner.Requests[0].Arguments);

        shouldFail = true;
        var ex = await Assert.ThrowsAsync<VcsCommandException>(() =>
            forge.MergePrAsync("acme/perga", 55, "stale_sha_000", CancellationToken.None));
        Assert.Contains("does not match", ex.Message);
    }

    [Fact]
    public async Task ViewIssueAsync_ParsesStructuredIssueFieldsAndLabels()
    {
        var runner = new RecordingProcessRunner(req =>
        {
            Assert.Equal(
                ["-R", "acme/perga", "issue", "view", "12", "--json", "number,url,state,title,body,labels"],
                req.Arguments);

            return new ProcessRunResult(
                0,
                """
                {
                  "number": 12,
                  "url": "https://github.com/acme/perga/issues/12",
                  "state": "OPEN",
                  "title": "Support JJ op restore",
                  "body": "Add first-class Jujutsu rollback tool.",
                  "labels": [{"name": "enhancement"}, {"name": "vcs"}]
                }
                """,
                string.Empty);
        });

        var forge = new GitHubForgeService(runner);
        var issue = await forge.ViewIssueAsync("acme/perga", 12, CancellationToken.None);

        Assert.Equal(12, issue.Number);
        Assert.Equal("https://github.com/acme/perga/issues/12", issue.Url);
        Assert.Equal("OPEN", issue.State);
        Assert.Equal("Support JJ op restore", issue.Title);
        Assert.Equal("Add first-class Jujutsu rollback tool.", issue.Body);
        Assert.Equal(["enhancement", "vcs"], issue.Labels);
    }

    private sealed class RecordingProcessRunner(Func<ProcessRunRequest, ProcessRunResult> handler) : IProcessRunner
    {
        public List<ProcessRunRequest> Requests { get; } = [];

        public Task<ProcessRunResult> RunAsync(ProcessRunRequest request, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Requests.Add(request);
            return Task.FromResult(handler(request));
        }
    }
}
