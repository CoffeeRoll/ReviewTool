using System.Text.RegularExpressions;

namespace ReviewTool.Core.Publishing;

/// <summary>A hosted repository identified from a git remote URL, e.g. github.com / acme / widgets.</summary>
/// <param name="Owner">Owner or (for GitLab) the full group path, e.g. "group/subgroup".</param>
public sealed partial record RemoteRepository(string RemoteName, string Url, string Host, string Owner, string Name)
{
    public string FullName => $"{Owner}/{Name}";

    // git@github.com:owner/repo.git  (scp-like syntax)
    [GeneratedRegex(@"^(?:[^@/\s]+@)?(?<host>[^:/\s]+):(?<path>[^/\\].*)$")]
    private static partial Regex ScpLike();

    /// <summary>Parses https, ssh and scp-style remote URLs. Returns null for local paths and unknown forms.</summary>
    public static RemoteRepository? TryParse(string remoteName, string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        url = url.Trim();
        string host, path;
        if (url.Contains("://", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http" or "ssh" or "git") || string.IsNullOrEmpty(uri.Host))
                return null;
            host = uri.Host.ToLowerInvariant();
            path = Uri.UnescapeDataString(uri.AbsolutePath);
        }
        else
        {
            var m = ScpLike().Match(url);
            // "C:\repo" looks scp-like; a one-letter host is a Windows drive, not a server.
            if (!m.Success || m.Groups["host"].Value.Length == 1) return null;
            host = m.Groups["host"].Value.ToLowerInvariant();
            path = m.Groups["path"].Value;
        }

        path = path.Trim('/');
        if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) path = path[..^4];
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return null;
        return new RemoteRepository(remoteName, url, host, string.Join('/', parts[..^1]), parts[^1]);
    }
}
