// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Kumbu.Core.Search;

/// <summary>
/// The tokenizer of the embedding model, read from its Hugging Face <c>tokenizer.json</c>: SentencePiece's
/// precompiled normalizer (nmt_nfkc), whitespace split with a "▁" before each word, a Unigram model
/// (the most probable split of each word, by Viterbi), and &lt;s&gt; … &lt;/s&gt; around it. It follows
/// the Hugging Face implementation step by step, so the ids are the ones the model was trained with.
/// </summary>
public sealed class UnigramTokenizer
{
    private const char Space = '▁';
    private const double UnkPenalty = 10.0;

    private readonly Dictionary<string, (int Id, double Score)> _pieces;
    private readonly Dictionary<string, (int Id, double Score)>.AlternateLookup<ReadOnlySpan<char>> _lookup;
    private readonly int _maxPieceLength;
    private readonly double _unkScore;
    private readonly int _unkId;
    private readonly PrecompiledNormalizer? _normalizer;

    public UnigramTokenizer(IReadOnlyList<(string Piece, double Score)> vocab, int unkId, byte[]? precompiledCharsMap)
    {
        _pieces = new Dictionary<string, (int, double)>(vocab.Count, StringComparer.Ordinal);
        for (var id = 0; id < vocab.Count; id++) _pieces.TryAdd(vocab[id].Piece, (id, vocab[id].Score));
        _lookup = _pieces.GetAlternateLookup<ReadOnlySpan<char>>();
        _maxPieceLength = vocab.Max(v => v.Piece.Length);
        _unkScore = vocab.Min(v => v.Score) - UnkPenalty;
        _unkId = unkId;
        _normalizer = precompiledCharsMap is null ? null : new PrecompiledNormalizer(precompiledCharsMap);
    }

    public const int BosId = 0;
    public const int PadId = 1;
    public const int EosId = 2;

    public static UnigramTokenizer Load(Stream tokenizerJson)
    {
        using var doc = JsonDocument.Parse(tokenizerJson);
        var model = doc.RootElement.GetProperty("model");
        if (model.GetProperty("type").GetString() != "Unigram") throw new InvalidDataException("Expected a Unigram tokenizer");
        var vocab = model.GetProperty("vocab").EnumerateArray()
            .Select(e => (e[0].GetString()!, e[1].GetDouble()))
            .ToList();
        byte[]? charsMap = null;
        if (doc.RootElement.TryGetProperty("normalizer", out var normalizer) && normalizer.ValueKind == JsonValueKind.Object
            && normalizer.GetProperty("type").GetString() == "Precompiled")
        {
            charsMap = Convert.FromBase64String(normalizer.GetProperty("precompiled_charsmap").GetString()!);
        }

        return new UnigramTokenizer(vocab, model.GetProperty("unk_id").GetInt32(), charsMap);
    }

    /// <summary>The ids of <paramref name="text"/>, with &lt;s&gt; and &lt;/s&gt;, at most <paramref name="maxLength"/> in all.</summary>
    public List<int> Encode(string text, int maxLength = 512)
    {
        var ids = new List<int> { BosId };
        var normalized = _normalizer?.Normalize(text) ?? text;
        foreach (var word in Words(normalized))
        {
            EncodeWord(Space + word, ids);
            if (ids.Count >= maxLength - 1) break;
        }

        if (ids.Count > maxLength - 1) ids.RemoveRange(maxLength - 1, ids.Count - (maxLength - 1));
        ids.Add(EosId);
        return ids;
    }

    /// <summary>WhitespaceSplit: words between runs of whitespace (Rust's char::is_whitespace, the Unicode
    /// White_Space property, which .NET's char.IsWhiteSpace matches).</summary>
    private static IEnumerable<string> Words(string text)
    {
        var start = -1;
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                if (start >= 0) yield return text[start..i];
                start = -1;
            }
            else if (start < 0) start = i;
        }

        if (start >= 0) yield return text[start..];
    }

    /// <summary>Viterbi over the pieces, as Hugging Face's encode_optimized: unknown characters get the lowest
    /// score minus a penalty, and consecutive unknowns become one &lt;unk&gt;.</summary>
    private void EncodeWord(string word, List<int> ids)
    {
        var n = word.Length;
        var score = new double[n + 1];
        var from = new int[n + 1];
        var id = new int[n + 1];
        Array.Fill(from, -1);
        from[0] = 0;
        for (var start = 0; start < n; start = Next(word, start))
        {
            if (from[start] < 0) continue;
            var charLength = Next(word, start) - start;
            var single = false;
            for (var length = 1; length <= _maxPieceLength && start + length <= n; length++)
            {
                if (!_lookup.TryGetValue(word.AsSpan(start, length), out var piece)) continue;
                var end = start + length;
                var candidate = score[start] + piece.Score;
                if (from[end] < 0 || candidate > score[end]) (score[end], from[end], id[end]) = (candidate, start, piece.Id);
                if (length == charLength) single = true;
            }

            if (!single)
            {
                var end = start + charLength;
                var candidate = score[start] + _unkScore;
                if (from[end] < 0 || candidate > score[end]) (score[end], from[end], id[end]) = (candidate, start, _unkId);
            }
        }

        var path = new List<int>();
        for (var at = n; at > 0; at = from[at]) path.Add(id[at]);
        path.Reverse();
        foreach (var piece in path)
        {
            if (piece == _unkId && ids.Count > 0 && ids[^1] == _unkId) continue;
            ids.Add(piece);
        }
    }

    private static int Next(string s, int i) => char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]) ? i + 2 : i + 1;

    /// <summary>
    /// SentencePiece's precompiled character map: a darts-clone double-array trie over UTF-8, whose values point
    /// into a block of replacement strings. Applied per grapheme, as Hugging Face's spm_precompiled does.
    /// </summary>
    private sealed class PrecompiledNormalizer
    {
        private readonly uint[] _units;
        private readonly byte[] _normalized;

        public PrecompiledNormalizer(byte[] blob)
        {
            var trieSize = (int)BitConverter.ToUInt32(blob, 0);
            _units = new uint[trieSize / 4];
            Buffer.BlockCopy(blob, 4, _units, 0, trieSize);
            _normalized = blob[(4 + trieSize)..];
        }

        public string Normalize(string text)
        {
            var result = new StringBuilder(text.Length);
            var graphemes = StringInfo.GetTextElementEnumerator(text);
            while (graphemes.MoveNext())
            {
                var grapheme = graphemes.GetTextElement();
                if (Encoding.UTF8.GetByteCount(grapheme) < 6 && Transform(grapheme) is { } whole)
                {
                    result.Append(whole);
                    continue;
                }

                for (var i = 0; i < grapheme.Length; i = Next(grapheme, i))
                {
                    var part = grapheme[i..Next(grapheme, i)];
                    result.Append(Transform(part) ?? part);
                }
            }

            return result.ToString();
        }

        /// <summary>The replacement of the first (shortest) prefix the trie knows, or null.</summary>
        private string? Transform(string chunk)
        {
            var key = Encoding.UTF8.GetBytes(chunk);
            uint pos = 0;
            var unit = _units[pos];
            pos ^= Offset(unit);
            foreach (var c in key)
            {
                pos ^= c;
                unit = _units[pos];
                if (Label(unit) != c) return null;
                pos ^= Offset(unit);
                if (HasLeaf(unit))
                {
                    var start = (int)Value(_units[pos]);
                    var end = Array.IndexOf(_normalized, (byte)0, start);
                    return Encoding.UTF8.GetString(_normalized, start, (end < 0 ? _normalized.Length : end) - start);
                }
            }

            return null;
        }

        private static bool HasLeaf(uint unit) => ((unit >> 8) & 1) == 1;

        private static uint Value(uint unit) => unit & ((1u << 31) - 1);

        private static uint Label(uint unit) => unit & ((1u << 31) | 0xFF);

        private static uint Offset(uint unit) => (unit >> 10) << (int)((unit & (1u << 9)) >> 6);
    }
}
