using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using ReviewTool.Core.Reviews;

namespace ReviewTool.Server.Cli;

/// <summary>Detects (and talks to) a ReviewTool server that is already running on a port.</summary>
public static class RunningInstance
{
    public static bool IsPortFree(string host, int port)
    {
        try
        {
            var address = host is "localhost" ? IPAddress.Loopback : IPAddress.TryParse(host, out var ip) ? ip : IPAddress.Loopback;
            using var listener = new TcpListener(address, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    /// <summary>True if a ReviewTool server answers at <paramref name="baseUrl"/>.</summary>
    public static async Task<bool> IsReviewToolAsync(string baseUrl, CancellationToken ct = default)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            var health = await http.GetFromJsonAsync<JsonElement>($"{baseUrl}/api/health", ct);
            return health.TryGetProperty("app", out var app) && app.GetString() == "reviewtool";
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Asks a running server to open or create a review; returns its id.</summary>
    public static async Task<string> OpenReviewAsync(string baseUrl, CreateReviewRequest request, CancellationToken ct = default)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        var response = await http.PostAsJsonAsync($"{baseUrl}/api/reviews/open", request, ct);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        if (!response.IsSuccessStatusCode)
        {
            var detail = body.TryGetProperty("detail", out var d) ? d.GetString() : response.ReasonPhrase;
            throw new ReviewValidationException(detail ?? "The running ReviewTool server could not open the review.");
        }
        return body.GetProperty("review").GetProperty("id").GetString()!;
    }
}
