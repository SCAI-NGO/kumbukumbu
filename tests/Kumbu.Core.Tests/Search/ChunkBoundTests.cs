// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Search;

namespace Kumbu.Core.Tests.Search;

public class ChunkBoundTests
{
    private static UnigramTokenizer Tokenizer()
    {
        Assert.SkipUnless(TestTokenizer.Load() is not null, TestTokenizer.SkipReason);
        return TestTokenizer.Load()!;
    }

    [Fact]
    public void Count_is_Encode_without_the_cut()
    {
        var tokenizer = Tokenizer();
        var text = string.Concat(Enumerable.Repeat(DenseFixtures.GermanProse + "\n", 10))[..3000];
        Assert.True(tokenizer.Count(text) > 512);
        Assert.Equal(tokenizer.Encode(text, int.MaxValue).Count, tokenizer.Count(text));

        const string Short = "Die Pumpe läuft.";
        Assert.Equal(tokenizer.Encode(Short).Count, tokenizer.Count(Short));
    }

    [Theory]
    [InlineData(nameof(DenseFixtures.GermanProse))]
    [InlineData(nameof(DenseFixtures.NumericTable))]
    [InlineData(nameof(DenseFixtures.Code))]
    [InlineData(nameof(DenseFixtures.Urls))]
    public void Costs_of_lines_add_up_to_the_count(string fixture)
    {
        var tokenizer = Tokenizer();
        var bound = ChunkBound.Of(tokenizer, TestTokenizer.Sha256);
        var lines = ((string)typeof(DenseFixtures).GetField(fixture)!.GetValue(null)!).Split('\n');
        Assert.Equal(tokenizer.Count(string.Join('\n', lines)), 2 + lines.Sum(bound.Cost));
        // From the memo, the same again.
        Assert.Equal(tokenizer.Count(string.Join('\n', lines)), 2 + lines.Sum(bound.Cost));
    }

    [Fact]
    public void The_fallback_counts_characters()
    {
        Assert.True(ChunkBound.Fallback.Fits(new string('x', 500)));
        Assert.False(ChunkBound.Fallback.Fits(new string('x', 501)));
        Assert.Equal(501, ChunkBound.Fallback.Budget);
        Assert.Equal(4, ChunkBound.Fallback.Cost("abc"));
        Assert.Equal("fallback", ChunkBound.Fallback.Id);
    }

    [Fact]
    public void Without_a_provider_only_words_count()
    {
        Assert.Equal("words", ChunkBound.Unbounded.Id);
        Assert.Equal(0, ChunkBound.Unbounded.Cost(DenseFixtures.Symbols));
        Assert.Equal(int.MaxValue, ChunkBound.Unbounded.Budget);
        Assert.True(ChunkBound.Unbounded.Fits(new string('x', 100_000)));
    }

    [Fact]
    public void The_scheme_names_the_bound()
    {
        var tokenizer = new UnigramTokenizer([("<s>", 0), ("<pad>", 0), ("</s>", 0), ("<unk>", 0)], unkId: 3, precompiledCharsMap: null);
        var bound = ChunkBound.Of(tokenizer, TestTokenizer.Sha256);
        Assert.Equal("2/tok-f1cc44ad7faa", bound.Scheme);
        Assert.Equal(ChunkBound.MaxTokens - 2, bound.Budget);
        Assert.Equal("2/fallback", ChunkBound.Fallback.Scheme);
    }

    [Fact]
    public void The_fallback_keeps_dense_text_under_the_limit()
    {
        var tokenizer = Tokenizer();
        foreach (var (name, text) in DenseFixtures.Dense)
        {
            var prefix = text[..ChunkBound.FallbackMaxChars];
            Assert.True(ChunkBound.Fallback.Fits(prefix));
            Assert.True(tokenizer.Count(prefix) <= ChunkBound.MaxTokens, $"{name}: {tokenizer.Count(prefix)} tokens");
        }
    }
}
