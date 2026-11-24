using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using ReviewTool.Core.Git;
using ReviewTool.Core.Publishing;
using ReviewTool.Core.Publishing.GitHub;
using ReviewTool.Core.Reviews;
using ReviewTool.Server.Cli;

namespace ReviewTool.Tests;

public class RemoteRepositoryTests
{
    [Theory]
    [InlineData("git@github.com:acme/widgets.git", "github.com", "acme", "widgets")]
    [InlineData("https://github.com/acme/widgets", "github.com", "acme", "widgets")]
    [InlineData("https://user@github.com/acme/widgets.git/", "github.com", "acme", "widgets")]
    [InlineData("ssh://git@github.example.com:2222/acme/widgets.git", "github.example.com", "acme", "widgets")]
    [InlineData("https://gitlab.com/group/sub/project.git", "gitlab.com", "group/sub", "project")]
    public void ParsesCommonRemoteUrls(string url, string host, string owner, string name)
    {
        var r = RemoteRepository.TryParse("origin", url)!;
        Assert.Equal((host, owner, name), (r.Host, r.Owner, r.Name));
    }

    [Theory]
    [InlineData("/home/me/repos/widgets")]
    [InlineData("C:\\repos\\widgets")]
    [InlineData("file:///srv/git/widgets.git")]
    [InlineData("https://github.com/justone")]
    public void IgnoresLocalOrIncompleteRemotes(string url) => Assert.Null(RemoteRepository.TryParse("origin", url));
}

public class CliOptionsTests
{
    [Fact]
    public void ParsesReviewTargetAndOptions()
    {
        var o = CliOptions.Parse(["../api", "--base", "develop", "--port=6000", "--no-browser", "--urls", "http://x"]);
        Assert.Null(o.Error);
        Assert.Equal("../api", o.RepoPath);
        Assert.Equal("develop", o.Base);
        Assert.Equal(6000, o.Port);
        Assert.True(o.NoBrowser);
        Assert.Equal(["--urls", "http://x"], o.HostArgs);
        Assert.True(o.HasReviewTarget);
    }

    [Fact]
    public void ParsesPullRequests()
    {
        var o = CliOptions.Parse(["--pr", "#42", "--remote", "upstream"]);
        var req = o.ToRequest("/repo");
        Assert.Equal(42, req.PullRequest!.Number);
        Assert.Equal("upstream", req.PullRequest.Remote);
        Assert.Null(req.HeadRef);
    }

    [Theory]
    [InlineData("--pr", "abc")]
    [InlineData("--port", "99999")]
    [InlineData("a", "b")]
    public void ReportsBadArguments(string a, string b) => Assert.NotNull(CliOptions.Parse([a, b]).Error);

    [Fact]
    public void NoArgumentsMeansCurrentBranch() => Assert.False(CliOptions.Parse([]).HasReviewTarget);
}

/// <summary>Fake GitHub REST API that records requests.</summary>
internal sealed class FakeGitHub : HttpMessageHandler
{
    public string HeadSha { get; set; } = "";
    public List<(HttpMethod Method, string Url, JsonNode? Body, string? Auth)> Requests { get; } = [];
    public Func<JsonNode, HttpResponseMessage?>? OnReview { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content == null ? null : JsonNode.Parse(await request.Content.ReadAsStringAsync(ct));
        var url = request.RequestUri!.ToString();
        Requests.Add((request.Method, url, body, request.Headers.Authorization?.ToString()));
        var pr = new JsonObject
        {
            ["number"] = 7,
            ["title"] = "Add feature",
            ["html_url"] = "https://github.com/acme/demo/pull/7",
            ["state"] = "open",
            ["head"] = new JsonObject { ["sha"] = HeadSha, ["ref"] = "feat" },
            ["base"] = new JsonObject { ["ref"] = "main" },
        };
        if (request.Method == HttpMethod.Get && url.Contains("/pulls?", StringComparison.Ordinal) && url.Contains("head=", StringComparison.Ordinal))
            return Json(url.Contains("head=acme%3Afeat", StringComparison.Ordinal) ? new JsonArray(pr) : new JsonArray());
        if (request.Method == HttpMethod.Get && url.Contains("/pulls?state=open", StringComparison.Ordinal))
            return Json(url.Contains("page=1", StringComparison.Ordinal) ? OpenList() : new JsonArray());
        if (request.Method == HttpMethod.Get && url.EndsWith("/pulls/5", StringComparison.Ordinal))
            return Json(new JsonObject { ["number"] = 5, ["title"] = "Old one", ["state"] = "closed", ["merged_at"] = "2026-01-01T00:00:00Z", ["head"] = new JsonObject { ["sha"] = "x", ["ref"] = "old" }, ["base"] = new JsonObject { ["ref"] = "main" } });
        if (request.Method == HttpMethod.Get && url.EndsWith("/pulls/7", StringComparison.Ordinal)) return Json(pr);
        if (request.Method == HttpMethod.Post && url.EndsWith("/pulls/7/reviews", StringComparison.Ordinal))
            return OnReview?.Invoke(body!) ?? Json(new JsonObject { ["id"] = 99, ["html_url"] = "https://github.com/acme/demo/pull/7#pullrequestreview-99" });
        return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{\"message\":\"Not Found\"}") };
    }

    /// <summary>Open PRs, newest first (as GitHub returns them with sort=created&amp;direction=desc).</summary>
    private static JsonArray OpenList()
    {
        JsonObject Pr(int n, string title, string head, string created, bool draft = false) => new()
        {
            ["number"] = n,
            ["title"] = title,
            ["html_url"] = $"https://github.com/acme/demo/pull/{n}",
            ["user"] = new JsonObject { ["login"] = "dev" + n },
            ["head"] = new JsonObject { ["ref"] = head, ["sha"] = new string((char)('a' + n), 40) },
            ["base"] = new JsonObject { ["ref"] = "main" },
            ["created_at"] = created,
            ["updated_at"] = created,
            ["draft"] = draft,
        };
        return new JsonArray(Pr(9, "Newest change", "topic-9", "2026-11-20T10:00:00Z", draft: true), Pr(7, "Add feature", "feat", "2026-11-10T10:00:00Z"), Pr(3, "Oldest open", "topic-3", "2026-10-01T10:00:00Z"));
    }

    public static HttpResponseMessage Json(JsonNode node, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(node.ToJsonString(), Encoding.UTF8, "application/json") };
}

internal sealed class FixedCredentials(string? token) : ICredentialResolver
{
    public Task<ResolvedCredential?> ResolveAsync(IReviewPublisher publisher, RemoteRepository remote, CancellationToken ct = default)
        => Task.FromResult(token == null ? null : new ResolvedCredential(token, "test"));
}

public class PublishingTests : IDisposable
{
    private readonly TestRepo _repo = new();
    private readonly DirectoryInfo _data = Directory.CreateTempSubdirectory("reviewtool-publish");
    private readonly ReviewService _reviews;
    private readonly FakeGitHub _github = new();

    public PublishingTests()
    {
        _reviews = new ReviewService(new JsonFileReviewStore(_data.FullName), new GitRunner());
        _repo.Git("remote", "add", "origin", "git@github.com:acme/demo.git");
        _repo.Write("f.txt", "a\nb\n");
        _repo.Commit("base");
        _repo.Git("checkout", "-q", "-b", "feat");
        _repo.Write("f.txt", "a\nb\nc1\nc2\nc3\n");
        _repo.Commit("one");
        _repo.Write("f.txt", "a\nb\nc1\nC2!\nc3\n");
        _repo.Commit("two");
        _repo.Write("f.txt", "zz\na\nb\nc1\nC2!\n");
        _repo.Commit("three");
    }

    public void Dispose()
    {
        _repo.Dispose();
        _data.Delete(true);
    }

    private PublishingService Service(string? token = "secret-token")
        => new(_reviews, [new GitHubPublisher(new HttpClient(_github), new GitHubOptions())], new FixedCredentials(token));

    private async Task<Review> CreateWithNotesAsync()
    {
        var review = await _reviews.CreateAsync(new CreateReviewRequest(_repo.Path, "main", "feat"));
        _github.HeadSha = review.HeadSha;
        var one = review.Commits[0].Sha;
        await _reviews.AddNoteAsync(review.Id, new NoteInput(one, "f.txt", DiffSide.New, 3, null, "c1", "c1 survives to head", NoteKind.Issue, null));
        await _reviews.AddNoteAsync(review.Id, new NoteInput(one, "f.txt", DiffSide.New, 4, null, "c2", "c2 is rewritten later", null, null));
        await _reviews.AddNoteAsync(review.Id, new NoteInput(one, null, null, null, null, null, "commit-level thought", NoteKind.Question, null));
        await _reviews.AddNoteAsync(review.Id, new NoteInput(Review.RangeKey, "f.txt", DiffSide.New, 1, 2, "zz", "range note", null, null));
        await _reviews.AddNoteAsync(review.Id, new NoteInput(one, "f.txt", DiffSide.New, 5, null, "c3", "already resolved", null, true));
        await _reviews.PatchAsync(review.Id, new ReviewPatch(null, "Looks good overall.", ReviewStatus.ChangesRequested, null));
        return review;
    }

    [Fact]
    public async Task DetectsGitHubTargetFromRemote()
    {
        var review = await CreateWithNotesAsync();
        var target = Assert.Single(await Service().GetTargetsAsync(review.Id));
        Assert.Equal(("github", "acme/demo", "https://github.com/acme/demo", true), (target.Provider, target.Repository, target.WebUrl, target.Authenticated));
        Assert.False((await Service(token: null).GetTargetsAsync(review.Id))[0].Authenticated);
    }

    [Fact]
    public async Task PlansInlineCommentsAtHeadAndExplainsTheRest()
    {
        var review = await CreateWithNotesAsync();
        var preview = await Service().PreviewAsync(review.Id, new PublishRequest());

        Assert.Empty(preview.Blockers);
        Assert.Equal(7, preview.PullRequest!.Number); // found by branch name
        Assert.Equal(PublishVerdict.RequestChanges, preview.Verdict); // from the review status
        Assert.Equal(1, preview.SkippedResolved);

        // c1 moved from line 3 to line 4 by the time of the head commit.
        var c1 = preview.Inline.Single(c => c.Body.Contains("c1 survives", StringComparison.Ordinal));
        Assert.Equal((4, 4, DiffSide.New), (c1.StartLine, c1.EndLine, c1.Side));
        Assert.StartsWith("**Issue:**", c1.Body);
        var range = preview.Inline.Single(c => c.Body == "range note");
        Assert.Equal((1, 2), (range.StartLine, range.EndLine));

        Assert.Equal("A later commit changes this code", preview.General.Single(g => g.Line == 4).Reason);
        Assert.Equal("Note on a whole commit", preview.General.Single(g => g.Path == null).Reason);
        Assert.Contains("Looks good overall.", preview.Body);
        Assert.Contains("c2 is rewritten later", preview.Body);
        Assert.Contains("```txt", preview.Body);
    }

    [Fact]
    public async Task PostsOneReviewWithInlineCommentsAndRemembersIt()
    {
        var review = await CreateWithNotesAsync();
        var result = await Service().PublishAsync(review.Id, new PublishRequest(Verdict: PublishVerdict.Comment));

        Assert.Equal("https://github.com/acme/demo/pull/7#pullrequestreview-99", result.Url);
        Assert.Equal(2, result.InlineComments);
        Assert.Equal(2, result.GeneralNotes);

        var post = _github.Requests.Single(r => r.Method == HttpMethod.Post);
        Assert.Equal("https://api.github.com/repos/acme/demo/pulls/7/reviews", post.Url);
        Assert.Equal("Bearer secret-token", post.Auth);
        Assert.Equal(review.HeadSha, (string)post.Body!["commit_id"]!);
        Assert.Equal("COMMENT", (string)post.Body["event"]!);
        var comments = post.Body["comments"]!.AsArray();
        Assert.Equal(2, comments.Count);
        var multi = comments.Single(c => (string)c!["body"]! == "range note")!;
        Assert.Equal((1, 2, "RIGHT", "RIGHT"), ((int)multi["start_line"]!, (int)multi["line"]!, (string)multi["side"]!, (string)multi["start_side"]!));

        // Recorded, so the same notes aren't posted again.
        var saved = await _reviews.GetAsync(review.Id);
        var record = Assert.Single(saved.Publications);
        Assert.Equal(("github", "acme/demo", 7, 4), (record.Provider, record.Repository, record.PullRequestNumber, record.NoteIds.Count));
        var again = await Service().PreviewAsync(review.Id, new PublishRequest());
        Assert.Empty(again.Inline);
        Assert.Equal(4, again.AlreadyPublished);
    }

    [Fact]
    public async Task FallsBackToTextWhenGitHubRejectsInlinePositions()
    {
        var review = await CreateWithNotesAsync();
        _github.OnReview = body => body["comments"]!.AsArray().Count > 0
            ? FakeGitHub.Json(new JsonObject { ["message"] = "Unprocessable Entity", ["errors"] = new JsonArray("Line could not be resolved") }, HttpStatusCode.UnprocessableEntity)
            : null;

        var result = await Service().PublishAsync(review.Id, new PublishRequest());
        Assert.Equal(0, result.InlineComments);
        Assert.Contains(result.Warnings, w => w.Contains("Line could not be resolved", StringComparison.Ordinal));
        var final = _github.Requests.Last(r => r.Method == HttpMethod.Post).Body!;
        Assert.Contains("c1 survives to head", (string)final["body"]!);
    }

    [Fact]
    public async Task BlocksWhenThePullRequestIsAtADifferentCommit()
    {
        var review = await CreateWithNotesAsync();
        _github.HeadSha = new string('a', 40);
        var preview = await Service().PreviewAsync(review.Id, new PublishRequest());
        Assert.Contains(preview.Blockers, b => b.Contains("Push your branch", StringComparison.Ordinal));
        var ex = await Assert.ThrowsAsync<PublishException>(() => Service().PublishAsync(review.Id, new PublishRequest()));
        Assert.Contains("aaaaaaa", ex.Message);
        Assert.DoesNotContain(_github.Requests, r => r.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task ExplainsHowToSignInWhenNoTokenIsAvailable()
    {
        var review = await CreateWithNotesAsync();
        var preview = await Service(token: null).PreviewAsync(review.Id, new PublishRequest());
        Assert.Contains(preview.Blockers, b => b.Contains("gh auth login", StringComparison.Ordinal));
        Assert.Empty(_github.Requests);
        Assert.NotEmpty(preview.Inline); // the plan is still shown
    }

    [Theory]
    [InlineData("feat", "origin", "feat")]
    [InlineData("origin/feature/x", "origin", "feature/x")]
    [InlineData("refs/heads/topic", "origin", "topic")]
    [InlineData("refs/reviewtool/origin/pr-3", "origin", null)]
    public void WorksOutTheRemoteBranchName(string headRef, string remote, string? expected)
        => Assert.Equal(expected, PublishingService.BranchName(headRef, remote));
}

public class PublishingRepostTests : IDisposable
{
    private readonly TestRepo _repo = TestRepo.WithFeatureBranch();
    private readonly DirectoryInfo _data = Directory.CreateTempSubdirectory("reviewtool-repost");

    public void Dispose()
    {
        _repo.Dispose();
        _data.Delete(true);
    }

    [Fact]
    public async Task RefusesToPostTheSameSummaryTwice()
    {
        _repo.Git("remote", "add", "origin", "https://github.com/acme/demo.git");
        var reviews = new ReviewService(new JsonFileReviewStore(_data.FullName), new GitRunner());
        var review = await reviews.CreateAsync(new CreateReviewRequest(_repo.Path, "main", "feature"));
        await reviews.PatchAsync(review.Id, new ReviewPatch(null, "LGTM", null, null));
        var github = new FakeGitHub { HeadSha = review.HeadSha };
        var svc = new PublishingService(reviews, [new GitHubPublisher(new HttpClient(github), new GitHubOptions())], new FixedCredentials("t"));

        Assert.False((await svc.PreviewAsync(review.Id, new PublishRequest(PullRequestNumber: 7))).NothingNew);
        await svc.PublishAsync(review.Id, new PublishRequest(PullRequestNumber: 7));
        Assert.True((await svc.PreviewAsync(review.Id, new PublishRequest(PullRequestNumber: 7))).NothingNew);
        await Assert.ThrowsAsync<PublishException>(() => svc.PublishAsync(review.Id, new PublishRequest(PullRequestNumber: 7)));
    }
}
