namespace ReviewTool.Core.Publishing;

/// <summary>
/// Adapter for one hosting platform (GitHub, GitLab, Azure DevOps, ...). Everything platform-neutral —
/// choosing where each note goes, formatting, credentials lookup, bookkeeping — happens outside the
/// adapter, so a new platform only has to talk to its API.
/// </summary>
public interface IReviewPublisher
{
    /// <summary>Stable id used in requests and stored records, e.g. "github".</summary>
    string Id { get; }

    string DisplayName { get; }

    /// <summary>True when this adapter can talk to the repository's host.</summary>
    bool CanHandle(RemoteRepository remote);

    /// <summary>Browser URL of the repository.</summary>
    string WebUrl(RemoteRepository remote);

    /// <summary>Browser URL of a pull request.</summary>
    string PullRequestUrl(RemoteRepository remote, int number);

    /// <summary>Browser URL listing the pull requests whose source branch is <paramref name="branch"/> (when the number isn't known).</summary>
    string PullRequestSearchUrl(RemoteRepository remote, string branch);

    CredentialHints GetCredentialHints(string host);

    Task<PullRequestInfo> GetPullRequestAsync(RemoteRepository remote, int number, string token, CancellationToken ct);

    /// <summary>Finds the open pull request whose source branch is <paramref name="branch"/>, if any.</summary>
    Task<PullRequestInfo?> FindPullRequestAsync(RemoteRepository remote, string branch, string token, CancellationToken ct);

    Task<PublishOutcome> PublishAsync(PublishContext context, CancellationToken ct);

    /// <summary>Open pull requests in the repository, newest first.</summary>
    Task<List<PullRequestSummary>> ListOpenPullRequestsAsync(RemoteRepository remote, string token, CancellationToken ct);
}
