using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using ReviewTool.Core.Reviews;

namespace ReviewTool.Core.Publishing.GitHub;

public sealed class GitHubOptions
{
    /// <summary>Extra hosts to treat as GitHub Enterprise Server (github.com and "github.*" hosts are automatic).</summary>
    public List<string> EnterpriseHosts { get; set; } = [];

    /// <summary>API base URL per host, for GitHub Enterprise installs that don't use https://host/api/v3.</summary>
    public Dictionary<string, string> ApiBaseUrls { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Posts reviews to GitHub (and GitHub Enterprise Server) through the REST API.</summary>
public sealed class GitHubPublisher(HttpClient http, GitHubOptions options) : IReviewPublisher
{
    public string Id => "github";
    public string DisplayName => "GitHub";

    public bool CanHandle(RemoteRepository remote)
        => remote.Host == "github.com"
           || remote.Host.StartsWith("github.", StringComparison.Ordinal)
           || options.EnterpriseHosts.Contains(remote.Host, StringComparer.OrdinalIgnoreCase)
           || options.ApiBaseUrls.ContainsKey(remote.Host);

    public string WebUrl(RemoteRepository remote) => $"https://{remote.Host}/{remote.FullName}";

    public CredentialHints GetCredentialHints(string host) => new(
        host == "github.com" ? ["GITHUB_TOKEN", "GH_TOKEN"] : ["GH_ENTERPRISE_TOKEN", "GITHUB_ENTERPRISE_TOKEN"],
        "gh",
        ["auth", "token", "--hostname", "{host}"],
        "Sign in with the GitHub CLI (`gh auth login`), set the GITHUB_TOKEN environment variable, or set "
        + "ReviewTool:Credentials:Tokens:github in appsettings. The token needs permission to write pull request reviews.");

    private string ApiBase(string host)
        => options.ApiBaseUrls.TryGetValue(host, out var configured) ? configured.TrimEnd('/')
            : host == "github.com" ? "https://api.github.com"
            : $"https://{host}/api/v3";

    private string Repo(RemoteRepository r) => $"{ApiBase(r.Host)}/repos/{Uri.EscapeDataString(r.Owner)}/{Uri.EscapeDataString(r.Name)}";

    public async Task<PullRequestInfo> GetPullRequestAsync(RemoteRepository remote, int number, string token, CancellationToken ct)
    {
        var json = await SendAsync(HttpMethod.Get, $"{Repo(remote)}/pulls/{number}", token, null, ct);
        return ToPullRequest(json);
    }

    public async Task<PullRequestInfo?> FindPullRequestAsync(RemoteRepository remote, string branch, string token, CancellationToken ct)
    {
        var url = $"{Repo(remote)}/pulls?state=open&per_page=10&head={Uri.EscapeDataString($"{remote.Owner}:{branch}")}";
        var json = await SendAsync(HttpMethod.Get, url, token, null, ct);
        return json is JsonArray { Count: > 0 } list ? ToPullRequest(list[0]!) : null;
    }

    public async Task<PublishOutcome> PublishAsync(PublishContext context, CancellationToken ct)
    {
        var warnings = new List<string>();
        var url = $"{Repo(context.Remote)}/pulls/{context.PullRequest.Number}/reviews";
        var comments = new JsonArray(context.Plan.Inline.Select(c => (JsonNode)Comment(c, context.InlineBodies[c.Note.Id])).ToArray());
        var payload = new JsonObject
        {
            ["commit_id"] = context.Plan.HeadSha,
            ["body"] = context.Body,
            ["event"] = Event(context.Verdict),
            ["comments"] = comments,
        };

        JsonNode response;
        var inline = context.Plan.Inline.Count;
        try
        {
            response = await SendAsync(HttpMethod.Post, url, context.Token, payload, ct);
        }
        catch (GitHubApiException ex) when (ex.Status == HttpStatusCode.UnprocessableEntity && inline > 0)
        {
            // GitHub rejects the whole review if any inline position is invalid. Post everything as text instead.
            payload["comments"] = new JsonArray();
            payload["body"] = context.FallbackBody;
            try
            {
                response = await SendAsync(HttpMethod.Post, url, context.Token, payload, ct);
            }
            catch (GitHubApiException)
            {
                throw new PublishException(ex.Message); // the original error explains the real problem
            }
            warnings.Add($"GitHub didn't accept the inline comments ({ex.Message}), so all notes were included in the review text instead.");
            inline = 0;
        }

        var reviewUrl = response["html_url"]?.GetValue<string>() ?? context.PullRequest.Url;
        var id = response["id"]?.ToString() ?? "";
        return new PublishOutcome(reviewUrl, id, inline, warnings);
    }

    private static JsonObject Comment(PlannedComment c, string body)
    {
        var side = c.Position.Side == DiffSide.Old ? "LEFT" : "RIGHT";
        var o = new JsonObject
        {
            ["path"] = c.Position.Path,
            ["body"] = body,
            ["line"] = c.Position.EndLine,
            ["side"] = side,
        };
        if (c.Position.StartLine < c.Position.EndLine)
        {
            o["start_line"] = c.Position.StartLine;
            o["start_side"] = side;
        }
        return o;
    }

    private static string Event(PublishVerdict v) => v switch
    {
        PublishVerdict.Approve => "APPROVE",
        PublishVerdict.RequestChanges => "REQUEST_CHANGES",
        _ => "COMMENT",
    };

    private static PullRequestInfo ToPullRequest(JsonNode json)
    {
        var merged = json["merged_at"] is JsonValue m && m.GetValueKind() == JsonValueKind.String;
        return new PullRequestInfo(
            json["number"]!.GetValue<int>(),
            json["title"]?.GetValue<string>() ?? "",
            json["html_url"]?.GetValue<string>() ?? "",
            json["head"]?["sha"]?.GetValue<string>() ?? "",
            json["head"]?["ref"]?.GetValue<string>() ?? "",
            json["base"]?["ref"]?.GetValue<string>() ?? "",
            merged ? "merged" : json["state"]?.GetValue<string>() ?? "open");
    }

    private sealed class GitHubApiException(HttpStatusCode status, string message) : PublishException(message)
    {
        public HttpStatusCode Status { get; } = status;
    }

    private async Task<JsonNode> SendAsync(HttpMethod method, string url, string token, JsonNode? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("ReviewTool", "1.0"));
        // Buffered (with Content-Length) rather than streamed: friendlier to proxies and Enterprise gateways.
        if (body != null) request.Content = new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json");

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new PublishException($"Couldn't reach {new Uri(url).Host}: {ex.Message}");
        }
        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            if (response.IsSuccessStatusCode) return JsonNode.Parse(text.Length == 0 ? "{}" : text)!;
            throw new GitHubApiException(response.StatusCode, Describe(response.StatusCode, text));
        }
    }

    private static string Describe(HttpStatusCode status, string body)
    {
        string? message = null;
        try
        {
            var json = JsonNode.Parse(body);
            message = json?["message"]?.GetValue<string>();
            if (json?["errors"] is JsonArray errors && errors.Count > 0)
                message += ": " + string.Join("; ", errors.Select(e => e is JsonValue v ? v.ToString() : e?["message"]?.ToString() ?? e?.ToJsonString()));
        }
        catch (JsonException)
        {
            // not JSON
        }
        return status switch
        {
            HttpStatusCode.Unauthorized => "GitHub rejected the token (401 Unauthorized). Sign in again or use a different token.",
            HttpStatusCode.NotFound => "GitHub says the repository or pull request doesn't exist, or the token can't see it (404).",
            HttpStatusCode.Forbidden => $"GitHub refused the request (403): {message ?? "the token may lack permission to write pull request reviews"}.",
            _ => $"GitHub returned {(int)status}: {message ?? status.ToString()}",
        };
    }
}
