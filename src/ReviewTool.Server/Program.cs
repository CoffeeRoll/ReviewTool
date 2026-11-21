using ReviewTool.Server;

var builder = WebApplication.CreateBuilder(args);

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
    var index = Path.Combine(app.Environment.WebRootPath ?? Path.Combine(app.Environment.ContentRootPath, "wwwroot"), "index.html");
    if (File.Exists(index))
    {
        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.SendFileAsync(index);
        return;
    }
    context.Response.ContentType = "text/html; charset=utf-8";
    await context.Response.WriteAsync("""
        <!doctype html><meta charset="utf-8"><title>ReviewTool</title>
        <body style="font-family:system-ui;max-width:40rem;margin:4rem auto;line-height:1.5">
        <h1>ReviewTool API is running</h1>
        <p>The web UI has not been built yet. Either run <code>npm install &amp;&amp; npm run build</code> in
        <code>src/ReviewTool.Web</code>, or start the dev server with <code>npm run dev</code> and open
        <a href="http://localhost:5173">http://localhost:5173</a>.</p></body>
        """);
});

var logger = app.Services.GetRequiredService<ILogger<Program>>();
var options = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<ReviewToolOptions>>().Value;
logger.LogInformation("Reviews are stored in {Dir}", options.ResolveDataDirectory());

app.Run();

public partial class Program;
