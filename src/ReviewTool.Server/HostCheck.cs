using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace ReviewTool.Server;

/// <summary>
/// Rejects API requests whose Host header isn't an expected name. The API can read any repository on
/// the machine and post with the user's GitHub token, so it must not answer requests that reach it
/// through DNS rebinding (a web page pointing its own domain at 127.0.0.1).
/// </summary>
/// <remarks>
/// Loopback names are always allowed. Other names come from <see cref="ReviewToolOptions.AllowedHosts"/>
/// or, when that is empty, from the host app's own ASP.NET <c>AllowedHosts</c> setting (unless it is "*"),
/// so an app that already restricts its hosts needs no extra configuration. Patterns follow ASP.NET's
/// host filtering: "example.com", "*.example.com", or "*" to allow any host.
/// </remarks>
public sealed class HostCheck
{
    public static readonly string[] LoopbackHosts = ["localhost", "127.0.0.1", "[::1]", "::1"];

    private readonly IList<StringSegment> _patterns;
    private readonly ILogger<HostCheck> _logger;
    private readonly ConcurrentDictionary<string, bool> _warned = new(StringComparer.OrdinalIgnoreCase);

    public bool AllowsAnyHost { get; }

    /// <summary>The effective list (for diagnostics and tests).</summary>
    public IReadOnlyList<string> AllowedHosts { get; }

    public HostCheck(IOptions<ReviewToolOptions> options, IConfiguration configuration, ILogger<HostCheck> logger)
    {
        _logger = logger;
        var configured = Split(options.Value.AllowedHosts);
        if (configured.Count == 0)
        {
            // Inherit the host app's restriction when it has one.
            var appHosts = Split(configuration["AllowedHosts"]?.Split(';'));
            if (!appHosts.Contains("*")) configured = appHosts;
        }
        AllowsAnyHost = configured.Contains("*");
        AllowedHosts = LoopbackHosts.Concat(configured).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        _patterns = AllowedHosts.Select(h => new StringSegment(h)).ToList();
    }

    private static List<string> Split(IEnumerable<string>? values)
        => (values ?? []).SelectMany(v => v.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).ToList();

    public bool IsAllowed(HostString host)
    {
        if (AllowsAnyHost) return true;
        // HTTP/1.0 requests may omit Host; they can't come from a browser page, so allow them.
        if (!host.HasValue) return true;
        return HostString.MatchesAny(new StringSegment(host.Host), _patterns);
    }

    /// <summary>Endpoint filter applied to the whole API group.</summary>
    public async ValueTask<object?> FilterAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var host = context.HttpContext.Request.Host;
        if (IsAllowed(host)) return await next(context);

        if (_warned.TryAdd(host.Host, true))
            _logger.LogWarning(
                "Rejected a ReviewTool API request for host '{Host}'. If ReviewTool is meant to be reached by that name, "
                + "add it to ReviewTool:AllowedHosts (or run with --allowed-host {Host}).", host.Host, host.Host);
        return Results.Problem(
            title: "Host not allowed",
            detail: $"This ReviewTool server doesn't accept requests for '{host.Host}'. Add it to ReviewTool:AllowedHosts if that's intended.",
            statusCode: StatusCodes.Status400BadRequest);
    }
}
