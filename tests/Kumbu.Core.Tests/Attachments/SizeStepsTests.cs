// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Attachments;

namespace Kumbu.Core.Tests.Attachments;

public class SizeStepsTests
{
    [Theory]
    [InlineData(SizeStep.Small, 768, 400_000, 150 * 1024)]
    [InlineData(SizeStep.Medium, 1568, 1_200_000, 400 * 1024)]
    [InlineData(SizeStep.Large, 2576, 3_700_000, 1024 * 1024)]
    public void Each_step_has_its_limits(SizeStep step, int longEdge, int pixels, int bytes)
    {
        Assert.Equal(new StepLimits(longEdge, pixels, bytes), SizeSteps.Of(step));
    }

    [Theory]
    [InlineData("small", SizeStep.Small)]
    [InlineData("Large", SizeStep.Large)]
    [InlineData("MEDIUM", SizeStep.Medium)]
    public void A_name_parses_whatever_its_case(string text, SizeStep expected)
    {
        Assert.True(SizeSteps.TryParse(text, out var step));
        Assert.Equal(expected, step);
        Assert.Equal(text.ToLowerInvariant(), SizeSteps.Name(step));
    }

    [Theory]
    [InlineData("")]
    [InlineData("huge")]
    [InlineData("1")]
    public void Another_word_is_not_a_step(string text)
    {
        Assert.False(SizeSteps.TryParse(text, out _));
    }

    [Fact]
    public void The_table_prints_every_limit()
    {
        var table = SizeSteps.Table;
        foreach (var part in new[] { "768", "1568", "2576", "0.4 MP", "1.2 MP", "3.7 MP", "150 KB", "400 KB", "1 MB", "small", "medium", "large" })
        {
            Assert.Contains(part, table);
        }
    }

    [Fact]
    public void The_table_is_aligned_for_a_monospace_reader()
    {
        Assert.Equal(
            "  step              long edge  pixels  ~Claude tokens  file size aim\n" +
            "  small             768 px     0.4 MP  ~500            150 KB\n" +
            "  medium (default)  1568 px    1.2 MP  ~1,550          400 KB\n" +
            "  large             2576 px    3.7 MP  ~4,750          1 MB",
            SizeSteps.Table);
    }
}
