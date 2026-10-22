// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Sh;

namespace Kumbu.Core.Tests.Sh;

public class LinesTests
{
    [Theory]
    [InlineData("", new string[0])]
    [InlineData("\n", new[] { "" })]
    [InlineData("a", new[] { "a" })]
    [InlineData("a\n", new[] { "a" })]
    [InlineData("a\nb", new[] { "a", "b" })]
    [InlineData("a\n\nb\n\n", new[] { "a", "", "b", "" })]
    public void Split(string text, string[] expected) => Assert.Equal(expected, Lines.Split(text));

    [Theory]
    [InlineData("", "0 lines")]
    [InlineData("a\n", "1 line")]
    [InlineData("a\nb", "2 lines")]
    public void Count(string text, string expected) => Assert.Equal(expected, Lines.Count(text));

    [Fact]
    public void JoinHasNoTrailingNewline() => Assert.Equal("a\nb", Lines.Join(["a", "b"]));
}
