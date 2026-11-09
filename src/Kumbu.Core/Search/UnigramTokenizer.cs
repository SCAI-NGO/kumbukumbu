// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.Concurrent;
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

    // The pieces as a trie, walked a character at a time: the child of a node by a character (the node in the high
    // bits, the character in the low 16), and the piece that ends at a node (-1 for none) with its score.
    private readonly Dictionary<long, int> _children;
    private readonly int[] _pieceAt;
    private readonly double[] _scores;
    private readonly double _unkScore;
    private readonly int _unkId;
    private readonly PrecompiledNormalizer? _normalizer;
    private readonly bool _collapseSpaces;
    private readonly bool _metaspaceOnly;

    /// <summary>The pieces of a word (without its "▁") as <see cref="EncodeWord"/> gives them, and whether the first
    /// and the last are &lt;unk&gt; (consecutive unknowns across words become one).</summary>
    private readonly ConcurrentDictionary<string, (int Count, bool UnkFirst, bool UnkLast)> _wordCounts = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (int Count, bool UnkFirst, bool UnkLast)>.AlternateLookup<ReadOnlySpan<char>> _wordLookup;
    private int _wordsMemoised;

    /// <summary>More distinct words than notes ordinarily hold; the memo starts afresh when it is full.</summary>
    private const int WordMemoLimit = 200_000;

    /// <param name="collapseSpaces">The Replace normalizer " {2,}" to " ", applied after the precompiled map.</param>
    /// <param name="metaspaceOnly">Metaspace without WhitespaceSplit: only spaces separate words, and a trailing
    /// space leaves a "▁" of its own.</param>
    public UnigramTokenizer(IReadOnlyList<(string Piece, double Score)> vocab, int unkId, byte[]? precompiledCharsMap,
        bool collapseSpaces = false, bool metaspaceOnly = false)
    {
        _children = new Dictionary<long, int>(vocab.Count * 2);
        var pieceAt = new List<int> { -1 };
        _scores = new double[vocab.Count];
        for (var id = 0; id < vocab.Count; id++)
        {
            var node = 0;
            foreach (var c in vocab[id].Piece)
            {
                if (!_children.TryGetValue(Edge(node, c), out var child))
                {
                    _children[Edge(node, c)] = child = pieceAt.Count;
                    pieceAt.Add(-1);
                }

                node = child;
            }

            // The first of two equal pieces counts, as a dictionary's TryAdd keeps it.
            if (node > 0 && pieceAt[node] < 0) pieceAt[node] = id;
            _scores[id] = vocab[id].Score;
        }

        _pieceAt = [.. pieceAt];
        _unkScore = vocab.Min(v => v.Score) - UnkPenalty;
        _unkId = unkId;
        _normalizer = precompiledCharsMap is null ? null : new PrecompiledNormalizer(precompiledCharsMap);
        _collapseSpaces = collapseSpaces;
        _metaspaceOnly = metaspaceOnly;
        _wordLookup = _wordCounts.GetAlternateLookup<ReadOnlySpan<char>>();
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

    /// <summary>
    /// How many ids <see cref="Encode"/> gives for <paramref name="text"/> without a cut, &lt;s&gt; and &lt;/s&gt;
    /// included. With the whitespace split the words are encoded on their own, so the count is the sum of the words'
    /// (memoised by word, after the whole text's normalisation), less one where an &lt;unk&gt; ends a word and the
    /// next starts with one. Metaspace alone counts by <see cref="Encode"/>.
    /// </summary>
    public int Count(string text)
    {
        if (_metaspaceOnly) return Encode(text, int.MaxValue).Count;
        var normalized = _normalizer?.Normalize(text) ?? text;
        if (_collapseSpaces) normalized = SpaceRuns().Replace(normalized, " ");
        var count = 2;
        var unkLast = false;
        var start = -1;
        for (var i = 0; i <= normalized.Length; i++)
        {
            if (i < normalized.Length && !char.IsWhiteSpace(normalized[i]))
            {
                if (start < 0) start = i;
                continue;
            }

            if (start < 0) continue;
            var word = WordCount(normalized.AsSpan(start, i - start));
            count += word.Count - (unkLast && word.UnkFirst ? 1 : 0);
            unkLast = word.UnkLast;
            start = -1;
        }

        return count;
    }

    /// <summary>
    /// At least <see cref="Count"/>: the normalised length plus three. Each word's pieces cover "▁" and its characters, a
    /// character or more each, and there are no more words than runs of whitespace plus one; &lt;s&gt; and &lt;/s&gt;
    /// add two. The text isn't normalised for it (<see cref="PrecompiledNormalizer.LengthCeiling"/>).
    /// </summary>
    public int CountCeiling(string text) => (_normalizer?.LengthCeiling(text) ?? text.Length) + 3;

    /// <summary>The precompiled map's normalisation, by the ASCII fast path where it applies or grapheme by grapheme
    /// throughout (for tests: the two must agree).</summary>
    internal string Normalized(string text, bool graphemesOnly) =>
        _normalizer is null ? text : graphemesOnly ? _normalizer.Graphemes(text) : _normalizer.Normalize(text);

    private (int Count, bool UnkFirst, bool UnkLast) WordCount(ReadOnlySpan<char> word)
    {
        if (_wordLookup.TryGetValue(word, out var known)) return known;
        known = CountWord(word);
        if (Interlocked.Increment(ref _wordsMemoised) >= WordMemoLimit)
        {
            _wordCounts.Clear();
            Interlocked.Exchange(ref _wordsMemoised, 0);
        }

        _wordLookup.TryAdd(word, known);
        return known;
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
            for (var (length, node) = (1, 0); start + length <= n; length++)
            {
                if (!_children.TryGetValue(Edge(node, word[start + length - 1]), out node)) break;
                if (_pieceAt[node] is not (>= 0 and var piece)) continue;
                var end = start + length;
                var candidate = score[start] + _scores[piece];
                if (from[end] < 0 || candidate > score[end]) (score[end], from[end], id[end]) = (candidate, start, piece);
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

    /// <summary>
    /// <see cref="EncodeWord"/> of "▁" and <paramref name="bare"/>, counted: the same Viterbi without allocating (most
    /// words fit on the stack), the pieces of the best path with consecutive unknowns as one.
    /// </summary>
    private (int Count, bool UnkFirst, bool UnkLast) CountWord(ReadOnlySpan<char> bare)
    {
        var n = bare.Length + 1;
        const int StackLimit = 128;
        var rented = n + 1 > StackLimit ? System.Buffers.ArrayPool<char>.Shared.Rent(n) : null;
        var rentedScores = n + 1 > StackLimit ? System.Buffers.ArrayPool<double>.Shared.Rent(n + 1) : null;
        var rentedInts = n + 1 > StackLimit ? System.Buffers.ArrayPool<int>.Shared.Rent(2 * (n + 1)) : null;
        try
        {
            var word = rented is null ? stackalloc char[n] : rented.AsSpan(0, n);
            var score = rentedScores is null ? stackalloc double[n + 1] : rentedScores.AsSpan(0, n + 1);
            var ints = rentedInts is null ? stackalloc int[2 * (n + 1)] : rentedInts.AsSpan(0, 2 * (n + 1));
            var from = ints[..(n + 1)];
            var id = ints[(n + 1)..];
            word[0] = Space;
            bare.CopyTo(word[1..]);
            score.Clear();
            from.Fill(-1);
            from[0] = 0;
            for (var start = 0; start < n; start = Next(word, start))
            {
                if (from[start] < 0) continue;
                var charLength = Next(word, start) - start;
                var single = false;
                for (var (length, node) = (1, 0); start + length <= n; length++)
                {
                    if (!_children.TryGetValue(Edge(node, word[start + length - 1]), out node)) break;
                    if (_pieceAt[node] is not (>= 0 and var piece)) continue;
                    var end = start + length;
                    var candidate = score[start] + _scores[piece];
                    if (from[end] < 0 || candidate > score[end]) (score[end], from[end], id[end]) = (candidate, start, piece);
                    if (length == charLength) single = true;
                }

                if (!single)
                {
                    var end = start + charLength;
                    var candidate = score[start] + _unkScore;
                    if (from[end] < 0 || candidate > score[end]) (score[end], from[end], id[end]) = (candidate, start, _unkId);
                }
            }

            // Backwards over the path: the last piece first; an unknown right before an unknown doesn't count.
            var (count, first, last) = (0, false, n > 0 && id[n] == _unkId);
            for (var at = n; at > 0; at = from[at])
            {
                var unk = id[at] == _unkId;
                if (!(unk && from[at] > 0 && id[from[at]] == _unkId)) count++;
                first = unk;
            }

            return (count, first, last);
        }
        finally
        {
            if (rented is not null) System.Buffers.ArrayPool<char>.Shared.Return(rented);
            if (rentedScores is not null) System.Buffers.ArrayPool<double>.Shared.Return(rentedScores);
            if (rentedInts is not null) System.Buffers.ArrayPool<int>.Shared.Return(rentedInts);
        }
    }

    private static long Edge(int node, char c) => ((long)node << 16) | c;

    private static int Next(ReadOnlySpan<char> s, int i) => char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]) ? i + 2 : i + 1;

    private static int Next(string s, int i) => char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]) ? i + 2 : i + 1;

    /// <summary>
    /// SentencePiece's precompiled character map: a darts-clone double-array trie over UTF-8, whose values point
    /// into a block of replacement strings. Applied per grapheme, as Hugging Face's spm_precompiled does.
    /// </summary>
    private sealed class PrecompiledNormalizer
    {
        private readonly uint[] _units;
        private readonly byte[] _normalized;

        /// <summary>What each ASCII character but CR becomes, if the map leaves every printable one as it is; else null.</summary>
        private readonly string[]? _ascii;

        /// <summary>The normalised runs around non-ASCII characters ("för", "ü"): few, and met again and again.</summary>
        private readonly ConcurrentDictionary<string, string> _segments = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, string>.AlternateLookup<ReadOnlySpan<char>> _segmentLookup;
        private int _segmentsMemoised;

        /// <summary>The most characters one replacement of the map has.</summary>
        private readonly int _longestReplacement;
        private const int SegmentMemoLimit = 50_000;

        public PrecompiledNormalizer(byte[] blob)
        {
            var trieSize = (int)BitConverter.ToUInt32(blob, 0);
            _units = new uint[trieSize / 4];
            Buffer.BlockCopy(blob, 4, _units, 0, trieSize);
            _normalized = blob[(4 + trieSize)..];

            // Checked against the map itself: another tokenizer may well map some ASCII.
            var ascii = new string[128];
            for (var c = 0; c < 128; c++) ascii[c] = Transform(((char)c).ToString()) ?? ((char)c).ToString();
            if (Enumerable.Range(0x20, 0x7F - 0x20).All(c => ascii[c] == ((char)c).ToString())) _ascii = ascii;
            _segmentLookup = _segments.GetAlternateLookup<ReadOnlySpan<char>>();
            _longestReplacement = Math.Max(2, Encoding.UTF8.GetString(_normalized).Split('\0').Max(r => r.Length));
        }

        /// <summary>
        /// The normalised text. With the ASCII table, runs of ASCII are mapped character by character and only the rest
        /// goes grapheme by grapheme: between two ASCII characters other than CR there is always a grapheme boundary
        /// (none of them extends, prepends or joins), so the two ways give the same text.
        /// </summary>
        public string Normalize(string text)
        {
            if (_ascii is null) return Graphemes(text);
            var result = (StringBuilder?)null;
            var i = 0;
            while (i < text.Length)
            {
                if (Plain(text, i) && (i + 1 == text.Length || Plain(text, i + 1)))
                {
                    var mapped = _ascii[text[i]];
                    if (result is null && (mapped.Length != 1 || mapped[0] != text[i])) result = new StringBuilder(text.Length).Append(text, 0, i);
                    result?.Append(mapped);
                    i++;
                    continue;
                }

                var end = i + 1;
                while (end < text.Length && !(Plain(text, end - 1) && Plain(text, end))) end++;
                result ??= new StringBuilder(text.Length).Append(text, 0, i);
                result.Append(Segment(text.AsSpan(i, end - i)));
                i = end;
            }

            return result?.ToString() ?? text;
        }

        private string Segment(ReadOnlySpan<char> segment)
        {
            if (_segmentLookup.TryGetValue(segment, out var known)) return known;
            known = Graphemes(segment.ToString());
            if (segment.Length > 64) return known;
            if (Interlocked.Increment(ref _segmentsMemoised) >= SegmentMemoLimit)
            {
                _segments.Clear();
                Interlocked.Exchange(ref _segmentsMemoised, 0);
            }

            _segmentLookup.TryAdd(segment, known);
            return known;
        }

        /// <summary>
        /// At least the length of <see cref="Normalize"/>'s result, without normalising: an ASCII character but CR
        /// becomes what the table says, and any other character at most the longest replacement the map holds (a
        /// grapheme replaced whole gets one replacement for all its characters).
        /// </summary>
        public int LengthCeiling(string text)
        {
            if (_ascii is null) return Normalize(text).Length;
            var length = 0;
            foreach (var c in text) length += c < 128 && c != '\r' ? _ascii[c].Length : _longestReplacement;
            return length;
        }

        private static bool Plain(string text, int i) => text[i] < 128 && text[i] != '\r';

        public string Graphemes(string text)
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
