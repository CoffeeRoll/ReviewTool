using ReviewTool.Core.Git;
using ReviewTool.Core.Reviews;

namespace ReviewTool.Tests;

/// <summary>Later-changes, note follow-ups, file history and refresh matching.</summary>
public class HistoryTests : IDisposable
{
    private readonly TestRepo _repo = new();
    private readonly DirectoryInfo _data = Directory.CreateTempSubdirectory("reviewtool-history");
    private readonly ReviewService _svc;
    private readonly GitRunner _git = new();

    public HistoryTests()
    {
        _svc = new ReviewService(new JsonFileReviewStore(_data.FullName), _git);
        _repo.Write("f.txt", "a\nb\n");
        _repo.Write("other.txt", "x\n");
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

    private Task<Review> Create() => _svc.CreateAsync(new CreateReviewRequest(_repo.Path, "main", "feat"));

    [Fact]
    public async Task FindsLinesChangedByLaterCommits()
    {
        var review = await Create();
        var (one, two, three) = (review.Commits[0].Sha, review.Commits[1].Sha, review.Commits[2].Sha);

        var later = await _svc.GetLaterChangesAsync(review.Id, one, "f.txt");
        Assert.Equal([new LaterChange(4, two, 4), new LaterChange(5, three, 5)], later.OrderBy(l => l.Line));

        Assert.Empty(await _svc.GetLaterChangesAsync(review.Id, three, "f.txt")); // last commit
        Assert.Empty(await _svc.GetLaterChangesAsync(review.Id, Review.RangeKey, "f.txt"));
    }

    [Fact]
    public async Task CarriesNotesToTheCommitThatChangesTheirCode()
    {
        var review = await Create();
        var one = review.Commits[0].Sha;
        var onC2 = await _svc.AddNoteAsync(review.Id, new NoteInput(one, "f.txt", DiffSide.New, 4, null, "c2", "rename?", null, null));
        var onRange = await _svc.AddNoteAsync(review.Id, new NoteInput(one, "f.txt", DiffSide.New, 3, 5, "c1", "block", null, null));
        await _svc.AddNoteAsync(review.Id, new NoteInput(one, "f.txt", DiffSide.New, 1, null, "a", "untouched later", null, null));

        var followUps = await _svc.GetNoteFollowUpsAsync(review.Id);
        Assert.Equal(2, followUps.Count);
        var c2 = followUps.Single(f => f.NoteId == onC2.Id);
        Assert.Equal((review.Commits[1].Sha, 4, 4, 1, 1), (c2.CommitSha, c2.Line, c2.EndLine, c2.ChangedLines, c2.TotalLines));
        var range = followUps.Single(f => f.NoteId == onRange.Id);
        Assert.Equal(review.Commits[1].Sha, range.CommitSha); // earliest later change wins
        Assert.Equal(2, range.ChangedLines);
    }

    [Fact]
    public async Task ListsFileHistoryFollowingRenames()
    {
        _repo.Git("mv", "f.txt", "g.txt");
        _repo.Commit("rename");
        var review = await Create();
        var history = await _svc.GetFileHistoryAsync(review.Id, "g.txt");
        Assert.Equal(["one", "two", "three", "rename"], history.Select(h => review.FindCommit(h.Sha)!.Subject));
        Assert.Equal(ChangeKind.Renamed, history[^1].Status);
        Assert.Equal("f.txt", history[^1].OldPath);
    }

    [Fact]
    public async Task TracksHunkLevelProgress()
    {
        var review = await Create();
        var one = review.Commits[0].Sha;
        await _svc.SetLinesReviewedAsync(review.Id, one, "f.txt", ["N3-4"], true);
        var r = await _svc.SetLinesReviewedAsync(review.Id, one, "f.txt", ["N5"], true);
        Assert.Equal(["N3-5"], r.ReviewedLines[one]["f.txt"]);
        // Un-reviewing the whole file clears its hunk progress.
        r = await _svc.SetFilesReviewedAsync(review.Id, one, ["f.txt"], false);
        Assert.False(r.ReviewedLines.ContainsKey(one));
    }

    [Fact]
    public async Task RebaseKeepsProgressForUnchangedCommits()
    {
        var review = await Create();
        var oldOne = review.Commits[0].Sha;
        var oldTwo = review.Commits[1].Sha;
        await _svc.SetFilesReviewedAsync(review.Id, oldOne, ["f.txt"], true);
        await _svc.AddNoteAsync(review.Id, new NoteInput(oldOne, "f.txt", DiffSide.New, 3, null, "c1", "note on c1", null, null));

        // main moves on (unrelated file), the branch is rebased, and the last commit is amended.
        _repo.Git("checkout", "-q", "main");
        _repo.Write("other.txt", "x\ny\n");
        _repo.Commit("main moves");
        _repo.Git("checkout", "-q", "feat");
        _repo.Git("rebase", "-q", "main");
        _repo.Write("f.txt", "zz\na\nb\nc1\nC2 (better)\n");
        _repo.Git("add", "-A");
        _repo.Git("commit", "-q", "--amend", "--no-edit");

        var refreshed = await _svc.RefreshAsync(review.Id);
        var active = refreshed.Commits.Where(c => !c.Outdated).ToList();
        Assert.Equal([CommitChange.Unchanged, CommitChange.Unchanged, CommitChange.Modified],
            active.Select(c => c.SinceLastReview!.Value));
        Assert.Equal(oldOne, active[0].PreviousSha);
        Assert.NotEqual(oldOne, active[0].Sha);

        // Progress and notes moved to the rebased copy of "one".
        Assert.Equal(["f.txt"], refreshed.ReviewedFiles[active[0].Sha]);
        Assert.Equal(active[0].Sha, Assert.Single(refreshed.Notes).CommitSha);
        Assert.DoesNotContain(refreshed.Commits, c => c.Sha == oldOne);
        Assert.Equal(review.HeadSha, refreshed.PreviousHead!.HeadSha);

        // "Since last review" only lists files the PR touches, not other.txt from main.
        var since = await _svc.GetChangedFilesAsync(review.Id, Review.SinceKey);
        Assert.Equal(["f.txt"], since.Select(f => f.Path));
        Assert.Equal(oldTwo, active[1].PreviousSha);
        Assert.NotEqual(oldTwo, active[1].Sha);
    }

    [Fact]
    public async Task MarksAppendedCommitsAsNew()
    {
        var review = await Create();
        _repo.Write("f.txt", "zz\na\nb\nc1\nC2!\nnew\n");
        _repo.Commit("four");
        var refreshed = await _svc.RefreshAsync(review.Id);
        Assert.Equal([CommitChange.Unchanged, CommitChange.Unchanged, CommitChange.Unchanged, CommitChange.New],
            refreshed.Commits.Select(c => c.SinceLastReview!.Value));
        Assert.Equal(["f.txt"], (await _svc.GetChangedFilesAsync(review.Id, Review.SinceKey)).Select(f => f.Path));
    }

    [Fact]
    public async Task ReportsGeneratedFilesAndWhitespaceOnlyChanges()
    {
        _repo.Write(".gitattributes", "gen/** linguist-generated=true\n");
        _repo.Write("gen/out.txt", "generated\n");
        _repo.Write("yarn.lock", "lock\n");
        _repo.Write("other.txt", "   x\n");
        _repo.Commit("generated + whitespace");
        var review = await Create();
        var files = await _svc.GetChangedFilesAsync(review.Id, review.Commits[^1].Sha);
        Assert.True(files.Single(f => f.Path == "gen/out.txt").IsGenerated);
        Assert.True(files.Single(f => f.Path == "yarn.lock").IsGenerated);
        Assert.False(files.Single(f => f.Path == "other.txt").IsGenerated);

        var ws = await _svc.GetChangedFilesAsync(review.Id, review.Commits[^1].Sha, ignoreWhitespace: true);
        Assert.Equal(0, ws.Single(f => f.Path == "other.txt").Additions);
        var diff = await _svc.GetFileDiffAsync(review.Id, review.Commits[^1].Sha, "other.txt", null, 3, ignoreWhitespace: true);
        Assert.Empty(diff.Hunks);
    }

    [Fact]
    public async Task DetectsMovedCodeAcrossFilesInACommit()
    {
        var body = "int Alpha() => 1;\nint Bravo() => 2;\nint Charlie() => 3;\nint Delta() => 4;\n";
        _repo.Write("m1.txt", "start\n" + body + "end\n");
        _repo.Commit("add m1");
        _repo.Write("m1.txt", "start\nend\n");
        _repo.Write("m2.txt", "top\n    " + body.Replace("\n", "\n    ").TrimEnd(' ') + "bottom\n");
        _repo.Commit("move to m2");
        var review = await Create();
        var moved = await _svc.GetMovedBlocksAsync(review.Id, review.Commits[^1].Sha, false);
        var block = Assert.Single(moved);
        Assert.Equal(("m1.txt", 2, 5, "m2.txt", 2, 5), (block.FromPath, block.FromStart, block.FromEnd, block.ToPath, block.ToStart, block.ToEnd));
    }
}

public class FinalLocationTests : IDisposable
{
    private readonly TestRepo _repo = new();
    private readonly DirectoryInfo _data = Directory.CreateTempSubdirectory("reviewtool-final");
    private readonly ReviewService _svc;

    public FinalLocationTests()
    {
        _svc = new ReviewService(new JsonFileReviewStore(_data.FullName), new GitRunner());
        _repo.Write("f.txt", "a\nb\n");
        _repo.Commit("base");
        _repo.Git("checkout", "-q", "-b", "feat");
        _repo.Write("f.txt", "a\nb\nc1\nc2\nc3\nc4\n");    // one
        _repo.Commit("one");
        _repo.Write("f.txt", "a\nb\nc1\nC2 v2\nc3\nc4\n");  // two: rewrites c2
        _repo.Commit("two");
        _repo.Write("f.txt", "top\na\nb\nc1\nC2 v3\nC2 extra\nc4\n"); // three: rewrites again, drops c3, adds a line on top
        _repo.Commit("three");
    }

    public void Dispose()
    {
        _repo.Dispose();
        _data.Delete(true);
    }

    [Fact]
    public async Task FollowsALineToItsFinalVersionThroughSeveralRewrites()
    {
        var review = await _svc.CreateAsync(new CreateReviewRequest(_repo.Path, "main", "feat"));
        var one = review.Commits[0].Sha;

        // c2 (line 4 in "one") was rewritten twice and c3 removed: the replacement is what sits
        // between the surviving neighbours c1 and c4 at head (lines 5-6: "C2 v3", "C2 extra").
        var loc = await _svc.GetFinalLocationAsync(review.Id, one, "f.txt", 4, null);
        Assert.Equal(new FinalLocation("f.txt", 5, 6, false), loc);

        // c1 survived, shifted down by the line added on top.
        Assert.Equal(new FinalLocation("f.txt", 4, 4, true), await _svc.GetFinalLocationAsync(review.Id, one, "f.txt", 3, null));

        // The combined view is already the final version.
        Assert.Equal(new FinalLocation("f.txt", 2, 3, true), await _svc.GetFinalLocationAsync(review.Id, Review.RangeKey, "f.txt", 2, 3));
    }

    [Fact]
    public async Task ReportsRemovedLinesAsAnEmptyRegion()
    {
        _repo.Write("f.txt", "top\na\nb\nc1\nc4\n"); // four: drops the C2 lines entirely
        _repo.Commit("four");
        var review = await _svc.CreateAsync(new CreateReviewRequest(_repo.Path, "main", "feat"));
        var loc = await _svc.GetFinalLocationAsync(review.Id, review.Commits[0].Sha, "f.txt", 4, 5);
        Assert.NotNull(loc);
        Assert.False(loc.Unchanged);
        Assert.True(loc.EndLine < loc.StartLine); // nothing between c1 (line 4) and c4 (line 5)
        Assert.Equal(5, loc.StartLine);
    }
}
