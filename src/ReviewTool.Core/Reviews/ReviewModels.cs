using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace ReviewTool.Core.Reviews;

/// <summary>
/// A persisted review of a range of commits (<see cref="BaseRef"/>..<see cref="HeadRef"/>)
/// in a local repository. Stored as one JSON document per review.
/// </summary>
public sealed class Review
{
    /// <summary>Key used instead of a commit SHA for the combined "whole PR" diff.</summary>
    public const string RangeKey = "range";

    /// <summary>Key for the diff between the previously reviewed head and the current head.</summary>
    public const string SinceKey = "since";

    public static bool IsSpecialKey(string key) => key is RangeKey or SinceKey;

    public int SchemaVersion { get; set; } = 1;
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string RepoPath { get; set; } = "";
    public string BaseRef { get; set; } = "";
    public string HeadRef { get; set; } = "";

    /// <summary>Tip of <see cref="BaseRef"/> when the review was last refreshed.</summary>
    public string BaseSha { get; set; } = "";

    /// <summary>Merge base of base and head; the left side of the combined diff.</summary>
    public string MergeBaseSha { get; set; } = "";

    public string HeadSha { get; set; } = "";

    /// <summary>Optional pull request metadata when the review was created from a PR number.</summary>
    public PullRequestRef? PullRequest { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? RefreshedAt { get; set; }

    public ReviewStatus Status { get; set; } = ReviewStatus.InProgress;

    /// <summary>Free-form overall summary, included at the top of exported notes.</summary>
    public string Summary { get; set; } = "";

    public List<ReviewCommit> Commits { get; set; } = [];

    /// <summary>Reviewed file paths keyed by commit SHA (or <see cref="RangeKey"/>).</summary>
    public Dictionary<string, List<string>> ReviewedFiles { get; set; } = [];

    /// <summary>
    /// Partially reviewed files: commit key → path → changed-line ranges already reviewed,
    /// encoded like <c>N12-20</c> (new side) or <c>O5</c> (old side). See <see cref="LineRangeSet"/>.
    /// </summary>
    public Dictionary<string, Dictionary<string, List<string>>> ReviewedLines { get; set; } = [];

    /// <summary>The head that was reviewed before the most recent refresh that moved it.</summary>
    public ReviewSnapshot? PreviousHead { get; set; }

    public List<ReviewNote> Notes { get; set; } = [];

    /// <summary>Opaque UI state owned by the frontend (current commit, open panes, view options...).</summary>
    public JsonObject? UiState { get; set; }

    public ReviewCommit? FindCommit(string sha) => Commits.FirstOrDefault(c => c.Sha == sha);
}

public sealed class PullRequestRef
{
    public string Remote { get; set; } = "origin";
    public int Number { get; set; }
    public string Provider { get; set; } = "github";
}

[JsonConverter(typeof(JsonStringEnumConverter<ReviewStatus>))]
public enum ReviewStatus
{
    InProgress,
    Approved,
    ChangesRequested,
    Commented,
}

public sealed class ReviewCommit
{
    public string Sha { get; set; } = "";
    public List<string> Parents { get; set; } = [];

    /// <summary>What this commit is diffed against: first parent, or the empty tree for root commits.</summary>
    public string DiffBase { get; set; } = "";

    public string Subject { get; set; } = "";
    public string Body { get; set; } = "";
    public string AuthorName { get; set; } = "";
    public string AuthorEmail { get; set; } = "";
    public DateTimeOffset AuthorDate { get; set; }
    public int FileCount { get; set; }
    public int Additions { get; set; }
    public int Deletions { get; set; }
    public bool IsMerge => Parents.Count > 1;

    /// <summary>True when the commit is no longer part of the range (e.g. after a force push). Kept for its notes.</summary>
    public bool Outdated { get; set; }

    /// <summary>How this commit relates to the previously reviewed head (null when there was no previous head).</summary>
    public CommitChange? SinceLastReview { get; set; }

    /// <summary>The matching commit before the last refresh (same patch, or same subject when modified).</summary>
    public string? PreviousSha { get; set; }
}

[JsonConverter(typeof(JsonStringEnumConverter<CommitChange>))]
public enum CommitChange
{
    /// <summary>Same patch as a previously reviewed commit (e.g. only rebased).</summary>
    Unchanged,

    /// <summary>A previously reviewed commit (matched by subject) whose changes differ.</summary>
    Modified,

    /// <summary>Not present before.</summary>
    New,
}

public sealed class ReviewSnapshot
{
    public string HeadSha { get; set; } = "";
    public string MergeBaseSha { get; set; } = "";
    public string BaseSha { get; set; } = "";
    public DateTimeOffset RecordedAt { get; set; }
}

[JsonConverter(typeof(JsonStringEnumConverter<NoteKind>))]
public enum NoteKind
{
    Comment,
    Question,
    Issue,
    Suggestion,
    Nit,
    Praise,
}

[JsonConverter(typeof(JsonStringEnumConverter<DiffSide>))]
public enum DiffSide
{
    New,
    Old,
}

public sealed class ReviewNote
{
    public string Id { get; set; } = "";

    /// <summary>Commit SHA the note was written against, or <see cref="Review.RangeKey"/>.</summary>
    public string CommitSha { get; set; } = "";

    /// <summary>File the note is attached to; null for a commit-level note.</summary>
    public string? FilePath { get; set; }

    public DiffSide Side { get; set; } = DiffSide.New;

    /// <summary>First line (1-based) on <see cref="Side"/>; null for file- or commit-level notes.</summary>
    public int? Line { get; set; }

    /// <summary>Last line of a multi-line selection (inclusive).</summary>
    public int? EndLine { get; set; }

    /// <summary>Snapshot of the code the note refers to, so it still makes sense if the code moves.</summary>
    public string? LineText { get; set; }

    public string Body { get; set; } = "";
    public NoteKind Kind { get; set; } = NoteKind.Comment;
    public bool Resolved { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Lightweight projection used for the review list.</summary>
public sealed record ReviewSummary(
    string Id,
    string Name,
    string RepoPath,
    string BaseRef,
    string HeadRef,
    string HeadSha,
    ReviewStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int CommitCount,
    int ReviewedCommitCount,
    int FileCount,
    int ReviewedFileCount,
    int NoteCount,
    int OpenNoteCount,
    PullRequestRef? PullRequest)
{
    public static ReviewSummary From(Review r)
    {
        var active = r.Commits.Where(c => !c.Outdated).ToList();
        int reviewedFiles = 0, reviewedCommits = 0;
        foreach (var c in active)
        {
            var count = r.ReviewedFiles.TryGetValue(c.Sha, out var list) ? Math.Min(list.Count, c.FileCount) : 0;
            reviewedFiles += count;
            if (count >= c.FileCount) reviewedCommits++;
        }
        return new ReviewSummary(r.Id, r.Name, r.RepoPath, r.BaseRef, r.HeadRef, r.HeadSha, r.Status,
            r.CreatedAt, r.UpdatedAt, active.Count, reviewedCommits, active.Sum(c => c.FileCount), reviewedFiles,
            r.Notes.Count, r.Notes.Count(n => !n.Resolved), r.PullRequest);
    }
}
