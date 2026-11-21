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

public sealed class ReviewNotFoundException(string id) : Exception($"Review '{id}' was not found.");

public sealed class ReviewValidationException(string message) : Exception(message);

/// <summary>Application logic tying git and persistence together.</summary>
public sealed class ReviewService(IReviewStore store, GitRunner git)
{
    private const int MaxCommits = 2000;

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
            });
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
            else review.ReviewedFiles.Remove(old.Sha);
        }

        // If the combined diff changed, its "reviewed" state no longer applies.
        if (review.HeadSha != headSha || review.MergeBaseSha != mergeBase) review.ReviewedFiles.Remove(Review.RangeKey);

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
        var commit = review.FindCommit(commitKey) ?? throw new ReviewValidationException($"Commit {commitKey} is not part of this review.");
        return (commit.DiffBase, commit.Sha);
    }

    public async Task<List<ChangedFile>> GetChangedFilesAsync(string id, string commitKey, CancellationToken ct = default)
    {
        var review = await GetAsync(id, ct);
        var (from, to) = ResolveDiffTarget(review, commitKey);
        var repo = await OpenRepoAsync(review.RepoPath, ct);
        return await repo.GetChangedFilesAsync(from, to, ct);
    }

    public async Task<FileDiff> GetFileDiffAsync(string id, string commitKey, string path, string? oldPath, int context, CancellationToken ct = default)
    {
        var review = await GetAsync(id, ct);
        var (from, to) = ResolveDiffTarget(review, commitKey);
        var repo = await OpenRepoAsync(review.RepoPath, ct);
        return await repo.GetFileDiffAsync(from, to, path, oldPath, context < 0 ? GitRepository.FullContext : context, ct: ct);
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
            if (commitKey != Review.RangeKey && r.FindCommit(commitKey) == null)
                throw new ReviewValidationException($"Commit {commitKey} is not part of this review.");
            if (!r.ReviewedFiles.TryGetValue(commitKey, out var list)) r.ReviewedFiles[commitKey] = list = [];
            var set = new HashSet<string>(list, StringComparer.Ordinal);
            foreach (var p in paths)
            {
                if (reviewed) set.Add(p); else set.Remove(p);
            }
            r.ReviewedFiles[commitKey] = set.Order(StringComparer.Ordinal).ToList();
            if (r.ReviewedFiles[commitKey].Count == 0) r.ReviewedFiles.Remove(commitKey);
            return Task.CompletedTask;
        }, ct) ?? throw new ReviewNotFoundException(id);

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
            if (note.CommitSha != Review.RangeKey && r.FindCommit(note.CommitSha) == null)
                throw new ReviewValidationException($"Commit {note.CommitSha} is not part of this review.");
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
            .OrderBy(g => g.Key == Review.RangeKey ? int.MaxValue : order.GetValueOrDefault(g.Key, int.MaxValue - 1));
        foreach (var group in groups)
        {
            sb.AppendLine();
            if (group.Key == Review.RangeKey) sb.AppendLine("### All changes");
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
