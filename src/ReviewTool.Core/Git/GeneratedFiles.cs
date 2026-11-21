namespace ReviewTool.Core.Git;

/// <summary>Heuristics for files that are generated and rarely worth reading line by line.</summary>
public static class GeneratedFiles
{
    private static readonly HashSet<string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        "package-lock.json", "npm-shrinkwrap.json", "yarn.lock", "pnpm-lock.yaml", "bun.lockb",
        "packages.lock.json", "project.assets.json", "Cargo.lock", "Gemfile.lock", "composer.lock",
        "poetry.lock", "Pipfile.lock", "uv.lock", "go.sum", "Podfile.lock", "pubspec.lock", "flake.lock",
    };

    private static readonly string[] Suffixes =
    [
        ".designer.cs", ".g.cs", ".g.i.cs", ".generated.cs", ".min.js", ".min.css", ".js.map", ".css.map",
        ".pb.go", "_pb2.py", ".pb.cc", ".pb.h", ".snap",
    ];

    public static bool MatchesKnownPattern(string path)
    {
        var name = path[(path.LastIndexOf('/') + 1)..];
        if (Names.Contains(name)) return true;
        foreach (var s in Suffixes)
            if (name.EndsWith(s, StringComparison.OrdinalIgnoreCase)) return true;
        // EF Core migration snapshots and designer files.
        if (name.EndsWith("ModelSnapshot.cs", StringComparison.Ordinal) && path.Contains("/Migrations/", StringComparison.Ordinal)) return true;
        return false;
    }
}
