using ReviewTool.Core.Git;
using ReviewTool.Core.Reviews;

namespace ReviewTool.Tests;

public class ReviewServiceTests : IDisposable
{
    private readonly TestRepo _repo = TestRepo.WithFeatureBranch();
    private readonly DirectoryInfo _data = Directory.CreateTempSubdirectory("reviewtool-data");
    private readonly ReviewService _svc;

    public ReviewServiceTests()
    {
        _svc = new ReviewService(new JsonFileReviewStore(_data.FullName), new GitRunner());
    }

    public void Dispose()
    {
        _repo.Dispose();
        _data.Delete(true);
    }

    [Fact]
    public async Task CreatesReviewWithCommitStats()
    {
        var review = await _svc.CreateAsync(new CreateReviewRequest(_repo.Path, "main", "feature"));
        Assert.Equal("feature → main", review.Name);
        Assert.Equal(3, review.Commits.Count);
        Assert.Equal(1, review.Commits[0].FileCount);
        Assert.Equal(2, review.Commits[2].FileCount);
        Assert.Equal(review.BaseSha, review.MergeBaseSha);

        var loaded = await _svc.GetAsync(review.Id);
        Assert.Equal(review.HeadSha, loaded.HeadSha);
    }

    [Fact]
    public async Task DefaultsHeadToCurrentBranchAndBaseToDefault()
    {
        var review = await _svc.CreateAsync(new CreateReviewRequest(_repo.Path));
        Assert.Equal("main", review.BaseRef);
        Assert.Equal("feature", review.HeadRef);
    }

    [Fact]
    public async Task RejectsEmptyRanges()
        => await Assert.ThrowsAsync<ReviewValidationException>(() => _svc.CreateAsync(new CreateReviewRequest(_repo.Path, "feature", "main")));

    [Fact]
    public async Task TracksReviewedFilesAndNotes()
    {
        var review = await _svc.CreateAsync(new CreateReviewRequest(_repo.Path, "main", "feature"));
        var sha = review.Commits[1].Sha;

        await _svc.SetFilesReviewedAsync(review.Id, sha, ["src/app.txt"], true);
        var note = await _svc.AddNoteAsync(review.Id, new NoteInput(sha, "src/app.txt", DiffSide.New, 2, 3, "line two", "Why?", NoteKind.Question, null));
        await _svc.UpdateNoteAsync(review.Id, note.Id, new NoteInput(null, null, null, null, null, null, "Why this?", null, true));

        var loaded = await _svc.GetAsync(review.Id);
        Assert.Equal(["src/app.txt"], loaded.ReviewedFiles[sha]);
        var saved = Assert.Single(loaded.Notes);
        Assert.Equal("Why this?", saved.Body);
        Assert.True(saved.Resolved);
        Assert.Equal(3, saved.EndLine);

        var summary = ReviewSummary.From(loaded);
        Assert.Equal(1, summary.ReviewedCommitCount);
        Assert.Equal(1, summary.ReviewedFileCount);
        Assert.Equal(4, summary.FileCount);

        await _svc.DeleteNoteAsync(review.Id, note.Id);
        Assert.Empty((await _svc.GetAsync(review.Id)).Notes);
    }

    [Fact]
    public async Task RejectsNotesForUnknownCommits()
    {
        var review = await _svc.CreateAsync(new CreateReviewRequest(_repo.Path, "main", "feature"));
        await Assert.ThrowsAsync<ReviewValidationException>(() =>
            _svc.AddNoteAsync(review.Id, new NoteInput("deadbeef", "x", DiffSide.New, 1, null, null, "hi", null, null)));
    }

    [Fact]
    public async Task RefreshKeepsNotesOnRewrittenCommits()
    {
        var review = await _svc.CreateAsync(new CreateReviewRequest(_repo.Path, "main", "feature"));
        var last = review.Commits[^1].Sha;
        var first = review.Commits[0].Sha;
        await _svc.AddNoteAsync(review.Id, new NoteInput(last, "logo.bin", DiffSide.New, null, null, null, "binary?", null, null));
        await _svc.SetFilesReviewedAsync(review.Id, first, ["src/new.txt"], true);

        // Simulate a force push: amend the last commit.
        _repo.Write("extra.txt", "more\n");
        _repo.Git("add", "-A");
        _repo.Git("commit", "-q", "--amend", "-m", "Rename readme, add binary (amended)");

        var refreshed = await _svc.RefreshAsync(review.Id);
        Assert.Equal(4, refreshed.Commits.Count);
        var outdated = Assert.Single(refreshed.Commits, c => c.Outdated);
        Assert.Equal(last, outdated.Sha);
        Assert.Single(refreshed.Notes);
        Assert.Equal(["src/new.txt"], refreshed.ReviewedFiles[first]);
        Assert.Equal(3, ReviewSummary.From(refreshed).CommitCount);
    }

    [Fact]
    public async Task ServesDiffsForCommitsAndRange()
    {
        var review = await _svc.CreateAsync(new CreateReviewRequest(_repo.Path, "main", "feature"));
        var rangeFiles = await _svc.GetChangedFilesAsync(review.Id, Review.RangeKey);
        Assert.Equal(4, rangeFiles.Count);
        var diff = await _svc.GetFileDiffAsync(review.Id, review.Commits[1].Sha, "src/app.txt", null, 3);
        Assert.Single(diff.Hunks);
        var old = await _svc.GetBlobAsync(review.Id, review.Commits[1].Sha, "src/app.txt", DiffSide.Old);
        Assert.Equal("line 1\nline 2\nline 3\n", old.Text);
        await Assert.ThrowsAsync<ReviewValidationException>(() => _svc.GetChangedFilesAsync(review.Id, "not-a-commit"));
    }

    [Fact]
    public async Task ExportsMarkdown()
    {
        var review = await _svc.CreateAsync(new CreateReviewRequest(_repo.Path, "main", "feature", "My review"));
        await _svc.AddNoteAsync(review.Id, new NoteInput(review.Commits[1].Sha, "src/app.txt", DiffSide.New, 2, null, "line two", "Rename this", NoteKind.Nit, null));
        await _svc.AddNoteAsync(review.Id, new NoteInput(review.Commits[1].Sha, "src/app.txt", DiffSide.New, 4, null, null, "Done already", null, true));
        await _svc.PatchAsync(review.Id, new ReviewPatch(null, "Looks good overall.", ReviewStatus.Approved, null));

        var md = ReviewService.ExportMarkdown(await _svc.GetAsync(review.Id), includeResolved: false);
        Assert.Contains("# Review: My review", md);
        Assert.Contains("Looks good overall.", md);
        Assert.Contains("**Nit** `src/app.txt:2`", md);
        Assert.Contains("Edit app", md);
        Assert.DoesNotContain("Done already", md);
    }
}
