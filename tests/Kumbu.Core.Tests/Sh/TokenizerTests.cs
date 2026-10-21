// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Sh;

namespace Kumbu.Core.Tests.Sh;

public class TokenizerTests
{
    [Theory]
    [InlineData("ls /Work", new[] { "ls", "/Work" })]
    [InlineData("  ls \t  /Work \t", new[] { "ls", "/Work" })]
    [InlineData("", new string[0])]
    [InlineData("   ", new string[0])]
    [InlineData("cat \"/Work/Meeting Notes.md\"", new[] { "cat", "/Work/Meeting Notes.md" })]
    [InlineData("cat '/Work/it''s.md'", new[] { "cat", "/Work/its.md" })]
    [InlineData("cat '/a\\b.md'", new[] { "cat", "/a\\b.md" })]
    [InlineData("cat \"/a \\\"q\\\".md\"", new[] { "cat", "/a \"q\".md" })]
    [InlineData("cat /Work/Meeting\\ Notes.md", new[] { "cat", "/Work/Meeting Notes.md" })]
    [InlineData("cat \"\"", new[] { "cat", "" })]
    public void Tokenizes(string input, string[] expected)
    {
        Assert.True(Tokenizer.TryTokenize(input, out var tokens, out var error));
        Assert.Null(error);
        Assert.Equal(expected, tokens);
    }

    [Theory]
    [InlineData("cat \"/Work/a.md")]
    [InlineData("cat '/Work/a.md")]
    public void UnterminatedQuoteFails(string input)
    {
        Assert.False(Tokenizer.TryTokenize(input, out var tokens, out var error));
        Assert.Equal("sh: unterminated quote", error);
        Assert.Empty(tokens);
    }
}
