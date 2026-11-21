using System.Text.Json.Serialization;

namespace ReviewTool.Core.Git;

[JsonConverter(typeof(JsonStringEnumConverter<ChangeKind>))]
public enum ChangeKind
{
    Modified,
    Added,
    Deleted,
    Renamed,
    Copied,
    TypeChanged,
    Unknown,
}

[JsonConverter(typeof(JsonStringEnumConverter<DiffLineType>))]
public enum DiffLineType
{
    Context,
    Add,
    Delete,
}

public sealed record CommitInfo(
    string Sha,
    IReadOnlyList<string> Parents,
    string AuthorName,
    string AuthorEmail,
    DateTimeOffset AuthorDate,
    string CommitterName,
    DateTimeOffset CommitDate,
    string Subject,
    string Body);

public sealed record ChangedFile(
    string Path,
    string? OldPath,
    ChangeKind Status,
    int Additions,
    int Deletions,
    bool IsBinary,
    int? Similarity = null);

public sealed record DiffLine(DiffLineType Type, int? OldLine, int? NewLine, string Text, bool NoNewlineAtEnd = false);

public sealed record DiffHunk(int OldStart, int OldCount, int NewStart, int NewCount, string Header, IReadOnlyList<DiffLine> Lines);

public sealed record FileDiff(
    string Path,
    string? OldPath,
    ChangeKind Status,
    bool IsBinary,
    string? OldMode,
    string? NewMode,
    IReadOnlyList<DiffHunk> Hunks,
    bool Truncated = false,
    int? OldLineCount = null,
    int? NewLineCount = null);

public sealed record RefInfo(string Name, string Kind, string Sha);

public sealed record RepoRefs(string? CurrentBranch, string? DefaultBranch, IReadOnlyList<RefInfo> Refs);

public sealed record RepoInfo(string Path, string? CurrentBranch, IReadOnlyList<string> Remotes);

public sealed record BlobContent(string Path, string Sha, bool IsBinary, bool Truncated, long Size, string? Text);
