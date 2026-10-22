// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Sh;
using Kumbu.Core.Tests.Fakes;

namespace Kumbu.Core.Tests.Sh;

/// <summary>head, tail, wc and sort, through the shell as an agent would call them.</summary>
public class FilterTests
{
    private static readonly string Twelve = string.Concat(Enumerable.Range(1, 12).Select(i => $"line {i}\n"));

    private static readonly Shell Sh = new(new InMemoryNoteReader(
        ("/W/twelve.md", Twelve),
        ("/W/words.md", "Größe über alles\nzwei  Wörter\n"),
        ("/W/unsorted.md", "b\nB\na\n10 ten\n9 nine\nb\n-1 minus\n"),
        ("/W/no-newline.md", "a\nb"),
        ("/W/dir/x.md", "x")));

    private static async Task<string> Ok(string line)
    {
        var result = await Sh.RunAsync(line, CancellationToken.None);
        Assert.False(result.IsError, result.Output);
        return result.Output;
    }

    private static async Task<string> Err(string line)
    {
        var result = await Sh.RunAsync(line, CancellationToken.None);
        Assert.True(result.IsError, result.Output);
        return result.Output;
    }

    private static string Range(int from, int to) =>
        string.Join('\n', Enumerable.Range(from, to - from + 1).Select(i => $"line {i}"));

    [Theory]
    [InlineData("head /W/twelve.md", 1, 10)]
    [InlineData("head -n 3 /W/twelve.md", 1, 3)]
    [InlineData("head -3 /W/twelve.md", 1, 3)]
    [InlineData("head /W/twelve.md -n3", 1, 3)]
    [InlineData("head --lines=2 /W/twelve.md", 1, 2)]
    [InlineData("cat /W/twelve.md | head -n 4", 1, 4)]
    [InlineData("head -n 100 /W/twelve.md", 1, 12)]
    [InlineData("tail /W/twelve.md", 3, 12)]
    [InlineData("tail -n 2 /W/twelve.md", 11, 12)]
    [InlineData("tail -2 /W/twelve.md", 11, 12)]
    [InlineData("tail -n +11 /W/twelve.md", 11, 12)]
    [InlineData("tail -n +1 /W/twelve.md", 1, 12)]
    [InlineData("tail -n +0 /W/twelve.md", 1, 12)]
    [InlineData("cat /W/twelve.md | tail -n +5 | head -n 2", 5, 6)]
    public async Task HeadAndTail(string line, int from, int to) => Assert.Equal(Range(from, to), await Ok(line));

    [Theory]
    [InlineData("head -n 0 /W/twelve.md")]
    [InlineData("tail -n 0 /W/twelve.md")]
    public async Task ZeroLinesIsEmpty(string line) => Assert.Equal("", await Ok(line));

    [Theory]
    [InlineData("head -n x /W/twelve.md", "head: invalid number of lines: 'x'")]
    [InlineData("head -n -2 /W/twelve.md", "head: invalid number of lines: '-2'")]
    [InlineData("tail -n +x /W/twelve.md", "tail: invalid number of lines: '+x'")]
    [InlineData("head", "head: missing operand (give a path, or pipe into it)")]
    [InlineData("head /W/twelve.md /W/words.md", "head: too many arguments")]
    [InlineData("head twelve.md", "head: twelve.md: paths must be absolute (there is no working directory)")]
    [InlineData("head /W/nope.md", "head: /W/nope.md: No such file or directory")]
    [InlineData("tail /W/dir", "tail: /W/dir: Is a directory")]
    [InlineData("ls /W | head /W/twelve.md", "head: reads from the pipe, so it takes no path (got '/W/twelve.md')")]
    [InlineData("head -x /W/twelve.md", "head: invalid option -- 'x'")]
    public async Task HeadAndTailErrors(string line, string expected) => Assert.Equal(expected, await Err(line));

    [Theory]
    [InlineData("wc /W/words.md", "2 5 34 /W/words.md")]
    [InlineData("wc -l /W/words.md", "2 /W/words.md")]
    [InlineData("wc -w /W/words.md", "5 /W/words.md")]
    [InlineData("wc -m /W/words.md", "30 /W/words.md")]
    [InlineData("wc -c /W/words.md", "34 /W/words.md")]
    [InlineData("wc -lw /W/words.md", "2 5 /W/words.md")]
    [InlineData("wc --lines /W/no-newline.md", "2 /W/no-newline.md")]
    [InlineData("cat /W/twelve.md | wc -l", "12")]
    [InlineData("ls /W | wc -l", "5")]
    [InlineData("cat /W/twelve.md | head -0 | wc", "0 0 0")]
    public async Task Wc(string line, string expected) => Assert.Equal(expected, await Ok(line));

    [Theory]
    [InlineData("sort /W/unsorted.md", "-1 minus\n10 ten\n9 nine\na\nb\nb\nB")]
    [InlineData("sort -r /W/unsorted.md", "B\nb\nb\na\n9 nine\n10 ten\n-1 minus")]
    [InlineData("sort -u /W/unsorted.md", "-1 minus\n10 ten\n9 nine\na\nb\nB")]
    [InlineData("sort -n /W/unsorted.md", "-1 minus\na\nb\nb\nB\n9 nine\n10 ten")]
    [InlineData("sort -f /W/unsorted.md", "-1 minus\n10 ten\n9 nine\na\nb\nb\nB")]
    [InlineData("sort -fu /W/unsorted.md", "-1 minus\n10 ten\n9 nine\na\nb")]
    [InlineData("sort -rn /W/unsorted.md", "10 ten\n9 nine\nB\nb\nb\na\n-1 minus")]
    [InlineData("ls /W | sort -r | head -2", "words.md\nunsorted.md")]
    public async Task Sort(string line, string expected) => Assert.Equal(expected, await Ok(line));
}

public class TextOrderTests
{
    private static readonly Shell Sh = new(new InMemoryNoteReader(
        ("/W/names.md", "Zebra\nÜberlegungen\nuhu\nApfel\näpfel\napfel\nUlm\n"),
        ("/W/Zoo.md", "z"), ("/W/Übersicht.md", "ü"), ("/W/auto.md", "a"), ("/W/Ulm.md", "u")));

    private static async Task<string> Ok(string line)
    {
        var result = await Sh.RunAsync(line, CancellationToken.None);
        Assert.False(result.IsError, result.Output);
        return result.Output;
    }

    [Fact]
    public void RunsWithIcu() => Assert.True(TextOrder.IsCultureAware, "ICU is missing: umlauts would sort after z");

    [Fact]
    public async Task SortPutsUmlautsNextToTheirBaseLetter() =>
        Assert.Equal("apfel\nApfel\näpfel\nÜberlegungen\nuhu\nUlm\nZebra", await Ok("sort /W/names.md"));

    [Fact]
    public async Task SortUniqueIgnoringCaseKeepsUmlautsApart() =>
        Assert.Equal("apfel\näpfel\nÜberlegungen\nuhu\nUlm\nZebra", await Ok("sort -fu /W/names.md"));

    [Fact]
    public async Task LsUsesTheSameOrder() =>
        Assert.Equal("auto.md\nnames.md\nÜbersicht.md\nUlm.md\nZoo.md", await Ok("ls /W"));

    [Fact]
    public async Task RgFilesUsesTheSameOrder() => Assert.Equal(
        "/W/auto.md\n/W/names.md\n/W/Übersicht.md\n/W/Ulm.md\n/W/Zoo.md", await Ok("rg --files /W"));
}
