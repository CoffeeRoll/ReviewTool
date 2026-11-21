using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using ReviewTool.Core.Git;
using ReviewTool.Core.Reviews;
using ReviewTool.Server;
using ReviewTool.Server.Cli;

var cli = CliOptions.Parse(args);
if (cli.ShowHelp)
{
    Console.WriteLine(CliOptions.HelpText);
    return 0;
}
if (cli.ShowVersion)
{
    Console.WriteLine(ReviewToolApi.Version);
    return 0;
}
if (cli.Error != null)
{
    Console.Error.WriteLine($"error: {cli.Error}\nRun 'reviewtool --help' for usage.");
    return 2;
}

// Content (appsettings, wwwroot) lives next to the app, not in the folder it was started from:
// as a dotnet tool, the current directory is the user's repository.
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = cli.HostArgs.ToArray(),
    ContentRootPath = AppContext.BaseDirectory,
});
// Serve the UI straight from the project's wwwroot when running from source (no-op when installed).
builder.WebHost.UseStaticWebAssets();

if (cli.DataDirectory != null) builder.Configuration["ReviewTool:DataDirectory"] = Path.GetFullPath(cli.DataDirectory);
var cliHosts = cli.EffectiveAllowedHosts();
if (cliHosts.Count > 0)
{
    // Appended after any hosts from configuration.
    var existingHosts = builder.Configuration.GetSection("ReviewTool:AllowedHosts").GetChildren().Count();
    for (var i = 0; i < cliHosts.Count; i++) builder.Configuration[$"ReviewTool:AllowedHosts:{existingHosts + i}"] = cliHosts[i];
}
if (cli.ListensEverywhere && cli.AllowedHosts.Count == 0)
    Console.WriteLine($"Note: listening on all interfaces, but only requests addressed to localhost are accepted. "
        + "Add --allowed-host <name> for each name or IP this machine will be reached by.");
var host = cli.Host ?? "localhost";
var explicitUrls = cli.HostArgs.Any(a => a.StartsWith("--urls", StringComparison.Ordinal)) || Environment.GetEnvironmentVariable("ASPNETCORE_URLS") is { Length: > 0 };
var port = cli.Port ?? CliOptions.DefaultPort;
var isDev = builder.Environment.IsDevelopment();
var wantsReview = !cli.ServerOnly && (cli.HasReviewTarget || !isDev);
var cwd = Directory.GetCurrentDirectory();

// In Development the launch profile (or test host) decides the address.
if (!explicitUrls && (!isDev || cli.Port != null))
{
    if (!RunningInstance.IsPortFree(host, port))
    {
        var existing = $"http://{host}:{port}";
        if (await RunningInstance.IsReviewToolAsync(existing))
        {
            // Another ReviewTool is already serving this port: hand the review to it and exit.
            var target = existing + "/";
            if (wantsReview && await IsGitRepoAsync(cli.RepoPath ?? cwd))
            {
                try
                {
                    var id = await RunningInstance.OpenReviewAsync(existing, cli.ToRequest(cli.RepoPath ?? cwd));
                    target = $"{existing}/#/review/{id}";
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Couldn't start a review: {ex.Message}");
                    target = $"{existing}/#/?repo={Uri.EscapeDataString(Path.GetFullPath(cli.RepoPath ?? cwd))}";
                }
            }
            Console.WriteLine($"ReviewTool is already running at {existing}");
            Console.WriteLine($"  {target}");
            if (!cli.NoBrowser) BrowserLauncher.TryOpen(target);
            return 0;
        }
        Console.WriteLine($"Port {port} is in use; picking another one.");
        port = 0;
    }
    // Kestrel can't pick a dynamic port for "localhost", so use the loopback address for that.
    builder.WebHost.UseUrls(port == 0 && host == "localhost" ? "http://127.0.0.1:0" : $"http://{host}:{port}");
}

builder.Services.AddReviewTool(builder.Configuration);
builder.Services.AddCors(o => o.AddPolicy("dev", p => p
    .WithOrigins("http://localhost:5173", "http://127.0.0.1:5173")
    .AllowAnyHeader()
    .AllowAnyMethod()));

var app = builder.Build();

app.UseReviewToolErrors();
if (app.Environment.IsDevelopment()) app.UseCors("dev");

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapReviewToolApi();

// Client-side routes fall back to the SPA; unknown API routes stay 404.
app.MapFallback("/api/{**rest}", () => Results.NotFound());
app.MapFallback(async context =>
{
    var index = app.Environment.WebRootFileProvider.GetFileInfo("index.html");
    context.Response.ContentType = "text/html; charset=utf-8";
    if (index.Exists)
    {
        await context.Response.SendFileAsync(index);
        return;
    }
    await context.Response.WriteAsync("""
        <!doctype html><meta charset="utf-8"><title>ReviewTool</title>
        <body style="font-family:system-ui;max-width:40rem;margin:4rem auto;line-height:1.5">
        <h1>ReviewTool API is running</h1>
        <p>The web UI has not been built yet. Either run <code>npm install &amp;&amp; npm run build</code> in
        <code>src/ReviewTool.Web</code>, or start the dev server with <code>npm run dev</code> and open
        <a href="http://localhost:5173">http://localhost:5173</a>.</p></body>
        """);
});

await app.StartAsync();

var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault() ?? $"http://{host}:{port}";
// Wildcard and loopback binds print as "localhost" so the URL can be opened.
foreach (var bind in new[] { "127.0.0.1", "0.0.0.0", "[::]", "[::1]", "*", "+" })
    address = address.Replace($"//{bind}:", "//localhost:", StringComparison.Ordinal);
address = address.TrimEnd('/');
var options = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<ReviewToolOptions>>().Value;
var startUrl = address + "/";

if (wantsReview)
{
    var repoPath = cli.RepoPath ?? cwd;
    if (await IsGitRepoAsync(repoPath))
    {
        try
        {
            var service = app.Services.GetRequiredService<ReviewService>();
            var (review, created) = await service.OpenOrCreateAsync(cli.ToRequest(repoPath));
            var active = review.Commits.Count(c => !c.Outdated);
            Console.WriteLine($"{(created ? "Started" : "Resuming")} review \"{review.Name}\" ({active} commit{(active == 1 ? "" : "s")})");
            startUrl = $"{address}/#/review/{review.Id}";
        }
        catch (Exception ex) when (ex is GitException or ReviewValidationException)
        {
            Console.WriteLine($"Couldn't start a review automatically: {ex.Message}");
            Console.WriteLine("Pick the branches in the browser instead.");
            startUrl = $"{address}/#/?repo={Uri.EscapeDataString(Path.GetFullPath(repoPath))}";
        }
    }
    else if (cli.HasReviewTarget)
    {
        Console.Error.WriteLine($"Not a git repository: {Path.GetFullPath(repoPath)}");
    }
}

Console.WriteLine();
Console.WriteLine($"  ReviewTool {ReviewToolApi.Version} is running at {address}");
Console.WriteLine($"  Reviews are saved in {options.ResolveDataDirectory()}");
Console.WriteLine($"  Press Ctrl+C to stop.");
Console.WriteLine();
if (!cli.NoBrowser && !isDev && !BrowserLauncher.TryOpen(startUrl)) Console.WriteLine($"Open {startUrl} in your browser.");
else if (cli.NoBrowser || isDev) Console.WriteLine($"  Open {startUrl}");

await app.WaitForShutdownAsync();
return 0;

static async Task<bool> IsGitRepoAsync(string path)
{
    if (!Directory.Exists(path)) return false;
    var r = await new GitRunner(timeout: TimeSpan.FromSeconds(10)).RunAsync(path, ["rev-parse", "--is-inside-work-tree"]);
    return r.Success && r.StdOut.Trim() == "true";
}

public partial class Program;
