using ReviewTool.Core.Git;

namespace ReviewTool.Tests;

public class GitRepositoryTests : IDisposable
{
    private readonly TestRepo _repo = TestRepo.WithFeatureBranch();
    private readonly GitRunner _git = new();

    public void Dispose() => _repo.Dispose();

    [Fact]
    public async Task OpensFromSubdirectory()
    {
        var repo = await GitRepository.OpenAsync(Path.Combine(_repo.Path, "src"), _git);
        Assert.Equal(Path.GetFullPath(_repo.Path), repo.Path);
    }

    [Fact]
    public async Task RejectsNonRepositories()
    {
        var dir = Directory.CreateTempSubdirectory("reviewtool-notrepo");
        try { await Assert.ThrowsAsync<GitException>(() => GitRepository.OpenAsync(dir.FullName, _git)); }
        finally { dir.Delete(true); }
    }

    [Fact]
    public async Task ListsCommitsOldestFirst()
    {
        var repo = await GitRepository.OpenAsync(_repo.Path, _git);
        var commits = await repo.ListCommitsAsync("main", "feature");
        Assert.Equal(["Add new file", "Edit app", "Rename readme, add binary"], commits.Select(c => c.Subject));
        Assert.Equal("Test User", commits[0].AuthorName);
    }

    [Fact]
    public async Task ReportsChangedFilesWithRenamesAndBinaries()
    {
        var repo = await GitRepository.OpenAsync(_repo.Path, _git);
        var head = await repo.ResolveCommitAsync("feature");
        var files = await repo.GetChangedFilesAsync(head + "~1", head);
        var rename = Assert.Single(files, f => f.Status == ChangeKind.Renamed);
        Assert.Equal("docs/readme.md", rename.OldPath);
        Assert.Equal("docs/README.md", rename.Path);
        Assert.True(Assert.Single(files, f => f.Path == "logo.bin").IsBinary);
    }

    [Fact]
    public async Task ProducesFileDiffWithContext()
    {
        var repo = await GitRepository.OpenAsync(_repo.Path, _git);
        var head = await repo.ResolveCommitAsync("feature~1");
        var diff = await repo.GetFileDiffAsync(head + "~1", head, "src/app.txt", null, contextLines: GitRepository.FullContext);
        var lines = diff.Hunks.SelectMany(h => h.Lines).ToList();
        Assert.Equal(5, lines.Count); // line 1, -line 2, +line two, line 3, +line 4
        Assert.Contains(lines, l => l.Type == DiffLineType.Delete && l.Text == "line 2");
        Assert.Contains(lines, l => l.Type == DiffLineType.Add && l.Text == "line two" && l.NewLine == 2);
    }

    [Fact]
    public async Task DiffsRootCommitAgainstEmptyTree()
    {
        var repo = await GitRepository.OpenAsync(_repo.Path, _git);
        var root = await repo.ResolveCommitAsync("main");
        var files = await repo.GetChangedFilesAsync(await repo.GetEmptyTreeAsync(), root);
        Assert.All(files, f => Assert.Equal(ChangeKind.Added, f.Status));
        Assert.Equal(2, files.Count);
    }

    [Fact]
    public async Task ReadsBlobsAndTrees()
    {
        var repo = await GitRepository.OpenAsync(_repo.Path, _git);
        var head = await repo.ResolveCommitAsync("feature");
        Assert.Contains("docs/README.md", await repo.ListTreeAsync(head));
        var blob = await repo.GetBlobAsync(head, "src/app.txt");
        Assert.Equal("line 1\nline two\nline 3\nline 4\n", blob.Text);
        Assert.True((await repo.GetBlobAsync(head, "logo.bin")).IsBinary);
        await Assert.ThrowsAsync<GitException>(() => repo.GetBlobAsync(head, "missing.txt"));
    }

    [Theory]
    [InlineData("--output=/tmp/x")]
    [InlineData("-c")]
    [InlineData("main\nfoo")]
    public async Task RejectsOptionLikeRevisions(string rev)
    {
        var repo = await GitRepository.OpenAsync(_repo.Path, _git);
        await Assert.ThrowsAsync<GitException>(() => repo.ResolveCommitAsync(rev));
    }

    [Fact]
    public async Task ListsRefsWithDefaultBranch()
    {
        var repo = await GitRepository.OpenAsync(_repo.Path, _git);
        var refs = await repo.ListRefsAsync();
        Assert.Equal("feature", refs.CurrentBranch);
        Assert.Equal("main", refs.DefaultBranch);
        Assert.Contains(refs.Refs, r => r.Name == "feature" && r.Kind == "branch");
    }
}
