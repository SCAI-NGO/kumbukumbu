// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Paths;
using Kumbu.Core.Search;

namespace Kumbu.Core.Tests.Search;

public sealed class PropertyTextsTests
{
    private static readonly VaultPath Path = VaultPath.TryParse("/Work/Sync notes.md", out var p) ? p : throw new InvalidOperationException();

    [Fact]
    public void Of_gives_title_and_value_for_text_summary_and_description()
    {
        var texts = PropertyTexts.Of(Path, "---\nsummary: Offline sync conflicts\ndescription: How two copies meet\n---\n# X\n", ChunkBound.Fallback);

        Assert.Equal([("summary", "Sync notes\nOffline sync conflicts"), ("description", "Sync notes\nHow two copies meet")], texts);
    }

    [Theory]
    [InlineData("---\nsummary: [a, b]\n---\n")]
    [InlineData("---\ndescription: \"  \"\n---\n")]
    [InlineData("---\nsummary: 42\n---\n")]
    [InlineData("---\nsummary:\n  part: one\n---\n")]
    [InlineData("# No frontmatter\n")]
    public void Of_skips_lists_numbers_and_blank(string content)
    {
        Assert.Empty(PropertyTexts.Of(Path, content, ChunkBound.Fallback));
    }

    [Fact]
    public void Of_cuts_a_long_value_to_the_bound()
    {
        var value = string.Concat(Enumerable.Repeat("word ", 1_000)).TrimEnd();
        Assert.True(value.Length >= 4_999);

        var (name, text) = Assert.Single(PropertyTexts.Of(Path, $"---\nsummary: {value}\n---\n", ChunkBound.Fallback));

        Assert.Equal("summary", name);
        Assert.StartsWith("Sync notes\nword word", text);
        Assert.True(ChunkBound.Fallback.Fits(text), $"{text.Length} characters");
    }
}
