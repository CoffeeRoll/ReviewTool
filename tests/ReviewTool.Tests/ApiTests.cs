using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ReviewTool.Tests;

public class ApiTests : IClassFixture<ApiTests.Factory>, IDisposable
{
    public sealed class Factory : WebApplicationFactory<Program>
    {
        public string DataDir { get; } = Directory.CreateTempSubdirectory("reviewtool-api").FullName;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
            => builder.UseSetting("ReviewTool:DataDirectory", DataDir);

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            try { Directory.Delete(DataDir, true); } catch { /* ignore */ }
        }
    }

    private readonly HttpClient _client;
    private readonly TestRepo _repo = TestRepo.WithFeatureBranch();

    private readonly Factory _factory;

    public ApiTests(Factory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    public void Dispose() => _repo.Dispose();

    [Fact]
    public async Task FullReviewRoundTrip()
    {
        var create = await _client.PostAsJsonAsync("/api/reviews", new { repoPath = _repo.Path, baseRef = "main", headRef = "feature" });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var review = (await create.Content.ReadFromJsonAsync<JsonObject>())!;
        var id = (string)review["id"]!;
        var sha = (string)review["commits"]![1]!["sha"]!;

        var files = await _client.GetFromJsonAsync<JsonArray>($"/api/reviews/{id}/commits/{sha}/files");
        Assert.Equal("src/app.txt", (string)files![0]!["path"]!);
        Assert.Equal("Modified", (string)files[0]!["status"]!);

        var diff = await _client.GetFromJsonAsync<JsonObject>($"/api/reviews/{id}/commits/{sha}/diff?path=src/app.txt&context=-1");
        Assert.NotEmpty(diff!["hunks"]!.AsArray());

        var note = await _client.PostAsJsonAsync($"/api/reviews/{id}/notes", new { commitSha = sha, filePath = "src/app.txt", side = "New", line = 2, body = "Hmm", kind = "Issue" });
        note.EnsureSuccessStatusCode();

        var reviewed = await _client.PutAsJsonAsync($"/api/reviews/{id}/commits/{sha}/reviewed", new { paths = new[] { "src/app.txt" }, reviewed = true });
        reviewed.EnsureSuccessStatusCode();

        var patch = await _client.PatchAsJsonAsync($"/api/reviews/{id}", new { uiState = new { commitKey = sha, layout = "panes" } });
        patch.EnsureSuccessStatusCode();

        var reloaded = await _client.GetFromJsonAsync<JsonObject>($"/api/reviews/{id}");
        Assert.Equal("panes", (string)reloaded!["uiState"]!["layout"]!);
        Assert.Single(reloaded["notes"]!.AsArray());

        var list = await _client.GetFromJsonAsync<JsonArray>("/api/reviews");
        var summary = list!.Single(r => (string)r!["id"]! == id)!;
        Assert.Equal(1, (int)summary["reviewedCommitCount"]!);

        var md = await _client.GetStringAsync($"/api/reviews/{id}/export.md");
        Assert.Contains("**Issue** `src/app.txt:2`", md);

        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync($"/api/reviews/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/api/reviews/{id}")).StatusCode);
    }

    [Fact]
    public async Task ReturnsProblemDetailsForGitErrors()
    {
        var res = await _client.PostAsJsonAsync("/api/reviews", new { repoPath = _repo.Path, baseRef = "main", headRef = "no-such-branch" });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var problem = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("no-such-branch", problem.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task RepoEndpointsDescribeTheRepository()
    {
        var refs = await _client.GetFromJsonAsync<JsonObject>($"/api/repo/refs?path={Uri.EscapeDataString(_repo.Path)}");
        Assert.Equal("main", (string)refs!["defaultBranch"]!);
        var commits = await _client.GetFromJsonAsync<JsonArray>($"/api/repo/commits?path={Uri.EscapeDataString(_repo.Path)}&base=main&head=feature");
        Assert.Equal(3, commits!.Count);
    }

    [Fact]
    public async Task ServesLanguageMappingsForTheUi()
    {
        using var configured = factoryWith("ReviewTool:Languages:.props", "xml");
        var settings = await configured.CreateClient().GetFromJsonAsync<JsonObject>("/api/settings");
        Assert.Equal("xml", (string)settings!["languages"]![".props"]!);
    }

    private WebApplicationFactory<Program> factoryWith(string key, string value) => _factory.WithWebHostBuilder(b => b.UseSetting(key, value));

    [Fact]
    public async Task UnknownApiRoutesAre404()
        => Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync("/api/does-not-exist")).StatusCode);
}
