using System.Globalization;
using ReviewTool.Core.Reviews;

namespace ReviewTool.Server.Cli;

/// <summary>Command-line options for the <c>reviewtool</c> command.</summary>
public sealed class CliOptions
{
    public const int DefaultPort = 5178;

    /// <summary>Repository folder to review (defaults to the current directory).</summary>
    public string? RepoPath { get; private set; }
    public string? Base { get; private set; }
    public string? Head { get; private set; }
    public int? PullRequest { get; private set; }
    public string? Remote { get; private set; }
    public string? Host { get; private set; }
    public int? Port { get; private set; }
    public bool NoBrowser { get; private set; }
    public bool ServerOnly { get; private set; }
    public string? DataDirectory { get; private set; }

    /// <summary>Extra host names the API should answer to (besides localhost).</summary>
    public List<string> AllowedHosts { get; } = [];
    public bool ShowHelp { get; private set; }
    public bool ShowVersion { get; private set; }
    public string? Error { get; private set; }

    /// <summary>Arguments not recognized here, passed on to ASP.NET Core (e.g. <c>--urls</c>).</summary>
    public List<string> HostArgs { get; } = [];

    /// <summary>
    /// Host names to allow based on the options: every --allowed-host, plus --host itself when it's a
    /// specific name rather than "listen everywhere" (0.0.0.0, ::, *, +).
    /// </summary>
    public List<string> EffectiveAllowedHosts()
    {
        var list = new List<string>(AllowedHosts);
        if (Host is { } h && h is not ("0.0.0.0" or "::" or "[::]" or "*" or "+" or "localhost")) list.Add(h);
        return list;
    }

    /// <summary>True when --host listens on every interface, so the machine's own names aren't known up front.</summary>
    public bool ListensEverywhere => Host is "0.0.0.0" or "::" or "[::]" or "*" or "+";

    /// <summary>True when the user asked for something specific to review.</summary>
    public bool HasReviewTarget => RepoPath != null || Base != null || Head != null || PullRequest != null;

    public static CliOptions Parse(IReadOnlyList<string> args)
    {
        var o = new CliOptions();
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            string? Value()
            {
                var eq = arg.IndexOf('=');
                if (arg.StartsWith("--", StringComparison.Ordinal) && eq > 0) return arg[(eq + 1)..];
                if (i + 1 < args.Count) return args[++i];
                o.Error ??= $"Missing value for {arg}.";
                return null;
            }
            var name = arg.StartsWith("--", StringComparison.Ordinal) && arg.Contains('=') ? arg[..arg.IndexOf('=')] : arg;
            switch (name)
            {
                case "-h" or "--help" or "-?" or "help":
                    o.ShowHelp = true;
                    break;
                case "--version":
                    o.ShowVersion = true;
                    break;
                case "-b" or "--base":
                    o.Base = Value();
                    break;
                case "--head":
                    o.Head = Value();
                    break;
                case "--pr":
                    var pr = Value();
                    if (pr != null)
                    {
                        if (int.TryParse(pr.TrimStart('#'), NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0) o.PullRequest = n;
                        else o.Error ??= $"--pr expects a pull request number, got '{pr}'.";
                    }
                    break;
                case "--remote":
                    o.Remote = Value();
                    break;
                case "--host":
                    o.Host = Value();
                    break;
                case "-p" or "--port":
                    var port = Value();
                    if (port != null)
                    {
                        if (int.TryParse(port, NumberStyles.None, CultureInfo.InvariantCulture, out var p) && p is >= 0 and <= 65535) o.Port = p;
                        else o.Error ??= $"--port expects a port number, got '{port}'.";
                    }
                    break;
                case "--no-browser":
                    o.NoBrowser = true;
                    break;
                case "--serve":
                    o.ServerOnly = true;
                    break;
                case "--data-dir":
                    o.DataDirectory = Value();
                    break;
                case "--allowed-host":
                    if (Value() is { } allowed) o.AllowedHosts.Add(allowed);
                    break;
                default:
                    if (arg.StartsWith('-'))
                    {
                        // Unknown option: let ASP.NET Core have it (with its value, if any).
                        o.HostArgs.Add(arg);
                        if (!arg.Contains('=') && i + 1 < args.Count && !args[i + 1].StartsWith('-')) o.HostArgs.Add(args[++i]);
                    }
                    else if (o.RepoPath == null) o.RepoPath = arg;
                    else o.Error ??= $"Unexpected argument '{arg}'.";
                    break;
            }
        }
        return o;
    }

    public CreateReviewRequest ToRequest(string defaultRepo) => new(
        RepoPath ?? defaultRepo,
        Base,
        PullRequest == null ? Head : null,
        PullRequest: PullRequest is { } n ? new PullRequestInput(n, Remote) : null);

    public const string HelpText = """
        ReviewTool — review a git branch or pull request one commit at a time.

        Usage:
          reviewtool [path] [options]

        Run it inside a repository to review the current branch against the default branch.
        If a review of the same branches already exists, it is resumed.

        Arguments:
          path                 Repository folder (default: current directory)

        Options:
          -b, --base <ref>     Base branch to compare against (default: origin/HEAD, main or master)
              --head <ref>     Branch with the changes (default: the current branch)
              --pr <number>    Review pull request #number (fetched from the remote)
              --remote <name>  Remote for --pr (default: origin)
          -p, --port <port>    Port to listen on (default: 5178; another free port is used if taken)
              --host <name>    Interface to bind (default: localhost). Keep it local: the API can read any repo.
              --allowed-host <name>
                               Also accept requests addressed to this host name (repeatable). Requests for
                               other names are refused, which protects against DNS-rebinding attacks.
              --no-browser     Don't open a browser window
              --serve          Just start the server and open the review list
              --data-dir <dir> Where reviews are stored (default: ~/.reviewtool)
              --version        Show the version
          -h, --help           Show this help

        Examples:
          reviewtool                          Review the current branch
          reviewtool --pr 123                 Review pull request #123
          reviewtool ../api --base develop    Review ../api's current branch against develop
        """;
}
