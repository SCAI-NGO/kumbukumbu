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
    [InlineData("ls /Work\n", new[] { "ls", "/Work" })]
    [InlineData("ls /Work\r\n", new[] { "ls", "/Work" })]
    [InlineData("\r\n  ls /Work \n\n", new[] { "ls", "/Work" })]
    [InlineData("cat \"/Work/a\nb.md\"", new[] { "cat", "/Work/a\nb.md" })]
    [InlineData("cat '/Work/a\r\nb.md'", new[] { "cat", "/Work/a\r\nb.md" })]
    public void Tokenizes(string input, string[] expected)
    {
        Assert.True(Tokenizer.TryTokenize(input, out var tokens, out var error));
        Assert.Null(error);
        Assert.All(tokens, t => Assert.False(t.IsPipe));
        Assert.Equal(expected, tokens.Select(t => t.Text));
    }

    [Theory]
    [InlineData("ls /W | wc -l", "ls,/W,|,wc,-l")]
    [InlineData("ls /W|wc", "ls,/W,|,wc")]
    [InlineData("a | b | c", "a,|,b,|,c")]
    [InlineData("rg 'a|b'", "rg,a|b")]
    [InlineData("rg \"a|b\"", "rg,a|b")]
    [InlineData("rg a\\|b", "rg,a|b")]
    public void PipeIsAnOperatorOnlyUnquoted(string input, string expected)
    {
        Assert.True(Tokenizer.TryTokenize(input, out var tokens, out _));
        Assert.Equal(expected, string.Join(',', tokens.Select(t => t.IsPipe ? "|" : t.Text)));
    }

    [Fact]
    public void QuotedPipeAloneIsAWord()
    {
        Assert.True(Tokenizer.TryTokenize("rg '|'", out var tokens, out _));
        Assert.Equal([Token.Word("rg"), Token.Word("|")], tokens);
    }

    [Theory]
    [InlineData("ls /W || ls /X", "||")]
    [InlineData("ls /W && ls /X", "&&")]
    [InlineData("ls /W &", "&")]
    [InlineData("ls /W; ls /X", ";")]
    [InlineData("ls /W > /X/out.md", ">")]
    [InlineData("ls /W >> /X/out.md", ">>")]
    [InlineData("ls /W 2>&1", ">")]
    [InlineData("wc -l < /W/a.md", "<")]
    [InlineData("cat $(ls /W)", "$(")]
    [InlineData("cat ${HOME}", "${")]
    [InlineData("cat `ls`", "`")]
    [InlineData("(ls /W)", "(")]
    [InlineData("rg foo(bar)", "(")]
    public void OtherOperatorsAreRejected(string input, string op)
    {
        Assert.False(Tokenizer.TryTokenize(input, out var tokens, out var error));
        Assert.Equal($"sh: '{op}' is not supported; the only operator is '|' (quote it to use it literally)", error);
        Assert.Empty(tokens);
    }

    [Theory]
    [InlineData("rg 'a && b; c > d'", "a && b; c > d")]
    [InlineData("rg \"(x)\"", "(x)")]
    [InlineData("rg \\(x\\)", "(x)")]
    [InlineData("rg 'x$'", "x$")]
    [InlineData("rg x$", "x$")]
    [InlineData("rg $HOME", "$HOME")]
    [InlineData("cat /W/a.md#Heading", "/W/a.md#Heading")]
    [InlineData("rg *.md", "*.md")]
    public void OperatorCharactersAreLiteralWhenQuotedOrHarmless(string input, string second)
    {
        Assert.True(Tokenizer.TryTokenize(input, out var tokens, out _));
        Assert.Equal(second, tokens[1].Text);
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

    [Theory]
    [InlineData("ls /Work\nls /Ideas")]
    [InlineData("ls /Work\r\ncat /Ideas/a.md")]
    [InlineData("ls /Work\rls /Ideas")]
    [InlineData("ls \\\n/Work")]
    public void MultipleLinesFail(string input)
    {
        Assert.False(Tokenizer.TryTokenize(input, out var tokens, out var error));
        Assert.Equal("sh: one command per call", error);
        Assert.Empty(tokens);
    }
}
