using System.Diagnostics;
using System.Text;

namespace ReviewTool.Core.Git;

/// <summary>Result of a single git invocation.</summary>
public sealed record GitResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Success => ExitCode == 0;
}

/// <summary>Thrown when git exits with a non-zero code (or cannot be started).</summary>
public sealed class GitException(string message, string? stdErr = null, int exitCode = -1) : Exception(message)
{
    public string? StdErr { get; } = stdErr;
    public int ExitCode { get; } = exitCode;
}

/// <summary>
/// Thin wrapper around the git CLI. Arguments are always passed through
/// <see cref="ProcessStartInfo.ArgumentList"/>, never through a shell, so values
/// cannot be interpreted as shell syntax.
/// </summary>
public sealed class GitRunner
{
    private static readonly string[] BaseArgs =
    [
        "-c", "core.quotepath=false",
        "-c", "color.ui=never",
        "-c", "diff.noprefix=false",
        "-c", "diff.mnemonicPrefix=false",
        "--no-pager",
    ];

    public string GitExecutable { get; }
    public TimeSpan Timeout { get; }

    public GitRunner(string gitExecutable = "git", TimeSpan? timeout = null)
    {
        GitExecutable = gitExecutable;
        Timeout = timeout ?? TimeSpan.FromSeconds(60);
    }

    public async Task<GitResult> RunAsync(string workingDirectory, IEnumerable<string> args, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(GitExecutable)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        foreach (var a in BaseArgs) psi.ArgumentList.Add(a);
        foreach (var a in args) psi.ArgumentList.Add(a);

        // Never block waiting for credentials or an editor.
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.Environment["GIT_ASKPASS"] = "";
        psi.Environment["GIT_EDITOR"] = "true";
        psi.Environment["GIT_PAGER"] = "cat";
        psi.Environment["LC_ALL"] = "C.UTF-8";

        using var process = new Process { StartInfo = psi };
        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            throw new GitException($"Could not start git ('{GitExecutable}'). Is git installed and on PATH? {ex.Message}");
        }
        process.StandardInput.Close();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(Timeout);

        var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            return new GitResult(process.ExitCode, stdout, stderr);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            if (ct.IsCancellationRequested) throw;
            throw new GitException($"git {string.Join(' ', args)} timed out after {Timeout.TotalSeconds:0}s");
        }
    }

    /// <summary>Runs git and throws <see cref="GitException"/> on a non-zero exit code.</summary>
    public async Task<string> RunCheckedAsync(string workingDirectory, IEnumerable<string> args, CancellationToken ct = default)
    {
        var list = args as IReadOnlyCollection<string> ?? args.ToList();
        var result = await RunAsync(workingDirectory, list, ct);
        if (!result.Success)
        {
            var cmd = string.Join(' ', list);
            var err = result.StdErr.Trim();
            throw new GitException($"git {cmd} failed: {(err.Length > 0 ? err : $"exit code {result.ExitCode}")}", err, result.ExitCode);
        }
        return result.StdOut;
    }
}
