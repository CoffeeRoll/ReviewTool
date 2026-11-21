using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http.HttpResults;
using ReviewTool.Core.Git;
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

/// <summary>
/// Registration helpers so the review API can be hosted by this server or embedded in another ASP.NET Core app.
/// </summary>
public static class ReviewToolApi
{
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

        api.MapGet("/health", () => Results.Ok(new { status = "ok" }));

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
        reviews.MapGet("/{id}", (string id, ReviewService svc, CancellationToken ct) => svc.GetAsync(id, ct));
        reviews.MapPatch("/{id}", (string id, ReviewPatch patch, ReviewService svc, CancellationToken ct) => svc.PatchAsync(id, patch, ct));
        reviews.MapDelete("/{id}", async Task<Results<NoContent, NotFound>> (string id, ReviewService svc, CancellationToken ct) =>
            await svc.DeleteAsync(id, ct) ? TypedResults.NoContent() : TypedResults.NotFound());
        reviews.MapPost("/{id}/refresh", (string id, ReviewService svc, CancellationToken ct) => svc.RefreshAsync(id, ct));

        reviews.MapGet("/{id}/commits/{commit}/files", (string id, string commit, ReviewService svc, CancellationToken ct) =>
            svc.GetChangedFilesAsync(id, commit, ct));
        reviews.MapGet("/{id}/commits/{commit}/diff", (string id, string commit, string path, string? oldPath, int? context, ReviewService svc, CancellationToken ct) =>
            svc.GetFileDiffAsync(id, commit, path, oldPath, context ?? 3, ct));
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
