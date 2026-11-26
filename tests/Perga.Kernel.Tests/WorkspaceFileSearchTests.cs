using Perga.Kernel.Ports;

namespace Perga.Kernel.Tests;

public sealed class WorkspaceFileSearchTests
{
    [Theory]
    [InlineData("cafe", "docs/café.md", "docs/menu.md")]
    [InlineData("^src", "src-index.ts", "vendor/src-index.ts")]
    [InlineData("quick pub", "quick-open/public.tsx", "quick-open/types.ts")]
    [InlineData("!ignore", "assorted.txt", "docs/ignore.txt")]
    [InlineData("'src", "vendor/src-index.ts", "vendor/s-r-c.ts")]
    [InlineData(".ts$", "src/file.ts", "src/file.ts.bak")]
    [InlineData("^src$", "src", "src-index.ts")]
    [InlineData("^docs | ^src ts$", "src/file.ts", "docs/file.md")]
    [InlineData("quick\\ open", "quick open.md", "quick-open.md")]
    [InlineData("^notes.md", "docs/^notes.md", "docs/notes.md")]
    [InlineData("!notes.md", "docs/!notes.md", "docs/notes.md")]
    [InlineData("^cafe\u0301.md", "docs/^cafe\u0301.md", "docs/café.md")]
    [InlineData("^cafe\u0301.md", "docs/^café.md", "docs/café.md")]
    [InlineData("lodz", "docs/łódź.md", "docs/menu.md")]
    [InlineData("README", "docs/readme", "docs/menu.md")]
    public void RanksExtendedTermsAndRetainsLiteralNames(string query, string included, string excluded)
    {
        var result = WorkspaceFileSearch.Rank([excluded, included], false, query, 10);
        Assert.Equal([included], result.Paths);
        Assert.False(result.Truncated);
    }

    [Fact]
    public void PrefersExactCaseAtTheResultLimit()
    {
        const string exact = "Src-RealCandidate.ts";
        var paths = Enumerable.Range(0, 200).Select(index => $"src-{index:D3}.ts").Append(exact);

        var result = WorkspaceFileSearch.Rank(paths, false, "^Src", 200);

        Assert.Equal(exact, result.Paths[0]);
        Assert.Equal(200, result.Paths.Count);
        Assert.True(result.Truncated);
    }

    [Fact]
    public void PrefersLiteralBasenameOverInverseMatchesAtTheResultLimit()
    {
        const string literal = "docs/!notes.md";
        var paths = Enumerable.Range(0, 200).Select(index => $"{index:D3}.md").Append(literal);

        var result = WorkspaceFileSearch.Rank(paths, false, "!notes.md", 200);

        Assert.Equal(literal, result.Paths[0]);
        Assert.Equal(200, result.Paths.Count);
        Assert.True(result.Truncated);
    }
}
