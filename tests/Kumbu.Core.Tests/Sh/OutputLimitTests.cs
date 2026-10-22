// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Sh;

namespace Kumbu.Core.Tests.Sh;

public class OutputLimitTests
{
    [Fact]
    public void ExactlyAtLimitIsUnchanged()
    {
        var result = OutputLimit.Apply(ShellResult.Ok(new string('a', 24_000)));
        Assert.Equal(new string('a', 24_000), result.Output);
        Assert.False(result.IsError);
    }

    [Fact]
    public void CutsAfterTheLastWholeLine()
    {
        // 600 lines of 99 chars + '\n' = 60,000 chars; 240 whole lines fit into 24,000.
        var line = new string('x', 99);
        var input = string.Concat(Enumerable.Repeat(line + "\n", 600));

        var result = OutputLimit.Apply(ShellResult.Ok(input));

        var kept = string.Join('\n', Enumerable.Repeat(line, 240));
        Assert.Equal(
            kept + "\n[truncated after line 240 — showing 23999 of 60000 chars (~20000 tokens); narrow it with outline, cat 'note.md#Section', rg, head or tail]",
            result.Output);
    }

    [Fact]
    public void LineEndingExactlyAtTheLimitIsKeptWhole()
    {
        var input = new string('a', 23_999) + "\n" + new string('b', 100);
        var result = OutputLimit.Apply(ShellResult.Ok(input));
        Assert.StartsWith(new string('a', 23_999) + "\n[truncated after line 1 — showing 23999 of", result.Output);
    }

    [Fact]
    public void FirstLineLongerThanTheLimitIsCutAndSaysSo()
    {
        var result = OutputLimit.Apply(ShellResult.Ok(new string('a', 61_200)));
        Assert.Equal(
            new string('a', 24_000)
                + "\n[truncated inside line 1, which is longer than the limit — showing 24000 of 61200 chars (~20400 tokens); narrow it with outline, cat 'note.md#Section', rg, head or tail]",
            result.Output);
    }

    [Fact]
    public void PreservesIsError()
    {
        var result = OutputLimit.Apply(ShellResult.Error(new string('x', 30_000)));
        Assert.True(result.IsError);
        Assert.Contains("[truncated", result.Output);
    }

    [Fact]
    public void DoesNotSplitSurrogatePair()
    {
        var input = new string('a', 23_999) + "😀" + new string('b', 1_000);
        var result = OutputLimit.Apply(ShellResult.Ok(input));
        var footerIndex = result.Output.IndexOf("\n[truncated", StringComparison.Ordinal);
        Assert.Equal(23_999, footerIndex);
        Assert.Equal('a', result.Output[footerIndex - 1]);
        Assert.Contains($"showing 23999 of {input.Length} chars", result.Output);
    }
}
