using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ReviewTool.Server;
using ReviewTool.Server.Cli;

namespace ReviewTool.Tests;

public class HostCheckTests
{
    private static HostCheck Create(string[]? reviewToolHosts = null, string? appAllowedHosts = null)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(appAllowedHosts == null ? [] : [new("AllowedHosts", appAllowedHosts)])
            .Build();
        var options = Options.Create(new ReviewToolOptions { AllowedHosts = [.. reviewToolHosts ?? []] });
        return new HostCheck(options, config, NullLogger<HostCheck>.Instance);
    }

    [Theory]
    [InlineData("localhost:5178", true)]
    [InlineData("127.0.0.1:5178", true)]
    [InlineData("[::1]:5178", true)]
    [InlineData("LOCALHOST", true)]
    [InlineData("evil.example:5178", false)]
    [InlineData("localhost.evil.example", false)]
    public void DefaultsToLoopbackOnly(string host, bool allowed)
        => Assert.Equal(allowed, Create(appAllowedHosts: "*").IsAllowed(new HostString(host)));

    [Fact]
    public void InheritsTheAppsAllowedHosts()
    {
        var check = Create(appAllowedHosts: "reviews.example.com;*.corp.example");
        Assert.True(check.IsAllowed(new HostString("reviews.example.com")));
        Assert.True(check.IsAllowed(new HostString("tools.corp.example:8443")));
        Assert.True(check.IsAllowed(new HostString("localhost:5178"))); // loopback always works
        Assert.False(check.IsAllowed(new HostString("evil.example")));
    }

    [Fact]
    public void ExplicitHostsTakePriorityOverTheAppSetting()
    {
        var check = Create(["reviews.example.com"], appAllowedHosts: "other.example");
        Assert.True(check.IsAllowed(new HostString("reviews.example.com")));
        Assert.False(check.IsAllowed(new HostString("other.example")));
    }

    [Fact]
    public void StarTurnsTheCheckOff()
    {
        var check = Create(["*"]);
        Assert.True(check.AllowsAnyHost);
        Assert.True(check.IsAllowed(new HostString("anything.example")));
    }

    [Fact]
    public void CliAllowsTheBoundHostName()
    {
        Assert.Equal(["10.0.0.5", "dev-box"], CliOptions.Parse(["--host", "dev-box", "--allowed-host", "10.0.0.5"]).EffectiveAllowedHosts().Order());
        var everywhere = CliOptions.Parse(["--host", "0.0.0.0"]);
        Assert.Empty(everywhere.EffectiveAllowedHosts());
        Assert.True(everywhere.ListensEverywhere);
    }
}

public class HostCheckApiTests(ApiTests.Factory factory) : IClassFixture<ApiTests.Factory>
{
    [Fact]
    public async Task RejectsRequestsForOtherHostNames()
    {
        var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("http://localhost/api/health")).StatusCode);

        var rebinding = await client.GetAsync("http://attacker.example/api/reviews");
        Assert.Equal(HttpStatusCode.BadRequest, rebinding.StatusCode);
        Assert.Contains("Host not allowed", await rebinding.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ConfiguredHostsAreAccepted()
    {
        using var configured = factory.WithWebHostBuilder(b => b.UseSetting("ReviewTool:AllowedHosts:0", "reviews.example.com"));
        var client = configured.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("http://reviews.example.com/api/health")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("http://attacker.example/api/health")).StatusCode);
    }
}

/// <summary>ReviewTool's API embedded in a different app, served under its own host name and prefix.</summary>
public class EmbeddedHostTests
{
    private static async Task<HttpClient> StartAppAsync(string allowedHosts)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration["AllowedHosts"] = allowedHosts; // the host app's own setting
        builder.Configuration["ReviewTool:DataDirectory"] = Directory.CreateTempSubdirectory("reviewtool-embedded").FullName;
        builder.Services.AddReviewTool(builder.Configuration);
        var app = builder.Build();
        app.MapReviewToolApi("/review-api");
        app.MapGet("/", () => "host app");
        await app.StartAsync();
        return app.GetTestClient();
    }

    [Fact]
    public async Task InheritsTheHostAppsAllowedHostsWithoutExtraConfiguration()
    {
        var client = await StartAppAsync("portal.example.com");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("http://portal.example.com/review-api/health")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("http://attacker.example/review-api/health")).StatusCode);
    }

    [Fact]
    public async Task OnlyGuardsReviewToolEndpoints()
    {
        // The host app doesn't use host filtering itself; ReviewTool still protects its own endpoints.
        var client = await StartAppAsync("*");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("http://anything.example/")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("http://anything.example/review-api/health")).StatusCode);
    }
}
