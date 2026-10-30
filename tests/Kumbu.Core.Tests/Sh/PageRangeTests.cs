// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Sh;

namespace Kumbu.Core.Tests.Sh;

/// <summary>The pages of a <c>pdf</c> call: N, N-M or N- (to the end), counted from 1.</summary>
public class PageRangeTests
{
    [Theory]
    [InlineData("4", 4, 4)]
    [InlineData("5-9", 5, 9)]
    [InlineData("5-", 5, null)]
    [InlineData("7-7", 7, 7)]
    public void A_range_is_parsed(string text, int first, int? last)
    {
        Assert.True(PageRange.TryParse(text, out var range, out var error), error);
        Assert.Equal(new PageRange(first, last), range);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("0-3")]
    [InlineData("x")]
    [InlineData("-3")]
    [InlineData("3-x")]
    [InlineData("1,3")]
    [InlineData(" 4")]
    [InlineData("99999999999")]
    public void Anything_else_is_refused_naming_the_form(string text)
    {
        Assert.False(PageRange.TryParse(text, out _, out var error));
        Assert.Equal($"'{text}' is not a page range: give N, N-M or N- (pages count from 1), e.g. 5-9", error);
    }

    [Fact]
    public void A_range_backwards_is_refused()
    {
        Assert.False(PageRange.TryParse("9-5", out _, out var error));
        Assert.Equal("'9-5' is not a page range: the first page comes first, e.g. 5-9", error);
    }

    [Theory]
    [InlineData(5, 9, 12, 5, 9)]
    [InlineData(5, 99, 12, 5, 12)]
    [InlineData(5, null, 12, 5, 12)]
    [InlineData(12, null, 12, 12, 12)]
    public void A_range_is_clamped_to_the_page_count(int first, int? last, int count, int clampedFirst, int clampedLast)
    {
        Assert.True(new PageRange(first, last).TryClamp(count, "/a.pdf", out var clamped, out var error), error);
        Assert.Equal(new PageRange(clampedFirst, clampedLast), clamped);
    }

    [Theory]
    [InlineData(13, 12, "/a.pdf has 12 pages; there is no page 13")]
    [InlineData(40, 1, "/a.pdf has 1 page; there is no page 40")]
    public void A_start_past_the_end_is_refused(int first, int count, string message)
    {
        Assert.False(new PageRange(first, null).TryClamp(count, "/a.pdf", out _, out var error));
        Assert.Equal(message, error);
    }

    [Theory]
    [InlineData(4, 4, "4")]
    [InlineData(5, 9, "5-9")]
    [InlineData(5, null, "5-")]
    public void It_prints_as_it_is_typed(int first, int? last, string text) => Assert.Equal(text, new PageRange(first, last).ToString());

    [Fact]
    public void One_page_is_single()
    {
        Assert.True(new PageRange(4, 4).IsSingle);
        Assert.False(new PageRange(4, 5).IsSingle);
        Assert.False(new PageRange(4, null).IsSingle);
    }
}
