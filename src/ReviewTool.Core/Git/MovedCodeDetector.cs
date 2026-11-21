namespace ReviewTool.Core.Git;

/// <summary>
/// Finds blocks of code that were deleted in one place and re-added elsewhere in the same change
/// (possibly in another file, possibly re-indented), so reviewers can tell a move from a rewrite.
/// </summary>
public static class MovedCodeDetector
{
    /// <summary>Minimum number of non-trivial lines for a block to count as moved.</summary>
    public const int MinSignificantLines = 3;

    private const int MaxLines = 50_000;
    private const int MaxCandidatesPerLine = 32;

    private sealed record Line(string Path, int Number, string Norm, bool Significant, int Run, int Hunk);

    /// <param name="files">Diff of the whole change, ideally produced with <c>-U0</c>.</param>
    public static List<MovedBlock> Detect(IReadOnlyList<FileDiff> files)
    {
        var deletes = new List<List<Line>>();
        var adds = new List<List<Line>>();
        var hunkId = 0;
        var total = 0;
        foreach (var file in files)
        {
            if (file.IsBinary) continue;
            foreach (var hunk in file.Hunks)
            {
                hunkId++;
                List<Line>? delRun = null, addRun = null;
                foreach (var l in hunk.Lines)
                {
                    if (l.Type == DiffLineType.Context)
                    {
                        delRun = addRun = null;
                        continue;
                    }
                    total++;
                    var norm = Normalize(l.Text);
                    if (l.Type == DiffLineType.Delete)
                    {
                        if (delRun == null) deletes.Add(delRun = []);
                        delRun.Add(new Line(file.OldPath ?? file.Path, l.OldLine!.Value, norm, IsSignificant(norm), deletes.Count - 1, hunkId));
                        addRun = null;
                    }
                    else
                    {
                        if (addRun == null) adds.Add(addRun = []);
                        addRun.Add(new Line(file.Path, l.NewLine!.Value, norm, IsSignificant(norm), adds.Count - 1, hunkId));
                    }
                }
            }
        }
        if (total > MaxLines || deletes.Count == 0 || adds.Count == 0) return [];

        // Index significant added lines by their normalized text.
        var index = new Dictionary<string, List<(int Run, int Pos)>>(StringComparer.Ordinal);
        for (var r = 0; r < adds.Count; r++)
            for (var p = 0; p < adds[r].Count; p++)
            {
                var line = adds[r][p];
                if (!line.Significant) continue;
                if (!index.TryGetValue(line.Norm, out var list)) index[line.Norm] = list = [];
                if (list.Count < MaxCandidatesPerLine) list.Add((r, p));
            }

        var usedAdds = adds.Select(run => new bool[run.Count]).ToArray();
        var blocks = new List<MovedBlock>();

        foreach (var del in deletes)
        {
            var usedDel = new bool[del.Count];
            for (var p = 0; p < del.Count; p++)
            {
                if (usedDel[p] || !del[p].Significant || !index.TryGetValue(del[p].Norm, out var candidates)) continue;

                (int Run, int Pos, int Len, int Sig) best = default;
                foreach (var (r, q) in candidates)
                {
                    var add = adds[r];
                    // A delete and re-add in the same hunk is an in-place edit (e.g. re-indent), not a move.
                    if (add[q].Hunk == del[p].Hunk) continue;
                    int len = 0, sig = 0;
                    while (p + len < del.Count && q + len < add.Count && !usedDel[p + len] && !usedAdds[r][q + len]
                           && del[p + len].Norm == add[q + len].Norm)
                    {
                        if (del[p + len].Significant) sig++;
                        len++;
                    }
                    if (sig > best.Sig) best = (r, q, len, sig);
                }
                if (best.Sig < MinSignificantLines) continue;

                for (var k = 0; k < best.Len; k++)
                {
                    usedDel[p + k] = true;
                    usedAdds[best.Run][best.Pos + k] = true;
                }
                var from = del[p];
                var to = adds[best.Run][best.Pos];
                blocks.Add(new MovedBlock(from.Path, from.Number, del[p + best.Len - 1].Number,
                    to.Path, to.Number, adds[best.Run][best.Pos + best.Len - 1].Number));
                p += best.Len - 1;
            }
        }
        return blocks;
    }

    private static string Normalize(string text)
    {
        // Ignore indentation and runs of whitespace so re-indented moves are still detected.
        var span = text.AsSpan().Trim();
        var sb = new System.Text.StringBuilder(span.Length);
        var space = false;
        foreach (var c in span)
        {
            if (char.IsWhiteSpace(c))
            {
                space = true;
                continue;
            }
            if (space && sb.Length > 0) sb.Append(' ');
            space = false;
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>Lines like "{", "}", "});" or blank lines match too easily to anchor a move on their own.</summary>
    private static bool IsSignificant(string norm)
    {
        var letters = 0;
        foreach (var c in norm)
            if (char.IsLetterOrDigit(c)) letters++;
        return letters >= 3;
    }
}
