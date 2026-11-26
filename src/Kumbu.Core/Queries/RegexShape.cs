// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Kumbu.Core.Queries;

/// <summary>What a regular expression's text says about its cost before it runs (bases spec 2026-11-25 decision 15):
/// its size, every position written out (an atom, a class or an anchor is one, an alternation adds its branches, a count
/// multiplies its body, ?, * and + count it once: <c>\d{4}-\d{2}</c> is seven, <c>(?:a|aa){0,10}</c> thirty), whether a
/// count passes RE2's bound of 1,000 (alone, or multiplied through nesting), and the longest text a match can be, if
/// there is one. The offline engine reads a pattern the same way (src/query/regexShape.ts), so the same patterns are null in both.
/// It reads the syntax only as far as these need: a pattern that doesn't compile fails later anyway.</summary>
public static class RegexShape
{
    /// <summary>How large a pattern that runs may be. .NET's linear engine builds its states as it matches, without
    /// looking at its timeout, and for optional groups of overlapping branches they grow steeply with the size: the
    /// first search of 1,000,000 characters by (?i) and 21 groups (?:a|aa)? (63) takes 380 ms, by 25 (75) 660 ms, by 33
    /// (99) 5.5 s. At 64 the costliest pattern found takes under 400 ms (task 5 report, fix round 3).</summary>
    public const int MaxSize = 64;

    /// <summary>RE2's bound on a count, alone and multiplied through nesting.</summary>
    public const int MaxCount = 1000;

    // Large enough to pass every bound, small enough that products stay far from overflowing.
    private const long Cap = 1_000_000_000;

    public sealed record Shape(long Size, long? Longest, bool CountTooLarge)
    {
        public bool Runs => !CountTooLarge && Size <= MaxSize;
    }

    public static Shape Of(string pattern)
    {
        var reader = new Reader(pattern);
        var part = reader.Alternation();
        // A ) without its ( ends the alternation early; the rest is read on as more of it.
        while (reader.At < pattern.Length)
        {
            reader.At++;
            part = part.Or(reader.Alternation());
        }

        return new Shape(part.Size, part.Longest, reader.CountTooLarge);
    }

    // Size: the positions it stands for, its counts multiplied out; Longest: the longest text it matches, null when
    // unbounded; Product: its counts multiplied through nesting.
    private readonly record struct Part(long Size, long? Longest, long Product)
    {
        public static readonly Part Empty = new(0, 0, 1);
        public static readonly Part One = new(1, 1, 1);

        public Part Then(Part next) => new(Add(Size, next.Size),
            Longest is { } a && next.Longest is { } b ? Add(a, b) : null, Math.Max(Product, next.Product));

        public Part Or(Part other) => new(Add(Size, other.Size),
            Longest is { } a && other.Longest is { } b ? Math.Max(a, b) : null, Math.Max(Product, other.Product));
    }

    private static long Add(long a, long b) => Math.Min(a + b, Cap);

    private static long Times(long a, long b) => a == 0 || b == 0 ? 0 : a > Cap / b ? Cap : Math.Min(a * b, Cap);

    private sealed class Reader(string p)
    {
        public int At;
        public bool CountTooLarge;

        private char? Next => At < p.Length ? p[At] : null;

        public Part Alternation()
        {
            var part = Sequence();
            while (Next == '|')
            {
                At++;
                part = part.Or(Sequence());
            }

            return part;
        }

        private Part Sequence()
        {
            var part = Part.Empty;
            while (Next is { } c && c != '|' && c != ')') part = part.Then(Quantified(Atom()));
            return part;
        }

        private Part Quantified(Part atom)
        {
            switch (Next)
            {
                case '*' or '+':
                    At++;
                    Lazy();
                    return atom with { Longest = null };
                case '?':
                    At++;
                    Lazy();
                    return atom;
                case '{' when Count() is (var least, var most):
                    Lazy();
                    var times = most ?? least;
                    var product = Times(atom.Product, Math.Max(times, 1));
                    if (least > MaxCount || most > MaxCount || product > MaxCount) CountTooLarge = true;
                    // Its body, inner counts included, written out so many times.
                    return new Part(Times(atom.Size, times), most is { } m && atom.Longest is { } longest ? Times(longest, m) : null, product);
                default:
                    return atom;
            }
        }

        private void Lazy()
        {
            if (Next == '?') At++;
        }

        // {n}, {n,} or {n,m}; anything else after { is a { of its own.
        private (long Least, long? Most)? Count()
        {
            var i = At + 1;
            long Number()
            {
                long n = 0;
                while (i < p.Length && char.IsAsciiDigit(p[i])) n = Math.Min(n * 10 + (p[i++] - '0'), Cap);
                return n;
            }

            var start = i;
            var least = Number();
            if (i == start) return null;
            long? most = least;
            if (i < p.Length && p[i] == ',')
            {
                i++;
                var from = i;
                var m = Number();
                most = i == from ? null : m;
            }

            if (i >= p.Length || p[i] != '}') return null;
            At = i + 1;
            return (least, most);
        }

        private Part Atom()
        {
            switch (p[At])
            {
                case '\\':
                    At = Math.Min(At + 2, p.Length);
                    return Part.One;
                case '[':
                    At++;
                    if (Next == '^') At++;
                    if (Next == ']') At++;
                    while (Next is { } c && c != ']') At = Math.Min(At + (c == '\\' ? 2 : 1), p.Length);
                    if (Next == ']') At++;
                    return Part.One;
                case '(':
                    At++;
                    if (Next == '?' && !Prefix()) return Part.Empty;
                    var inner = Alternation();
                    if (Next == ')') At++;
                    return inner;
                default:
                    At++;
                    return Part.One;
            }
        }

        // What follows (? : a group's kind or name, or flags; false when it is flags alone, (?i), which match nothing.
        private bool Prefix()
        {
            At++;
            switch (Next)
            {
                case ':' or '=' or '!':
                    At++;
                    return true;
                case '<' when At + 1 < p.Length && p[At + 1] is '=' or '!':
                    At += 2;
                    return true;
                case '<' or 'P':
                    var close = p.IndexOf('>', At);
                    At = close < 0 ? p.Length : close + 1;
                    return true;
                default:
                    while (Next is { } c && c != ':' && c != ')') At++;
                    if (Next == ':')
                    {
                        At++;
                        return true;
                    }

                    if (Next == ')') At++;
                    return false;
            }
        }
    }
}
