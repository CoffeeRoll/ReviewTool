using System.Globalization;

namespace ReviewTool.Core.Git;

/// <summary>Read-mostly operations against one local git repository.</summary>
public sealed class GitRepository
{
    /// <summary>Number of context lines that effectively means "the whole file".</summary>
    public const int FullContext = 1_000_000;

    private readonly GitRunner _git;
    private string? _emptyTree;

    public string Path { get; }

    private GitRepository(string path, GitRunner git)
    {
        Path = path;
        _git = git;
    }

    /// <summary>Opens the repository containing <paramref name="path"/> (any sub directory works).</summary>
    public static async Task<GitRepository> OpenAsync(string path, GitRunner git, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new GitException("A repository path is required.");
        var full = System.IO.Path.GetFullPath(ExpandHome(path.Trim()));
        if (!Directory.Exists(full)) throw new GitException($"Directory not found: {full}");
        var result = await git.RunAsync(full, ["rev-parse", "--show-toplevel"], ct);
        if (!result.Success) throw new GitException($"Not a git repository: {full}", result.StdErr);
        var top = result.StdOut.Trim();
        return new GitRepository(System.IO.Path.GetFullPath(top), git);
    }

    private static string ExpandHome(string path)
    {
        if (path == "~" || path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal))
            return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path.Length > 2 ? path[2..] : "");
        return path;
    }

    private Task<string> Run(CancellationToken ct, params string[] args) => _git.RunCheckedAsync(Path, args, ct);

    /// <summary>Rejects values that git could interpret as options.</summary>
    public static string ValidateRev(string rev, string what = "revision")
    {
        if (string.IsNullOrWhiteSpace(rev)) throw new GitException($"A {what} is required.");
        rev = rev.Trim();
        if (rev.StartsWith('-') || rev.Contains('\0') || rev.Contains('\n') || rev.Contains('\r'))
            throw new GitException($"Invalid {what}: '{rev}'");
        return rev;
    }

    private static string ValidatePath(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Contains('\0'))
            throw new GitException("Invalid file path.");
        return path;
    }

    public async Task<RepoInfo> GetInfoAsync(CancellationToken ct = default)
    {
        var branch = await GetCurrentBranchAsync(ct);
        var remotes = (await Run(ct, "remote")).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return new RepoInfo(Path, branch, remotes);
    }

    public async Task<string?> GetRemoteUrlAsync(string remote, CancellationToken ct = default)
    {
        var r = await _git.RunAsync(Path, ["remote", "get-url", ValidateRev(remote, "remote")], ct);
        return r.Success ? r.StdOut.Trim() : null;
    }

    private async Task<string?> GetCurrentBranchAsync(CancellationToken ct)
    {
        var r = await _git.RunAsync(Path, ["symbolic-ref", "--short", "-q", "HEAD"], ct);
        return r.Success ? r.StdOut.Trim() : null;
    }

    public async Task<string> ResolveCommitAsync(string rev, CancellationToken ct = default)
    {
        rev = ValidateRev(rev);
        var r = await _git.RunAsync(Path, ["rev-parse", "--verify", "--quiet", "--end-of-options", rev + "^{commit}"], ct);
        if (!r.Success) throw new GitException($"Unknown revision '{rev}'.", r.StdErr, r.ExitCode);
        return r.StdOut.Trim();
    }

    public async Task<string?> MergeBaseAsync(string a, string b, CancellationToken ct = default)
    {
        var r = await _git.RunAsync(Path, ["merge-base", ValidateRev(a), ValidateRev(b)], ct);
        return r.Success ? r.StdOut.Trim() : null;
    }

    public async Task<string> GetEmptyTreeAsync(CancellationToken ct = default)
    {
        if (_emptyTree != null) return _emptyTree;
        // Works for both SHA-1 and SHA-256 repositories.
        var r = await _git.RunAsync(Path, ["hash-object", "-t", "tree", "/dev/null"], ct);
        _emptyTree = r.Success && r.StdOut.Trim().Length >= 40
            ? r.StdOut.Trim()
            : "4b825dc642cb6eb9a060e54bf8d69288fbee4904";
        return _emptyTree;
    }

    public async Task<RepoRefs> ListRefsAsync(CancellationToken ct = default)
    {
        var output = await Run(ct, "for-each-ref",
            "--format=%(refname)%1f%(objectname)%1f%(*objectname)%1f%(refname:short)",
            "refs/heads", "refs/remotes", "refs/tags", "refs/reviewtool");
        var refs = new List<RefInfo>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('\x1f');
            if (parts.Length < 4) continue;
            var full = parts[0];
            if (full.EndsWith("/HEAD", StringComparison.Ordinal)) continue;
            var kind = full.StartsWith("refs/heads/", StringComparison.Ordinal) ? "branch"
                : full.StartsWith("refs/remotes/", StringComparison.Ordinal) ? "remote"
                : full.StartsWith("refs/tags/", StringComparison.Ordinal) ? "tag"
                : "other";
            var sha = parts[2].Length > 0 ? parts[2] : parts[1];
            var name = kind == "other" ? full : parts[3];
            refs.Add(new RefInfo(name, kind, sha));
        }

        var current = await GetCurrentBranchAsync(ct);
        string? defaultBranch = null;
        var originHead = await _git.RunAsync(Path, ["symbolic-ref", "--short", "-q", "refs/remotes/origin/HEAD"], ct);
        if (originHead.Success) defaultBranch = originHead.StdOut.Trim();
        defaultBranch ??= new[] { "origin/main", "origin/master", "main", "master", "develop" }
            .FirstOrDefault(c => refs.Any(r => r.Name == c));
        return new RepoRefs(current, defaultBranch, refs);
    }

    /// <summary>Commits reachable from <paramref name="head"/> but not from <paramref name="baseRev"/>, oldest first.</summary>
    public async Task<List<CommitInfo>> ListCommitsAsync(string baseRev, string head, int max = 2000, CancellationToken ct = default)
    {
        var output = await Run(ct, "log", "--reverse", "--topo-order",
            $"--max-count={max}",
            "--format=%H%x1f%P%x1f%an%x1f%ae%x1f%aI%x1f%cn%x1f%cI%x1f%s%x1f%b%x1e",
            "--end-of-options",
            $"{ValidateRev(baseRev)}..{ValidateRev(head)}");
        return ParseLog(output);
    }

    public async Task<CommitInfo> GetCommitAsync(string rev, CancellationToken ct = default)
    {
        var output = await Run(ct, "log", "-1",
            "--format=%H%x1f%P%x1f%an%x1f%ae%x1f%aI%x1f%cn%x1f%cI%x1f%s%x1f%b%x1e",
            "--end-of-options", ValidateRev(rev));
        return ParseLog(output).Single();
    }

    internal static List<CommitInfo> ParseLog(string output)
    {
        var commits = new List<CommitInfo>();
        foreach (var raw in output.Split('\x1e'))
        {
            var record = raw.TrimStart('\n', '\r');
            if (record.Length == 0) continue;
            var f = record.Split('\x1f');
            if (f.Length < 9) continue;
            commits.Add(new CommitInfo(
                Sha: f[0],
                Parents: f[1].Split(' ', StringSplitOptions.RemoveEmptyEntries),
                AuthorName: f[2],
                AuthorEmail: f[3],
                AuthorDate: DateTimeOffset.Parse(f[4], CultureInfo.InvariantCulture),
                CommitterName: f[5],
                CommitDate: DateTimeOffset.Parse(f[6], CultureInfo.InvariantCulture),
                Subject: f[7],
                Body: f[8].TrimEnd()));
        }
        return commits;
    }

    /// <summary>Files changed between two trees, with rename detection and line counts.</summary>
    public async Task<List<ChangedFile>> GetChangedFilesAsync(string from, string to, CancellationToken ct = default, bool ignoreWhitespace = false)
    {
        from = ValidateRev(from);
        to = ValidateRev(to);
        var ws = ignoreWhitespace ? "-w" : "--no-color";
        var nameStatusTask = Run(ct, "diff", "--no-ext-diff", "--no-textconv", "-M", "--name-status", "-z", from, to, "--");
        var numstatTask = Run(ct, "diff", "--no-ext-diff", "--no-textconv", "-M", ws, "--numstat", "-z", from, to, "--");
        await Task.WhenAll(nameStatusTask, numstatTask);
        var files = MergeStatus(ParseNameStatus(await nameStatusTask), ParseNumstat(await numstatTask));
        var generated = await GetGeneratedPathsAsync(to, files.Select(f => f.Path).ToList(), ct);
        return files.Select(f => generated.Contains(f.Path) ? f with { IsGenerated = true } : f).ToList();
    }

    /// <summary>
    /// Paths that are generated: either matching well-known patterns (lockfiles, *.Designer.cs, ...)
    /// or marked <c>linguist-generated</c> in .gitattributes.
    /// </summary>
    public async Task<HashSet<string>> GetGeneratedPathsAsync(string rev, IReadOnlyCollection<string> paths, CancellationToken ct = default)
    {
        var result = paths.Where(GeneratedFiles.MatchesKnownPattern).ToHashSet(StringComparer.Ordinal);
        if (paths.Count == 0) return result;
        var input = string.Join('\0', paths) + "\0";
        // --source reads .gitattributes from the commit itself (git 2.40+); fall back to the working tree.
        var r = await _git.RunAsync(Path, ["check-attr", "-z", "--stdin", "--source", ValidateRev(rev), "linguist-generated"], ct, input);
        if (!r.Success) r = await _git.RunAsync(Path, ["check-attr", "-z", "--stdin", "linguist-generated"], ct, input);
        if (!r.Success) return result;
        var tokens = r.StdOut.Split('\0');
        for (var i = 0; i + 2 < tokens.Length; i += 3)
        {
            var value = tokens[i + 2];
            if (value is "set" or "true") result.Add(tokens[i]);
            else if (value is "unset" or "false") result.Remove(tokens[i]);
        }
        return result;
    }

    internal static List<(char Status, int? Similarity, string? OldPath, string Path)> ParseNameStatus(string output)
    {
        var tokens = output.Split('\0');
        var result = new List<(char, int?, string?, string)>();
        var i = 0;
        while (i < tokens.Length && tokens[i].Length > 0)
        {
            var code = tokens[i++];
            var status = code[0];
            int? similarity = code.Length > 1 && int.TryParse(code[1..], NumberStyles.None, CultureInfo.InvariantCulture, out var s) ? s : null;
            if (status is 'R' or 'C')
            {
                if (i + 1 >= tokens.Length) break;
                result.Add((status, similarity, tokens[i], tokens[i + 1]));
                i += 2;
            }
            else
            {
                if (i >= tokens.Length) break;
                result.Add((status, similarity, null, tokens[i]));
                i++;
            }
        }
        return result;
    }

    internal static Dictionary<string, (int Add, int Del, bool Binary)> ParseNumstat(string output)
    {
        var tokens = output.Split('\0');
        var result = new Dictionary<string, (int, int, bool)>(StringComparer.Ordinal);
        var i = 0;
        while (i < tokens.Length && tokens[i].Length > 0)
        {
            var parts = tokens[i++].Split('\t');
            if (parts.Length < 3) continue;
            var binary = parts[0] == "-";
            var add = binary ? 0 : int.Parse(parts[0], CultureInfo.InvariantCulture);
            var del = binary ? 0 : int.Parse(parts[1], CultureInfo.InvariantCulture);
            string path;
            if (parts[2].Length == 0)
            {
                // Rename/copy: "<add>\t<del>\t\0<old>\0<new>\0"
                if (i + 1 >= tokens.Length) break;
                path = tokens[i + 1];
                i += 2;
            }
            else path = parts[2];
            result[path] = (add, del, binary);
        }
        return result;
    }

    private static List<ChangedFile> MergeStatus(
        List<(char Status, int? Similarity, string? OldPath, string Path)> statuses,
        Dictionary<string, (int Add, int Del, bool Binary)> numstat)
    {
        var files = new List<ChangedFile>(statuses.Count);
        foreach (var s in statuses)
        {
            numstat.TryGetValue(s.Path, out var n);
            var kind = s.Status switch
            {
                'M' => ChangeKind.Modified,
                'A' => ChangeKind.Added,
                'D' => ChangeKind.Deleted,
                'R' => ChangeKind.Renamed,
                'C' => ChangeKind.Copied,
                'T' => ChangeKind.TypeChanged,
                _ => ChangeKind.Unknown,
            };
            files.Add(new ChangedFile(s.Path, s.OldPath, kind, n.Add, n.Del, n.Binary, s.Similarity));
        }
        return files;
    }

    /// <summary>Unified diff for a single file between two trees.</summary>
    public async Task<FileDiff> GetFileDiffAsync(string from, string to, string path, string? oldPath, int contextLines = 3, int maxLines = 20_000, CancellationToken ct = default, bool ignoreWhitespace = false)
    {
        var args = new List<string>
        {
            "diff", "--no-ext-diff", "--no-textconv", "-M", "--no-color",
            "--src-prefix=a/", "--dst-prefix=b/",
            $"-U{Math.Clamp(contextLines, 0, FullContext)}",
        };
        if (ignoreWhitespace) args.Add("-w");
        args.AddRange([ValidateRev(from), ValidateRev(to), "--", ValidatePath(path)]);
        if (!string.IsNullOrEmpty(oldPath) && oldPath != path) args.Add(ValidatePath(oldPath));
        var output = await _git.RunCheckedAsync(Path, args, ct);
        var parsed = DiffParser.Parse(output, maxLines);
        var match = parsed.FirstOrDefault(f => f.Path == path) ?? parsed.FirstOrDefault();
        return match ?? new FileDiff(path, oldPath, ChangeKind.Modified, false, null, null, []);
    }

    /// <summary>Whole diff between two trees with no context lines (used for moved-code detection).</summary>
    public async Task<List<FileDiff>> GetZeroContextDiffAsync(string from, string to, bool ignoreWhitespace, CancellationToken ct = default)
    {
        var args = new List<string> { "diff", "--no-ext-diff", "--no-textconv", "-M", "--no-color", "--src-prefix=a/", "--dst-prefix=b/", "-U0" };
        if (ignoreWhitespace) args.Add("-w");
        args.AddRange([ValidateRev(from), ValidateRev(to), "--"]);
        return DiffParser.Parse(await _git.RunCheckedAsync(Path, args, ct), maxLinesPerFile: 20_000);
    }

    /// <summary>
    /// For each line of <paramref name="path"/> at <paramref name="from"/>, the last commit up to
    /// <paramref name="to"/> where it still existed (<c>git blame --reverse</c>).
    /// </summary>
    public async Task<List<ReverseBlameLine>> ReverseBlameAsync(string from, string to, string path, CancellationToken ct = default)
    {
        var output = await Run(ct, "blame", "--reverse", "--porcelain", $"{ValidateRev(from)}..{ValidateRev(to)}", "--", ValidatePath(path));
        return ParseReverseBlame(output);
    }

    internal static List<ReverseBlameLine> ParseReverseBlame(string porcelain)
    {
        var result = new List<ReverseBlameLine>();
        foreach (var line in porcelain.Split('\n'))
        {
            // Header lines look like "<sha> <line in sha's version> <line in starting version> [<group size>]".
            if (line.Length < 44 || line[0] == '\t') continue;
            var parts = line.Split(' ');
            if (parts.Length < 3 || parts[0].Length < 40 || !parts[0].All(Uri.IsHexDigit)) continue;
            if (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var orig)) continue;
            if (!int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var final)) continue;
            result.Add(new ReverseBlameLine(final, parts[0], orig));
        }
        return result;
    }

    /// <summary>Commits in <c>base..head</c> that change <paramref name="path"/> (following renames), oldest first.</summary>
    public async Task<List<FileHistoryEntry>> GetFileHistoryAsync(string baseRev, string head, string path, CancellationToken ct = default)
    {
        var output = await Run(ct, "log", "--follow", "-M", "--format=%x1e%H", "--name-status", "-z",
            "--end-of-options", $"{ValidateRev(baseRev)}..{ValidateRev(head)}", "--", ValidatePath(path));
        var entries = new List<FileHistoryEntry>();
        foreach (var record in output.Split('\x1e', StringSplitOptions.RemoveEmptyEntries))
        {
            // Each record is "<sha>\0\n<status>\0<path>[\0<new path>]\0".
            var tokens = record.Split('\0').Select(t => t.Trim('\n')).ToList();
            var sha = tokens[0];
            if (sha.Length < 40) continue;
            var rest = tokens.Skip(1).Where(t => t.Length > 0);
            var status = ParseNameStatus(string.Join('\0', rest.Where(t => t.Length > 0)) + "\0").FirstOrDefault();
            if (status == default) continue;
            var kind = status.Status switch
            {
                'A' => ChangeKind.Added,
                'D' => ChangeKind.Deleted,
                'R' => ChangeKind.Renamed,
                'C' => ChangeKind.Copied,
                'T' => ChangeKind.TypeChanged,
                _ => ChangeKind.Modified,
            };
            entries.Add(new FileHistoryEntry(sha, status.Path, status.OldPath, kind));
        }
        entries.Reverse();
        return entries;
    }

    /// <summary>Stable patch ids for each non-merge commit in <c>base..head</c>, keyed by commit SHA.</summary>
    public async Task<Dictionary<string, string>> GetPatchIdsAsync(string baseRev, string head, CancellationToken ct = default)
    {
        var patches = await Run(ct, "log", "-p", "--no-color", "--no-ext-diff", "--format=commit %H",
            "--end-of-options", $"{ValidateRev(baseRev)}..{ValidateRev(head)}");
        var ids = await _git.RunCheckedAsync(Path, ["patch-id", "--stable"], ct, patches);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in ids.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split(' ');
            if (parts.Length == 2) result[parts[1]] = parts[0];
        }
        return result;
    }

    public async Task<List<string>> ListTreeAsync(string rev, CancellationToken ct = default)
    {
        var output = await Run(ct, "ls-tree", "-r", "-z", "--name-only", "--full-tree", ValidateRev(rev));
        return output.Split('\0', StringSplitOptions.RemoveEmptyEntries).ToList();
    }

    public async Task<BlobContent> GetBlobAsync(string rev, string path, long maxBytes = 2 * 1024 * 1024, CancellationToken ct = default)
    {
        var spec = $"{ValidateRev(rev)}:{ValidatePath(path)}";
        var sizeResult = await _git.RunAsync(Path, ["cat-file", "-s", spec], ct);
        if (!sizeResult.Success) throw new GitException($"'{path}' does not exist at {rev[..Math.Min(rev.Length, 12)]}.", sizeResult.StdErr);
        var size = long.Parse(sizeResult.StdOut.Trim(), CultureInfo.InvariantCulture);
        if (size > maxBytes) return new BlobContent(path, rev, false, true, size, null);
        var text = await Run(ct, "cat-file", "blob", spec);
        var probe = text.AsSpan(0, Math.Min(text.Length, 8000));
        if (probe.Contains('\0')) return new BlobContent(path, rev, true, false, size, null);
        return new BlobContent(path, rev, false, false, size, text);
    }

    /// <summary>Fetches a pull/merge request head from a remote into <c>refs/reviewtool/...</c>.</summary>
    public async Task<string> FetchPullRequestAsync(string remote, int number, string provider = "github", CancellationToken ct = default)
    {
        if (number <= 0) throw new GitException("Pull request number must be positive.");
        var info = await GetInfoAsync(ct);
        if (!info.Remotes.Contains(remote)) throw new GitException($"Unknown remote '{remote}'. Known remotes: {string.Join(", ", info.Remotes)}");
        var source = provider.ToLowerInvariant() switch
        {
            "gitlab" => $"refs/merge-requests/{number}/head",
            _ => $"refs/pull/{number}/head",
        };
        var localRef = $"refs/reviewtool/{remote}/pr-{number}";
        await Run(ct, "fetch", "--no-tags", "--quiet", remote, $"+{source}:{localRef}");
        return localRef;
    }
}
