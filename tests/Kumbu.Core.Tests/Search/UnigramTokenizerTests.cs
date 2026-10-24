// SPDX-License-Identifier: AGPL-3.0-or-later
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

    /// <summary>The model's own tokenizer.json, against ids from Hugging Face's tokenizers library
    /// (tokenizer-reference.json). Runs where the model has been downloaded: KUMBU_MODEL_DIR.</summary>
    [Fact]
    public void MatchesHuggingFaceOnTheRealVocabulary()
    {
        var dir = Environment.GetEnvironmentVariable("KUMBU_MODEL_DIR");
        var file = dir is null ? null : Path.Combine(dir, "tokenizer.json");
        Assert.SkipUnless(file is not null && File.Exists(file), "KUMBU_MODEL_DIR with tokenizer.json not set");

        using var stream = File.OpenRead(file!);
        var tokenizer = UnigramTokenizer.Load(stream);
        var cases = JsonSerializer.Deserialize<List<Case>>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Search", "tokenizer-reference.json")))!;
        foreach (var c in cases) Assert.Equal(c.ids, tokenizer.Encode(c.text));
    }

    private sealed record Case(string text, List<int> ids);
}
