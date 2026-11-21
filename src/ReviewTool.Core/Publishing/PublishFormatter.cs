using System.Text;
using ReviewTool.Core.Reviews;

namespace ReviewTool.Core.Publishing;

/// <summary>Markdown for published reviews. Kept platform-neutral (GitHub, GitLab and Azure DevOps all render it).</summary>
public static class PublishFormatter
{
    public const string Footer = "<sub>Reviewed commit by commit with ReviewTool.</sub>";

    public static string InlineBody(ReviewNote note)
        => note.Kind == NoteKind.Comment ? note.Body.Trim() : $"**{note.Kind}:** {note.Body.Trim()}";

    /// <summary>The review's main text: summary, then notes that couldn't be placed inline.</summary>
    public static string ReviewBody(Review review, IEnumerable<GeneralNote> general, bool includeFooter = true)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(review.Summary)) sb.AppendLine(review.Summary.Trim()).AppendLine();

        var list = general.ToList();
        if (list.Count > 0)
        {
            sb.AppendLine(list.Count == 1 ? "#### Note" : "#### Notes").AppendLine();
            foreach (var group in list.GroupBy(g => g.Note.CommitSha))
            {
                var commit = review.FindCommit(group.Key);
                var heading = group.Key == Review.RangeKey ? "All changes"
                    : group.Key == Review.SinceKey ? "Since the last review"
                    : $"`{Short(group.Key)}` {commit?.Subject}";
                sb.AppendLine($"**{heading.Trim()}**").AppendLine();
                foreach (var g in group) AppendNote(sb, g.Note);
                sb.AppendLine();
            }
        }
        if (includeFooter) sb.AppendLine(Footer);
        return sb.ToString().Trim();
    }

    private static void AppendNote(StringBuilder sb, ReviewNote n)
    {
        var where = n.FilePath == null ? "" : n.Line == null ? $" `{n.FilePath}`"
            : $" `{n.FilePath}:{n.Line}{(n.EndLine != null ? $"-{n.EndLine}" : "")}`{(n.Side == DiffSide.Old ? " (removed lines)" : "")}";
        var lines = n.Body.Trim().Split('\n');
        sb.AppendLine($"- **{n.Kind}**{where}: {lines[0]}");
        foreach (var l in lines.Skip(1)) sb.AppendLine("  " + l);
        if (!string.IsNullOrWhiteSpace(n.LineText))
        {
            var fence = n.LineText.Contains("```", StringComparison.Ordinal) ? "~~~" : "```";
            sb.AppendLine($"  {fence}{Language(n.FilePath)}");
            foreach (var l in n.LineText.TrimEnd('\n').Split('\n')) sb.AppendLine("  " + l);
            sb.AppendLine($"  {fence}");
        }
    }

    private static string Language(string? path)
    {
        var ext = Path.GetExtension(path ?? "").TrimStart('.').ToLowerInvariant();
        return ext switch { "cs" => "csharp", "ts" or "tsx" => "ts", "js" or "jsx" => "js", "py" => "python", "md" => "markdown", _ => ext };
    }

    private static string Short(string sha) => sha.Length > 7 ? sha[..7] : sha;
}
