using DanmuApi.Core;

namespace DanmuApi.Tests;

public sealed class UnifiedDiffParserTests
{
    [Fact]
    public void EmptyPatchYieldsNoLines()
    {
        Assert.Empty(UnifiedDiffParser.Parse(string.Empty));
    }

    [Fact]
    public void NullPatchThrows()
    {
        Assert.Throws<ArgumentNullException>(() => UnifiedDiffParser.Parse(null!));
    }

    [Fact]
    public void ClassifiesHunkMetaAddedRemovedAndContext()
    {
        var patch = string.Join('\n',
            "--- a/app.js",
            "+++ b/app.js",
            "@@ -1,3 +1,3 @@",
            " const a = 1;",
            "-const b = 2;",
            "+const b = 3;");

        var lines = UnifiedDiffParser.Parse(patch);

        Assert.Equal(
            [
                (DiffLineKind.Meta, "--- a/app.js"),
                (DiffLineKind.Meta, "+++ b/app.js"),
                (DiffLineKind.Hunk, "@@ -1,3 +1,3 @@"),
                (DiffLineKind.Context, "const a = 1;"),
                (DiffLineKind.Removed, "const b = 2;"),
                (DiffLineKind.Added, "const b = 3;"),
            ],
            lines.Select(line => (line.Kind, line.Text)));
    }

    [Fact]
    public void NormalizesCrLfAndDropsTrailingNewline()
    {
        var lines = UnifiedDiffParser.Parse("+added\r\n context\r\n");

        Assert.Equal(
            [
                (DiffLineKind.Added, "added"),
                (DiffLineKind.Context, "context"),
            ],
            lines.Select(line => (line.Kind, line.Text)));
    }

    [Fact]
    public void EmptyAddedLineKeepsEmptyTextWithoutPrefix()
    {
        var lines = UnifiedDiffParser.Parse("+\n+\n");

        Assert.All(lines, line => Assert.Equal((DiffLineKind.Added, string.Empty), (line.Kind, line.Text)));
    }

    [Fact]
    public void MissingLeadingSpaceOnContextLineDoesNotEatFirstCharacter()
    {
        var lines = UnifiedDiffParser.Parse("bare context line");

        var line = Assert.Single(lines);
        Assert.Equal(DiffLineKind.Context, line.Kind);
        Assert.Equal("bare context line", line.Text);
    }
}
