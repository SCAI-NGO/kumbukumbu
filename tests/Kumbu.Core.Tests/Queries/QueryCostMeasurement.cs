// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Core.Queries;
using Kumbu.Core.Sh;
using Kumbu.Core.Tests.Fakes;

namespace Kumbu.Core.Tests.Queries;

/// <summary>
/// What a query costs, read, parse and evaluation apart, over synthetic vaults (ADR 0025: properties join the sealed
/// extract only once parsing is measured to matter). Not a test: it runs with KUMBU_QUERY_MEASURE=1 and prints its
/// figures, p50 and p95 in milliseconds. The figures of 2026-11-24 are in ADR 0027.
/// </summary>
public class QueryCostMeasurement
{
    private const int Warmups = 5;
    private const int Runs = 30;

    private const string Birthdays = """
        ---
        type: query
        params:
          month:
            type: number
            required: true
        ---
        ```base
        filters:
          and:
            - file.inFolder("People")
            - birthday.month == this.month
        views:
          - type: table
            name: By day
            order: [file.basename, birthday, kind]
            sort:
              - property: birthday
        ```
        """;

    // Every note passes the filter and is sorted: the worst a single view does.
    private const string OpenTasks = """
        ```base
        formulas:
          late: if(due < today(), "late", "")
        filters:
          or:
            - status != "done"
            - tags.contains("work")
            - title.lower().contains("e")
        views:
          - type: table
            name: Open
            order: [file.name, status, due, formula.late]
            sort:
              - property: due
                direction: DESC
              - property: priority
        ```
        """;

    [Theory]
    [InlineData(1_000)]
    [InlineData(5_000)]
    [InlineData(20_000)]
    public async Task Measure(int size)
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("KUMBU_QUERY_MEASURE") == "1", "KUMBU_QUERY_MEASURE=1 not set");
        var output = TestContext.Current.TestOutputHelper!;
        var store = Vault(size);
        var sh = TestShell.Of(store);
        var root = VaultPath.Root.Child("V");
        var types = PropertyTypes.Read("""{"types": {"birthday": "date", "due": "date"}}""");
        var now = new DateTime(2026, 11, 24, 12, 0, 0);
        var ct = CancellationToken.None;

        var read = Time(() => store.ReadNotesUnderAsync(root, ct).GetAwaiter().GetResult());
        var notes = await store.ReadNotesUnderAsync(root, ct);
        // Copies are contents not seen before, as after an unlock or a write; the store's own are parsed already.
        var parse = Time(() => notes.Select(n => Properties.Read(new string(n.Content), types)).ToList());
        var parsedOnce = Time(() => notes.Select(n => Properties.Read(n.Content, types)).ToList());
        var parsed = notes.Select(n => new QueryNote(string.Join('/', n.Path.Segments.Skip(1)), n.Content, Properties.Read(n.Content, types))).ToList();
        var bytes = notes.Sum(n => (long)Encoding.UTF8.GetByteCount(n.Content));

        output.WriteLine($"{size:N0} notes, {bytes / 1024 / 1024.0:F1} MiB");
        output.WriteLine($"  read (store listing)        {read}");
        output.WriteLine($"  parse frontmatter           {parse}");
        output.WriteLine($"  parse, already parsed       {parsedOnce}");
        foreach (var (name, text, args) in new[]
                 {
                     ("Birthdays", Birthdays, new Dictionary<string, IReadOnlyList<string>> { ["month"] = ["11"] }),
                     ("Open tasks", OpenTasks, new Dictionary<string, IReadOnlyList<string>>()),
                 })
        {
            var query = Query.Read(new QueryNote("Queries/q.md", text, Properties.Read(text, types)));
            var rows = query.Run(null, args, parsed, now).Rows.Count;
            output.WriteLine($"  evaluate {name,-19} {Time(() => query.Run(null, args, parsed, now))}  ({rows:N0} rows)");
        }

        foreach (var line in new[] { "query execute /V/Queries/Birthdays.md --param month=11", "query execute '/V/Queries/Open tasks.md'" })
            output.WriteLine($"  sh {line[14..],-48} {Time(() => sh.RunAsync(line, ct).GetAwaiter().GetResult())}");
    }

    private static string Time(Func<object> run)
    {
        for (var i = 0; i < Warmups; i++) run();
        var ms = new double[Runs];
        for (var i = 0; i < Runs; i++)
        {
            var watch = Stopwatch.StartNew();
            GC.KeepAlive(run());
            ms[i] = watch.Elapsed.TotalMilliseconds;
        }

        Array.Sort(ms);
        return string.Create(CultureInfo.InvariantCulture, $"p50 {ms[Runs / 2],7:F1}  p95 {ms[(int)(Runs * 0.95)],7:F1}");
    }

    // People, tasks and plain notes, as a vault of a few years has them: 5 to 15 properties, 2 to 5 KB of prose.
    private static InMemoryNoteStore Vault(int size)
    {
        var random = new Random(size);
        var words = "the of note meeting idea plan project review draft open later because which there people work garden cat story".Split(' ');
        var notes = new List<(string, string)>(size + 2)
        {
            ("/V/Queries/Birthdays.md", Birthdays),
            ("/V/Queries/Open tasks.md", OpenTasks),
        };
        for (var i = 0; i < size; i++)
        {
            var kind = i % 3;
            var front = new StringBuilder("---\n");
            front.Append(CultureInfo.InvariantCulture, $"title: {words[random.Next(words.Length)]} {i}\n");
            front.Append(CultureInfo.InvariantCulture, $"created: {new DateTime(2020, 1, 1).AddDays(random.Next(2000)):yyyy-MM-dd}\n");
            front.Append("tags:\n").Append(CultureInfo.InvariantCulture, $"  - {(random.Next(2) == 0 ? "work" : "home")}\n  - t{random.Next(50)}\n");
            if (kind == 0)
            {
                front.Append(CultureInfo.InvariantCulture, $"birthday: {new DateTime(1950, 1, 1).AddDays(random.Next(20000)):yyyy-MM-dd}\n");
                front.Append(CultureInfo.InvariantCulture, $"kind: {(random.Next(2) == 0 ? "friend" : "family")}\nage: {random.Next(18, 90)}\n");
            }
            else if (kind == 1)
            {
                front.Append(CultureInfo.InvariantCulture, $"status: {(random.Next(3) == 0 ? "done" : "open")}\npriority: {random.Next(1, 5)}\n");
                front.Append(CultureInfo.InvariantCulture, $"due: {new DateTime(2026, 1, 1).AddDays(random.Next(700)):yyyy-MM-dd}\ndone: {(random.Next(2) == 0 ? "true" : "false")}\n");
            }

            for (var extra = random.Next(0, 9); extra > 0; extra--)
                front.Append(CultureInfo.InvariantCulture, $"p{extra}: \"{words[random.Next(words.Length)]}\"\n");
            front.Append("---\n# Note ").Append(i).Append("\n\n");
            for (var length = random.Next(2_000, 5_000); front.Length < length;)
                front.Append(words[random.Next(words.Length)]).Append(random.Next(12) == 0 ? ".\n\n" : " ");
            var folder = kind switch { 0 => "People", 1 => "Tasks", _ => "Notes" };
            notes.Add(($"/V/{folder}/n{i}.md", front.ToString()));
        }

        var store = new InMemoryNoteStore([.. notes]);
        store.AddTextFile("/V/.obsidian/types.json", """{"types": {"birthday": "date", "due": "date"}}""");
        return store;
    }
}
