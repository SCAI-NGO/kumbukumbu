// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Attachments;

namespace Kumbu.Core.Tests.Attachments;

public class RotationTests
{
    [Theory]
    [InlineData("90", 90)]
    [InlineData("180", 180)]
    [InlineData("270", 270)]
    [InlineData("0", 0)]
    [InlineData("45", 0)]
    [InlineData("-90", 0)]
    [InlineData("+90", 0)]
    [InlineData("090", 0)]
    [InlineData("90.0", 0)]
    [InlineData(" 90", 0)]
    [InlineData("", 0)]
    public void Only_the_three_angles_parse(string text, int expected)
    {
        Assert.Equal(expected != 0, Rotation.TryParse(text, out var degrees));
        Assert.Equal(expected, degrees);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(90, true)]
    [InlineData(180, false)]
    [InlineData(270, true)]
    public void Only_a_quarter_turn_swaps_the_sides(int degrees, bool swaps) => Assert.Equal(swaps, Rotation.SwapsSides(degrees));
}
