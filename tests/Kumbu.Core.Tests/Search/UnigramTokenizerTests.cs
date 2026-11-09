// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using System.Text.Json;
using Kumbu.Core.Search;

namespace Kumbu.Core.Tests.Search;

public class UnigramTokenizerTests
{
    private static readonly UnigramTokenizer Tiny = new(
        [("<s>", 0), ("<pad>", 0), ("</s>", 0), ("<unk>", 0), ("▁", -2), ("▁he", -3), ("llo", -3), ("▁hello", -4), ("h", -5), ("e", -5), ("l", -5), ("o", -5), ("▁w", -4), ("orld", -4)],
        unkId: 3, precompiledCharsMap: null);

    [Fact]
    public void ThePrimaryPathWinsAndWordsGetASpaceMark() =>
        Assert.Equal([0, 7, 12, 13, 2], Tiny.Encode("hello world"));

    [Fact]
    public void UnknownCharactersFuseIntoOneUnk() =>
        Assert.Equal([0, 7, 4, 3, 2], Tiny.Encode("  hello\t?!🐈 "));

    [Fact]
    public void LongTextIsCutWithTheEndMarkKept()
    {
        var ids = Tiny.Encode(string.Join(' ', Enumerable.Repeat("hello", 100)), maxLength: 8);
        Assert.Equal([0, 7, 7, 7, 7, 7, 7, 2], ids);
    }

    private const string TinyVocab = """[["<s>",0],["<pad>",0],["</s>",0],["<unk>",0],["▁",-2],["▁he",-3],["llo",-3],["▁hello",-4],["h",-5],["e",-5],["l",-5],["o",-5],["▁w",-4],["orld",-4]]""";

    private static UnigramTokenizer TinyFromJson() => UnigramTokenizer.Load(new MemoryStream(Encoding.UTF8.GetBytes(
        """{"normalizer":{"type":"Sequence","normalizers":[{"type":"Replace","pattern":{"Regex":" {2,}"},"content":" "}]},""" +
        """ "pre_tokenizer":{"type":"Metaspace","replacement":"▁","add_prefix_space":true,"prepend_scheme":"always"},""" +
        """ "model":{"type":"Unigram","unk_id":3,"vocab":""" + TinyVocab + "}}")));

    // The expected ids of the three tests below come from Hugging Face's tokenizers library on the same JSON.
    [Fact]
    public void ReplaceCollapsesRunsOfSpaces() =>
        Assert.Equal([0, 7, 12, 13, 2], TinyFromJson().Encode("hello    world"));

    [Fact]
    public void MetaspaceAloneKeepsTrailingSpaceMark() =>
        Assert.Equal([0, 7, 12, 13, 4, 2], TinyFromJson().Encode("hello  world  "));

    [Fact]
    public void MetaspaceAloneAddsNoSecondMarkToALeadingSpace() =>
        Assert.Equal([0, 7, 2], TinyFromJson().Encode("  hello"));

    [Fact]
    public void UnknownNormalizersAreRejectedByName()
    {
        var json = """{"normalizer":{"type":"Lowercase"},"model":{"type":"Unigram","unk_id":3,"vocab":""" + TinyVocab + "}}";
        var ex = Assert.Throws<InvalidDataException>(() => UnigramTokenizer.Load(new MemoryStream(Encoding.UTF8.GetBytes(json))));
        Assert.Contains("Lowercase", ex.Message);
    }

    /// <summary>The models' own tokenizer.json files, against ids from Hugging Face's tokenizers library.
    /// Runs where the models have been downloaded: KUMBU_MODEL_DIR with arctic-m/ and arctic-l/.</summary>
    [Fact]
    public void MatchesHuggingFaceOnTheRealVocabulary() => MatchesHuggingFace("arctic-m", "tokenizer-reference.json");

    [Fact]
    public void MatchesHuggingFaceOnArcticL() => MatchesHuggingFace("arctic-l", "tokenizer-reference-arctic-l.json");

    private static void MatchesHuggingFace(string model, string reference)
    {
        var dir = Environment.GetEnvironmentVariable("KUMBU_MODEL_DIR");
        var file = dir is null ? null : Path.Combine(dir, model, "tokenizer.json");
        Assert.SkipUnless(file is not null && File.Exists(file), $"KUMBU_MODEL_DIR with {model}/tokenizer.json not set");

        using var stream = File.OpenRead(file!);
        var tokenizer = UnigramTokenizer.Load(stream);
        var cases = JsonSerializer.Deserialize<List<Case>>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Search", reference)))!;
        foreach (var c in cases) Assert.Equal(c.ids, tokenizer.Encode(c.text));
    }

    /// <summary>Texts of every kind: the fixtures, the reference texts, and lines of seeded random words mixed with
    /// characters the normaliser maps, joins or splits (decomposed accents, ligatures, CR LF, odd spaces, emoji).</summary>
    private static IEnumerable<string> Texts()
    {
        foreach (var (_, text) in DenseFixtures.Dense) yield return text;
        yield return DenseFixtures.GermanProse;
        yield return DenseFixtures.NumericTable;
        yield return DenseFixtures.Code;
        yield return DenseFixtures.Urls;
        foreach (var text in ChunkerFixtures.All.Values) yield return text;
        foreach (var reference in new[] { "tokenizer-reference.json", "tokenizer-reference-arctic-l.json" })
        {
            foreach (var c in JsonSerializer.Deserialize<List<Case>>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Search", reference)))!) yield return c.text;
        }

        // Characters NFKC expands into several, in runs: their count may well exceed their length.
        yield return string.Concat(Enumerable.Repeat("ﷺ", 40));
        yield return string.Concat(Enumerable.Repeat("㎏㎒ﬃ", 30));

        string[] odd = ["e\u0301", " \u0301x", "ﬁ", "㎏", "\r\n", "\r", "\u00A0", "\u3000", "\u200B", "\u0600a", "ﷺ", "👩‍👩‍👧", "🇩🇪🇫🇷", "\t", "Ⅻ", "ＡＢＣ", "ß", "Ä", "…", "\u00AD", "\uFEFF", "\u0007", "\u007F", "→∑", "x\u20DD", "̈a"];
        var words = (DenseFixtures.GermanProse + " " + ChunkerFixtures.NestedFlowers + " " + DenseFixtures.Code).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Distinct().ToArray();
        var random = new Random(11);
        for (var line = 0; line < 3_000; line++)
        {
            var text = new StringBuilder();
            for (var w = random.Next(1, 16); w > 0; w--)
            {
                text.Append(random.Next(6) == 0 ? odd[random.Next(odd.Length)] : "");
                text.Append(words[random.Next(words.Length)]);
                text.Append(random.Next(8) == 0 ? odd[random.Next(odd.Length)] : random.Next(10) == 0 ? "  " : " ");
            }

            yield return text.ToString();
        }
    }

    private static UnigramTokenizer? Named(string name)
    {
        if (name == "tiny") return Tiny;
        // Without "▁" a word can start with an unknown, which joins the unknown that ends the word before.
        if (name == "tiny-without-mark") return new UnigramTokenizer([("<s>", 0), ("<pad>", 0), ("</s>", 0), ("<unk>", 0), ("▁he", -3), ("llo", -3), ("e", -5)], unkId: 3, precompiledCharsMap: null);
        if (name == "arctic-m") return TestTokenizer.Load();
        var dir = Environment.GetEnvironmentVariable("KUMBU_MODEL_DIR");
        var file = dir is null ? null : Path.Combine(dir, name, "tokenizer.json");
        if (file is null || !File.Exists(file)) return null;
        using var stream = File.OpenRead(file);
        return UnigramTokenizer.Load(stream);
    }

    [Theory]
    [InlineData("tiny")]
    [InlineData("tiny-without-mark")]
    [InlineData("arctic-m")]
    [InlineData("arctic-l")]
    public void Count_is_the_length_of_the_encoding_for_every_kind_of_text(string name)
    {
        var tokenizer = Named(name);
        Assert.SkipUnless(tokenizer is not null, name == "arctic-m" ? TestTokenizer.SkipReason : $"KUMBU_MODEL_DIR with {name}/tokenizer.json not set");

        foreach (var text in Texts())
        {
            var expected = tokenizer!.Encode(text, int.MaxValue).Count;
            Assert.Equal(expected, tokenizer.Count(text));
            Assert.True(tokenizer.CountCeiling(text) >= expected, $"ceiling {tokenizer.CountCeiling(text)} < {expected}");
            // Again, from the memo of words.
            Assert.Equal(expected, tokenizer.Count(text));
            Assert.Equal(tokenizer.Normalized(text, graphemesOnly: true), tokenizer.Normalized(text, graphemesOnly: false));
        }
    }

    private sealed record Case(string text, List<int> ids);
}
