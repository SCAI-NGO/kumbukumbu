// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;

namespace Kumbu.Core.Sh.Commands;

/// <summary>The records of a CSV text as RFC 4180 reads them: fields between a separator, a field in double quotes
/// may hold the separator, line breaks and doubled quotes. Lines end in LF or CRLF; an empty line is no record.</summary>
internal static class CsvParser
{
    /// <summary>The separators sniffed for, in the order that breaks a tie.</summary>
    public static readonly char[] Separators = [',', ';', '\t', '|'];

    /// <summary>The separator of a text with one column: a text file holds no NUL, so nothing splits.</summary>
    public const char None = '\0';

    public sealed record Result(List<string[]> Rows, bool Quoted, int? UnclosedLine);

    /// <summary>The first <paramref name="max"/> records; at an unclosed quote, those before it and the line it opens on.</summary>
    public static Result Parse(string text, char separator, int max = int.MaxValue)
    {
        var rows = new List<string[]>();
        var fields = new List<string>();
        var quotedField = new StringBuilder();
        var quoted = false;
        var line = 1;
        var i = 0;
        while (i < text.Length && rows.Count < max)
        {
            // An empty line is no record.
            if (text[i] == '\n' || (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n'))
            {
                i += text[i] == '\r' ? 2 : 1;
                line++;
                continue;
            }

            while (true)
            {
                string field;
                if (i < text.Length && text[i] == '"')
                {
                    quoted = true;
                    var opened = line;
                    i++;
                    quotedField.Clear();
                    while (true)
                    {
                        var close = text.IndexOf('"', i);
                        if (close < 0) return new Result(rows, quoted, opened);
                        var chunk = text.AsSpan(i, close - i);
                        line += chunk.Count('\n');
                        quotedField.Append(chunk);
                        i = close + 1;
                        if (i < text.Length && text[i] == '"')
                        {
                            quotedField.Append('"');
                            i++;
                            continue;
                        }

                        break;
                    }

                    // Anything after the closing quote, up to the separator, is kept as it stands rather than guessed at.
                    var rest = UnquotedEnd(text, i, separator);
                    field = rest > i ? quotedField.Append(text, i, rest - i).ToString() : quotedField.ToString();
                    i = rest;
                }
                else
                {
                    var end = UnquotedEnd(text, i, separator);
                    field = text[i..end];
                    i = end;
                }

                // CRLF ends the line, and so does a CR that ends the text.
                if (field.EndsWith('\r') && (i == text.Length || text[i] == '\n')) field = field[..^1];
                fields.Add(field);
                if (i < text.Length && text[i] == separator)
                {
                    i++;
                    continue;
                }

                // The end of the line, or of the text.
                if (i < text.Length)
                {
                    i++;
                    line++;
                }

                break;
            }

            rows.Add([.. fields]);
            fields.Clear();
        }

        return new Result(rows, quoted, null);
    }

    private static int UnquotedEnd(string text, int from, char separator)
    {
        var at = separator == None ? text.IndexOf('\n', from) : text.AsSpan(from).IndexOfAny(separator, '\n');
        if (at < 0) return text.Length;
        return separator == None ? at : from + at;
    }

    /// <summary>The separator that splits the first 20 records into the most consistent count of fields above one;
    /// <see cref="None"/> when none does.</summary>
    public static char Sniff(string text)
    {
        var best = None;
        var bestScore = 0;
        foreach (var separator in Separators)
        {
            var counts = Parse(text, separator, 20).Rows.Select(r => r.Length).ToList();
            if (counts.Count == 0) continue;
            var mode = counts.GroupBy(c => c).OrderByDescending(g => g.Count()).ThenByDescending(g => g.Key).First();
            if (mode.Key <= 1 || mode.Count() <= bestScore) continue;
            best = separator;
            bestScore = mode.Count();
        }

        return best;
    }
}
