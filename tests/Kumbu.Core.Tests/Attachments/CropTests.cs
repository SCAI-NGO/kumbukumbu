// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Attachments;

namespace Kumbu.Core.Tests.Attachments;

public class CropTests
{
    [Fact]
    public void Four_numbers_parse()
    {
        Assert.True(Crop.TryParse("0,1200,1440,800", out var crop, out var error));
        Assert.Equal(new Crop(0, 1200, 1440, 800), crop);
        Assert.Equal("", error);
    }

    [Fact]
    public void Decimals_use_a_point_whatever_the_culture()
    {
        var before = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            Assert.True(Crop.TryParse("10.5, 20.25,300,400.5", out var crop, out _));
            Assert.Equal(new Crop(10.5, 20.25, 300, 400.5), crop);
            Assert.Equal("10.5,20.25,300,400.5", crop.ToString());
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = before;
        }
    }

    [Theory]
    [InlineData("1,2,3")]
    [InlineData("1,2,3,4,5")]
    [InlineData("a,b,c,d")]
    [InlineData("")]
    [InlineData("-1,0,10,10")]
    [InlineData("0,-1,10,10")]
    [InlineData("0,0,0,10")]
    [InlineData("0,0,10,0")]
    [InlineData("0,0,-5,10")]
    [InlineData("NaN,0,10,10")]
    [InlineData("0,0,Infinity,10")]
    [InlineData("0,0,10,NaN")]
    public void Anything_else_is_refused_with_the_expected_form(string text)
    {
        Assert.False(Crop.TryParse(text, out _, out var error));
        Assert.Contains("x,y,w,h", error);
    }
}
