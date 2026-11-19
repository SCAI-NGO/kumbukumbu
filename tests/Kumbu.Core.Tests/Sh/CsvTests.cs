// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Diagnostics;
using System.Text;
using Kumbu.Core.Attachments;
using Kumbu.Core.Notes;
using Kumbu.Core.Sh;
using Kumbu.Core.Tests.Fakes;
using Microsoft.Extensions.Time.Testing;

namespace Kumbu.Core.Tests.Sh;

/// <summary><c>csv</c>: a text file read as a table, its shape first, then ranges of rows and columns.</summary>
public class CsvTests
{
    private const string Costs = "Date,Amount,Note\n2026-01-02,12.50,Coffee\n2026-01-03,3.20,Bus\n2026-01-04,40.00,Books\n";

    private readonly InMemoryNoteStore _store = new(("/Work/plan.md", "| a | b |\n|---|---|\n| 1 | 2 |\n"));
    private readonly Shell _sh;

    public CsvTests() => _sh = TestShell.Of(_store);

    private Task<ShellResult> Run(string line) => _sh.RunAsync(line, CancellationToken.None);

    private async Task<string> Ok(string line)
    {
        var result = await Run(line);
        Assert.False(result.IsError, result.Output);
        return result.Output;
    }

    private async Task<string> Fails(string line)
    {
        var result = await Run(line);
        Assert.True(result.IsError, result.Output);
        return result.Output;
    }

    private static string Rows(int count) =>
        "Id,Name,Score\n" + string.Concat(Enumerable.Range(1, count).Select(i => $"{i},name {i},{i * 10}\n"));

    [Fact]
    public async Task Without_options_it_shows_the_shape_and_the_first_rows()
    {
        _store.AddTextFile("/Work/costs.csv", Costs);

        Assert.Equal("""
            dialect: separator ',', no quotes, a header row
            rows: 3, columns: 3
            header: A Date, B Amount, C Note

            | Date | Amount | Note |
            | --- | --- | --- |
            | 2026-01-02 | 12.50 | Coffee |
            | 2026-01-03 | 3.20 | Bus |
            | 2026-01-04 | 40.00 | Books |
            """, await Ok("csv /Work/costs.csv"));
    }

    [Fact]
    public async Task The_overview_shows_ten_rows_and_says_how_to_read_more()
    {
        _store.AddTextFile("/Work/scores.csv", Rows(48_113));

        var output = await Ok("csv /Work/scores.csv");

        Assert.StartsWith("dialect: separator ',', no quotes, a header row\nrows: 48,113, columns: 3\n", output);
        Assert.Contains("| 10 | name 10 | 100 |\n\n", output);
        Assert.DoesNotContain("| 11 |", output);
        Assert.EndsWith("\nfirst 10 of 48,113 rows; read more with --rows 11-60, --cols or a range such as B2:C20", output);
    }

    [Fact]
    public async Task Rows_count_data_rows_from_one_and_take_the_forms_of_cut()
    {
        _store.AddTextFile("/Work/scores.csv", Rows(30));

        Assert.Equal("| Id | Name | Score |\n| --- | --- | --- |\n| 5 | name 5 | 50 |", await Ok("csv /Work/scores.csv --rows 5"));
        Assert.Equal("| Id | Name | Score |\n| --- | --- | --- |\n| 2 | name 2 | 20 |\n| 3 | name 3 | 30 |", await Ok("csv --rows 2-3 /Work/scores.csv"));
        Assert.EndsWith("| 29 | name 29 | 290 |\n| 30 | name 30 | 300 |", await Ok("csv /Work/scores.csv --rows 29-"));
        var first = await Ok("csv /Work/scores.csv --rows -2");
        Assert.Equal("| Id | Name | Score |\n| --- | --- | --- |\n| 1 | name 1 | 10 |\n| 2 | name 2 | 20 |", first);
        // Past the end: what there is; a start past the end is an error.
        Assert.EndsWith("| 30 | name 30 | 300 |", await Ok("csv /Work/scores.csv --rows 29-40"));
        Assert.Equal("csv: --rows 31-: the file has 30 rows", await Fails("csv /Work/scores.csv --rows 31-"));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("3-2")]
    [InlineData("a-b")]
    [InlineData("-")]
    [InlineData("")]
    public async Task A_bad_row_range_is_refused(string rows) =>
        Assert.Equal($"csv: '{rows}' is not a row range: give N, N-M, N- or -M (data rows count from 1, the header not counted), e.g. 2-20",
            await Fails($"csv /Work/x.csv --rows '{rows}'"));

    [Fact]
    public async Task Columns_by_name_letter_or_number_in_the_order_given()
    {
        _store.AddTextFile("/Work/costs.csv", Costs);

        var table = "| Note | Date |\n| --- | --- |\n| Coffee | 2026-01-02 |\n| Bus | 2026-01-03 |\n| Books | 2026-01-04 |";
        Assert.Equal(table, await Ok("csv /Work/costs.csv --cols Note,Date"));
        Assert.Equal(table, await Ok("csv /Work/costs.csv --cols C,A"));
        Assert.Equal(table, await Ok("csv /Work/costs.csv --cols 3,1"));
        Assert.Equal(table, await Ok("csv /Work/costs.csv --cols Note,1"));
        Assert.Equal("| Amount |\n| --- |\n| 3.20 |", await Ok("csv /Work/costs.csv --cols Amount --rows 2"));
    }

    [Fact]
    public async Task An_unknown_column_is_an_error_that_lists_the_header()
    {
        _store.AddTextFile("/Work/costs.csv", Costs);

        Assert.Equal("csv: --cols: no column 'amount'; the header has Date, Amount, Note (names are case-sensitive), or give letters (A-C) or numbers (1-3)",
            await Fails("csv /Work/costs.csv --cols amount"));
        Assert.Equal("csv: --cols: no column 'D'; the file has 3 columns (A-C)", await Fails("csv /Work/costs.csv --cols D"));
        Assert.Equal("csv: --cols: no column '4'; the file has 3 columns (A-C)", await Fails("csv /Work/costs.csv --cols 4"));
        Assert.Equal("csv: --cols: no column '0'; the file has 3 columns (A-C)", await Fails("csv /Work/costs.csv --cols 0"));
        _store.AddTextFile("/Work/plain.csv", "1,2\n3,4\n");
        Assert.Equal("csv: --cols: no column 'Date'; the file has no header, so give letters (A-B) or numbers (1-2)", await Fails("csv /Work/plain.csv --cols Date"));
    }

    [Fact]
    public async Task A_header_name_wins_over_a_letter()
    {
        _store.AddTextFile("/Work/odd.csv", "B,A\nx,1\ny,2\n");

        Assert.Equal("| A |\n| --- |\n| 1 |\n| 2 |", await Ok("csv /Work/odd.csv --cols A"));
    }

    [Fact]
    public async Task A_spreadsheet_range_counts_the_header_as_row_one()
    {
        _store.AddTextFile("/Work/costs.csv", Costs);

        Assert.Equal("| Amount | Note |\n| --- | --- |\n| 3.20 | Bus |\n| 40.00 | Books |", await Ok("csv /Work/costs.csv B3:C4"));
        Assert.Equal("| Amount | Note |\n| --- | --- |\n| 12.50 | Coffee |", await Ok("csv /Work/costs.csv B1:C2"));
        Assert.Equal("| Note |\n| --- |\n| Bus |", await Ok("csv /Work/costs.csv C3"));
        Assert.Equal("| Date | Amount |\n| --- | --- |\n| 2026-01-02 | 12.50 |\n| 2026-01-03 | 3.20 |\n| 2026-01-04 | 40.00 |", await Ok("csv /Work/costs.csv A:B"));
        Assert.Equal("csv: B9:C9: the file has 4 lines (a header and 3 rows)", await Fails("csv /Work/costs.csv B9:C9"));
        Assert.Equal("csv: B2:D3: the file has 3 columns (A-C)", await Fails("csv /Work/costs.csv B2:D3"));
        Assert.Equal("csv: C2:B3 is not a range: the first cell comes first, e.g. B2:D20", await Fails("csv /Work/costs.csv C2:B3"));
        Assert.Equal("csv: a range and --rows or --cols don't go together; give one", await Fails("csv /Work/costs.csv B2:C3 --rows 1"));
    }

    [Fact]
    public async Task Without_a_header_row_one_is_the_first_row_and_columns_are_letters()
    {
        _store.AddTextFile("/Work/plain.csv", "1,2,3\n4,5,6\n7,8,9\n");

        Assert.Equal("""
            dialect: separator ',', no quotes, no header (columns by letter or number)
            rows: 3, columns: 3

            | A | B | C |
            | --- | --- | --- |
            | 1 | 2 | 3 |
            | 4 | 5 | 6 |
            | 7 | 8 | 9 |
            """, await Ok("csv /Work/plain.csv"));
        Assert.Equal("| B | C |\n| --- | --- |\n| 2 | 3 |", await Ok("csv /Work/plain.csv B1:C1"));
        Assert.Equal("| C |\n| --- |\n| 9 |", await Ok("csv /Work/plain.csv --cols C --rows 3"));
    }

    [Fact]
    public async Task Cells_are_escaped_for_markdown()
    {
        _store.AddTextFile("/Work/odd.csv", "Name,Text\nrose,\"a | b\"\ntulip,\"two\nlines\"\nlily,\"crlf\r\nhere\"\n");

        Assert.Equal("| Name | Text |\n| --- | --- |\n| rose | a \\| b |\n| tulip | two<br>lines |\n| lily | crlf<br>here |",
            await Ok("csv /Work/odd.csv --header --rows 1-"));
    }

    [Fact]
    public async Task Tsv_prints_tab_separated_values_with_the_header_line()
    {
        _store.AddTextFile("/Work/odd.csv", "Name,Text,N\nrose,\"a\tb\",1\ntulip,\"two\nlines\",2\n");

        Assert.Equal("Name\tText\tN\nrose\ta b\t1\ntulip\ttwo lines\t2", await Ok("csv /Work/odd.csv --tsv"));
        Assert.Equal("Name\tN\ntulip\t2", await Ok("csv /Work/odd.csv --tsv --rows 2 --cols Name,N"));
        Assert.Equal("2", await Ok("csv /Work/odd.csv --tsv --cols N --rows 2- | tail -n 1"));
    }

    [Fact]
    public async Task Rfc_4180_quoting_with_doubled_quotes_and_quoted_separators()
    {
        _store.AddTextFile("/Work/q.csv", "Name,Quote\r\n\"Smith, Jane\",\"she said \"\"hi\"\"\"\r\nDoe,\"\"\r\n");

        Assert.Equal("""
            dialect: separator ',', double quotes, a header row
            rows: 2, columns: 2
            header: A Name, B Quote

            | Name | Quote |
            | --- | --- |
            | Smith, Jane | she said "hi" |
            | Doe |  |
            """, await Ok("csv /Work/q.csv --header"));
    }

    [Theory]
    [InlineData("Name,Amount\r\nTea,3\r")]
    [InlineData("Name,Amount\r\nTea,\"3\"\r")]
    public async Task A_carriage_return_that_ends_the_file_ends_the_line(string text)
    {
        _store.AddTextFile("/Work/cr.csv", text);

        Assert.Equal("Name\tAmount\nTea\t3", await Ok("csv /Work/cr.csv --tsv"));
    }

    [Theory]
    [InlineData("Name,Note\n\"Tea\",\"ends in CR\r\"\n")]
    [InlineData("Name,Note\r\n\"Tea\",\"ends in CR\r\"\r\n")]
    [InlineData("Name,Note\n\"Tea\",\"ends in CR\r\"")]
    public void A_carriage_return_inside_quotes_is_kept(string text) =>
        Assert.Equal(["Tea", "ends in CR\r"], Kumbu.Core.Sh.Commands.CsvParser.Parse(text, ',').Rows[1]);

    [Fact]
    public async Task A_byte_order_mark_is_dropped_from_a_file_and_from_piped_input()
    {
        _store.AddTextFile("/Work/bom.csv", "﻿Name,Amount\nTea,3\n");

        Assert.Equal("Name\tAmount\nTea\t3", await Ok("csv /Work/bom.csv --tsv"));
        Assert.Equal("Name\tAmount\nTea\t3", await Ok("cat /Work/bom.csv | csv --tsv"));
    }

    [Fact]
    public async Task Excel_semicolon_csv_with_decimal_commas_and_a_byte_order_mark()
    {
        var bytes = (byte[])[0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("Datum;Betrag;Konto\r\n02.01.2026;12,50;Kasse\r\n03.01.2026;1.203,20;Bank\r\n")];
        await Upload("/Work/excel.csv", bytes);

        Assert.Equal("""
            dialect: separator ';', no quotes, a header row
            rows: 2, columns: 3
            header: A Datum, B Betrag, C Konto

            | Datum | Betrag | Konto |
            | --- | --- | --- |
            | 02.01.2026 | 12,50 | Kasse |
            | 03.01.2026 | 1.203,20 | Bank |
            """, await Ok("csv /Work/excel.csv"));
        Assert.Equal("| Betrag |\n| --- |\n| 12,50 |\n| 1.203,20 |", await Ok("csv /Work/excel.csv --cols Betrag"));
    }

    [Theory]
    [InlineData("a\tb\n1\t2\n", "tab")]
    [InlineData("a|b\n1|2\n", "'|'")]
    [InlineData("a;b\n1;2\n", "';'")]
    public async Task The_separator_is_sniffed(string text, string shown)
    {
        _store.AddTextFile("/Work/s.csv", text);

        Assert.StartsWith($"dialect: separator {shown}, no quotes, a header row\nrows: 1, columns: 2\n", await Ok("csv /Work/s.csv"));
    }

    [Fact]
    public async Task Sniffing_takes_the_most_consistent_count_and_ties_go_in_order()
    {
        // ',' splits every line into 2 fields, ';' too: the tie goes to ','.
        _store.AddTextFile("/Work/tie.csv", "a,b;c\n1,2;3\n");
        Assert.Contains("| b;c |", await Ok("csv /Work/tie.csv"));
        // A comma inside a quoted field doesn't count; ';' is consistent.
        _store.AddTextFile("/Work/semi.csv", "a;b\n\"1,5\";2\n\"3,5,6\";4\n");
        Assert.StartsWith("dialect: separator ';', double quotes", await Ok("csv /Work/semi.csv"));
    }

    [Fact]
    public async Task The_separator_and_the_header_can_be_given()
    {
        _store.AddTextFile("/Work/tie.csv", "a,b;c\n1,2;3\n");

        Assert.Contains("| a,b | c |", await Ok("csv /Work/tie.csv --sep ';'"));
        _store.AddTextFile("/Work/tabs.csv", "a\tb,c\n1\t2,3\n");
        Assert.Contains("| a | b,c |", await Ok("csv /Work/tabs.csv --sep tab"));
        Assert.Contains("| a | b,c |", await Ok("csv /Work/tabs.csv --sep '\\t'"));
        _store.AddTextFile("/Work/pipes.csv", "a|b,c\n1|2,3\n");
        Assert.Contains("| a | b,c |", await Ok("csv /Work/pipes.csv --sep '|'"));
        Assert.Equal("csv: --sep takes ',', ';', '|' or a tab ('\\t' or tab), not ':'", await Fails("csv /Work/tie.csv --sep :"));

        _store.AddTextFile("/Work/costs.csv", Costs);
        var none = await Ok("csv /Work/costs.csv --no-header");
        Assert.StartsWith("dialect: separator ',', no quotes, no header (columns by letter or number)\nrows: 4, columns: 3\n", none);
        Assert.Contains("| A | B | C |\n| --- | --- | --- |\n| Date | Amount | Note |", none);
        Assert.Equal("csv: --header and --no-header don't go together", await Fails("csv /Work/costs.csv --header --no-header"));
    }

    [Theory]
    [InlineData("Name,Score\nrose,3\n", true)]
    [InlineData("Name,Day\nrose,2026-01-02\n", true)]
    [InlineData("Name,Kind\nrose,flower\n", false)]
    [InlineData("Name,\nrose,3\n", false)]
    [InlineData("Name,2026\nrose,3\n", false)]
    [InlineData("1,2\n3,4\n", false)]
    public async Task A_header_is_a_first_row_without_empty_cells_or_numbers_over_a_row_with_a_number(string text, bool header)
    {
        _store.AddTextFile("/Work/h.csv", text);

        Assert.Contains(header ? ", a header row\n" : ", no header (columns by letter or number)\n", await Ok("csv /Work/h.csv"));
    }

    [Fact]
    public async Task An_empty_file_has_no_rows()
    {
        _store.AddTextFile("/Work/empty.csv", "");
        Assert.Equal("rows: 0 (the file is empty)", await Ok("csv /Work/empty.csv"));
        _store.AddTextFile("/Work/blank.csv", "\n\n");
        Assert.Equal("rows: 0 (the file is empty)", await Ok("csv /Work/blank.csv"));
    }

    [Fact]
    public async Task A_header_alone_has_no_rows()
    {
        _store.AddTextFile("/Work/head.csv", "Date,Amount\n");

        Assert.Equal("""
            dialect: separator ',', no quotes, a header row
            rows: 0, columns: 2
            header: A Date, B Amount

            | Date | Amount |
            | --- | --- |
            """, await Ok("csv /Work/head.csv --header"));
        // Without a second row to compare with, a lone row is a row.
        Assert.Contains("no header", await Ok("csv /Work/head.csv"));
        Assert.Equal("csv: --rows 1: the file has 0 rows", await Fails("csv /Work/head.csv --header --rows 1"));
    }

    [Fact]
    public async Task One_column()
    {
        _store.AddTextFile("/Work/one.csv", "Name\nrose\ntulip\n");

        Assert.Equal("""
            dialect: one column (no separator found), no quotes, no header (columns by letter or number)
            rows: 3, columns: 1

            | A |
            | --- |
            | Name |
            | rose |
            | tulip |
            """, await Ok("csv /Work/one.csv"));
        Assert.Equal("| Name |\n| --- |\n| tulip |", await Ok("csv /Work/one.csv --header --rows 2"));
    }

    [Fact]
    public async Task Ragged_rows_are_padded_and_said()
    {
        _store.AddTextFile("/Work/ragged.csv", "a,b,c\n1,2,3\n4,5\n6,7,8,9\n");

        Assert.Equal("""
            dialect: separator ',', no quotes, a header row
            rows: 3, columns: 4 (rows have 2 to 4 fields)
            header: A a, B b, C c, D

            | a | b | c | D |
            | --- | --- | --- | --- |
            | 1 | 2 | 3 |  |
            | 4 | 5 |  |  |
            | 6 | 7 | 8 | 9 |
            """, await Ok("csv /Work/ragged.csv"));
    }

    [Fact]
    public async Task An_unclosed_quote_is_reported_with_its_line()
    {
        _store.AddTextFile("/Work/costs.csv", "a,b\n1,2\n3,\"oops\n4,5\n");

        Assert.Equal("csv: /Work/costs.csv: line 3: a quoted field isn't closed", await Fails("csv /Work/costs.csv"));
        Assert.Equal("csv: line 3: a quoted field isn't closed", await Fails("cat /Work/costs.csv | csv"));
    }

    [Fact]
    public async Task Output_is_cut_at_the_limit_with_a_line_on_how_to_narrow_it()
    {
        _store.AddTextFile("/Work/scores.csv", Rows(48_113));

        var output = await Ok("csv /Work/scores.csv --rows 1-");

        Assert.True(output.Length <= OutputLimit.MaxChars, output.Length.ToString());
        Assert.Matches(@"\n\ncsv: [\d,]+ of 48,113 rows shown; narrow with --rows or --cols$", output);
        Assert.DoesNotContain("[truncated", output);
        var shown = output.Split('\n').Count(l => l.StartsWith("| ", StringComparison.Ordinal)) - 2;
        Assert.EndsWith($"csv: {shown:N0} of 48,113 rows shown; narrow with --rows or --cols", output);
        Assert.DoesNotContain("shown", await Ok("csv /Work/scores.csv --rows 1001-1100 --cols Id"));
    }

    [Fact]
    public async Task Piped_input_is_read_as_csv()
    {
        _store.AddTextFile("/Work/costs.csv", Costs);

        Assert.Equal("| Note |\n| --- |\n| Bus |", await Ok("cat /Work/costs.csv | csv --cols Note --rows 2"));
        Assert.StartsWith("dialect: separator ',', no quotes, a header row\n", await Ok("cat /Work/costs.csv | csv"));
        Assert.Equal("| Note |\n| --- |\n| Bus |", await Ok("cat /Work/costs.csv | csv C3"));
        Assert.Equal("csv: reads from the pipe, so it takes no path (got '/Work/costs.csv')", await Fails("cat /Work/costs.csv | csv /Work/costs.csv"));
    }

    [Fact]
    public async Task Refuses_what_is_not_a_text_file()
    {
        await Upload("/Work/costs.csv", [0xFF, 0xFE, 0x41, 0x00]);
        _store.AddAttachment("/Work/shot.png");
        _store.AddAttachment("/Work/paper.pdf");

        Assert.Equal("csv: /Work/costs.csv is kept as bytes (not UTF-8 text); Kumbu² can't read it as CSV", await Fails("csv /Work/costs.csv"));
        Assert.Equal("csv: /Work/shot.png is an image, not text; Kumbu² can't read it as CSV", await Fails("csv /Work/shot.png"));
        Assert.Equal("csv: /Work/paper.pdf is a PDF, not text; Kumbu² can't read it as CSV", await Fails("csv /Work/paper.pdf"));
        Assert.Equal("csv: /Work/plan.md is a note; cat reads its tables", await Fails("csv /Work/plan.md"));
        Assert.StartsWith("csv: /Work/nope.csv: No such file or directory", await Fails("csv /Work/nope.csv"));
        Assert.Equal("csv: /Work: Is a directory", await Fails("csv /Work"));
        Assert.Equal("csv: Work/costs.csv: paths must be absolute (there is no working directory)", await Fails("csv Work/costs.csv"));
        Assert.Equal("csv: missing operand (give a path, or pipe into it)", await Fails("csv"));
        Assert.StartsWith("csv: too many arguments", await Fails("csv /Work/a.csv /Work/b.csv"));
    }

    [Fact]
    public async Task Refuses_a_file_larger_than_15_MB()
    {
        var time = new FakeTimeProvider(InMemoryNoteStore.Seeded.AddDays(1));
        var files = new AttachmentWriter(_store, _store, time, new FixedCaller(Writers.Dev));
        var bytes = new byte[AttachmentFormat.MaxBytes + 1];
        Array.Fill(bytes, (byte)'a');
        var head = bytes.AsMemory(0, 64);
        var blob = await files.PutLargeAsync("Work", head, new MemoryStream(bytes, 64, bytes.Length - 64), bytes.Length, CancellationToken.None);
        Assert.False((await files.WriteLargeAsync("/Work/big.csv", blob, head, null, null, CancellationToken.None)).Result.IsError);

        Assert.Equal("csv: /Work/big.csv is larger than 15 MB: Kumbu² keeps it, but doesn't read it", await Fails("csv /Work/big.csv"));
    }

    [Fact]
    public async Task A_txt_holding_csv_is_read_as_csv() =>
        Assert.Contains("| 1 | 2 |", await OkAfter("/Work/data.txt", "a,b\n1,2\n", "csv /Work/data.txt"));

    [Fact]
    public async Task Two_megabytes_read_quickly()
    {
        var text = new StringBuilder("Id;Name;Amount;Note\n");
        for (var i = 0; text.Length < 2 * 1024 * 1024; i++) text.Append(i).Append(";\"name ").Append(i).Append("\";").Append(i % 977).Append(",50;a note, with a comma\n");
        _store.AddTextFile("/Work/big.csv", text.ToString());

        var clock = Stopwatch.StartNew();
        var overview = await Ok("csv /Work/big.csv");
        var tail = await Ok("csv /Work/big.csv --tsv --cols Amount | wc -l");
        clock.Stop();

        Assert.StartsWith("dialect: separator ';', double quotes, a header row\n", overview);
        Assert.True(int.Parse(tail) > 40_000, tail);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), clock.Elapsed.ToString());
    }

    [Fact]
    public async Task Help_csv_starts_with_its_usage_and_shows_examples()
    {
        var help = await Ok("help csv");

        Assert.StartsWith("csv [options] [path] [B2:D20]", help);
        Assert.Contains("csv /Work/costs.csv --rows 2-20 --cols Date,Amount", help);
        Assert.Contains("\n  csv [options] [path] [B2:D20] ", await Ok("help"));
        Assert.Contains("\n  csv [options] [path] [B2:D20] ", Manual.ToolDescription);
    }

    private async Task<string> OkAfter(string path, string text, string line)
    {
        _store.AddTextFile(path, text);
        return await Ok(line);
    }

    private async Task Upload(string path, byte[] bytes)
    {
        var time = new FakeTimeProvider(InMemoryNoteStore.Seeded.AddDays(1));
        var files = new AttachmentWriter(_store, _store, time, new FixedCaller(Writers.Dev));
        var result = await files.WriteAsync(path, bytes, null, null, CancellationToken.None);
        Assert.False(result.Result.IsError, result.Result.Output);
    }
}
