// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Sh;

namespace Kumbu.Core.Tests.Sh;

public class PipelineTests
{
    private static string[][] Parse(string input)
    {
        Assert.True(Pipeline.TryParse(input, out var stages, out var error), error);
        return [.. stages.Select(s => s.ToArray())];
    }

    [Fact]
    public void SingleCommandIsOneStage() => Assert.Equal([["ls", "/W"]], Parse("ls /W"));

    [Fact]
    public void PipesSplitStages() =>
        Assert.Equal([["cat", "/W/a.md"], ["rg", "-n", "x"], ["head", "-3"]], Parse("cat /W/a.md | rg -n x | head -3"));

    [Fact]
    public void QuotedPipeStaysInTheStage() => Assert.Equal([["rg", "a|b", "/W"]], Parse("rg 'a|b' /W"));

    [Fact]
    public void EmptyInputHasNoStages() => Assert.Empty(Parse("  "));

    [Theory]
    [InlineData("| head", "sh: syntax error near '|'")]
    [InlineData("ls | | head", "sh: syntax error near '|'")]
    [InlineData("ls |", "sh: syntax error: '|' needs a command after it")]
    [InlineData("ls /W ||", "sh: '||' is not supported; the only operator is '|' (quote it to use it literally)")]
    [InlineData("cat \"/W", "sh: unterminated quote")]
    public void SyntaxErrors(string input, string expected)
    {
        Assert.False(Pipeline.TryParse(input, out var stages, out var error));
        Assert.Equal(expected, error);
        Assert.Empty(stages);
    }
}
