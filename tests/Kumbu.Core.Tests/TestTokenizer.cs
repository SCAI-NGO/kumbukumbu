// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Search;

namespace Kumbu.Core.Tests;

/// <summary>arctic-m's tokenizer from the file <c>KUMBU_TOKENIZER</c> names (CI fetches it), or null where it isn't set.</summary>
public static class TestTokenizer
{
    public const string SkipReason = "KUMBU_TOKENIZER (arctic-m's tokenizer.json) not set";

    public const string Sha256 = "f1cc44ad7faaeec47241864835473fd5403f2da94673f3f764a77ebcb0a803ec";

    private static readonly Lazy<UnigramTokenizer?> Tokenizer = new(() =>
    {
        var file = Environment.GetEnvironmentVariable("KUMBU_TOKENIZER");
        if (string.IsNullOrEmpty(file) || !File.Exists(file)) return null;
        using var stream = File.OpenRead(file);
        return UnigramTokenizer.Load(stream);
    });

    public static UnigramTokenizer? Load() => Tokenizer.Value;
}
