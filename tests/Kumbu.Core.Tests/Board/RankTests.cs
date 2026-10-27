// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Board;

namespace Kumbu.Core.Tests.Board;

public class RankTests(ITestOutputHelper output)
{
    private static bool Sorted(IReadOnlyList<string> keys) =>
        keys.Zip(keys.Skip(1)).All(p => string.CompareOrdinal(p.First, p.Second) < 0);

    [Theory]
    [InlineData("V", "W")]
    [InlineData("A", "B")]
    [InlineData("A", "Az")]
    [InlineData("1", "2")]
    [InlineData("zz", "zzz1")]
    [InlineData("01", "02")]
    public void Between_two_keys_sorts_between_them(string a, string b)
    {
        var key = Rank.Between(a, b);

        Assert.True(string.CompareOrdinal(a, key) < 0, $"{a} < {key}");
        Assert.True(string.CompareOrdinal(key, b) < 0, $"{key} < {b}");
        Assert.True(Rank.IsValid(key));
    }

    [Fact]
    public void Between_open_ends()
    {
        var first = Rank.Between(null, null);
        var below = Rank.Between(null, first);
        var above = Rank.Between(first, null);
        var belowLowest = Rank.Between(null, "1");
        var aboveHighest = Rank.Between("zzz", null);

        Assert.True(Sorted([below, first, above]));
        Assert.True(string.CompareOrdinal(belowLowest, "1") < 0 && Rank.IsValid(belowLowest));
        Assert.True(string.CompareOrdinal("zzz", aboveHighest) < 0 && Rank.IsValid(aboveHighest));
    }

    [Fact]
    public void Between_refuses_neighbours_in_the_wrong_order()
    {
        Assert.Throws<ArgumentException>(() => Rank.Between("B", "A"));
        Assert.Throws<ArgumentException>(() => Rank.Between("A", "A"));
        Assert.Throws<ArgumentException>(() => Rank.Between("a b", null));
    }

    [Fact]
    public void Thousand_inserts_at_the_top_stay_sorted()
    {
        var keys = new List<string> { Rank.Between(null, null) };
        for (var i = 0; i < 1000; i++) keys.Insert(0, Rank.Between(null, keys[0]));

        Assert.True(Sorted(keys));
        Assert.All(keys, k => Assert.True(Rank.IsValid(k)));
        output.WriteLine($"Longest key after 1000 inserts at the top: {keys.Max(k => k.Length)}");
    }

    [Fact]
    public void Thousand_inserts_at_the_bottom_stay_sorted()
    {
        var keys = new List<string> { Rank.Between(null, null) };
        for (var i = 0; i < 1000; i++) keys.Add(Rank.Between(keys[^1], null));

        Assert.True(Sorted(keys));
        Assert.True(keys.Max(k => k.Length) <= 40);
    }

    [Fact]
    public void Thousand_inserts_between_the_same_two_stay_sorted_and_grow_slowly()
    {
        var low = "V";
        var high = "W";
        var keys = new List<string> { low, high };
        for (var i = 0; i < 1000; i++)
        {
            var key = Rank.Between(low, keys[1]);
            keys.Insert(1, key);
        }

        Assert.True(Sorted(keys));
        var longest = keys.Max(k => k.Length);
        output.WriteLine($"Longest key after 1000 inserts between the same two: {longest}");
        Assert.True(longest <= 40);
    }

    [Fact]
    public void Spread_is_ascending_and_short()
    {
        foreach (var count in new[] { 0, 1, 2, 5, 61, 62, 63, 500, 3000, 10_000 })
        {
            var keys = Rank.Spread(count);

            Assert.Equal(count, keys.Count);
            Assert.True(Sorted(keys), $"count {count}");
            Assert.All(keys, k => Assert.True(Rank.IsValid(k) && k.Length <= 3));
        }
    }

    [Fact]
    public void Spread_leaves_room_between_neighbours()
    {
        var keys = Rank.Spread(10_000);

        Assert.All(keys.Zip(keys.Skip(1)).Take(200), p => Assert.True(Rank.Between(p.First, p.Second).Length <= Rank.MaxLength));
        Assert.True(Rank.Between(null, keys[0]).Length <= 4);
        Assert.True(Rank.Between(keys[^1], null).Length <= 4);
    }

    [Theory]
    [InlineData("")]
    [InlineData("a b")]
    [InlineData("a-b")]
    [InlineData("é")]
    [InlineData("a/")]
    [InlineData("A0")]
    public void Invalid_characters_are_not_ranks(string key) => Assert.False(Rank.IsValid(key));

    [Fact]
    public void Plain_keys_are_ranks() => Assert.True(Rank.IsValid("a0Zz9"));
}
