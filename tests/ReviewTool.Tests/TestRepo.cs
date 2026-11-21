using System.Diagnostics;

namespace ReviewTool.Tests;

/// <summary>A throwaway git repository for tests. Deleted on dispose.</summary>
public sealed class TestRepo : IDisposable
{
    private int _clock;

    public string Path { get; }

    public TestRepo()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "reviewtool-test-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(Path);
        Git("init", "-q", "-b", "main");
        Git("config", "user.name", "Test User");
        Git("config", "user.email", "test@example.com");
        Git("config", "commit.gpgsign", "false");
    }

    public string Git(params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = Path, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        var date = new DateTimeOffset(2024, 1, 1, 12, 0, 0, TimeSpan.Zero).AddMinutes(_clock).ToString("o");
        psi.Environment["GIT_AUTHOR_DATE"] = date;
        psi.Environment["GIT_COMMITTER_DATE"] = date;
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {stderr}");
        return stdout.Trim();
    }

    public void Write(string relative, string content)
    {
        var full = System.IO.Path.Combine(Path, relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    public string Commit(string message)
    {
        _clock++;
        Git("add", "-A");
        Git("commit", "-q", "--allow-empty", "-m", message);
        return Git("rev-parse", "HEAD");
    }

    /// <summary>main: one commit; feature: three commits (add, modify, rename+binary).</summary>
    public static TestRepo WithFeatureBranch()
    {
        var repo = new TestRepo();
        repo.Write("src/app.txt", "line 1\nline 2\nline 3\n");
        repo.Write("docs/readme.md", "# Readme\n");
        repo.Commit("Initial");
        repo.Git("checkout", "-q", "-b", "feature");
        repo.Write("src/new.txt", "hello\n");
        repo.Commit("Add new file");
        repo.Write("src/app.txt", "line 1\nline two\nline 3\nline 4\n");
        repo.Commit("Edit app");
        repo.Git("mv", "docs/readme.md", "docs/README.md");
        File.WriteAllBytes(System.IO.Path.Combine(repo.Path, "logo.bin"), [0, 1, 2, 3, 0, 255]);
        repo.Commit("Rename readme, add binary");
        return repo;
    }

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(Path, true);
        }
        catch
        {
            // best effort
        }
    }
}
