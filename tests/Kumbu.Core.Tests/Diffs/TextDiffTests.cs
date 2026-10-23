// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Diffs;

namespace Kumbu.Core.Tests.Diffs;

public class TextDiffTests
{
    private static string Script(string a, string b) => string.Concat(
        Myers.Diff(a.ToCharArray(), b.ToCharArray()).Select(e => e.Kind switch { EditKind.Added => $"+{e.Item}", EditKind.Removed => $"-{e.Item}", _ => $"{e.Item}" }));

    [Theory]
    [InlineData("", "", "")]
    [InlineData("abc", "abc", "abc")]
    [InlineData("", "ab", "+a+b")]
    [InlineData("ab", "", "-a-b")]
    [InlineData("abcabba", "cbabac", "-a-bc+bab-ba+c")]
    public void MyersFindsAShortestScript(string a, string b, string expected)
    {
        var edits = Myers.Diff(a.ToCharArray(), b.ToCharArray());
        Assert.Equal(new string([.. edits.Where(e => e.Kind != EditKind.Added).Select(e => e.Item)]), a);
        Assert.Equal(new string([.. edits.Where(e => e.Kind != EditKind.Removed).Select(e => e.Item)]), b);
        Assert.Equal(expected.Count(c => c is '+' or '-'), Script(a, b).Count(c => c is '+' or '-'));
    }

    [Fact]
    public void RandomPairsReproduceBothSides()
    {
        var random = new Random(42);
        for (var run = 0; run < 300; run++)
        {
            var a = Enumerable.Range(0, random.Next(0, 30)).Select(_ => random.Next(0, 4)).ToArray();
            var b = Enumerable.Range(0, random.Next(0, 30)).Select(_ => random.Next(0, 4)).ToArray();
            var edits = Myers.Diff(a, b);
            Assert.Equal(a, edits.Where(e => e.Kind != EditKind.Added).Select(e => e.Item));
            Assert.Equal(b, edits.Where(e => e.Kind != EditKind.Removed).Select(e => e.Item));
        }
    }

    [Fact]
    public void UnifiedHunksWithThreeLinesOfContext()
    {
        var before = string.Join('\n', Enumerable.Range(1, 20).Select(i => $"line {i}")) + "\n";
        var after = before.Replace("line 5\n", "line five\n").Replace("line 18\n", "");

        Assert.Equal(
            [
                "@@ -2,7 +2,7 @@", " line 2", " line 3", " line 4", "-line 5", "+line five", " line 6", " line 7", " line 8",
                "@@ -15,6 +15,5 @@", " line 15", " line 16", " line 17", "-line 18", " line 19", " line 20",
            ],
            TextDiff.Hunks(before, after, words: false));
    }

    [Fact]
    public void CloseChangesShareAHunk() =>
        Assert.Single(TextDiff.Hunks("a\nb\nc\nd\ne\nf\ng\n", "A\nb\nc\nd\ne\nf\nG\n", words: false), l => l.StartsWith("@@"));

    [Fact]
    public void EmptyAndMissingFinalNewline()
    {
        Assert.Empty(TextDiff.Hunks("x", "x\n", words: false));
        Assert.Equal(["@@ -0,0 +1 @@", "+new"], TextDiff.Hunks("", "new", words: false));
        Assert.Equal(["@@ -1 +0,0 @@", "-old"], TextDiff.Hunks("old\n", "", words: false));
    }

    [Fact]
    public void WordDiffInsideALongLine()
    {
        var before = "Das Projekt Hecate startet im März, sagt Murat.\n";
        var after = "Das Projekt Selene startet im April, sagt Murat.\n";

        Assert.Equal(
            ["@@ -1 +1 @@", "~Das Projekt [-Hecate-]{+Selene+} startet im [-März-]{+April+}, sagt Murat."],
            TextDiff.Hunks(before, after, words: true));
    }

    [Fact]
    public void WordDiffAcrossLines() =>
        Assert.Equal("a [-b-]{+B+}\nc{+ d+}", TextDiff.Words("a b\nc", "a B\nc d"));

    [Fact]
    public void CountsLines() => Assert.Equal((2, 1), TextDiff.Count("a\nb\nc\n", "a\nB\nc\nd\n"));

    [Fact]
    public void LongSimilarTextsAreFast()
    {
        var before = string.Concat(Enumerable.Range(0, 20_000).Select(i => $"line {i}\n"));
        var after = before.Replace("line 10000\n", "changed\n");
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.Equal((1, 1), TextDiff.Count(before, after));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), watch.Elapsed.ToString());
    }
}
