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
    public void TruncatesWithFooter()
    {
        var result = OutputLimit.Apply(ShellResult.Ok(new string('a', 61_200)));
        Assert.Equal(
            new string('a', 24_000) + "\n[truncated — showing 24000 of 61200 chars (~20400 tokens)]",
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
