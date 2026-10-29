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
    [InlineData("-0,-0.0,10,10", "0,0,10,10")]
    [InlineData("0.0000001,1e2,10.123456,1E-1", "0,100,10.1235,0.1")]
    [InlineData("1000000,0,1,1", "1000000,0,1,1")]
    public void Numbers_are_kept_to_four_places_as_plain_decimals(string text, string shown)
    {
        // The sidecar takes digits with an optional decimal point only: no sign, no exponent.
        Assert.True(Crop.TryParse(text, out var crop, out _));
        Assert.Equal(shown, crop.ToString());
        Assert.Matches(@"^\d+(\.\d+)?(,\d+(\.\d+)?){3}$", crop.ToString());
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
    [InlineData("0,0,0.00001,10")]
    [InlineData("0,0,10,1e-7")]
    [InlineData("1e20,0,10,10")]
    [InlineData("0,0,1000000.1,10")]
    public void Anything_else_is_refused_with_the_expected_form(string text)
    {
        Assert.False(Crop.TryParse(text, out _, out var error));
        Assert.Contains("x,y,w,h", error);
    }
}
