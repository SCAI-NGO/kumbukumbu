// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Diagnostics;
using System.Text;
using Kumbu.Core.Calendar;
using Kumbu.Core.Tests.Fakes;

namespace Kumbu.Core.Tests.Calendar;

/// <summary>What reading a large vault on demand costs (spec, "Reading cost"); run by hand, never in CI. No timing assertion.</summary>
public class ReadingCostTests(ITestOutputHelper output)
{
    private static readonly TimeZoneInfo Vienna = Zones.Find("Europe/Vienna")!;
    private static readonly DateTime Now = new(2026, 11, 13, 9, 0, 0, DateTimeKind.Utc);

    [Fact(Explicit = true)]
    public async Task Whats_on_for_5000_notes()
    {
        var store = Vault(5000, cards: 50);

        var cold = await Measure(store);
        var warm = await Measure(store);
        var again = await Measure(store);
        // The same notes as new strings, as each request decrypts them: parsed again, but with the code already warm.
        var fresh = await Measure(Vault(5000, cards: 50));

        output.WriteLine($"5000 notes, 50 cards: {cold.Entries} entries, {cold.On} on");
        output.WriteLine($"cold: {cold.Milliseconds} ms; warm: {warm.Milliseconds} ms, {again.Milliseconds} ms; new strings: {fresh.Milliseconds} ms");
    }

    private static async Task<(long Milliseconds, int Entries, int On)> Measure(InMemoryNoteStore store)
    {
        var watch = Stopwatch.StartNew();
        var calendars = await CalendarReader.ReadAllAsync(store, null, CancellationToken.None);
        var on = WhatsOn.Of(calendars, Now, Vienna);
        watch.Stop();
        return (watch.ElapsedMilliseconds, calendars.Sum(c => c.Entries.Count), on.Count);
    }

    // One note in ten with when, one in fifty of those a task, 2 KB of text each, inline tags here and there.
    private static InMemoryNoteStore Vault(int count, int cards)
    {
        var random = new Random(46);
        var notes = new List<(string, string)>
        {
            ("/Work/Board/Board.md", "---\nkumbu-board: 1\ncolumns:\n  - Ideas\n  - Doing\n  - Done: [archivable]\n---\n"),
        };

        for (var i = 0; i < count; i++)
        {
            var text = new StringBuilder();
            if (i % 10 == 0)
            {
                var day = new DateOnly(2026, 11, 13).AddDays(random.Next(-60, 60));
                text.Append($"---\nwhen: {day:yyyy-MM-dd}{(i % 30 == 0 ? "T14:00" : "")}\n");
                if (i % 50 == 0) text.Append("task: open\nupcoming: 7d\n");
                text.Append("tags: [project/kumbu]\n---\n");
            }

            text.Append($"# Note {i}\n\n");
            while (text.Length < 2048)
                text.Append(i % 7 == 0 ? "Some words about #work and the [[Pump]] we talked about. " : "Lorem ipsum dolor sit amet, consectetur adipiscing elit. ");
            notes.Add(($"/Work/Folder {i % 40}/Note {i}.md", text.ToString()));
        }

        for (var i = 0; i < cards; i++)
        {
            var due = i % 2 == 0 ? $"due: {new DateOnly(2026, 11, 13).AddDays(random.Next(-20, 20)):yyyy-MM-dd}\n" : "";
            notes.Add(($"/Work/Board/Card {i}.md", $"---\ncolumn: {(i % 3 == 0 ? "Done" : "Ideas")}\n{due}---\nWhat it is about.\n"));
        }

        return new InMemoryNoteStore([.. notes]);
    }
}
