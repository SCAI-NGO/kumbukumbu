// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;

namespace Kumbu.Core.Queries;

/// <summary>What a regular expression's text says before it runs (bases spec 2026-11-25 decision 15). It reads the whole
/// pattern, and only the part of RE2's syntax that .NET reads the same way: literals, the escapes \d \D \w \W \s \S \b \B
/// \t \n \r \f \v \xHH and an escaped punctuation mark, classes of those and of ranges, the dot, ^ and $, groups (…) and
/// (?:…), alternation, the counts ? * + {n} {n,} {n,m} and their lazy forms, and the flags i, m and s, alone (?i) or for a
/// group (?i:…). Anything else (comments, lookaround, backreferences, named groups, \p{…}, \A, other flags, a count of a
/// count, a lone bracket, a character outside the BMP) is unsupported, so the two engines can't read one pattern two ways.
/// The offline engine reads a pattern the same way (src/query/regexShape.ts).
///
/// For a pattern it supports it gives its size, every position written out (an atom, a class or an anchor is one, an
/// alternation adds its branches, a count multiplies its body, ? counts it once, * and + twice, {n,} n + 1 times and at
/// least twice: <c>\d{4}-\d{2}</c> is seven, <c>(?:a|aa){0,10}</c> thirty, <c>\w+</c> two), whether a count passes RE2's bound of 1,000 (alone, or multiplied through
/// nesting), the longest text a match can be, if there is one, and the pattern for .NET: \d, \w and \s as RE2 means them
/// (ASCII), $ without m as the end of the text, and under i each letter with all the case forms RE2 folds it with.</summary>
public static class RegexShape
{
    /// <summary>How large a pattern that runs may be. .NET's linear engine builds its states as it matches, without
    /// looking at its timeout, and for optional groups of overlapping branches they grow steeply with the size: the
    /// first search of 1,000,000 characters by (?i) and 21 groups (?:a|aa)? (63) takes 380 ms, by 25 (75) 660 ms, by 33
    /// (99) 5.5 s. At 64 the costliest pattern found takes under 400 ms (task 5 report, fix rounds 3 and 4).</summary>
    public const int MaxSize = 64;

    /// <summary>RE2's bound on a count, alone and multiplied through nesting.</summary>
    public const int MaxCount = 1000;

    // How many times an unbounded count (* + {n,}) counts its body, at least: its states wrap around, so .NET builds more
    // of them than for the body written out once. (?:(?:a|aa)?)* of 10 groups (61) takes 137 ms on its first search of
    // 1,000,000 characters under i, of 12 groups 451 ms, of 20 groups 27 s (task 5 report, fix round 4). {n,} counts it
    // n + 1 times, n written out and the loop.
    private const int Loop = 2;

    // Deeper groups are unsupported, so reading a pattern can't run out of stack.
    private const int MaxDepth = 100;

    // Large enough to pass every bound, small enough that products stay far from overflowing.
    private const long Cap = 1_000_000_000;

    /// <param name="Unsupported">Where the first syntax outside the subset is, from 0; null when the whole pattern is in it.</param>
    /// <param name="Pattern">The pattern as .NET is to run it.</param>
    public sealed record Shape(long Size, long? Longest, bool CountTooLarge, int? Unsupported, string Pattern)
    {
        public bool Runs => Unsupported is null && !CountTooLarge && Size <= MaxSize;
    }

    public static Shape Of(string pattern, bool ignoreCase = false, bool multiline = false)
    {
        var reader = new Reader(pattern, new Flags(ignoreCase, multiline));
        try
        {
            var part = reader.Alternation();
            // A ) without its (.
            if (reader.At < pattern.Length) throw new UnsupportedException(reader.At);
            return new Shape(part.Size, part.Longest, reader.CountTooLarge, null, reader.Out.ToString());
        }
        catch (UnsupportedException e)
        {
            return new Shape(0, null, false, e.At, pattern);
        }
    }

    // The case forms RE2 folds together where .NET (IgnoreCase, invariant culture) folds only some of them, or none:
    // measured over every pair of case forms in the BMP, .NET's forms are always a part of RE2's (task 5 report, fix
    // round 4). A letter or class under i holding one of them gets all of them, and .NET folds the rest.
    private static readonly string[] Folds =
    [
        "Ssſ", "µΜμ", "ͅΙιι", "Ββϐ", "Εεϵ",
        "Θθϑϴ", "Κκϰ", "Ππϖ", "Ρρϱ", "Σςσ",
        "Φφϕ", "Ввᲀ", "Ддᲁ", "Ооᲂ", "Ссᲃ",
        "Ттᲄᲅ", "Ъъᲆ", "Ѣѣᲇ", "ᲈꙊꙋ", "Ṡṡẛ",
        "꟎꟏", "꟒ꟓ", "꟔ꟕ",
    ];

    // RE2's \d, \w and \s, as ranges and as .NET writes them in a class.
    private static readonly Dictionary<char, (string Written, (char From, char To)[] Ranges)> Perl = new()
    {
        ['d'] = ("0-9", [('0', '9')]),
        ['w'] = ("0-9A-Za-z_", [('0', '9'), ('A', 'Z'), ('a', 'z'), ('_', '_')]),
        ['s'] = (@"\t\n\f\r ", [('\t', '\n'), ('\f', '\r'), (' ', ' ')]),
    };

    private const string Punctuation = " !\"#$%&'()*+,-./:;<=>?@[\\]^`{|}~";

    private sealed class UnsupportedException(int at) : Exception
    {
        public int At { get; } = at;
    }

    private readonly record struct Flags(bool IgnoreCase, bool Multiline);

    // Size: the positions it stands for, its counts multiplied out; Longest: the longest text it matches, null when
    // unbounded; Product: its counts multiplied through nesting.
    private readonly record struct Part(long Size, long? Longest, long Product)
    {
        public static readonly Part Empty = new(0, 0, 1);
        public static readonly Part One = new(1, 1, 1);
        public static readonly Part Anchor = new(1, 0, 1);

        public Part Then(Part next) => new(Add(Size, next.Size),
            Longest is { } a && next.Longest is { } b ? Add(a, b) : null, Math.Max(Product, next.Product));

        public Part Or(Part other) => new(Add(Size, other.Size),
            Longest is { } a && other.Longest is { } b ? Math.Max(a, b) : null, Math.Max(Product, other.Product));
    }

    private static long Add(long a, long b) => Math.Min(a + b, Cap);

    private static long Times(long a, long b) => a == 0 || b == 0 ? 0 : a > Cap / b ? Cap : Math.Min(a * b, Cap);

    private sealed class Reader(string p, Flags flags)
    {
        public int At;
        public bool CountTooLarge;
        public readonly StringBuilder Out = new();
        private Flags flags = flags;
        private int depth;

        private char? Next => At < p.Length ? p[At] : null;

        private UnsupportedException Unsupported() => new(At);

        public Part Alternation()
        {
            var part = Sequence();
            while (Next == '|')
            {
                At++;
                Out.Append('|');
                part = part.Or(Sequence());
            }

            return part;
        }

        private Part Sequence()
        {
            var part = Part.Empty;
            while (Next is { } c && c != '|' && c != ')')
            {
                var (atom, counts) = Atom();
                if (Next is '?' or '*' or '+' or '{' && !counts) throw Unsupported();
                part = part.Then(counts ? Quantified(atom) : atom);
            }

            return part;
        }

        private Part Quantified(Part atom)
        {
            var start = At;
            // How often its body counts toward the size, and toward RE2's bound on counts; the most it repeats.
            long times = 1, counted = 1;
            long? most = 1;
            switch (Next)
            {
                case '*' or '+':
                    At++;
                    times = Loop;
                    most = null;
                    break;
                case '?':
                    At++;
                    break;
                case '{':
                    var (least, upTo) = Count();
                    if (least > MaxCount || upTo > MaxCount) CountTooLarge = true;
                    counted = Math.Max(upTo ?? least, 1);
                    times = upTo is null ? Math.Max(least + 1, Loop) : counted;
                    most = upTo;
                    break;
                default:
                    return atom;
            }

            if (Next == '?') At++;
            // A count of a count: possessive in some engines, an error in others.
            if (Next is '?' or '*' or '+' or '{') throw Unsupported();
            Out.Append(p, start, At - start);
            var product = Times(atom.Product, counted);
            if (product > MaxCount) CountTooLarge = true;
            // Its body, inner counts included, written out so many times.
            return new Part(Times(atom.Size, times), most is { } m && atom.Longest is { } longest ? Times(longest, m) : null, product);
        }

        // {n}, {n,} or {n,m} with n ≤ m; anything else after { is unsupported.
        private (long Least, long? Most) Count()
        {
            At++;
            var least = Number() ?? throw Unsupported();
            long? most = least;
            if (Next == ',')
            {
                At++;
                most = Number();
            }

            if (Next != '}' || most < least) throw Unsupported();
            At++;
            return (least, most);
        }

        private long? Number()
        {
            var start = At;
            long n = 0;
            while (Next is { } c && char.IsAsciiDigit(c))
            {
                n = Math.Min(n * 10 + (c - '0'), Cap);
                At++;
            }

            return At == start ? null : n;
        }

        // An atom, and whether a count may follow it.
        private (Part Part, bool Counts) Atom()
        {
            var c = p[At];
            switch (c)
            {
                case '\\':
                    return Escape();
                case '[':
                    Class();
                    return (Part.One, true);
                case '(':
                    return Group();
                case '.':
                    At++;
                    Out.Append('.');
                    return (Part.One, true);
                case '^':
                    At++;
                    Out.Append('^');
                    return (Part.Anchor, false);
                case '$':
                    At++;
                    Out.Append(flags.Multiline ? "$" : @"\z");
                    return (Part.Anchor, false);
                case '*' or '+' or '?' or '{' or '}' or ']':
                    throw Unsupported();
                default:
                    if (char.IsSurrogate(c)) throw Unsupported();
                    At++;
                    Letter(c, c.ToString());
                    return (Part.One, true);
            }
        }

        private (Part Part, bool Counts) Escape()
        {
            At++;
            if (Next is not { } e) throw Unsupported();
            switch (e)
            {
                case 'd' or 'w' or 's' or 'D' or 'W' or 'S':
                    At++;
                    var (written, ranges) = Perl[char.ToLowerInvariant(e)];
                    Out.Append(char.IsUpper(e) ? "[^" : "[").Append(written).Append(Folded(ranges)).Append(']');
                    return (Part.One, true);
                case 'b' or 'B':
                    At++;
                    Out.Append('\\').Append(e);
                    return (Part.Anchor, false);
                default:
                    var start = At - 1;
                    var value = Escaped();
                    Letter(value, p[start..At]);
                    return (Part.One, true);
            }
        }

        // The character an escape that stands for one character stands for: \t \n \r \f \v, \xHH, or punctuation.
        private char Escaped()
        {
            var e = p[At++];
            switch (e)
            {
                case 't': return '\t';
                case 'n': return '\n';
                case 'r': return '\r';
                case 'f': return '\f';
                case 'v': return '\v';
                case 'x':
                    if (At + 2 > p.Length || !char.IsAsciiHexDigit(p[At]) || !char.IsAsciiHexDigit(p[At + 1]))
                    {
                        At--;
                        throw Unsupported();
                    }

                    At += 2;
                    return (char)Convert.ToInt32(p.Substring(At - 2, 2), 16);
                default:
                    if (Punctuation.Contains(e)) return e;
                    At--;
                    throw Unsupported();
            }
        }

        // A character as written, or under i, where RE2 folds it with forms .NET doesn't, the class of all of them.
        private void Letter(char c, string written)
        {
            var folded = Folded([(c, c)]);
            Out.Append(folded.Length == 0 ? written : $"[{folded}]");
        }

        private string Folded(IEnumerable<(char From, char To)> ranges)
        {
            if (!flags.IgnoreCase) return "";
            var list = ranges.ToList();
            var added = new StringBuilder();
            foreach (var fold in Folds)
                if (fold.Any(f => list.Any(r => r.From <= f && f <= r.To)))
                    foreach (var f in fold) added.Append($@"\u{(int)f:X4}");
            return added.ToString();
        }

        // [ ^? item+ ] where an item is a character, an escape for one, \d \w \s, or a range of two characters; - only
        // first or last, ] never first, [ never inside.
        private void Class()
        {
            At++;
            var written = new StringBuilder("[");
            if (Next == '^')
            {
                At++;
                written.Append('^');
            }

            var ranges = new List<(char From, char To)>();
            for (var first = true; ; first = false)
            {
                switch (Next)
                {
                    case null or '[':
                        throw Unsupported();
                    case ']':
                        if (first) throw Unsupported();
                        At++;
                        Out.Append(written).Append(Folded(ranges)).Append(']');
                        return;
                    case '-':
                        if (!first && At + 1 < p.Length && p[At + 1] != ']') throw Unsupported();
                        At++;
                        written.Append(@"\-");
                        ranges.Add(('-', '-'));
                        continue;
                }

                if (p[At] == '\\' && At + 1 < p.Length && p[At + 1] is 'd' or 'w' or 's')
                {
                    var (set, of) = Perl[p[At + 1]];
                    At += 2;
                    written.Append(set);
                    ranges.AddRange(of);
                    continue;
                }

                var (from, fromWritten) = ClassCharacter();
                if (Next == '-' && At + 1 < p.Length && p[At + 1] != ']')
                {
                    At++;
                    var (to, toWritten) = ClassCharacter();
                    if (to < from) throw Unsupported();
                    written.Append(fromWritten).Append('-').Append(toWritten);
                    ranges.Add((from, to));
                }
                else
                {
                    written.Append(fromWritten);
                    ranges.Add((from, from));
                }
            }
        }

        private (char Value, string Written) ClassCharacter()
        {
            var start = At;
            var c = p[At];
            if (c == '\\')
            {
                At++;
                if (Next is not { } e || e is 'd' or 'w' or 's') throw Unsupported();
                return (Escaped(), p[start..At]);
            }

            if (char.IsSurrogate(c) || c is '[' or ']') throw Unsupported();
            At++;
            return (c, c == '^' ? @"\^" : c.ToString());
        }

        // (…), (?:…), (?flags:…), or (?flags) alone, which sets them for the rest of its group and matches nothing.
        private (Part Part, bool Counts) Group()
        {
            var start = At;
            At++;
            var outer = flags;
            if (Next == '?')
            {
                At++;
                var letters = "";
                while (Next is 'i' or 'm' or 's' && !letters.Contains(Next.Value)) letters += p[At++];
                var set = flags with
                {
                    IgnoreCase = flags.IgnoreCase || letters.Contains('i'),
                    Multiline = flags.Multiline || letters.Contains('m'),
                };
                if (Next == ')' && letters.Length > 0)
                {
                    At++;
                    flags = set;
                    Out.Append(p, start, At - start);
                    return (Part.Empty, false);
                }

                if (Next != ':') throw Unsupported();
                At++;
                flags = set;
            }

            if (++depth > MaxDepth) throw Unsupported();
            Out.Append(p, start, At - start);
            var inner = Alternation();
            if (Next != ')') throw Unsupported();
            At++;
            Out.Append(')');
            depth--;
            flags = outer;
            return (inner, true);
        }
    }
}
