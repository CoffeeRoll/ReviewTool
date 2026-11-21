using System.Text;
using System.Text.Json.Nodes;
using ReviewTool.Core.Git;

namespace ReviewTool.Core.Reviews;

public sealed record PullRequestInput(int Number, string? Remote = null, string? Provider = null);

public sealed record CreateReviewRequest(
    string RepoPath,
    string? BaseRef = null,
    string? HeadRef = null,
    string? Name = null,
    PullRequestInput? PullRequest = null);

public sealed record NoteInput(
    string? CommitSha,
    string? FilePath,
    DiffSide? Side,
    int? Line,
    int? EndLine,
    string? LineText,
    string? Body,
    NoteKind? Kind,
    bool? Resolved);

public sealed record ReviewPatch(string? Name, string? Summary, ReviewStatus? Status, JsonObject? UiState);

/// <summary>A line (in the reviewed commit's version) that a later commit in the review changes or deletes.</summary>
/// <param name="Line">Line number on the new side of the reviewed commit.</param>
/// <param name="CommitSha">The later commit that changes it.</param>
/// <param name="OldLine">The line's number on the old side of <paramref name="CommitSha"/>'s diff.</param>
public sealed record LaterChange(int Line, string CommitSha, int OldLine);

/// <summary>A note whose code is changed by a later commit, and where it lands in that commit's diff (old side).</summary>
public sealed record NoteFollowUp(string NoteId, string CommitSha, string Path, int Line, int EndLine, int ChangedLines, int TotalLines);


public sealed class ReviewNotFoundException(string id) : Exception($"Review '{id}' was not found.");

public sealed class ReviewValidationException(string message) : Exception(message);

/// <summary>Application logic tying git and persistence together.</summary>
public sealed class ReviewService(IReviewStore store, GitRunner git)
{
    private const int MaxCommits = 2000;

    private readonly AnalysisCache<List<MovedBlock>> _movedCache = new();
    private readonly AnalysisCache<List<ReverseBlameLine>> _blameCache = new(2000);

    public IReviewStore Store => store;

    public Task<GitRepository> OpenRepoAsync(string path, CancellationToken ct = default) => GitRepository.OpenAsync(path, git, ct);

    public async Task<IReadOnlyList<ReviewSummary>> ListAsync(CancellationToken ct = default)
        => (await store.ListAsync(ct)).Select(ReviewSummary.From).ToList();

    public async Task<Review> GetAsync(string id, CancellationToken ct = default)
        => await store.GetAsync(id, ct) ?? throw new ReviewNotFoundException(id);

    public async Task<Review> CreateAsync(CreateReviewRequest request, CancellationToken ct = default)
    {
        var repo = await OpenRepoAsync(request.RepoPath, ct);
        var refs = await repo.ListRefsAsync(ct);

        PullRequestRef? pr = null;
        var headRef = request.HeadRef?.Trim();
        if (request.PullRequest is { } prInput)
        {
            var remote = string.IsNullOrWhiteSpace(prInput.Remote) ? "origin" : prInput.Remote.Trim();
            var provider = string.IsNullOrWhiteSpace(prInput.Provider) ? "github" : prInput.Provider.Trim();
            headRef = await repo.FetchPullRequestAsync(remote, prInput.Number, provider, ct);
            pr = new PullRequestRef { Remote = remote, Number = prInput.Number, Provider = provider };
        }
        if (string.IsNullOrWhiteSpace(headRef)) headRef = refs.CurrentBranch ?? "HEAD";

        var baseRef = request.BaseRef?.Trim();
        if (string.IsNullOrWhiteSpace(baseRef))
            baseRef = refs.DefaultBranch ?? throw new ReviewValidationException("Could not determine a default base branch; please specify one.");

        var now = DateTimeOffset.UtcNow;
        var review = new Review
        {
            Id = NewId(),
            Name = string.IsNullOrWhiteSpace(request.Name)
                ? (pr != null ? $"PR #{pr.Number}" : headRef.Replace("refs/reviewtool/", "", StringComparison.Ordinal)) + $" → {baseRef}"
                : request.Name.Trim(),
            RepoPath = repo.Path,
            BaseRef = baseRef,
            HeadRef = headRef,
            PullRequest = pr,
            CreatedAt = now,
            UpdatedAt = now,
        };
        await PopulateCommitsAsync(repo, review, ct);
        if (review.Commits.Count == 0)
            throw new ReviewValidationException($"There are no commits in {baseRef}..{headRef}.");
        await store.SaveAsync(review, ct);
        return review;
    }

    /// <summary>
    /// Resumes the most recent review of the same repository and branches (or pull request), refreshing
    /// it, or creates a new one. Used by the command line so running it twice continues the same review.
    /// </summary>
    public async Task<(Review Review, bool Created)> OpenOrCreateAsync(CreateReviewRequest request, CancellationToken ct = default)
    {
        var repo = await OpenRepoAsync(request.RepoPath, ct);
        var all = (await store.ListAsync(ct)).Where(r => string.Equals(r.RepoPath, repo.Path, StringComparison.Ordinal)).ToList();

        Review? existing;
        if (request.PullRequest is { } pr)
        {
            var remote = string.IsNullOrWhiteSpace(pr.Remote) ? "origin" : pr.Remote.Trim();
            existing = all.FirstOrDefault(r => r.PullRequest?.Number == pr.Number && r.PullRequest.Remote == remote);
        }
        else
        {
            var refs = await repo.ListRefsAsync(ct);
            var head = string.IsNullOrWhiteSpace(request.HeadRef) ? refs.CurrentBranch ?? "HEAD" : request.HeadRef.Trim();
            var baseRef = string.IsNullOrWhiteSpace(request.BaseRef) ? refs.DefaultBranch : request.BaseRef.Trim();
            existing = all.FirstOrDefault(r => r.PullRequest == null && r.HeadRef == head && (baseRef == null || r.BaseRef == baseRef));
        }

        if (existing == null) return (await CreateAsync(request, ct), true);
        try { return (await RefreshAsync(existing.Id, ct), false); }
        catch (GitException) { return (existing, false); } // e.g. offline PR re-fetch; resume what we have
    }

    /// <summary>Re-resolves base/head (re-fetching the PR if needed) and updates the commit list, keeping notes and progress.</summary>
    public async Task<Review> RefreshAsync(string id, CancellationToken ct = default)
    {
        var existing = await GetAsync(id, ct);
        var repo = await OpenRepoAsync(existing.RepoPath, ct);
        if (existing.PullRequest is { } pr)
        {
            try { await repo.FetchPullRequestAsync(pr.Remote, pr.Number, pr.Provider, ct); }
            catch (GitException) { /* offline: fall back to the last fetched head */ }
        }
        return await store.UpdateAsync(id, r => PopulateCommitsAsync(repo, r, ct), ct) ?? throw new ReviewNotFoundException(id);
    }

    private async Task PopulateCommitsAsync(GitRepository repo, Review review, CancellationToken ct)
    {
        var headSha = await repo.ResolveCommitAsync(review.HeadRef, ct);
        var baseSha = await repo.ResolveCommitAsync(review.BaseRef, ct);
        var emptyTree = await repo.GetEmptyTreeAsync(ct);
        var mergeBase = await repo.MergeBaseAsync(baseSha, headSha, ct) ?? emptyTree;

        var commits = await repo.ListCommitsAsync(baseSha, headSha, MaxCommits, ct);
        var previous = review.Commits.ToDictionary(c => c.Sha);
        var oldActive = review.Commits.Where(c => !c.Outdated).ToList();
        var headMoved = review.HeadSha.Length > 0 && review.HeadSha != headSha;

        // Line stats per commit, computed with bounded parallelism.
        var stats = new (int Files, int Add, int Del)[commits.Count];
        await Parallel.ForEachAsync(Enumerable.Range(0, commits.Count),
            new ParallelOptions { MaxDegreeOfParallelism = 6, CancellationToken = ct },
            async (i, token) =>
            {
                var c = commits[i];
                if (previous.TryGetValue(c.Sha, out var known) && known.DiffBase.Length > 0)
                {
                    stats[i] = (known.FileCount, known.Additions, known.Deletions);
                    return;
                }
                var files = await repo.GetChangedFilesAsync(c.Parents.Count > 0 ? c.Parents[0] : emptyTree, c.Sha, token);
                stats[i] = (files.Count, files.Sum(f => f.Additions), files.Sum(f => f.Deletions));
            });

        var updated = new List<ReviewCommit>(commits.Count);
        for (var i = 0; i < commits.Count; i++)
        {
            var c = commits[i];
            updated.Add(new ReviewCommit
            {
                Sha = c.Sha,
                Parents = c.Parents.ToList(),
                DiffBase = c.Parents.Count > 0 ? c.Parents[0] : emptyTree,
                Subject = c.Subject,
                Body = c.Body,
                AuthorName = c.AuthorName,
                AuthorEmail = c.AuthorEmail,
                AuthorDate = c.AuthorDate,
                FileCount = stats[i].Files,
                Additions = stats[i].Add,
                Deletions = stats[i].Del,
                SinceLastReview = !headMoved && previous.TryGetValue(c.Sha, out var p) ? p.SinceLastReview : null,
                PreviousSha = !headMoved && previous.TryGetValue(c.Sha, out var q) ? q.PreviousSha : null,
            });
        }

        if (headMoved && oldActive.Count > 0)
        {
            await MatchPreviousCommitsAsync(repo, review, oldActive, updated, baseSha, headSha, ct);
            review.PreviousHead = new ReviewSnapshot
            {
                HeadSha = review.HeadSha,
                MergeBaseSha = review.MergeBaseSha,
                BaseSha = review.BaseSha,
                RecordedAt = DateTimeOffset.UtcNow,
            };
            review.ReviewedFiles.Remove(Review.SinceKey);
            review.ReviewedLines.Remove(Review.SinceKey);
        }

        // Keep commits that dropped out of the range if they carry notes, so nothing is lost after a force push.
        var current = updated.Select(c => c.Sha).ToHashSet();
        foreach (var old in review.Commits.Where(c => !current.Contains(c.Sha)))
        {
            if (review.Notes.Any(n => n.CommitSha == old.Sha))
            {
                old.Outdated = true;
                updated.Add(old);
            }
            else
            {
                review.ReviewedFiles.Remove(old.Sha);
                review.ReviewedLines.Remove(old.Sha);
            }
        }

        // If the combined diff changed, its "reviewed" state no longer applies.
        if (review.HeadSha != headSha || review.MergeBaseSha != mergeBase)
        {
            review.ReviewedFiles.Remove(Review.RangeKey);
            review.ReviewedLines.Remove(Review.RangeKey);
        }

        review.Commits = updated;
        review.HeadSha = headSha;
        review.BaseSha = baseSha;
        review.MergeBaseSha = mergeBase;
        review.RefreshedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>Returns the (from, to) pair to diff for a commit SHA or the range key.</summary>
    public static (string From, string To) ResolveDiffTarget(Review review, string commitKey)
    {
        if (commitKey == Review.RangeKey) return (review.MergeBaseSha, review.HeadSha);
        if (commitKey == Review.SinceKey)
            return review.PreviousHead is { } prev
                ? (prev.HeadSha, review.HeadSha)
                : throw new ReviewValidationException("This review has no previous head to compare against yet.");
        var commit = review.FindCommit(commitKey) ?? throw new ReviewValidationException($"Commit {commitKey} is not part of this review.");
        return (commit.DiffBase, commit.Sha);
    }

    public async Task<List<ChangedFile>> GetChangedFilesAsync(string id, string commitKey, CancellationToken ct = default, bool ignoreWhitespace = false)
    {
        var review = await GetAsync(id, ct);
        var (from, to) = ResolveDiffTarget(review, commitKey);
        var repo = await OpenRepoAsync(review.RepoPath, ct);
        var files = await repo.GetChangedFilesAsync(from, to, ct, ignoreWhitespace);
        if (commitKey != Review.SinceKey) return files;

        // Between two heads the base may have moved too; only show files the PR itself touches.
        var prPaths = new HashSet<string>(StringComparer.Ordinal);
        void AddAll(IEnumerable<ChangedFile> list)
        {
            foreach (var f in list)
            {
                prPaths.Add(f.Path);
                if (f.OldPath != null) prPaths.Add(f.OldPath);
            }
        }
        AddAll(await repo.GetChangedFilesAsync(review.MergeBaseSha, review.HeadSha, ct));
        try { AddAll(await repo.GetChangedFilesAsync(review.PreviousHead!.MergeBaseSha, review.PreviousHead.HeadSha, ct)); }
        catch (GitException) { /* old objects may have been garbage collected */ }
        return files.Where(f => prPaths.Contains(f.Path) || (f.OldPath != null && prPaths.Contains(f.OldPath))).ToList();
    }

    public async Task<FileDiff> GetFileDiffAsync(string id, string commitKey, string path, string? oldPath, int context, CancellationToken ct = default, bool ignoreWhitespace = false)
    {
        var review = await GetAsync(id, ct);
        var (from, to) = ResolveDiffTarget(review, commitKey);
        var repo = await OpenRepoAsync(review.RepoPath, ct);
        return await repo.GetFileDiffAsync(from, to, path, oldPath, context < 0 ? GitRepository.FullContext : context, ct: ct, ignoreWhitespace: ignoreWhitespace);
    }

    /// <summary>Blocks of code moved (not rewritten) within a commit, across all of its files.</summary>
    public async Task<List<MovedBlock>> GetMovedBlocksAsync(string id, string commitKey, bool ignoreWhitespace, CancellationToken ct = default)
    {
        var review = await GetAsync(id, ct);
        var (from, to) = ResolveDiffTarget(review, commitKey);
        var repo = await OpenRepoAsync(review.RepoPath, ct);
        return await _movedCache.GetOrAdd($"{repo.Path}|{from}|{to}|{ignoreWhitespace}", async () =>
            MovedCodeDetector.Detect(await repo.GetZeroContextDiffAsync(from, to, ignoreWhitespace, CancellationToken.None)));
    }

    /// <summary>Lines of <paramref name="path"/> (as of the commit) that a later commit in the review changes or deletes.</summary>
    public async Task<List<LaterChange>> GetLaterChangesAsync(string id, string commitKey, string path, CancellationToken ct = default)
    {
        var review = await GetAsync(id, ct);
        var repo = await OpenRepoAsync(review.RepoPath, ct);
        return await GetLaterChangesAsync(review, repo, commitKey, path);
    }

    private async Task<List<LaterChange>> GetLaterChangesAsync(Review review, GitRepository repo, string commitKey, string path)
    {
        var active = review.Commits.Where(c => !c.Outdated).ToList();
        var index = active.FindIndex(c => c.Sha == commitKey);
        if (index < 0 || index == active.Count - 1) return [];

        List<ReverseBlameLine> blame;
        try
        {
            blame = await _blameCache.GetOrAdd($"{repo.Path}|{commitKey}|{review.HeadSha}|{path}",
                () => repo.ReverseBlameAsync(commitKey, review.HeadSha, path, CancellationToken.None));
        }
        catch (GitException)
        {
            return []; // e.g. the file doesn't exist at this commit
        }

        // A line last seen in commit X is changed by the commit whose parent is X.
        var byParent = new Dictionary<string, ReviewCommit>(StringComparer.Ordinal);
        foreach (var c in active.Skip(index + 1)) byParent.TryAdd(c.DiffBase, c);
        var result = new List<LaterChange>();
        foreach (var line in blame)
        {
            if (line.LastSeenSha == review.HeadSha) continue;
            if (byParent.TryGetValue(line.LastSeenSha, out var child)) result.Add(new LaterChange(line.Line, child.Sha, line.LastSeenLine));
        }
        return result;
    }

    /// <summary>Open line notes whose code is changed by a later commit, so the note can be shown again there.</summary>
    public async Task<List<NoteFollowUp>> GetNoteFollowUpsAsync(string id, CancellationToken ct = default)
    {
        var review = await GetAsync(id, ct);
        var order = review.Commits.Where(c => !c.Outdated).Select((c, i) => (c.Sha, i)).ToDictionary(x => x.Sha, x => x.i);
        var candidates = review.Notes
            .Where(n => !n.Resolved && n.FilePath != null && n.Line != null && n.Side == DiffSide.New && order.ContainsKey(n.CommitSha))
            .ToList();
        if (candidates.Count == 0) return [];

        var repo = await OpenRepoAsync(review.RepoPath, ct);
        var result = new List<NoteFollowUp>();
        foreach (var group in candidates.GroupBy(n => (n.CommitSha, n.FilePath!)))
        {
            var changes = (await GetLaterChangesAsync(review, repo, group.Key.CommitSha, group.Key.Item2)).ToDictionary(c => c.Line);
            if (changes.Count == 0) continue;
            foreach (var note in group)
            {
                var lines = Enumerable.Range(note.Line!.Value, (note.EndLine ?? note.Line.Value) - note.Line.Value + 1).ToList();
                var changed = lines.Select(l => changes.GetValueOrDefault(l)).OfType<LaterChange>().ToList();
                if (changed.Count == 0) continue;
                var first = changed.MinBy(c => order.GetValueOrDefault(c.CommitSha, int.MaxValue))!;
                var there = changed.Where(c => c.CommitSha == first.CommitSha).Select(c => c.OldLine).ToList();
                result.Add(new NoteFollowUp(note.Id, first.CommitSha, note.FilePath!, there.Min(), there.Max(), changed.Count, lines.Count));
            }
        }
        return result;
    }

    /// <summary>Commits in the review that change a file (following renames), oldest first.</summary>
    public async Task<List<FileHistoryEntry>> GetFileHistoryAsync(string id, string path, CancellationToken ct = default)
    {
        var review = await GetAsync(id, ct);
        var repo = await OpenRepoAsync(review.RepoPath, ct);
        var active = review.Commits.Where(c => !c.Outdated).Select(c => c.Sha).ToHashSet(StringComparer.Ordinal);
        var history = await repo.GetFileHistoryAsync(review.BaseSha, review.HeadSha, path, ct);
        return history.Where(h => active.Contains(h.Sha)).ToList();
    }

    public async Task<List<string>> ListTreeAsync(string id, string commitKey, CancellationToken ct = default)
    {
        var review = await GetAsync(id, ct);
        var (_, to) = ResolveDiffTarget(review, commitKey);
        var repo = await OpenRepoAsync(review.RepoPath, ct);
        return await repo.ListTreeAsync(to, ct);
    }

    public async Task<BlobContent> GetBlobAsync(string id, string commitKey, string path, DiffSide side, CancellationToken ct = default)
    {
        var review = await GetAsync(id, ct);
        var (from, to) = ResolveDiffTarget(review, commitKey);
        var repo = await OpenRepoAsync(review.RepoPath, ct);
        return await repo.GetBlobAsync(side == DiffSide.Old ? from : to, path, ct: ct);
    }

    public async Task<Review> PatchAsync(string id, ReviewPatch patch, CancellationToken ct = default)
        => await store.UpdateAsync(id, r =>
        {
            if (patch.Name != null && patch.Name.Trim().Length > 0) r.Name = patch.Name.Trim();
            if (patch.Summary != null) r.Summary = patch.Summary;
            if (patch.Status != null) r.Status = patch.Status.Value;
            if (patch.UiState != null) r.UiState = patch.UiState;
            return Task.CompletedTask;
        }, ct) ?? throw new ReviewNotFoundException(id);

    public async Task<Review> SetFilesReviewedAsync(string id, string commitKey, IReadOnlyCollection<string> paths, bool reviewed, CancellationToken ct = default)
        => await store.UpdateAsync(id, r =>
        {
            EnsureKey(r, commitKey);
            if (!r.ReviewedFiles.TryGetValue(commitKey, out var list)) r.ReviewedFiles[commitKey] = list = [];
            var set = new HashSet<string>(list, StringComparer.Ordinal);
            foreach (var p in paths)
            {
                if (reviewed) set.Add(p); else set.Remove(p);
                // Un-reviewing a file also forgets which of its hunks were reviewed.
                if (!reviewed && r.ReviewedLines.TryGetValue(commitKey, out var byPath)) byPath.Remove(p);
            }
            if (r.ReviewedLines.TryGetValue(commitKey, out var lines) && lines.Count == 0) r.ReviewedLines.Remove(commitKey);
            r.ReviewedFiles[commitKey] = set.Order(StringComparer.Ordinal).ToList();
            if (r.ReviewedFiles[commitKey].Count == 0) r.ReviewedFiles.Remove(commitKey);
            return Task.CompletedTask;
        }, ct) ?? throw new ReviewNotFoundException(id);

    /// <summary>Marks changed-line ranges of one file as reviewed (hunk-level progress).</summary>
    public async Task<Review> SetLinesReviewedAsync(string id, string commitKey, string path, IReadOnlyCollection<string> ranges, bool reviewed, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(path)) throw new ReviewValidationException("A file path is required.");
        LineRangeSet.Parse(ranges); // validate before taking the lock
        return await store.UpdateAsync(id, r =>
        {
            EnsureKey(r, commitKey);
            if (!r.ReviewedLines.TryGetValue(commitKey, out var byPath)) r.ReviewedLines[commitKey] = byPath = [];
            var set = LineRangeSet.Parse(byPath.GetValueOrDefault(path));
            if (reviewed) set.Add(ranges); else set.Remove(ranges);
            if (set.Count == 0) byPath.Remove(path); else byPath[path] = set.ToRanges();
            if (byPath.Count == 0) r.ReviewedLines.Remove(commitKey);
            return Task.CompletedTask;
        }, ct) ?? throw new ReviewNotFoundException(id);
    }

    private static void EnsureKey(Review r, string commitKey)
    {
        if (commitKey == Review.RangeKey || (commitKey == Review.SinceKey && r.PreviousHead != null)) return;
        if (r.FindCommit(commitKey) == null) throw new ReviewValidationException($"Commit {commitKey} is not part of this review.");
    }

    public async Task<ReviewNote> AddNoteAsync(string id, NoteInput input, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(input.Body)) throw new ReviewValidationException("A note needs some text.");
        if (string.IsNullOrWhiteSpace(input.CommitSha)) throw new ReviewValidationException("A note must reference a commit.");
        if (input.Line is <= 0 || input.EndLine is <= 0 || (input.Line != null && input.EndLine < input.Line))
            throw new ReviewValidationException("Invalid line range.");
        var now = DateTimeOffset.UtcNow;
        var note = new ReviewNote
        {
            Id = NewId(),
            CommitSha = input.CommitSha.Trim(),
            FilePath = string.IsNullOrWhiteSpace(input.FilePath) ? null : input.FilePath,
            Side = input.Side ?? DiffSide.New,
            Line = input.Line,
            EndLine = input.EndLine == input.Line ? null : input.EndLine,
            LineText = input.LineText,
            Body = input.Body.Trim(),
            Kind = input.Kind ?? NoteKind.Comment,
            Resolved = input.Resolved ?? false,
            CreatedAt = now,
            UpdatedAt = now,
        };
        await store.UpdateAsync(id, r =>
        {
            EnsureKey(r, note.CommitSha);
            r.Notes.Add(note);
            return Task.CompletedTask;
        }, ct);
        return note;
    }

    public async Task<ReviewNote> UpdateNoteAsync(string id, string noteId, NoteInput input, CancellationToken ct = default)
    {
        ReviewNote? updated = null;
        await store.UpdateAsync(id, r =>
        {
            var note = r.Notes.FirstOrDefault(n => n.Id == noteId) ?? throw new ReviewValidationException("Note not found.");
            if (input.Body != null)
            {
                if (string.IsNullOrWhiteSpace(input.Body)) throw new ReviewValidationException("A note needs some text.");
                note.Body = input.Body.Trim();
            }
            if (input.Kind != null) note.Kind = input.Kind.Value;
            if (input.Resolved != null) note.Resolved = input.Resolved.Value;
            note.UpdatedAt = DateTimeOffset.UtcNow;
            updated = note;
            return Task.CompletedTask;
        }, ct);
        return updated ?? throw new ReviewNotFoundException(id);
    }

    public async Task DeleteNoteAsync(string id, string noteId, CancellationToken ct = default)
    {
        var result = await store.UpdateAsync(id, r =>
        {
            r.Notes.RemoveAll(n => n.Id == noteId);
            return Task.CompletedTask;
        }, ct);
        if (result == null) throw new ReviewNotFoundException(id);
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken ct = default) => await store.DeleteAsync(id, ct);

    /// <summary>
    /// After the head moved (push, force push, rebase): works out which new commits correspond to
    /// previously reviewed ones, and carries reviewed state and notes across where it's safe.
    /// </summary>
    private static async Task MatchPreviousCommitsAsync(GitRepository repo, Review review, List<ReviewCommit> oldActive,
        List<ReviewCommit> updated, string baseSha, string headSha, CancellationToken ct)
    {
        Dictionary<string, string> oldIds = [], newIds = [];
        try { oldIds = await repo.GetPatchIdsAsync(review.BaseSha, review.HeadSha, ct); } catch (GitException) { /* objects gone */ }
        try { newIds = await repo.GetPatchIdsAsync(baseSha, headSha, ct); } catch (GitException) { }

        var unmatched = new List<ReviewCommit>(oldActive);
        var oldBySha = oldActive.ToDictionary(c => c.Sha);
        foreach (var commit in updated)
        {
            ReviewCommit? match = null;
            var kind = CommitChange.New;
            if (oldBySha.TryGetValue(commit.Sha, out var same))
            {
                match = same;
                kind = CommitChange.Unchanged;
            }
            else if (newIds.TryGetValue(commit.Sha, out var pid)
                     && unmatched.FirstOrDefault(o => oldIds.GetValueOrDefault(o.Sha) == pid) is { } byPatch)
            {
                match = byPatch;
                kind = CommitChange.Unchanged;
            }
            else if (unmatched.FirstOrDefault(o => o.Subject == commit.Subject && o.Subject.Length > 0) is { } bySubject)
            {
                match = bySubject;
                kind = CommitChange.Modified;
            }
            commit.SinceLastReview = kind;
            commit.PreviousSha = match?.Sha;
            if (match != null) unmatched.Remove(match);
            if (match == null || match.Sha == commit.Sha) continue;

            if (kind == CommitChange.Unchanged)
            {
                // Same patch: progress carries over as-is.
                if (review.ReviewedFiles.Remove(match.Sha, out var files)) review.ReviewedFiles[commit.Sha] = files;
                if (review.ReviewedLines.Remove(match.Sha, out var lines)) review.ReviewedLines[commit.Sha] = lines;
            }
            await MoveNotesAsync(repo, review, match.Sha, commit, kind, ct);
        }
    }

    /// <summary>
    /// Moves notes from an old commit to its new counterpart when the code they point at can still be
    /// found (by the saved line text). Notes that can't be placed stay on the old, outdated commit.
    /// </summary>
    private static async Task MoveNotesAsync(GitRepository repo, Review review, string oldSha, ReviewCommit target, CommitChange kind, CancellationToken ct)
    {
        var blobs = new Dictionary<string, string[]?>();
        async Task<string[]?> Lines(string rev, string path)
        {
            var key = rev + ":" + path;
            if (blobs.TryGetValue(key, out var cached)) return cached;
            string[]? lines = null;
            try
            {
                var blob = await repo.GetBlobAsync(rev, path, ct: ct);
                lines = blob.Text?.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
            }
            catch (GitException) { }
            return blobs[key] = lines;
        }

        foreach (var note in review.Notes.Where(n => n.CommitSha == oldSha))
        {
            if (note.FilePath == null || note.Line == null)
            {
                note.CommitSha = target.Sha; // commit- and file-level notes always follow
                continue;
            }
            if (string.IsNullOrWhiteSpace(note.LineText))
            {
                if (kind == CommitChange.Unchanged) note.CommitSha = target.Sha;
                continue;
            }
            var lines = await Lines(note.Side == DiffSide.Old ? target.DiffBase : target.Sha, note.FilePath);
            if (lines == null) continue;
            var found = FindLine(lines, note.LineText.Split('\n')[0], note.Line.Value);
            if (found == null) continue;
            var shift = found.Value - note.Line.Value;
            note.Line += shift;
            if (note.EndLine != null) note.EndLine += shift;
            note.CommitSha = target.Sha;
        }
    }

    internal static int? FindLine(string[] lines, string text, int expected, int window = 500)
    {
        var target = text.Trim();
        bool Match(int line) => line >= 1 && line <= lines.Length && lines[line - 1].Trim() == target;
        if (Match(expected)) return expected;
        for (var d = 1; d <= window; d++)
        {
            if (Match(expected - d)) return expected - d;
            if (Match(expected + d)) return expected + d;
        }
        return null;
    }

    /// <summary>Renders all notes as Markdown, ready to paste into a PR conversation.</summary>
    public static string ExportMarkdown(Review review, bool includeResolved = true)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Review: {review.Name}");
        sb.AppendLine();
        sb.AppendLine($"- **Range:** `{review.BaseRef}` ({Short(review.MergeBaseSha)}) → `{review.HeadRef}` ({Short(review.HeadSha)})");
        if (review.PullRequest is { } pr) sb.AppendLine($"- **Pull request:** #{pr.Number} ({pr.Remote})");
        sb.AppendLine($"- **Status:** {Humanize(review.Status.ToString())}");
        var summary = ReviewSummary.From(review);
        sb.AppendLine($"- **Progress:** {summary.ReviewedCommitCount}/{summary.CommitCount} commits, {summary.ReviewedFileCount}/{summary.FileCount} file changes reviewed");
        sb.AppendLine();
        if (!string.IsNullOrWhiteSpace(review.Summary))
        {
            sb.AppendLine("## Summary");
            sb.AppendLine();
            sb.AppendLine(review.Summary.Trim());
            sb.AppendLine();
        }

        var notes = review.Notes.Where(n => includeResolved || !n.Resolved).ToList();
        if (notes.Count == 0)
        {
            sb.AppendLine("_No notes._");
            return sb.ToString();
        }

        sb.AppendLine("## Notes");
        var order = review.Commits.Select((c, i) => (c.Sha, i)).ToDictionary(x => x.Sha, x => x.i);
        var groups = notes
            .GroupBy(n => n.CommitSha)
            .OrderBy(g => Review.IsSpecialKey(g.Key) ? int.MaxValue : order.GetValueOrDefault(g.Key, int.MaxValue - 1));
        foreach (var group in groups)
        {
            sb.AppendLine();
            if (group.Key == Review.RangeKey) sb.AppendLine("### All changes");
            else if (group.Key == Review.SinceKey) sb.AppendLine("### Changes since last review");
            else
            {
                var c = review.FindCommit(group.Key);
                sb.AppendLine($"### `{Short(group.Key)}` {c?.Subject ?? ""}{(c?.Outdated == true ? " _(no longer in range)_" : "")}");
            }
            foreach (var n in group.OrderBy(n => n.FilePath ?? "").ThenBy(n => n.Line ?? 0))
            {
                sb.AppendLine();
                var location = n.FilePath == null ? "commit"
                    : n.Line == null ? $"`{n.FilePath}`"
                    : $"`{n.FilePath}:{n.Line}{(n.EndLine != null ? $"-{n.EndLine}" : "")}`{(n.Side == DiffSide.Old ? " (old)" : "")}";
                sb.AppendLine($"- **{n.Kind}** {location}{(n.Resolved ? " ✅ resolved" : "")}");
                if (!string.IsNullOrWhiteSpace(n.LineText))
                {
                    sb.AppendLine("  ```");
                    foreach (var line in n.LineText.TrimEnd('\n').Split('\n')) sb.AppendLine("  " + line);
                    sb.AppendLine("  ```");
                }
                foreach (var line in n.Body.Split('\n')) sb.AppendLine("  " + line);
            }
        }
        return sb.ToString();
    }

    private static string Short(string sha) => sha.Length > 10 ? sha[..10] : sha;

    private static string Humanize(string s)
        => string.Concat(s.Select((c, i) => i > 0 && char.IsUpper(c) ? " " + char.ToLowerInvariant(c) : c.ToString()));

    private static string NewId() => Guid.NewGuid().ToString("N")[..12];
}
