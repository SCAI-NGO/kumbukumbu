// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Queries;

namespace Kumbu.Core.Tests.Queries;

// The limits on a base, which is anyone's input; what a base gives is in the conformance cases.
public class BaseTests
{
    [Fact]
    public void Anchors_that_expand_tenfold_per_level_are_an_error_not_a_hang()
    {
        var yaml = "a: &a [x, x, x, x, x, x, x, x, x, x]\n"
                   + string.Concat(Enumerable.Range(0, 8).Select(i => $"{(char)('b' + i)}: &{(char)('b' + i)} [{string.Join(", ", Enumerable.Repeat($"*{(char)('a' + i)}", 10))}]\n"));

        Assert.Equal("base", Assert.Throws<QueryError>(() => Base.Parse(yaml)).Code);
    }

    [Fact]
    public void Filter_groups_nested_too_deeply_are_an_error()
    {
        var yaml = "filters: " + string.Concat(Enumerable.Repeat("{and: [", 100)) + "true" + string.Concat(Enumerable.Repeat("]}", 100)) + "\n";

        var e = Assert.Throws<QueryError>(() => Base.Parse(yaml).Run(null, [], null, DateTime.UnixEpoch));
        Assert.Equal("base", e.Code);
    }
}
