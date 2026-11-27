// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Search;
using Microsoft.Extensions.Time.Testing;

namespace Kumbu.Core.Tests.Search;

/// <summary>The token a call that costs seconds of CPU runs under: the caller's, and ended at the time limit too.</summary>
public class HeavyCallTests
{
    private static FakeTimeProvider Clock() => new(new DateTimeOffset(2037, 1, 1, 0, 0, 0, TimeSpan.Zero));

    [Fact]
    public void Its_token_ends_at_the_time_limit()
    {
        var clock = Clock();
        using var call = new HeavyCall(CancellationToken.None, TimeSpan.FromSeconds(10), clock);
        clock.Advance(TimeSpan.FromSeconds(9));
        Assert.False(call.Token.IsCancellationRequested);

        clock.Advance(TimeSpan.FromSeconds(1));

        Assert.True(call.Token.IsCancellationRequested);
        Assert.True(call.TimedOut);
    }

    [Fact]
    public void Its_token_ends_with_the_callers_and_that_is_no_time_out()
    {
        using var left = new CancellationTokenSource();
        using var call = new HeavyCall(left.Token, TimeSpan.FromSeconds(10), Clock());
        Assert.False(call.Token.IsCancellationRequested);

        left.Cancel();

        Assert.True(call.Token.IsCancellationRequested);
        Assert.False(call.TimedOut);
    }
}
