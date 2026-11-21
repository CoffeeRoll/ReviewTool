using ReviewTool.Core.Git;
using ReviewTool.Core.Reviews;

namespace ReviewTool.Tests;

public class MovedCodeDetectorTests
{
    private static FileDiff File(string path, params DiffHunk[] hunks) => new(path, null, ChangeKind.Modified, false, null, null, hunks);

    private static DiffHunk Deleted(int start, params string[] lines)
        => new(start, lines.Length, start, 0, "", lines.Select((t, i) => new DiffLine(DiffLineType.Delete, start + i, null, t)).ToList());

    private static DiffHunk Added(int start, params string[] lines)
        => new(start, 0, start, lines.Length, "", lines.Select((t, i) => new DiffLine(DiffLineType.Add, null, start + i, t)).ToList());

    private static readonly string[] Method =
    [
        "public int Sum(int[] values)",
        "{",
        "    var total = 0;",
        "    foreach (var v in values) total += v;",
        "    return total;",
        "}",
    ];

    [Fact]
    public void DetectsCodeMovedToAnotherFileEvenWhenReindented()
    {
        var reindented = Method.Select(l => "        " + l).ToArray();
        var blocks = MovedCodeDetector.Detect([File("a.cs", Deleted(10, Method)), File("b.cs", Added(3, reindented))]);
        var block = Assert.Single(blocks);
        Assert.Equal(("a.cs", 10, 15), (block.FromPath, block.FromStart, block.FromEnd));
        Assert.Equal(("b.cs", 3, 8), (block.ToPath, block.ToStart, block.ToEnd));
    }

    [Fact]
    public void IgnoresInPlaceEdits()
    {
        var lines = Method.Select(l => new DiffLine(DiffLineType.Delete, 1, null, l))
            .Concat(Method.Select(l => new DiffLine(DiffLineType.Add, null, 1, "  " + l))).ToList();
        Assert.Empty(MovedCodeDetector.Detect([File("a.cs", new DiffHunk(1, 6, 1, 6, "", lines))]));
    }

    [Fact]
    public void IgnoresShortOrTrivialMatches()
    {
        string[] trivial = ["{", "}", "", "return x;"];
        Assert.Empty(MovedCodeDetector.Detect([File("a.cs", Deleted(1, trivial)), File("b.cs", Added(1, trivial))]));
    }
}

public class LineRangeSetTests
{
    [Fact]
    public void MergesAndSplitsRanges()
    {
        var set = LineRangeSet.Parse(["N1-3", "N5", "O2"]);
        set.Add(["N4"]);
        Assert.Equal(["O2", "N1-5"], set.ToRanges());
        set.Remove(["N3"]);
        Assert.Equal(["O2", "N1-2", "N4-5"], set.ToRanges());
        Assert.True(set.Contains('O', 2));
        Assert.False(set.Contains('N', 3));
    }

    [Theory]
    [InlineData("X1")]
    [InlineData("N0")]
    [InlineData("N5-3")]
    [InlineData("N")]
    public void RejectsInvalidRanges(string range) => Assert.Throws<ArgumentException>(() => LineRangeSet.Parse([range]));
}

public class GeneratedFilesTests
{
    [Theory]
    [InlineData("src/package-lock.json", true)]
    [InlineData("Forms/Main.Designer.cs", true)]
    [InlineData("obj/Foo.g.cs", true)]
    [InlineData("Data/Migrations/AppDbContextModelSnapshot.cs", true)]
    [InlineData("wwwroot/site.min.js", true)]
    [InlineData("src/Program.cs", false)]
    public void RecognizesCommonGeneratedFiles(string path, bool expected) => Assert.Equal(expected, GeneratedFiles.MatchesKnownPattern(path));
}
