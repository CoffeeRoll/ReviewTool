using System.Collections.Concurrent;
using ReviewTool.Core.Git;
using ReviewTool.Core.Reviews;

namespace ReviewTool.Core.Publishing;

/// <summary>
/// Finds pull requests to review: open PRs from the hosting platform when signed in, otherwise from
/// the remote's git refs. Also reports whether the PRs behind existing reviews are still open.
/// </summary>
public sealed class PullRequestService(ReviewService reviews, IEnumerable<IReviewPublisher> publishers, ICredentialResolver credentials)
{
    private readonly List<IReviewPublisher> _publishers = publishers.ToList();
    private readonly ConcurrentDictionary<string, (string? State, DateTimeOffset At)> _stateCache = new();
    private static readonly TimeSpan StateCacheTime = TimeSpan.FromMinutes(2);

    public async Task<OpenPullRequestList> ListOpenAsync(string repoPath, string? remoteName = null, CancellationToken ct = default)
    {
        var repo = await reviews.OpenRepoAsync(repoPath, ct);
        var info = await repo.GetInfoAsync(ct);
        if (remoteName != null && !info.Remotes.Contains(remoteName)) throw new ReviewValidationException($"Unknown remote '{remoteName}'.");

        var hosted = await PublishingService.FindRemotesAsync(repo, _publishers, remoteName, ct);
        var target = hosted.FirstOrDefault(h => remoteName == null || h.Remote.RemoteName == remoteName);
        string? note = null;

        // 1. Ask the platform (titles, authors, only open PRs).
        if (target.Remote != null)
        {
            var credential = await credentials.ResolveAsync(target.Publisher, target.Remote, ct);
            if (credential != null)
            {
                try
                {
                    var prs = await target.Publisher.ListOpenPullRequestsAsync(target.Remote, credential.Token, ct);
                    var entries = prs.Select(p => new OpenPullRequestEntry(p.Number, p.Title, p.Author, p.Url, p.HeadRef, p.BaseRef, p.CreatedAt, p.UpdatedAt, p.Draft, null, 0, 0)).ToList();
                    return new OpenPullRequestList(target.Publisher.Id, target.Publisher.DisplayName, target.Remote.RemoteName, target.Remote.FullName,
                        target.Publisher.WebUrl(target.Remote), null, await AnnotateAsync(repo, target.Remote.RemoteName, entries, ct));
                }
                catch (PublishException ex)
                {
                    note = $"Couldn't list pull requests from {target.Publisher.DisplayName} ({ex.Message}) — showing what git can see instead.";
                }
            }
            else
            {
                note = $"Not signed in to {target.Publisher.DisplayName}, so only PR numbers are shown. {target.Publisher.GetCredentialHints(target.Remote.Host).HelpText}";
            }
        }

        // 2. Fall back to the remote's pull request refs (numbers only).
        var gitRemote = remoteName ?? target.Remote?.RemoteName ?? (info.Remotes.Contains("origin") ? "origin" : info.Remotes.FirstOrDefault());
        if (gitRemote == null) return new OpenPullRequestList("git", "git", null, null, null, "This repository has no remotes, so there are no pull requests to list.", []);

        List<RemotePullRef> refs;
        try
        {
            refs = await repo.ListRemotePullRefsAsync(gitRemote, ct);
        }
        catch (GitException ex)
        {
            return new OpenPullRequestList("git", "git", gitRemote, target.Remote?.FullName, null, Join(note, $"Couldn't reach '{gitRemote}': {ex.Message}"), []);
        }

        // GitHub keeps refs/pull/N/merge only while a PR is open; elsewhere we can't tell open from merged.
        var openNumbers = refs.Where(r => r.Kind == "merge").Select(r => r.Number).ToHashSet();
        var numbers = openNumbers.Count > 0 ? openNumbers : refs.Select(r => r.Number).ToHashSet();
        if (openNumbers.Count == 0 && numbers.Count > 0)
            note = Join(note, "The remote doesn't say which pull requests are still open, so merged and closed ones may be listed too.");
        var fallback = numbers.OrderByDescending(n => n).Select(n => new OpenPullRequestEntry(n, null, null, null, null, null, null, null, false, null, 0, 0)).ToList();
        return new OpenPullRequestList("git", "git", gitRemote, target.Remote?.FullName, target.Remote != null ? target.Publisher.WebUrl(target.Remote) : null,
            note, await AnnotateAsync(repo, gitRemote, fallback, ct));
    }

    /// <summary>Links pull requests to reviews already started for them (by PR number, or by branch).</summary>
    private async Task<List<OpenPullRequestEntry>> AnnotateAsync(GitRepository repo, string remote, List<OpenPullRequestEntry> entries, CancellationToken ct)
    {
        var existing = (await reviews.Store.ListAsync(ct)).Where(r => string.Equals(r.RepoPath, repo.Path, StringComparison.Ordinal)).ToList();
        return entries.Select(e =>
        {
            var match = existing.FirstOrDefault(r => r.PullRequest?.Number == e.Number && r.PullRequest.Remote == remote)
                ?? (e.HeadRef is { Length: > 0 } head
                    ? existing.FirstOrDefault(r => r.PullRequest == null && (r.HeadRef == head || r.HeadRef == $"{remote}/{head}"))
                    : null);
            if (match == null) return e;
            var s = ReviewSummary.From(match);
            return e with { ReviewId = match.Id, ReviewedFiles = s.ReviewedFileCount, TotalFiles = s.FileCount };
        }).ToList();
    }

    /// <summary>
    /// State ("open", "closed", "merged") of the pull request behind each review that was started from a
    /// PR number, where the platform can tell us. Reviews whose state is unknown are left out.
    /// </summary>
    public async Task<Dictionary<string, string>> GetReviewPullRequestStatesAsync(CancellationToken ct = default)
    {
        var withPr = (await reviews.Store.ListAsync(ct)).Where(r => r.PullRequest != null).ToList();
        var result = new ConcurrentDictionary<string, string>();
        var repos = new ConcurrentDictionary<string, Task<(GitRepository Repo, List<(RemoteRepository Remote, IReviewPublisher Publisher)> Hosted)?>>();
        var tokens = new ConcurrentDictionary<string, Task<ResolvedCredential?>>();

        await Parallel.ForEachAsync(withPr, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, async (review, token) =>
        {
            var pr = review.PullRequest!;
            var cacheKey = $"{review.RepoPath}|{pr.Remote}|{pr.Number}";
            if (_stateCache.TryGetValue(cacheKey, out var cached) && DateTimeOffset.UtcNow - cached.At < StateCacheTime)
            {
                if (cached.State != null) result[review.Id] = cached.State;
                return;
            }
            string? state = null;
            try
            {
                var found = await repos.GetOrAdd(review.RepoPath, async path =>
                {
                    try
                    {
                        var repo = await reviews.OpenRepoAsync(path, token);
                        return (repo, await PublishingService.FindRemotesAsync(repo, _publishers, null, token));
                    }
                    catch (GitException)
                    {
                        return null;
                    }
                });
                var hosted = found?.Hosted.FirstOrDefault(h => h.Remote.RemoteName == pr.Remote);
                if (hosted is { Remote: not null } h)
                {
                    var credential = await tokens.GetOrAdd($"{h.Publisher.Id}|{h.Remote.Host}", _ => credentials.ResolveAsync(h.Publisher, h.Remote, token));
                    if (credential != null) state = (await h.Publisher.GetPullRequestAsync(h.Remote, pr.Number, credential.Token, token)).State;
                }
            }
            catch (PublishException)
            {
                // Unknown (offline, no access): leave it out.
            }
            _stateCache[cacheKey] = (state, DateTimeOffset.UtcNow);
            if (state != null) result[review.Id] = state;
        });
        return new Dictionary<string, string>(result);
    }

    private static string Join(string? a, string b) => string.IsNullOrEmpty(a) ? b : $"{a} {b}";
}
