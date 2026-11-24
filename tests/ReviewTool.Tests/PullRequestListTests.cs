using ReviewTool.Core.Git;
using ReviewTool.Core.Publishing;
using ReviewTool.Core.Publishing.GitHub;
using ReviewTool.Core.Reviews;

namespace ReviewTool.Tests;

public class PullRequestListTests : IDisposable
{
    private readonly TestRepo _repo = TestRepo.WithFeatureBranch(); // main + "feature" (3 commits)
    private readonly DirectoryInfo _data = Directory.CreateTempSubdirectory("reviewtool-prs");
    private readonly DirectoryInfo _remote = Directory.CreateTempSubdirectory("reviewtool-remote");
    private readonly ReviewService _reviews;
    private readonly FakeGitHub _github = new();

    public PullRequestListTests()
    {
        _reviews = new ReviewService(new JsonFileReviewStore(_data.FullName), new GitRunner());
    }

    public void Dispose()
    {
        _repo.Dispose();
        _data.Delete(true);
        try { _remote.Delete(true); } catch { /* best effort */ }
    }

    private PullRequestService Service(string? token = "t")
        => new(_reviews, [new GitHubPublisher(new HttpClient(_github), new GitHubOptions())], new FixedCredentials(token));

    /// <summary>A local bare repo standing in for a GitHub remote: PRs 1-3, of which 2 and 3 are open.</summary>
    private void UseLocalRemoteLikeGitHub(bool withMergeRefs = true)
    {
        Git(_remote.FullName, "init", "-q", "--bare");
        _repo.Git("remote", "add", "origin", _remote.FullName);
        _repo.Git("push", "-q", "origin", "main", "feature");
        var head = _repo.Git("rev-parse", "feature");
        var mid = _repo.Git("rev-parse", "feature~1");
        _repo.Git("push", "-q", "origin", $"{mid}:refs/pull/1/head", $"{head}:refs/pull/2/head", $"{head}:refs/pull/3/head");
        if (withMergeRefs) _repo.Git("push", "-q", "origin", $"{head}:refs/pull/2/merge", $"{head}:refs/pull/3/merge");
    }

    private static void Git(string dir, params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git") { WorkingDirectory = dir, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = System.Diagnostics.Process.Start(psi)!;
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException(p.StandardError.ReadToEnd());
    }

    [Fact]
    public async Task ListsOpenPullRequestsFromGitHubNewestFirstAndLinksExistingReviews()
    {
        _repo.Git("remote", "add", "origin", "git@github.com:acme/demo.git");
        _repo.Git("branch", "feat", "feature");
        var started = await _reviews.CreateAsync(new CreateReviewRequest(_repo.Path, "main", "feat"));

        var list = await Service().ListOpenAsync(_repo.Path);
        Assert.Equal(("github", "acme/demo", null), (list.Source, list.Repository, list.Note));
        Assert.Equal([9, 7, 3], list.PullRequests.Select(p => p.Number));
        var nine = list.PullRequests[0];
        Assert.Equal(("Newest change", "dev9", "topic-9", "main", true), (nine.Title, nine.Author, nine.HeadRef, nine.BaseRef, nine.Draft));
        // PR #7's branch "feat" already has a review.
        Assert.Equal(started.Id, list.PullRequests.Single(p => p.Number == 7).ReviewId);
        Assert.Null(nine.ReviewId);
    }

    [Fact]
    public async Task FallsBackToGitRefsAndOnlyListsOpenPullRequests()
    {
        UseLocalRemoteLikeGitHub();
        var list = await Service(token: null).ListOpenAsync(_repo.Path);
        Assert.Equal(("git", "origin"), (list.Source, list.Remote));
        Assert.Equal([3, 2], list.PullRequests.Select(p => p.Number)); // #1 has no merge ref: merged/closed
        Assert.All(list.PullRequests, p => Assert.Null(p.Title));
    }

    [Fact]
    public async Task SaysSoWhenTheRemoteCantTellOpenFromMerged()
    {
        UseLocalRemoteLikeGitHub(withMergeRefs: false);
        var list = await Service(token: null).ListOpenAsync(_repo.Path);
        Assert.Equal([3, 2, 1], list.PullRequests.Select(p => p.Number));
        Assert.Contains("merged and closed ones may be listed", list.Note);
    }

    [Fact]
    public async Task StartingAPullRequestReviewFetchesItsBaseBranchFresh()
    {
        UseLocalRemoteLikeGitHub();
        _repo.Git("fetch", "-q", "origin");
        var review = await _reviews.CreateAsync(new CreateReviewRequest(_repo.Path, "origin/main", PullRequest: new PullRequestInput(2, "origin")));
        Assert.Equal(3, review.Commits.Count);

        // Someone merges another change into main on the remote; our local origin/main is now stale.
        var other = new TestRepo();
        try
        {
            other.Git("remote", "add", "origin", _remote.FullName);
            other.Git("fetch", "-q", "origin", "main");
            other.Git("checkout", "-q", "-b", "main", "FETCH_HEAD");
            other.Write("other.txt", "x\n");
            other.Commit("main moves");
            other.Git("push", "-q", "origin", "main");
            var newMain = other.Git("rev-parse", "HEAD");

            var refreshed = await _reviews.RefreshAsync(review.Id);
            Assert.Equal(newMain, refreshed.BaseSha); // base re-fetched on refresh
        }
        finally
        {
            other.Dispose();
        }
    }

    [Fact]
    public async Task ReportsWhetherThePullRequestsBehindReviewsAreStillOpen()
    {
        _repo.Git("remote", "add", "origin", "git@github.com:acme/demo.git");
        var store = _reviews.Store;
        async Task<string> Save(int number)
        {
            var r = await _reviews.CreateAsync(new CreateReviewRequest(_repo.Path, "main", "feature"));
            await store.UpdateAsync(r.Id, x =>
            {
                x.PullRequest = new PullRequestRef { Number = number, Remote = "origin", Provider = "github" };
                return Task.CompletedTask;
            });
            return r.Id;
        }
        var open = await Save(7);
        var merged = await Save(5);
        var plain = await _reviews.CreateAsync(new CreateReviewRequest(_repo.Path, "main", "feature"));

        var states = await Service().GetReviewPullRequestStatesAsync();
        Assert.Equal("open", states[open]);
        Assert.Equal("merged", states[merged]);
        Assert.False(states.ContainsKey(plain.Id)); // not started from a PR
    }
}
