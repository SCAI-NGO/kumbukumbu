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

    private sealed record Case(string text, List<int> ids);
}
