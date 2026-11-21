using System.ComponentModel;
using System.Diagnostics;
using ReviewTool.Core.Git;

namespace ReviewTool.Core.Publishing;

/// <summary>A token plus a human-readable description of where it came from (never the token itself).</summary>
public sealed record ResolvedCredential(string Token, string Source);

public interface ICredentialResolver
{
    Task<ResolvedCredential?> ResolveAsync(IReviewPublisher publisher, RemoteRepository remote, CancellationToken ct = default);
}

public sealed class CredentialSettings
{
    /// <summary>Tokens keyed by "provider:host" (e.g. "github:github.com"), host, or provider id.</summary>
    public Dictionary<string, string> Tokens { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Ask the platform's CLI (e.g. <c>gh auth token</c>) for a token.</summary>
    public bool UseCliTools { get; set; } = true;

    /// <summary>Ask git's credential helper (the one used for push/pull) for a token.</summary>
    public bool UseGitCredentialHelper { get; set; } = true;
}

/// <summary>
/// Looks for a token in order: configuration, environment variables, the platform CLI, and finally
/// git's own credential helper — so existing logins usually just work.
/// </summary>
public sealed class DefaultCredentialResolver(CredentialSettings settings) : ICredentialResolver
{
    private static readonly TimeSpan ExternalTimeout = TimeSpan.FromSeconds(20);

    public async Task<ResolvedCredential?> ResolveAsync(IReviewPublisher publisher, RemoteRepository remote, CancellationToken ct = default)
    {
        foreach (var key in new[] { $"{publisher.Id}:{remote.Host}", remote.Host, publisher.Id })
            if (settings.Tokens.TryGetValue(key, out var configured) && !string.IsNullOrWhiteSpace(configured))
                return new ResolvedCredential(configured.Trim(), "ReviewTool configuration");

        var hints = publisher.GetCredentialHints(remote.Host);
        foreach (var name in hints.EnvironmentVariables)
            if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } value)
                return new ResolvedCredential(value.Trim(), $"{name} environment variable");

        if (settings.UseCliTools && hints.CliTool != null)
        {
            var args = hints.CliArgs.Select(a => a.Replace("{host}", remote.Host, StringComparison.Ordinal)).ToList();
            var token = await RunAsync(hints.CliTool, args, null, ct);
            if (!string.IsNullOrWhiteSpace(token)) return new ResolvedCredential(token.Trim(), $"{hints.CliTool} CLI");
        }

        if (settings.UseGitCredentialHelper)
        {
            var output = await RunAsync("git", ["credential", "fill"], $"protocol=https\nhost={remote.Host}\n\n", ct);
            var password = output?.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("password=", StringComparison.Ordinal))?[9..];
            if (!string.IsNullOrWhiteSpace(password)) return new ResolvedCredential(password, "git credential helper");
        }
        return null;
    }

    /// <summary>Runs a helper program and returns stdout, or null if it isn't installed or fails.</summary>
    private static async Task<string?> RunAsync(string exe, IReadOnlyList<string> args, string? stdin, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0"; // never prompt on the server's console
        psi.Environment["GH_PROMPT_DISABLED"] = "1";
        try
        {
            using var p = Process.Start(psi);
            if (p == null) return null;
            if (stdin != null) await p.StandardInput.WriteAsync(stdin);
            p.StandardInput.Close();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ExternalTimeout);
            var stdout = p.StandardOutput.ReadToEndAsync(timeout.Token);
            _ = p.StandardError.ReadToEndAsync(timeout.Token);
            try { await p.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                try { p.Kill(entireProcessTree: true); } catch { /* gone */ }
                return null;
            }
            return p.ExitCode == 0 ? await stdout : null;
        }
        catch (Win32Exception)
        {
            return null; // not installed
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}
