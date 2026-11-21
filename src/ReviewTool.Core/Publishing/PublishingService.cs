using ReviewTool.Core.Git;
using ReviewTool.Core.Reviews;

namespace ReviewTool.Core.Publishing;

/// <summary>
/// Posts a review to the platform hosting the repository. Platform specifics live in
/// <see cref="IReviewPublisher"/> implementations; this class handles target discovery,
/// credentials, planning, previews and bookkeeping for all of them.
/// </summary>
public sealed class PublishingService(ReviewService reviews, IEnumerable<IReviewPublisher> publishers, ICredentialResolver credentials)
{
    private readonly List<IReviewPublisher> _publishers = publishers.ToList();

    /// <summary>Hosted repositories (one per supported remote) this review could be posted to.</summary>
    public async Task<List<PublishTargetInfo>> GetTargetsAsync(string reviewId, CancellationToken ct = default)
    {
        var review = await reviews.GetAsync(reviewId, ct);
        var repo = await reviews.OpenRepoAsync(review.RepoPath, ct);
        var targets = new List<PublishTargetInfo>();
        foreach (var (remote, publisher) in await FindRemotesAsync(review, repo, ct))
        {
            var credential = await credentials.ResolveAsync(publisher, remote, ct);
            targets.Add(Describe(remote, publisher, credential));
        }
        return targets;
    }

    public async Task<PublishPreview> PreviewAsync(string reviewId, PublishRequest request, CancellationToken ct = default)
        => (await PrepareAsync(reviewId, request, ct)).Preview;

    public async Task<PublishResult> PublishAsync(string reviewId, PublishRequest request, CancellationToken ct = default)
    {
        var p = await PrepareAsync(reviewId, request, ct);
        if (p.Preview.Blockers.Count > 0) throw new PublishException(p.Preview.Blockers[0]);
        if (p.Preview.NothingNew)
            throw new PublishException("There's nothing new to post: no new notes, and the summary was already posted (or is empty).");

        var context = new PublishContext(
            p.Remote,
            p.Credential!.Token,
            p.PullRequest!,
            p.Plan,
            p.Preview.Verdict,
            p.Preview.Body,
            p.Plan.Inline.ToDictionary(c => c.Note.Id, c => PublishFormatter.InlineBody(c.Note)),
            PublishFormatter.ReviewBody(p.Review, p.Plan.General.Concat(p.Plan.Inline.Select(c => new GeneralNote(c.Note, "")))));
        var outcome = await p.Publisher.PublishAsync(context, ct);

        var record = new PublicationRecord
        {
            Provider = p.Publisher.Id,
            Host = p.Remote.Host,
            Repository = p.Remote.FullName,
            PullRequestNumber = p.PullRequest!.Number,
            Url = outcome.Url,
            ExternalId = outcome.ExternalId,
            HeadSha = p.Review.HeadSha,
            Verdict = p.Preview.Verdict.ToString(),
            NoteIds = p.Plan.Inline.Select(c => c.Note.Id).Concat(p.Plan.General.Select(g => g.Note.Id)).ToList(),
            InlineComments = outcome.InlineCount,
            PublishedAt = DateTimeOffset.UtcNow,
        };
        await reviews.Store.UpdateAsync(reviewId, r =>
        {
            r.Publications.Add(record);
            return Task.CompletedTask;
        }, ct);
        return new PublishResult(outcome.Url, outcome.InlineCount, record.NoteIds.Count - outcome.InlineCount, outcome.Warnings, record);
    }

    private sealed record Prepared(
        Review Review,
        RemoteRepository Remote,
        IReviewPublisher Publisher,
        ResolvedCredential? Credential,
        PullRequestInfo? PullRequest,
        PublishPlan Plan,
        PublishPreview Preview);

    private async Task<Prepared> PrepareAsync(string reviewId, PublishRequest request, CancellationToken ct)
    {
        var review = await reviews.GetAsync(reviewId, ct);
        var repo = await reviews.OpenRepoAsync(review.RepoPath, ct);
        var candidates = await FindRemotesAsync(review, repo, ct);
        if (candidates.Count == 0)
            throw new PublishException($"None of this repository's remotes is on a supported platform ({string.Join(", ", _publishers.Select(p => p.DisplayName))}).");
        var (remote, publisher) = candidates.FirstOrDefault(c =>
            (request.Provider == null || c.Publisher.Id == request.Provider) && (request.Remote == null || c.Remote.RemoteName == request.Remote));
        if (remote == null) throw new PublishException("The requested remote/platform wasn't found for this repository.");

        var credential = await credentials.ResolveAsync(publisher, remote, ct);
        var warnings = new List<string>();
        var blockers = new List<string>();
        if (credential == null) blockers.Add($"Not signed in to {publisher.DisplayName}. {publisher.GetCredentialHints(remote.Host).HelpText}");

        // Find the pull request: explicit number, the one the review was created from, or by branch.
        PullRequestInfo? pr = null;
        var number = request.PullRequestNumber ?? (review.PullRequest?.Remote == remote.RemoteName ? review.PullRequest.Number : null);
        if (credential != null)
        {
            try
            {
                if (number is { } n) pr = await publisher.GetPullRequestAsync(remote, n, credential.Token, ct);
                else if (BranchName(review.HeadRef, remote.RemoteName) is { } branch)
                    pr = await publisher.FindPullRequestAsync(remote, branch, credential.Token, ct);
                if (pr == null) blockers.Add($"No open pull request was found for '{BranchName(review.HeadRef, remote.RemoteName) ?? review.HeadRef}'. Enter its number.");
            }
            catch (PublishException ex)
            {
                blockers.Add(ex.Message);
            }
        }

        if (pr != null)
        {
            if (!string.Equals(pr.HeadSha, review.HeadSha, StringComparison.OrdinalIgnoreCase))
                blockers.Add($"The pull request is at {Short(pr.HeadSha)} but this review is at {Short(review.HeadSha)}. "
                    + (review.PullRequest != null ? "Refresh the review first." : "Push your branch (or refresh the review) first, so comments land on the right lines."));
            if (!string.Equals(pr.State, "open", StringComparison.OrdinalIgnoreCase))
                warnings.Add($"Pull request #{pr.Number} is {pr.State}.");
        }

        var published = review.Publications
            .Where(p => p.Provider == publisher.Id && p.Repository == remote.FullName && (pr == null || p.PullRequestNumber == pr.Number))
            .SelectMany(p => p.NoteIds)
            .ToHashSet(StringComparer.Ordinal);
        var plan = await new PublishPlanner(repo, review).BuildAsync(request.IncludeResolved, request.Republish, published, ct);
        var verdict = request.Verdict ?? review.Status switch
        {
            ReviewStatus.Approved => PublishVerdict.Approve,
            ReviewStatus.ChangesRequested => PublishVerdict.RequestChanges,
            _ => PublishVerdict.Comment,
        };
        if (plan.AlreadyPublished.Count > 0)
            warnings.Add($"{plan.AlreadyPublished.Count} note{(plan.AlreadyPublished.Count == 1 ? " was" : "s were")} posted before and will be skipped.");

        var postedBefore = review.Publications.Any(x => x.Provider == publisher.Id && x.Repository == remote.FullName && (pr == null || x.PullRequestNumber == pr.Number));
        var nothingNew = plan.Inline.Count == 0 && plan.General.Count == 0
            && (string.IsNullOrWhiteSpace(review.Summary) || postedBefore) && !(verdict == PublishVerdict.Approve && !postedBefore);

        var preview = new PublishPreview(
            Describe(remote, publisher, credential),
            pr,
            verdict,
            plan.Inline.Select(c => new PreviewComment(c.Note.Id, c.Note.Kind, c.Position.Path, c.Position.Side, c.Position.StartLine, c.Position.EndLine, PublishFormatter.InlineBody(c.Note))).ToList(),
            plan.General.Select(g => new PreviewGeneral(g.Note.Id, g.Note.Kind, g.Note.FilePath, g.Note.Line, g.Reason)).ToList(),
            plan.AlreadyPublished.Count,
            plan.SkippedResolved,
            PublishFormatter.ReviewBody(review, plan.General),
            warnings,
            blockers,
            nothingNew);
        return new Prepared(review, remote, publisher, credential, pr, plan, preview);
    }

    private async Task<List<(RemoteRepository Remote, IReviewPublisher Publisher)>> FindRemotesAsync(Review review, GitRepository repo, CancellationToken ct)
    {
        var info = await repo.GetInfoAsync(ct);
        // Prefer the remote the PR came from, then origin/upstream, then the rest.
        var ordered = info.Remotes
            .OrderBy(r => r == review.PullRequest?.Remote ? 0 : r == "origin" ? 1 : r == "upstream" ? 2 : 3)
            .ThenBy(r => r, StringComparer.Ordinal);
        var result = new List<(RemoteRepository, IReviewPublisher)>();
        foreach (var name in ordered)
        {
            var url = await repo.GetRemoteUrlAsync(name, ct);
            if (url == null || RemoteRepository.TryParse(name, url) is not { } remote) continue;
            if (_publishers.FirstOrDefault(p => p.CanHandle(remote)) is not { } publisher) continue;
            if (result.Any(x => x.Item1.Host == remote.Host && x.Item1.FullName == remote.FullName)) continue;
            result.Add((remote, publisher));
        }
        return result;
    }

    private static PublishTargetInfo Describe(RemoteRepository remote, IReviewPublisher publisher, ResolvedCredential? credential) => new(
        publisher.Id,
        publisher.DisplayName,
        remote.RemoteName,
        remote.Host,
        remote.FullName,
        publisher.WebUrl(remote),
        credential != null,
        credential?.Source,
        credential == null ? publisher.GetCredentialHints(remote.Host).HelpText : null);

    /// <summary>The branch name on the remote for a local ref like "origin/feature/x" or "feature/x".</summary>
    internal static string? BranchName(string headRef, string remoteName)
    {
        var name = headRef;
        if (name.StartsWith("refs/reviewtool/", StringComparison.Ordinal)) return null;
        if (name.StartsWith("refs/heads/", StringComparison.Ordinal)) name = name["refs/heads/".Length..];
        else if (name.StartsWith("refs/remotes/", StringComparison.Ordinal)) name = name["refs/remotes/".Length..];
        if (name.StartsWith(remoteName + "/", StringComparison.Ordinal)) name = name[(remoteName.Length + 1)..];
        return name is "HEAD" or "" ? null : name;
    }

    private static string Short(string sha) => sha.Length > 7 ? sha[..7] : sha;
}
