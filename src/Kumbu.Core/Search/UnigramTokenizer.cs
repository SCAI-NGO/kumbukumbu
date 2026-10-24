// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Kumbu.Core.Search;

/// <summary>
/// The tokenizer of the embedding model, read from its Hugging Face <c>tokenizer.json</c>: SentencePiece's
/// precompiled normalizer (nmt_nfkc), optionally followed by a collapse of runs of spaces; a "▁" before each
/// word, either by whitespace split (arctic-m) or by Metaspace alone (arctic-l); a Unigram model
/// (the most probable split of each word, by Viterbi), and &lt;s&gt; … &lt;/s&gt; around it. It follows
/// the Hugging Face implementation step by step, so the ids are the ones the model was trained with.
/// </summary>
public sealed partial class UnigramTokenizer
{
    private const char Space = '▁';
    private const double UnkPenalty = 10.0;

    private readonly Dictionary<string, (int Id, double Score)> _pieces;
    private readonly Dictionary<string, (int Id, double Score)>.AlternateLookup<ReadOnlySpan<char>> _lookup;
    private readonly int _maxPieceLength;
    private readonly double _unkScore;
    private readonly int _unkId;
    private readonly PrecompiledNormalizer? _normalizer;
    private readonly bool _collapseSpaces;
    private readonly bool _metaspaceOnly;

    /// <param name="collapseSpaces">The Replace normalizer " {2,}" to " ", applied after the precompiled map.</param>
    /// <param name="metaspaceOnly">Metaspace without WhitespaceSplit: only spaces separate words, and a trailing
    /// space leaves a "▁" of its own.</param>
    public UnigramTokenizer(IReadOnlyList<(string Piece, double Score)> vocab, int unkId, byte[]? precompiledCharsMap,
        bool collapseSpaces = false, bool metaspaceOnly = false)
    {
        _pieces = new Dictionary<string, (int, double)>(vocab.Count, StringComparer.Ordinal);
        for (var id = 0; id < vocab.Count; id++) _pieces.TryAdd(vocab[id].Piece, (id, vocab[id].Score));
        _lookup = _pieces.GetAlternateLookup<ReadOnlySpan<char>>();
        _maxPieceLength = vocab.Max(v => v.Piece.Length);
        _unkScore = vocab.Min(v => v.Score) - UnkPenalty;
        _unkId = unkId;
        _normalizer = precompiledCharsMap is null ? null : new PrecompiledNormalizer(precompiledCharsMap);
        _collapseSpaces = collapseSpaces;
        _metaspaceOnly = metaspaceOnly;
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
        var collapseSpaces = false;
        if (doc.RootElement.TryGetProperty("normalizer", out var normalizer) && normalizer.ValueKind == JsonValueKind.Object)
        {
            var steps = normalizer.GetProperty("type").GetString() == "Sequence"
                ? normalizer.GetProperty("normalizers").EnumerateArray().ToList()
                : [normalizer];
            foreach (var step in steps)
            {
                switch (step.GetProperty("type").GetString())
                {
                    case "Precompiled":
                        charsMap = Convert.FromBase64String(step.GetProperty("precompiled_charsmap").GetString()!);
                        break;
                    case "Replace" when IsSpaceCollapse(step):
                        collapseSpaces = true;
                        break;
                    case var other:
                        throw new InvalidDataException($"Unsupported normalizer: {other}");
                }
            }
        }

        var metaspaceOnly = false;
        if (doc.RootElement.TryGetProperty("pre_tokenizer", out var pre) && pre.ValueKind == JsonValueKind.Object)
        {
            var type = pre.GetProperty("type").GetString();
            if (type == "Metaspace") metaspaceOnly = CheckMetaspace(pre);
            else if (type == "Sequence"
                && pre.GetProperty("pretokenizers").EnumerateArray().Select(p => p.GetProperty("type").GetString()).SequenceEqual(["WhitespaceSplit", "Metaspace"]))
                CheckMetaspace(pre.GetProperty("pretokenizers")[1]);
            else throw new InvalidDataException($"Unsupported pre-tokenizer: {type}");
        }

        return new UnigramTokenizer(vocab, model.GetProperty("unk_id").GetInt32(), charsMap, collapseSpaces, metaspaceOnly);
    }

    private static bool IsSpaceCollapse(JsonElement replace) =>
        replace.GetProperty("pattern").TryGetProperty("Regex", out var regex) && regex.GetString() == " {2,}"
        && replace.GetProperty("content").GetString() == " ";

    /// <summary>Only the settings this class implements: "▁", always a prefix, and the default split.</summary>
    private static bool CheckMetaspace(JsonElement metaspace)
    {
        if (metaspace.GetProperty("replacement").GetString() != "▁"
            || metaspace.TryGetProperty("prepend_scheme", out var scheme) && scheme.GetString() != "always"
            || metaspace.TryGetProperty("split", out var split) && !split.GetBoolean())
            throw new InvalidDataException("Unsupported Metaspace settings");
        return true;
    }

    /// <summary>The ids of <paramref name="text"/>, with &lt;s&gt; and &lt;/s&gt;, at most <paramref name="maxLength"/> in all.</summary>
    public List<int> Encode(string text, int maxLength = 512)
    {
        var ids = new List<int> { BosId };
        var normalized = _normalizer?.Normalize(text) ?? text;
        if (_collapseSpaces) normalized = SpaceRuns().Replace(normalized, " ");
        foreach (var word in _metaspaceOnly ? MetaspaceWords(normalized) : Words(normalized).Select(w => Space + w))
        {
            EncodeWord(word, ids);
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

    /// <summary>Metaspace alone: spaces become "▁", one is put in front unless the text starts with one, and the
    /// text is cut before each "▁" (split, merged with the next), so a trailing space is a word of its own. Empty text has no words.</summary>
    private static IEnumerable<string> MetaspaceWords(string text)
    {
        if (text.Length == 0) yield break;
        var marked = text.Replace(' ', Space);
        if (!marked.StartsWith(Space)) marked = Space + marked;
        var start = 0;
        for (var i = 1; i < marked.Length; i++)
        {
            if (marked[i] != Space) continue;
            yield return marked[start..i];
            start = i;
        }

        yield return marked[start..];
    }

    [GeneratedRegex(" {2,}")]
    private static partial Regex SpaceRuns();

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
