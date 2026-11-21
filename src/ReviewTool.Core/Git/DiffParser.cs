using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ReviewTool.Core.Git;

/// <summary>
/// Parses the unified diff format produced by <c>git diff</c> / <c>git show</c>
/// (with the default <c>a/</c> and <c>b/</c> prefixes) into structured hunks.
/// </summary>
public static partial class DiffParser
{
    [GeneratedRegex(@"^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@ ?(.*)$")]
    private static partial Regex HunkHeaderRegex();

    /// <summary>Parses a diff that may contain any number of files.</summary>
    /// <param name="maxLinesPerFile">Hunk lines beyond this count are dropped and the file is flagged as truncated.</param>
    public static List<FileDiff> Parse(string diffText, int maxLinesPerFile = int.MaxValue)
    {
        var files = new List<FileDiff>();
        var lines = SplitLines(diffText);
        var i = 0;
        while (i < lines.Count)
        {
            if (!lines[i].StartsWith("diff --git ", StringComparison.Ordinal))
            {
                i++;
                continue;
            }
            files.Add(ParseFile(lines, ref i, maxLinesPerFile));
        }
        return files;
    }

    private static FileDiff ParseFile(List<string> lines, ref int i, int maxLines)
    {
        var (headerOld, headerNew) = ParseDiffGitHeader(lines[i]);
        i++;

        string? oldPath = headerOld, newPath = headerNew;
        string? oldMode = null, newMode = null;
        var status = ChangeKind.Modified;
        var isBinary = false;

        // Extended header lines until the first hunk or the next file.
        while (i < lines.Count && !lines[i].StartsWith("diff --git ", StringComparison.Ordinal) && !lines[i].StartsWith("@@", StringComparison.Ordinal))
        {
            var line = lines[i];
            if (line.StartsWith("new file mode ", StringComparison.Ordinal)) { status = ChangeKind.Added; newMode = line[14..]; }
            else if (line.StartsWith("deleted file mode ", StringComparison.Ordinal)) { status = ChangeKind.Deleted; oldMode = line[18..]; }
            else if (line.StartsWith("old mode ", StringComparison.Ordinal)) oldMode = line[9..];
            else if (line.StartsWith("new mode ", StringComparison.Ordinal)) newMode = line[9..];
            else if (line.StartsWith("rename from ", StringComparison.Ordinal)) { status = ChangeKind.Renamed; oldPath = Unquote(line[12..]); }
            else if (line.StartsWith("rename to ", StringComparison.Ordinal)) { status = ChangeKind.Renamed; newPath = Unquote(line[10..]); }
            else if (line.StartsWith("copy from ", StringComparison.Ordinal)) { status = ChangeKind.Copied; oldPath = Unquote(line[10..]); }
            else if (line.StartsWith("copy to ", StringComparison.Ordinal)) { status = ChangeKind.Copied; newPath = Unquote(line[8..]); }
            else if (line.StartsWith("Binary files ", StringComparison.Ordinal) || line == "GIT binary patch") isBinary = true;
            else if (line.StartsWith("--- ", StringComparison.Ordinal))
            {
                var p = StripPrefix(Unquote(TrimTrailingTab(line[4..])), "a/");
                if (p != null) oldPath = p;
            }
            else if (line.StartsWith("+++ ", StringComparison.Ordinal))
            {
                var p = StripPrefix(Unquote(TrimTrailingTab(line[4..])), "b/");
                if (p != null) newPath = p;
            }
            i++;
        }

        if (status == ChangeKind.Modified && oldMode != null && newMode != null && oldMode != newMode
            && (IsSymlinkMode(oldMode) != IsSymlinkMode(newMode)))
        {
            status = ChangeKind.TypeChanged;
        }

        var hunks = new List<DiffHunk>();
        var lineCount = 0;
        var truncated = false;
        while (i < lines.Count && lines[i].StartsWith("@@", StringComparison.Ordinal))
        {
            var m = HunkHeaderRegex().Match(lines[i]);
            if (!m.Success) { i++; continue; }
            var oldStart = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            var oldCount = m.Groups[2].Success ? int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) : 1;
            var newStart = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
            var newCount = m.Groups[4].Success ? int.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture) : 1;
            var header = m.Groups[5].Value;
            i++;

            var hunkLines = new List<DiffLine>();
            int oldLine = oldStart, newLine = newStart;
            int oldRemaining = oldCount, newRemaining = newCount;
            while (i < lines.Count && (oldRemaining > 0 || newRemaining > 0 || (i < lines.Count && lines[i].StartsWith('\\'))))
            {
                var line = lines[i];
                if (line.StartsWith('\\'))
                {
                    // "\ No newline at end of file" applies to the previous line.
                    if (hunkLines.Count > 0) hunkLines[^1] = hunkLines[^1] with { NoNewlineAtEnd = true };
                    i++;
                    continue;
                }
                if (line.Length == 0)
                {
                    // Some tools strip the single space from blank context lines.
                    line = " ";
                }
                DiffLine? parsed = line[0] switch
                {
                    ' ' => new DiffLine(DiffLineType.Context, oldLine++, newLine++, line[1..]),
                    '+' => new DiffLine(DiffLineType.Add, null, newLine++, line[1..]),
                    '-' => new DiffLine(DiffLineType.Delete, oldLine++, null, line[1..]),
                    _ => null,
                };
                if (parsed is null) break;
                if (parsed.Type != DiffLineType.Add) oldRemaining--;
                if (parsed.Type != DiffLineType.Delete) newRemaining--;
                if (lineCount < maxLines) hunkLines.Add(parsed);
                else truncated = true;
                lineCount++;
                i++;
            }
            if (hunkLines.Count > 0 || !truncated)
                hunks.Add(new DiffHunk(oldStart, oldCount, newStart, newCount, header, hunkLines));
        }

        // Skip anything we don't understand until the next file (e.g. binary patch payloads).
        while (i < lines.Count && !lines[i].StartsWith("diff --git ", StringComparison.Ordinal)) i++;

        if (status == ChangeKind.Added) oldPath = null;
        var path = (status == ChangeKind.Deleted ? oldPath : newPath) ?? oldPath ?? newPath ?? "";
        var reportedOld = status is ChangeKind.Renamed or ChangeKind.Copied ? oldPath : null;
        return new FileDiff(path, reportedOld, status, isBinary, oldMode, newMode, hunks, truncated);
    }

    private static bool IsSymlinkMode(string mode) => mode.StartsWith("120", StringComparison.Ordinal);

    private static string TrimTrailingTab(string s)
    {
        // GNU diff can append "\t<timestamp>"; git only adds a tab when the path has spaces.
        var tab = s.IndexOf('\t');
        return tab >= 0 ? s[..tab] : s;
    }

    private static string? StripPrefix(string path, string prefix)
    {
        if (path == "/dev/null") return null;
        return path.StartsWith(prefix, StringComparison.Ordinal) ? path[prefix.Length..] : path;
    }

    /// <summary>Best-effort parse of "diff --git a/x b/y". Only used as a fallback.</summary>
    private static (string? Old, string? New) ParseDiffGitHeader(string line)
    {
        var rest = line["diff --git ".Length..];
        if (rest.StartsWith('"'))
        {
            var end = FindClosingQuote(rest);
            if (end < 0) return (null, null);
            var a = Unquote(rest[..(end + 1)]);
            var b = Unquote(rest[(end + 1)..].TrimStart());
            return (StripPrefix(a, "a/"), StripPrefix(b, "b/"));
        }
        // Unquoted: if both halves are equal (common case) split down the middle.
        if (rest.StartsWith("a/", StringComparison.Ordinal))
        {
            var idx = rest.IndexOf(" b/", StringComparison.Ordinal);
            while (idx >= 0)
            {
                var a = rest[2..idx];
                var b = rest[(idx + 3)..];
                if (a == b) return (a, b);
                idx = rest.IndexOf(" b/", idx + 1, StringComparison.Ordinal);
            }
            var first = rest.IndexOf(" b/", StringComparison.Ordinal);
            if (first >= 0) return (rest[2..first], rest[(first + 3)..]);
        }
        return (null, null);
    }

    private static int FindClosingQuote(string s)
    {
        for (var k = 1; k < s.Length; k++)
        {
            if (s[k] == '\\') { k++; continue; }
            if (s[k] == '"') return k;
        }
        return -1;
    }

    /// <summary>Decodes git's C-style quoted paths (used for paths with special characters).</summary>
    public static string Unquote(string s)
    {
        if (s.Length < 2 || s[0] != '"' || s[^1] != '"') return s;
        var bytes = new List<byte>();
        var body = s[1..^1];
        for (var k = 0; k < body.Length; k++)
        {
            var c = body[k];
            if (c != '\\' || k + 1 >= body.Length)
            {
                bytes.AddRange(Encoding.UTF8.GetBytes(c.ToString()));
                continue;
            }
            var n = body[++k];
            switch (n)
            {
                case 'n': bytes.Add((byte)'\n'); break;
                case 't': bytes.Add((byte)'\t'); break;
                case 'r': bytes.Add((byte)'\r'); break;
                case 'a': bytes.Add(7); break;
                case 'b': bytes.Add(8); break;
                case 'f': bytes.Add(12); break;
                case 'v': bytes.Add(11); break;
                case '"': bytes.Add((byte)'"'); break;
                case '\\': bytes.Add((byte)'\\'); break;
                default:
                    if (n is >= '0' and <= '7' && k + 2 < body.Length)
                    {
                        bytes.Add(Convert.ToByte(body.Substring(k, 3), 8));
                        k += 2;
                    }
                    else bytes.AddRange(Encoding.UTF8.GetBytes(n.ToString()));
                    break;
            }
        }
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    private static List<string> SplitLines(string text)
    {
        var result = new List<string>();
        var start = 0;
        for (var k = 0; k < text.Length; k++)
        {
            if (text[k] != '\n') continue;
            var end = k > start && text[k - 1] == '\r' ? k - 1 : k;
            result.Add(text[start..end]);
            start = k + 1;
        }
        if (start < text.Length) result.Add(text[start..]);
        return result;
    }
}
