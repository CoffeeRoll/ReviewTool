using ReviewTool.Core.Git;
using ReviewTool.Core.Reviews;

namespace ReviewTool.Core.Publishing;

/// <summary>
/// Decides, platform-independently, which notes can be posted as inline comments on the pull
/// request's diff and where. Platforms only accept inline comments on the PR's overall diff
/// (merge-base..head), while notes here are written against individual commits, so each note's
/// lines are traced forward to the head with <c>git blame --reverse</c>. Notes that can't be placed
/// (code rewritten later, commit-level notes, lines outside the diff) go into the review text.
/// </summary>
public sealed class PublishPlanner(GitRepository repo, Review review)
{
    private readonly Dictionary<string, Dictionary<int, ReverseBlameLine>?> _blame = [];
    private readonly Dictionary<string, FileDiff?> _prDiffs = [];

    public async Task<PublishPlan> BuildAsync(bool includeResolved, bool republish, ISet<string> alreadyPublished, CancellationToken ct = default)
    {
        var plan = new PublishPlan { HeadSha = review.HeadSha, BaseSha = review.MergeBaseSha };
        var order = review.Commits.Select((c, i) => (c.Sha, i)).ToDictionary(x => x.Sha, x => x.i);
        var notes = review.Notes
            .OrderBy(n => Review.IsSpecialKey(n.CommitSha) ? int.MaxValue : order.GetValueOrDefault(n.CommitSha, int.MaxValue - 1))
            .ThenBy(n => n.FilePath ?? "")
            .ThenBy(n => n.Line ?? 0);

        foreach (var note in notes)
        {
            if (note.Resolved && !includeResolved)
            {
                plan.SkippedResolved++;
                continue;
            }
            if (!republish && alreadyPublished.Contains(note.Id))
            {
                plan.AlreadyPublished.Add(note.Id);
                continue;
            }
            var (position, reason) = await PlaceAsync(note, ct);
            if (position != null) plan.Inline.Add(new PlannedComment(note, position));
            else plan.General.Add(new GeneralNote(note, reason!));
        }
        return plan;
    }

    private async Task<(InlinePosition? Position, string? Reason)> PlaceAsync(ReviewNote note, CancellationToken ct)
    {
        if (note.FilePath == null) return (null, Review.IsSpecialKey(note.CommitSha) ? "Note on the whole review" : "Note on a whole commit");
        if (note.Line == null) return (null, "Note on a whole file");
        var start = note.Line.Value;
        var end = note.EndLine ?? start;
        var commit = review.FindCommit(note.CommitSha);
        if (!Review.IsSpecialKey(note.CommitSha) && (commit == null || commit.Outdated))
            return (null, "Its commit is no longer part of the pull request");

        int headStart, headEnd;
        if (note.Side == DiffSide.New)
        {
            if (note.CommitSha is Review.RangeKey or Review.SinceKey || note.CommitSha == review.HeadSha)
            {
                (headStart, headEnd) = (start, end);
            }
            else
            {
                var blame = await BlameAsync(note.CommitSha, note.FilePath, ct);
                if (blame == null) return (null, "The file isn't in the pull request's final version");
                var mapped = new List<int>();
                for (var l = start; l <= end; l++)
                {
                    if (!blame.TryGetValue(l, out var b) || b.LastSeenSha != review.HeadSha)
                        return (null, "A later commit changes this code");
                    mapped.Add(b.LastSeenLine);
                }
                if (mapped.Last() - mapped.First() != end - start) return (null, "The lines are no longer next to each other");
                (headStart, headEnd) = (mapped.First(), mapped.Last());
            }
        }
        else
        {
            // Deleted lines can only be anchored on the base side of the PR diff.
            var atBase = note.CommitSha == Review.RangeKey || (commit != null && commit.DiffBase == review.MergeBaseSha);
            if (!atBase) return (null, "Note on a deleted line from a later commit");
            (headStart, headEnd) = (start, end);
        }

        var diff = await PrDiffAsync(note.FilePath, ct);
        if (diff == null || diff.Hunks.Count == 0) return (null, "The file has no changes in the pull request's diff");
        var inHunk = diff.Hunks.Any(h =>
        {
            var lines = note.Side == DiffSide.New
                ? h.Lines.Where(l => l.Type != DiffLineType.Delete).Select(l => l.NewLine!.Value)
                : h.Lines.Where(l => l.Type != DiffLineType.Add).Select(l => l.OldLine!.Value);
            var set = lines.ToHashSet();
            return set.Contains(headStart) && set.Contains(headEnd);
        });
        if (!inHunk) return (null, "The lines are outside the pull request's diff");
        return (new InlinePosition(note.FilePath, note.Side, headStart, headEnd), null);
    }

    private async Task<Dictionary<int, ReverseBlameLine>?> BlameAsync(string sha, string path, CancellationToken ct)
    {
        var key = sha + ":" + path;
        if (_blame.TryGetValue(key, out var cached)) return cached;
        Dictionary<int, ReverseBlameLine>? result;
        try { result = (await repo.ReverseBlameAsync(sha, review.HeadSha, path, ct)).ToDictionary(b => b.Line); }
        catch (GitException) { result = null; }
        return _blame[key] = result;
    }

    /// <summary>The file's diff as the platform shows it: merge-base..head with 3 lines of context.</summary>
    private async Task<FileDiff?> PrDiffAsync(string path, CancellationToken ct)
    {
        if (_prDiffs.TryGetValue(path, out var cached)) return cached;
        FileDiff? diff;
        try { diff = await repo.GetFileDiffAsync(review.MergeBaseSha, review.HeadSha, path, null, 3, ct: ct); }
        catch (GitException) { diff = null; }
        return _prDiffs[path] = diff;
    }
}
