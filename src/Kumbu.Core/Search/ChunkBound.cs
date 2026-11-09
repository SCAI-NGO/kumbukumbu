// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.Concurrent;

namespace Kumbu.Core.Search;

/// <summary>
/// The ceiling a chunk's embedding text stays under: the 512 tokens the embedding model reads, counted by arctic-m's
/// tokenizer for every provider; a bound by characters while there is no tokenizer; none when nothing is embedded.
/// Its <see cref="Id"/> is part of the chunking's <see cref="Scheme"/>: other boundaries, other chunks.
/// </summary>
public sealed class ChunkBound
{
    public const int MaxTokens = 512;

    /// <summary>
    /// The fallback's limit in characters. Measured with arctic-m's tokenizer in tokens per character: punctuation 0.874,
    /// symbols 0.943, mixed "aé1|-_.ßäöü§→" 0.881, base64 0.781, hex 0.605, a numeric table 0.636; German prose 0.26,
    /// English 0.21. Unbroken runs of rare punctuation or of "→∑≈€§¶•✓★♣" reach one token per character, so 500
    /// characters with &lt;s&gt; and &lt;/s&gt; are 502 tokens at most, unless NFKC expands a character into several
    /// (ligatures, squared units), which ordinary notes don't hold in runs.
    /// </summary>
    public const int FallbackMaxChars = 500;

    private const int MemoLimit = 100_000;

    /// <summary>Longer pieces are counted each time: they are rare, and would fill the memo with text.</summary>
    private const int MemoMaxLength = 4_000;

    private enum Kind { Tokens, Characters, None }

    private readonly Kind _kind;
    private readonly UnigramTokenizer? _tokenizer;
    private readonly ConcurrentDictionary<string, int> _costs = new(StringComparer.Ordinal);
    private int _memoised;

    private ChunkBound(string id, Kind kind, UnigramTokenizer? tokenizer)
    {
        (Id, _kind, _tokenizer) = (id, kind, tokenizer);
        Budget = kind switch
        {
            Kind.Tokens => MaxTokens - 2,
            Kind.Characters => FallbackMaxChars + 1,
            _ => int.MaxValue,
        };
    }

    /// <summary>A provider is active, but the tokenizer couldn't be had (or hasn't settled yet).</summary>
    public static ChunkBound Fallback { get; } = new("fallback", Kind.Characters, null);

    /// <summary>No provider is active, so nothing is embedded: only the chunker's word limits apply.</summary>
    public static ChunkBound Unbounded { get; } = new("words", Kind.None, null);

    /// <param name="sha256">The SHA-256 of the tokenizer's file, which names the bound.</param>
    public static ChunkBound Of(UnigramTokenizer tokenizer, string sha256) => new("tok-" + sha256[..12], Kind.Tokens, tokenizer);

    public string Id { get; }

    /// <summary>The chunking's <see cref="Chunker.SchemeVersion"/> and this bound's <see cref="Id"/>.</summary>
    public string Scheme => $"{Chunker.SchemeVersion}/{Id}";

    /// <summary>What the <see cref="Cost"/>s of a chunk's pieces may add up to: the tokens between &lt;s&gt; and
    /// &lt;/s&gt;, or the characters plus one for the fallback (each piece pays for its separator).</summary>
    public int Budget { get; }

    /// <summary>
    /// The cost of a piece of an embedding text, a line or a space-separated part of one: its tokens without
    /// &lt;s&gt; and &lt;/s&gt;, or its length plus one for the fallback. With arctic-m the costs of the pieces add
    /// up to the text's exactly: it splits words at whitespace, and a line break is a grapheme of its own.
    /// </summary>
    public int Cost(string piece)
    {
        switch (_kind)
        {
            case Kind.Tokens:
                if (piece.Length > MemoMaxLength) return _tokenizer!.Count(piece) - 2;
                if (_costs.TryGetValue(piece, out var cost)) return cost;
                cost = _tokenizer!.Count(piece) - 2;
                if (Interlocked.Increment(ref _memoised) >= MemoLimit)
                {
                    _costs.Clear();
                    Interlocked.Exchange(ref _memoised, 0);
                }

                _costs[piece] = cost;
                return cost;
            case Kind.Characters:
                return piece.Length + 1;
            default:
                return 0;
        }
    }

    /// <summary>Whether the whole embedding text fits, counted afresh rather than from the pieces' costs.</summary>
    public bool Fits(string embeddingText) => _kind switch
    {
        Kind.Tokens => _tokenizer!.Count(embeddingText) <= MaxTokens,
        Kind.Characters => embeddingText.Length <= FallbackMaxChars,
        _ => true,
    };
}
