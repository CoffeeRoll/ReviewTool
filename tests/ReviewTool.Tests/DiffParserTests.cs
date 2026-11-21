using ReviewTool.Core.Git;

namespace ReviewTool.Tests;

public class DiffParserTests
{
    [Fact]
    public void ParsesModifiedFileWithLineNumbers()
    {
        const string diff = """
            diff --git a/src/a.cs b/src/a.cs
            index 1111111..2222222 100644
            --- a/src/a.cs
            +++ b/src/a.cs
            @@ -1,4 +1,5 @@ class A
             one
            -two
            +TWO
            +2.5
             three
             four
            """;
        var file = Assert.Single(DiffParser.Parse(diff));
        Assert.Equal("src/a.cs", file.Path);
        Assert.Equal(ChangeKind.Modified, file.Status);
        var hunk = Assert.Single(file.Hunks);
        Assert.Equal("class A", hunk.Header);
        Assert.Equal(6, hunk.Lines.Count);
        Assert.Equal(new DiffLine(DiffLineType.Delete, 2, null, "two"), hunk.Lines[1]);
        Assert.Equal(new DiffLine(DiffLineType.Add, null, 3, "2.5"), hunk.Lines[3]);
        Assert.Equal(new DiffLine(DiffLineType.Context, 4, 5, "four"), hunk.Lines[5]);
    }

    [Fact]
    public void ParsesAddedDeletedRenamedAndBinaryFiles()
    {
        const string diff = """
            diff --git a/new.txt b/new.txt
            new file mode 100644
            index 0000000..e69de29
            --- /dev/null
            +++ b/new.txt
            @@ -0,0 +1,2 @@
            +a
            +b
            diff --git a/old.txt b/old.txt
            deleted file mode 100644
            index e69de29..0000000
            --- a/old.txt
            +++ /dev/null
            @@ -1 +0,0 @@
            -gone
            diff --git a/docs/a.md b/docs/b.md
            similarity index 90%
            rename from docs/a.md
            rename to docs/b.md
            index 1..2 100644
            --- a/docs/a.md
            +++ b/docs/b.md
            @@ -1 +1 @@
            -x
            +y
            diff --git a/img.png b/img.png
            new file mode 100644
            index 0000000..abc
            Binary files /dev/null and b/img.png differ
            """;
        var files = DiffParser.Parse(diff);
        Assert.Equal(4, files.Count);

        Assert.Equal(ChangeKind.Added, files[0].Status);
        Assert.Null(files[0].OldPath);
        Assert.Equal([1, 2], files[0].Hunks[0].Lines.Select(l => l.NewLine!.Value));

        Assert.Equal(ChangeKind.Deleted, files[1].Status);
        Assert.Equal("old.txt", files[1].Path);
        Assert.Equal(1, files[1].Hunks[0].OldCount);

        Assert.Equal(ChangeKind.Renamed, files[2].Status);
        Assert.Equal("docs/b.md", files[2].Path);
        Assert.Equal("docs/a.md", files[2].OldPath);

        Assert.True(files[3].IsBinary);
        Assert.Empty(files[3].Hunks);
    }

    [Fact]
    public void MarksMissingNewlineAtEndOfFile()
    {
        const string diff = "diff --git a/f b/f\n--- a/f\n+++ b/f\n@@ -1 +1 @@\n-old\n\\ No newline at end of file\n+new\n\\ No newline at end of file\n";
        var lines = DiffParser.Parse(diff)[0].Hunks[0].Lines;
        Assert.All(lines, l => Assert.True(l.NoNewlineAtEnd));
    }

    [Fact]
    public void TruncatesHugeFiles()
    {
        var body = string.Join("\n", Enumerable.Range(1, 50).Select(i => "+line" + i));
        var diff = $"diff --git a/f b/f\n--- /dev/null\n+++ b/f\n@@ -0,0 +1,50 @@\n{body}\n";
        var file = DiffParser.Parse(diff, maxLinesPerFile: 10)[0];
        Assert.True(file.Truncated);
        Assert.Equal(10, file.Hunks.Sum(h => h.Lines.Count));
    }

    [Theory]
    [InlineData("\"tab\\there\"", "tab\there")]
    [InlineData("\"caf\\303\\251.txt\"", "café.txt")]
    [InlineData("plain.txt", "plain.txt")]
    public void UnquotesGitPaths(string input, string expected) => Assert.Equal(expected, DiffParser.Unquote(input));

    [Fact]
    public void HandlesPathsWithSpaces()
    {
        const string diff = "diff --git a/my file.txt b/my file.txt\n--- a/my file.txt\n+++ b/my file.txt\n@@ -1 +1 @@\n-a\n+b\n";
        Assert.Equal("my file.txt", DiffParser.Parse(diff)[0].Path);
    }
}
