using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http.HttpResults;
using ReviewTool.Core.Git;
using ReviewTool.Core.Publishing;
using ReviewTool.Core.Publishing.GitHub;
using ReviewTool.Core.Reviews;

namespace ReviewTool.Server;

public sealed class ReviewToolOptions
{
    /// <summary>Where reviews are stored. Defaults to <c>~/.reviewtool</c>.</summary>
    public string? DataDirectory { get; set; }

    /// <summary>Path to the git executable.</summary>
    public string GitExecutable { get; set; } = "git";

    public int GitTimeoutSeconds { get; set; } = 120;

    public string ResolveDataDirectory()
        => string.IsNullOrWhiteSpace(DataDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".reviewtool")
            : Path.GetFullPath(Environment.ExpandEnvironmentVariables(DataDirectory));
}

public sealed record SetReviewedRequest(List<string> Paths, bool Reviewed);

public sealed record SetLinesReviewedRequest(string Path, List<string> Ranges, bool Reviewed);

/// <summary>
/// Registration helpers so the review API can be hosted by this server or embedded in another ASP.NET Core app.
/// </summary>
public static class ReviewToolApi
{
    /// <summary>Product version (from the assembly's informational version, without the source hash).</summary>
    public static string Version { get; } =
        (typeof(ReviewToolApi).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "0.0.0")
        .Split('+')[0];

    public static IServiceCollection AddReviewTool(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<ReviewToolOptions>(configuration.GetSection("ReviewTool"));
        services.AddSingleton(sp =>
        {
            var o = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<ReviewToolOptions>>().Value;
            return new GitRunner(o.GitExecutable, TimeSpan.FromSeconds(o.GitTimeoutSeconds));
        });
        services.AddSingleton<IReviewStore>(sp =>
        {
            var o = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<ReviewToolOptions>>().Value;
            return new JsonFileReviewStore(o.ResolveDataDirectory());
        });
        services.AddSingleton<ReviewService>();

        // Publishing: one IReviewPublisher per platform. To add a platform, implement the interface and register it here.
        var credentialSettings = configuration.GetSection("ReviewTool:Credentials").Get<CredentialSettings>() ?? new CredentialSettings();
        var gitHubOptions = configuration.GetSection("ReviewTool:GitHub").Get<GitHubOptions>() ?? new GitHubOptions();
        services.AddHttpClient("publishing", c => c.Timeout = TimeSpan.FromSeconds(60));
        services.AddSingleton<ICredentialResolver>(new DefaultCredentialResolver(credentialSettings));
        services.AddSingleton<IReviewPublisher>(sp =>
            new GitHubPublisher(sp.GetRequiredService<IHttpClientFactory>().CreateClient("publishing"), gitHubOptions));
        services.AddSingleton<PublishingService>();
        services.AddProblemDetails();
        return services;
    }

    /// <summary>Maps domain exceptions to RFC 7807 problem responses.</summary>
    public static IApplicationBuilder UseReviewToolErrors(this IApplicationBuilder app)
        => app.UseExceptionHandler(errorApp => errorApp.Run(async context =>
        {
            var ex = context.Features.Get<IExceptionHandlerFeature>()?.Error;
            var (status, title) = ex switch
            {
                ReviewNotFoundException => (StatusCodes.Status404NotFound, "Not found"),
                ReviewValidationException => (StatusCodes.Status400BadRequest, "Invalid request"),
                PublishException => (StatusCodes.Status400BadRequest, "Couldn't publish"),
                GitException => (StatusCodes.Status400BadRequest, "Git error"),
                ArgumentException => (StatusCodes.Status400BadRequest, "Invalid request"),
                BadHttpRequestException b => (b.StatusCode, "Bad request"),
                _ => (StatusCodes.Status500InternalServerError, "Unexpected error"),
            };
            context.Response.StatusCode = status;
            await Results.Problem(title: title, detail: ex?.Message, statusCode: status).ExecuteAsync(context);
        }));

    public static RouteGroupBuilder MapReviewToolApi(this IEndpointRouteBuilder endpoints, string prefix = "/api")
    {
        var api = endpoints.MapGroup(prefix);

        api.MapGet("/health", () => Results.Ok(new { status = "ok", app = "reviewtool", version = Version }));

        // ---- Repository helpers (used by the "new review" form) ----
        var repo = api.MapGroup("/repo");
        repo.MapGet("/", async (string path, ReviewService svc, CancellationToken ct) =>
            await (await svc.OpenRepoAsync(path, ct)).GetInfoAsync(ct));
        repo.MapGet("/refs", async (string path, ReviewService svc, CancellationToken ct) =>
            await (await svc.OpenRepoAsync(path, ct)).ListRefsAsync(ct));
        repo.MapGet("/commits", async (string path, string @base, string head, ReviewService svc, CancellationToken ct) =>
        {
            var r = await svc.OpenRepoAsync(path, ct);
            return await r.ListCommitsAsync(@base, head, 500, ct);
        });

        // ---- Reviews ----
        var reviews = api.MapGroup("/reviews");
        reviews.MapGet("/", (ReviewService svc, CancellationToken ct) => svc.ListAsync(ct));
        reviews.MapPost("/", async (CreateReviewRequest req, ReviewService svc, CancellationToken ct) =>
        {
            var review = await svc.CreateAsync(req, ct);
            return TypedResults.Created($"{prefix}/reviews/{review.Id}", review);
        });
        reviews.MapPost("/open", async (CreateReviewRequest req, ReviewService svc, CancellationToken ct) =>
        {
            var (review, created) = await svc.OpenOrCreateAsync(req, ct);
            return Results.Ok(new { review, created });
        });
        reviews.MapGet("/{id}", (string id, ReviewService svc, CancellationToken ct) => svc.GetAsync(id, ct));
        reviews.MapPatch("/{id}", (string id, ReviewPatch patch, ReviewService svc, CancellationToken ct) => svc.PatchAsync(id, patch, ct));
        reviews.MapDelete("/{id}", async Task<Results<NoContent, NotFound>> (string id, ReviewService svc, CancellationToken ct) =>
            await svc.DeleteAsync(id, ct) ? TypedResults.NoContent() : TypedResults.NotFound());
        reviews.MapPost("/{id}/refresh", (string id, ReviewService svc, CancellationToken ct) => svc.RefreshAsync(id, ct));

        reviews.MapGet("/{id}/commits/{commit}/files", (string id, string commit, bool? ignoreWhitespace, ReviewService svc, CancellationToken ct) =>
            svc.GetChangedFilesAsync(id, commit, ct, ignoreWhitespace ?? false));
        reviews.MapGet("/{id}/commits/{commit}/diff", (string id, string commit, string path, string? oldPath, int? context, bool? ignoreWhitespace, ReviewService svc, CancellationToken ct) =>
            svc.GetFileDiffAsync(id, commit, path, oldPath, context ?? 3, ct, ignoreWhitespace ?? false));
        reviews.MapGet("/{id}/commits/{commit}/moved", (string id, string commit, bool? ignoreWhitespace, ReviewService svc, CancellationToken ct) =>
            svc.GetMovedBlocksAsync(id, commit, ignoreWhitespace ?? false, ct));
        reviews.MapGet("/{id}/commits/{commit}/later-changes", (string id, string commit, string path, ReviewService svc, CancellationToken ct) =>
            svc.GetLaterChangesAsync(id, commit, path, ct));
        reviews.MapPut("/{id}/commits/{commit}/reviewed-lines", (string id, string commit, SetLinesReviewedRequest req, ReviewService svc, CancellationToken ct) =>
            svc.SetLinesReviewedAsync(id, commit, req.Path, req.Ranges ?? [], req.Reviewed, ct));
        reviews.MapGet("/{id}/file-history", (string id, string path, ReviewService svc, CancellationToken ct) =>
            svc.GetFileHistoryAsync(id, path, ct));
        reviews.MapGet("/{id}/followups", (string id, ReviewService svc, CancellationToken ct) => svc.GetNoteFollowUpsAsync(id, ct));

        // ---- Posting to GitHub (and other platforms) ----
        reviews.MapGet("/{id}/publish/targets", (string id, PublishingService svc, CancellationToken ct) => svc.GetTargetsAsync(id, ct));
        reviews.MapPost("/{id}/publish/preview", (string id, PublishRequest req, PublishingService svc, CancellationToken ct) => svc.PreviewAsync(id, req, ct));
        reviews.MapPost("/{id}/publish", (string id, PublishRequest req, PublishingService svc, CancellationToken ct) => svc.PublishAsync(id, req, ct));
        reviews.MapGet("/{id}/commits/{commit}/tree", (string id, string commit, ReviewService svc, CancellationToken ct) =>
            svc.ListTreeAsync(id, commit, ct));
        reviews.MapGet("/{id}/commits/{commit}/blob", (string id, string commit, string path, DiffSide? side, ReviewService svc, CancellationToken ct) =>
            svc.GetBlobAsync(id, commit, path, side ?? DiffSide.New, ct));
        reviews.MapPut("/{id}/commits/{commit}/reviewed", (string id, string commit, SetReviewedRequest req, ReviewService svc, CancellationToken ct) =>
            svc.SetFilesReviewedAsync(id, commit, req.Paths ?? [], req.Reviewed, ct));

        reviews.MapPost("/{id}/notes", (string id, NoteInput input, ReviewService svc, CancellationToken ct) => svc.AddNoteAsync(id, input, ct));
        reviews.MapPatch("/{id}/notes/{noteId}", (string id, string noteId, NoteInput input, ReviewService svc, CancellationToken ct) =>
            svc.UpdateNoteAsync(id, noteId, input, ct));
        reviews.MapDelete("/{id}/notes/{noteId}", async (string id, string noteId, ReviewService svc, CancellationToken ct) =>
        {
            await svc.DeleteNoteAsync(id, noteId, ct);
            return TypedResults.NoContent();
        });

        reviews.MapGet("/{id}/export.md", async (string id, bool? includeResolved, ReviewService svc, CancellationToken ct) =>
        {
            var review = await svc.GetAsync(id, ct);
            return Results.Text(ReviewService.ExportMarkdown(review, includeResolved ?? true), "text/markdown; charset=utf-8");
        });

        return api;
    }
}
