using System.Text.Json.Serialization;
using ReviewTool.Core.Reviews;

namespace ReviewTool.Core.Publishing;

/// <summary>The overall outcome posted with a review. Platforms map this to their own terms.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<PublishVerdict>))]
public enum PublishVerdict
{
    Comment,
    Approve,
    RequestChanges,
}

/// <summary>A problem posting to a platform, with a message suitable for showing to the user.</summary>
public class PublishException(string message) : Exception(message);

/// <summary>A pull/merge request on a hosting platform.</summary>
public sealed record PullRequestInfo(int Number, string Title, string Url, string HeadSha, string HeadRef, string BaseRef, string State);

/// <summary>An open pull request as listed by a hosting platform.</summary>
public sealed record PullRequestSummary(
    int Number,
    string Title,
    string Author,
    string Url,
    string HeadRef,
    string BaseRef,
    string HeadSha,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? UpdatedAt,
    bool Draft);

/// <summary>An open pull request offered for review, with any review of it already started.</summary>
public sealed record OpenPullRequestEntry(
    int Number,
    string? Title,
    string? Author,
    string? Url,
    string? HeadRef,
    string? BaseRef,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? UpdatedAt,
    bool Draft,
    string? ReviewId,
    int ReviewedFiles,
    int TotalFiles);

/// <summary>Open pull requests for a repository and where the list came from.</summary>
/// <param name="Source">The platform id (e.g. "github") or "git" when read from the remote's refs.</param>
/// <param name="Note">Why the list is limited (not signed in, couldn't reach the remote, ...), if it is.</param>
public sealed record OpenPullRequestList(string Source, string SourceName, string? Remote, string? Repository, string? WebUrl, string? Note, List<OpenPullRequestEntry> PullRequests);

/// <summary>Where a note can be attached in the pull request's diff (head version for New, base for Old).</summary>
public sealed record InlinePosition(string Path, DiffSide Side, int StartLine, int EndLine);

public sealed record PlannedComment(ReviewNote Note, InlinePosition Position);

/// <summary>A note that will be included in the review's main text instead of inline, and why.</summary>
public sealed record GeneralNote(ReviewNote Note, string Reason);

/// <summary>Platform-neutral description of what will be posted.</summary>
public sealed class PublishPlan
{
    public string HeadSha { get; init; } = "";
    public string BaseSha { get; init; } = "";
    public List<PlannedComment> Inline { get; } = [];
    public List<GeneralNote> General { get; } = [];
    public List<string> AlreadyPublished { get; } = [];
    public int SkippedResolved { get; set; }
}

/// <summary>Everything a platform adapter needs to post a review.</summary>
public sealed record PublishContext(
    RemoteRepository Remote,
    string Token,
    PullRequestInfo PullRequest,
    PublishPlan Plan,
    PublishVerdict Verdict,
    string Body,
    IReadOnlyDictionary<string, string> InlineBodies,
    string FallbackBody);

public sealed record PublishOutcome(string Url, string ExternalId, int InlineCount, List<string> Warnings);

/// <summary>Where credentials for a platform can be found, besides configuration and git's credential helper.</summary>
public sealed record CredentialHints(IReadOnlyList<string> EnvironmentVariables, string? CliTool, IReadOnlyList<string> CliArgs, string HelpText);

// ---- API-facing DTOs ----

public sealed record PublishTargetInfo(
    string Provider,
    string ProviderName,
    string Remote,
    string Host,
    string Repository,
    string WebUrl,
    bool Authenticated,
    string? AuthSource,
    string? AuthHelp);

public sealed record PublishRequest(
    string? Provider = null,
    string? Remote = null,
    int? PullRequestNumber = null,
    PublishVerdict? Verdict = null,
    bool IncludeResolved = false,
    bool Republish = false);

public sealed record PreviewComment(string NoteId, NoteKind Kind, string Path, DiffSide Side, int StartLine, int EndLine, string Body);

public sealed record PreviewGeneral(string NoteId, NoteKind Kind, string? Path, int? Line, string Reason);

public sealed record PublishPreview(
    PublishTargetInfo Target,
    PullRequestInfo? PullRequest,
    PublishVerdict Verdict,
    List<PreviewComment> Inline,
    List<PreviewGeneral> General,
    int AlreadyPublished,
    int SkippedResolved,
    string Body,
    List<string> Warnings,
    List<string> Blockers,
    bool NothingNew);

public sealed record PublishResult(string Url, int InlineComments, int GeneralNotes, List<string> Warnings, PublicationRecord Record);
