// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Kumbu.Core.Attachments;
using Kumbu.Core.Notes;

namespace Kumbu.Core.Sh.Commands;

/// <summary><c>csv</c>: a text file, or the piped input, read as a table (spec 2026-11-19, decisions 3–6).</summary>
public sealed partial class Csv(INoteReader notes) : ICommand
{
    private static readonly Option[] Spec =
    [
        new(null, "rows", TakesValue: true), new(null, "cols", TakesValue: true), new(null, "tsv"), new(null, "sep", TakesValue: true),
        new(null, "header"), new(null, "no-header"),
    ];

    // Room kept free for the notice above the output and the closing line below the table.
    private const int Budget = OutputLimit.MaxChars - 300;

    private const int OverviewRows = 10;

    public string Name => "csv";

    public string Usage => "csv [options] [path] [B2:D20]";

    public string Summary => "read a CSV file as a table: its shape, rows, columns, a range";

    public string Help => """"
        csv [options] [path] [B2:D20]
          Read a CSV file (a text file of rows) as a table, or the piped input. Without options, its shape:
          the dialect found (separator, quotes, header), the number of rows and columns, the header, and the
          first 10 rows as a Markdown table.
          --rows N-M      data rows, counted from 1 without the header: 5, 2-20, 5- (to the end), -20 (the first 20)
          --cols C,…      columns by header name (case-sensitive), letter (B,D) or number (2,4), in that order
          B2:D20          a spreadsheet range instead, after the path: row 1 is the header line when there is
                          one; B2 is one cell, B:D whole columns
          --tsv           tab-separated values instead of a table, the header line first, for sort, wc or rg;
                          tabs and line breaks inside cells become spaces
          --sep S         the separator: ',', ';', '|' or a tab ('\t' or tab). Otherwise the one that splits the
                          first 20 lines most evenly (Excel in German locales writes ';')
          --header, --no-header   whether the first row is the header. Otherwise it is, unless it has an
                          empty cell, a number (or date) or a name twice
          Quoting as RFC 4180 has it ("a, b", "say ""hi"""); a quoted field may span lines, shown as <br>, and
          a quote left open is an error with its line. A table is cut at the output limit with a line on how
          to narrow it; --tsv isn't, so a pipe gets every row.
          Reads text files only (UTF-8, up to 15 MB), whatever their name; a note's tables are read with cat.

          csv /Work/costs.csv
          csv /Work/costs.csv --rows 2-20 --cols Date,Amount
          csv /Work/costs.csv B2:D20
          csv /Work/costs.csv --tsv --cols Amount --rows 1- | sort -n | tail -n 3
          cat /Work/export.txt | csv --sep ';' --no-header
        """";

    public bool ReadsInput => true;

    public async Task<ShellResult> RunAsync(IReadOnlyList<string> args, string? input, CancellationToken ct)
    {
        if (!Options.TryParse(Name, args, Spec, out var options, out var error)) return error;

        var separator = (char?)null;
        if (options.Value("sep") is { } sep)
        {
            separator = sep switch { "," => ',', ";" => ';', "|" => '|', "\t" or "\\t" or "tab" => '\t', _ => null };
            if (separator is null) return ShellResult.Error($"csv: --sep takes ',', ';', '|' or a tab ('\\t' or tab), not '{sep}'");
        }

        if (options.Has("header") && options.Has("no-header")) return ShellResult.Error("csv: --header and --no-header don't go together");

        (int From, int? To)? rows = null;
        if (options.Value("rows") is { } rowsText)
        {
            if (!TryRows(rowsText, out var parsed)) return ShellResult.Error(
                $"csv: '{rowsText}' is not a row range: give N, N-M, N- or -M (data rows count from 1, the header not counted), e.g. 2-20");
            rows = parsed;
        }

        var ranges = options.Operands.Where(o => RangePattern().IsMatch(o)).ToList();
        var paths = options.Operands.Where(o => !RangePattern().IsMatch(o)).ToList();
        if (ranges.Count > 1) return ShellResult.Error("csv: one range at a time");
        Range? range = null;
        if (ranges.Count == 1)
        {
            if (rows is not null || options.Has("cols")) return ShellResult.Error("csv: a range and --rows or --cols don't go together; give one");
            if (!TryRange(ranges[0], out var parsed)) return ShellResult.Error($"csv: {ranges[0]} is not a range: the first cell comes first, e.g. B2:D20");
            range = parsed;
        }

        string text;
        string source;
        if (input is not null)
        {
            if (paths.Count > 0) return ShellResult.Error($"csv: reads from the pipe, so it takes no path (got '{paths[0]}')");
            text = input;
            source = "the input";
        }
        else
        {
            if (paths.Count == 0) return ShellResult.Error("csv: missing operand (give a path, or pipe into it)");
            if (paths.Count > 1) return ShellResult.Error($"csv: too many arguments{CommandArgs.QuoteHint}");
            var read = await ReadAsync(paths[0], ct);
            if (read.IsError) return read;
            text = read.Output;
            source = "the file";
        }

        separator ??= CsvParser.Sniff(text);
        var parse = CsvParser.Parse(text, separator.Value);
        if (parse.UnclosedLine is { } line)
        {
            return ShellResult.Error(input is null ? $"csv: {paths[0]}: line {line}: a quoted field isn't closed" : $"csv: line {line}: a quoted field isn't closed");
        }

        var records = parse.Rows;
        if (records.Count == 0) return ShellResult.Ok($"rows: 0 ({source} is empty)");

        var table = new Table(records, options.Has("header") || (!options.Has("no-header") && LooksLikeHeader(records)));
        var tsv = options.Has("tsv");

        if (range is { } r)
        {
            var lines = table.Data.Count + table.Offset;
            if (r.FirstRow is { } first && first > lines)
            {
                var what = table.HasHeader ? $"{Count(lines)} lines (a header and {Count(table.Data.Count)} rows)" : $"{Count(lines)} rows";
                return ShellResult.Error($"csv: {ranges[0]}: {source} has {what}");
            }

            if (r.LastColumn > table.Columns) return ShellResult.Error($"csv: {ranges[0]}: {source} has {ColumnsSaid(table.Columns)}");
            var from = Math.Max(1, (r.FirstRow ?? 1) - table.Offset);
            var to = Math.Min(table.Data.Count, (r.LastRow ?? lines) - table.Offset);
            var columns = Enumerable.Range(r.FirstColumn - 1, r.LastColumn - r.FirstColumn + 1).ToList();
            return Print(table, from, to, columns, tsv);
        }

        var cols = Enumerable.Range(0, table.Columns).ToList();
        if (options.Value("cols") is { } colsText)
        {
            var chosen = Columns(table, colsText, source, out var colsError);
            if (chosen is null) return colsError!;
            cols = chosen;
        }

        if (rows is { } wanted)
        {
            if (wanted.From > table.Data.Count) return ShellResult.Error($"csv: --rows {options.Value("rows")}: {source} has {Count(table.Data.Count)} rows");
            return Print(table, wanted.From, Math.Min(table.Data.Count, wanted.To ?? table.Data.Count), cols, tsv);
        }

        if (options.Has("cols") || tsv) return Print(table, 1, table.Data.Count, cols, tsv);
        return Overview(table, separator.Value, parse.Quoted);
    }

    private async Task<ShellResult> ReadAsync(string arg, CancellationToken ct)
    {
        if (!CommandArgs.TryParsePath(Name, arg, out var path, out var pathError)) return pathError!;
        if (await StoreAttachments.GetAsync(notes, path, ct) is not { } attachment)
        {
            var note = await CommandArgs.ReadNoteAsync(Name, notes, path, ct);
            return note.IsError ? note : ShellResult.Error($"csv: {path.Value} is a note; cat reads its tables");
        }

        var state = attachment.Current;
        if (AttachmentFormat.IsLarge(state.Size)) return ShellResult.Error($"csv: {path.Value} is {AttachmentFormat.NotRead}");
        if (state.MediaType == AttachmentFormat.OctetStream)
        {
            return ShellResult.Error($"csv: {path.Value} is kept as bytes (not UTF-8 text); Kumbu² can't read it as CSV");
        }

        if (state.MediaType != AttachmentFormat.TextMediaType)
        {
            return ShellResult.Error($"csv: {path.Value} is {(AttachmentFormat.IsPdf(state) ? "a PDF" : "an image")}, not text; Kumbu² can't read it as CSV");
        }

        return await StoreAttachments.ReadBytesAsync(notes, path, state, ct) is { } bytes
            ? ShellResult.Ok(StoreAttachments.Text(bytes))
            : ShellResult.Error($"csv: {path.Value}: the file's content is missing");
    }

    /// <summary>The records split into the header, when there is one, and the data rows below it.</summary>
    private sealed class Table(List<string[]> records, bool header)
    {
        public bool HasHeader { get; } = header;

        public int Offset => HasHeader ? 1 : 0;

        public string[]? Header { get; } = header ? records[0] : null;

        public IReadOnlyList<string[]> Data { get; } = header ? records.GetRange(1, records.Count - 1) : records;

        public int Columns { get; } = records.Max(r => r.Length);

        public int MinFields { get; } = records.Min(r => r.Length);

        /// <summary>A column's name: its header cell, or its letter where there is none.</summary>
        public string NameOf(int column) => Header is { } h && column < h.Length && h[column].Length > 0 ? h[column] : Letters(column + 1);
    }

    /// <summary>Decision 5: the first row, unless it has an empty cell, a number or a name twice. Most CSVs start with a
    /// header, and a header taken by mistake costs less than one missed (decided by Chris on 2026-11-19).</summary>
    private static bool LooksLikeHeader(List<string[]> records) =>
        records.Count >= 1 && records[0].All(c => c.Trim().Length > 0 && !IsNumber(c))
        && records[0].Select(c => c.Trim()).Distinct(StringComparer.Ordinal).Count() == records[0].Length;

    // A date counts as a number: "Date,Amount" over "2026-01-02,…" is a header as much as over "12.50".
    private static bool IsNumber(string cell) => NumberPattern().IsMatch(cell.Trim()) || DatePattern().IsMatch(cell.Trim());

    [GeneratedRegex(@"^[+-]?(\d+([.,']\d+)*|[.,]\d+)%?$")]
    private static partial Regex NumberPattern();

    [GeneratedRegex(@"^(\d{4}-\d{1,2}-\d{1,2}([T ][\d:.]+Z?)?|\d{1,2}[./]\d{1,2}[./]\d{2,4})$")]
    private static partial Regex DatePattern();

    [GeneratedRegex(@"^([A-Z]+[0-9]+(:[A-Z]+[0-9]+)?|[A-Z]+:[A-Z]+)$")]
    private static partial Regex RangePattern();

    private sealed record Range(int FirstColumn, int LastColumn, int? FirstRow, int? LastRow);

    private static bool TryRange(string text, out Range range)
    {
        range = null!;
        var cells = text.Split(':');
        var (c1, r1) = Cell(cells[0]);
        var (c2, r2) = cells.Length > 1 ? Cell(cells[1]) : (c1, r1);
        if (c2 < c1 || r2 < r1 || r1 == 0) return false;
        range = new Range(c1, c2, r1, r2);
        return true;

        static (int Column, int? Row) Cell(string cell)
        {
            var digits = cell.IndexOfAny("0123456789".ToCharArray());
            return digits < 0
                ? (Column(cell), null)
                : (Column(cell[..digits]), int.TryParse(cell[digits..], NumberStyles.None, CultureInfo.InvariantCulture, out var row) ? row : 0);
        }
    }

    private static bool TryRows(string text, out (int From, int? To) rows)
    {
        rows = default;
        var dash = text.IndexOf('-');
        var fromText = dash < 0 ? text : text[..dash];
        var toText = dash < 0 ? text : text[(dash + 1)..];
        if (fromText.Length == 0 && toText.Length == 0) return false;
        var from = 1;
        int? to = null;
        if (fromText.Length > 0 && !TryPositive(fromText, out from)) return false;
        if (toText.Length > 0)
        {
            if (!TryPositive(toText, out var last) || last < from) return false;
            to = last;
        }

        rows = (from, to);
        return true;

        static bool TryPositive(string s, out int n) => int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out n) && n >= 1;
    }

    /// <summary>The columns <c>--cols</c> names, 0-based, in the order given: a header name first, then a letter, then a number.</summary>
    private static List<int>? Columns(Table table, string text, string source, out ShellResult? error)
    {
        error = null;
        var columns = new List<int>();
        foreach (var name in text.Split(','))
        {
            var index = table.Header is { } header ? Array.IndexOf(header, name) : -1;
            if (index < 0)
            {
                int number;
                if (LettersPattern().IsMatch(name)) number = Column(name);
                else if (name.Length > 0 && name.All(char.IsAsciiDigit)) number = int.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : int.MaxValue;
                else
                {
                    var choice = $"letters ({Span(table.Columns)}) or numbers (1-{table.Columns})";
                    error = ShellResult.Error(table.Header is { } h
                        ? $"csv: --cols: no column '{name}'; the header has {string.Join(", ", h)} (names are case-sensitive), or give {choice}"
                        : $"csv: --cols: no column '{name}'; {source} has no header, so give {choice}");
                    return null;
                }

                if (number < 1 || number > table.Columns)
                {
                    error = ShellResult.Error($"csv: --cols: no column '{name}'; {source} has {ColumnsSaid(table.Columns)}");
                    return null;
                }

                index = number - 1;
            }

            columns.Add(index);
        }

        return columns;
    }

    [GeneratedRegex("^[A-Z]+$")]
    private static partial Regex LettersPattern();

    /// <summary>A column's number from its letters: A is 1, Z 26, AA 27.</summary>
    private static int Column(string letters)
    {
        var n = 0;
        foreach (var c in letters)
        {
            n = n * 26 + (c - 'A' + 1);
            if (n > 1_000_000) return int.MaxValue;
        }

        return n;
    }

    private static string Letters(int column)
    {
        var letters = "";
        for (; column > 0; column = (column - 1) / 26) letters = (char)('A' + (column - 1) % 26) + letters;
        return letters;
    }

    private static string Span(int columns) => columns == 1 ? "A" : $"A-{Letters(columns)}";

    private static string ColumnsSaid(int columns) => columns == 1 ? "1 column (A)" : $"{columns} columns ({Span(columns)})";

    private static string Count(int n) => n.ToString("N0", CultureInfo.InvariantCulture);

    private static ShellResult Overview(Table table, char separator, bool quoted)
    {
        var said = separator switch { CsvParser.None => "one column (no separator found)", '\t' => "separator tab", _ => $"separator '{separator}'" };
        var output = new StringBuilder()
            .Append($"dialect: {said}, {(quoted ? "double quotes" : "no quotes")}, {(table.HasHeader ? "a header row" : "no header (columns by letter or number)")}\n")
            .Append($"rows: {Count(table.Data.Count)}, columns: {table.Columns}")
            .Append(table.MinFields < table.Columns ? $" (rows have {table.MinFields} to {table.Columns} fields)\n" : "\n");
        if (table.HasHeader) output.Append("header: ").AppendJoin(", ", Enumerable.Range(0, table.Columns).Select(c => NameSaid(table, c))).Append('\n');
        output.Append('\n');

        var shown = Math.Min(OverviewRows, table.Data.Count);
        var printed = Markdown(output, table, 1, shown, [.. Enumerable.Range(0, table.Columns)]);
        if (printed < shown) return ShellResult.Ok(output.Append(CutLine(printed, table.Data.Count)).ToString());
        if (table.Data.Count > shown)
        {
            var last = Letters(Math.Min(table.Columns, 4));
            var example = table.Columns == 1 ? "A2:A20" : $"B2:{last}20";
            output.Append($"\n\nfirst {shown} of {Count(table.Data.Count)} rows; read more with --rows {shown + 1}-{shown + 50}, --cols or a range such as {example}");
        }

        return ShellResult.Ok(output.ToString());
    }

    private static string NameSaid(Table table, int column) =>
        table.Header is { } h && column < h.Length && h[column].Length > 0 ? $"{Letters(column + 1)} {h[column]}" : Letters(column + 1);

    private static ShellResult Print(Table table, int from, int to, List<int> columns, bool tsv)
    {
        if (tsv)
        {
            var lines = new StringBuilder();
            if (table.HasHeader) lines.AppendJoin('\t', columns.Select(c => Flat(Cell(table.Header!, c)))).Append('\n');
            for (var i = from; i <= to; i++) lines.AppendJoin('\t', columns.Select(c => Flat(Cell(table.Data[i - 1], c)))).Append('\n');
            return ShellResult.Ok(lines.ToString().TrimEnd('\n'));
        }

        var output = new StringBuilder();
        var wanted = Math.Max(0, to - from + 1);
        var printed = Markdown(output, table, from, to, columns);
        return ShellResult.Ok(printed < wanted ? output.Append(CutLine(printed, wanted)).ToString() : output.ToString());
    }

    private static string CutLine(int shown, int of) => $"\n\ncsv: {Count(shown)} of {Count(of)} rows shown; narrow with --rows or --cols";

    /// <summary>Appends the header and the data rows <paramref name="from"/>-<paramref name="to"/> (1-based) as a Markdown table,
    /// while they fit the budget; returns how many rows it appended.</summary>
    private static int Markdown(StringBuilder output, Table table, int from, int to, List<int> columns)
    {
        output.Append("| ").AppendJoin(" | ", columns.Select(c => Escape(table.NameOf(c)))).Append(" |\n|")
            .Append(string.Concat(Enumerable.Repeat(" --- |", columns.Count)));
        var printed = 0;
        var row = new StringBuilder();
        for (var i = from; i <= to; i++)
        {
            row.Clear().Append("\n| ").AppendJoin(" | ", columns.Select(c => Escape(Cell(table.Data[i - 1], c)))).Append(" |");
            if (output.Length + row.Length > Budget) break;
            output.Append(row);
            printed++;
        }

        return printed;
    }

    private static string Cell(string[] record, int column) => column < record.Length ? record[column] : "";

    private static string Escape(string cell) => cell.Replace("|", "\\|").Replace("\r\n", "<br>").Replace("\n", "<br>").Replace("\r", "<br>");

    private static string Flat(string cell) => cell.Replace("\r\n", " ").Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');
}
