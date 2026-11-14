// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Calendar;
using Kumbu.Core.Tags;
using Kumbu.Core.Tests.Fakes;

namespace Kumbu.Core.Tests.Calendar;

public class CalendarReaderTests
{
    private const string Board = "---\nkumbu-board: 1\ncolumns:\n  - Ideas\n  - Done: [archivable]\n---\n";

    private static IEnumerable<string> Paths(VaultCalendar calendar) => calendar.Entries.Select(e => e.Path.Value);

    [Fact]
    public async Task Reads_every_entry_of_a_vault_wherever_it_lives_cards_included()
    {
        var store = new InMemoryNoteStore(
            ("/Work/Pump/Certificate.md", "---\nwhen: 2026-11-20\ntask: open\n---\nRenew it.\n"),
            ("/Work/Calendar/2026-11-12.md", "# Thursday, 12 November 2026\n"),
            ("/Work/Board/Board.md", Board),
            ("/Work/Board/Offsite.md", "---\ncolumn: Ideas\ndue: 2026-11-27\n---\n"),
            ("/Work/Board/Undated.md", "---\ncolumn: Ideas\n---\n"),
            ("/Work/_archive/old.md", "---\nwhen: 2025-01-10\n---\n"),
            ("/Work/Plain.md", "# Nothing dated\n"),
            ("/Home/Zahnarzt.md", "---\nwhen: 2026-11-10T14:00\n---\n"));

        var calendar = await CalendarReader.ReadAsync(store, "Work", null, CancellationToken.None);

        Assert.Equal("Work", calendar.Vault);
        Assert.Equal(["/Work/Board/Offsite.md", "/Work/Calendar/2026-11-12.md", "/Work/Pump/Certificate.md"], Paths(calendar).Order(StringComparer.Ordinal));
        Assert.Equal(EntryKind.Card, calendar.Entries.Single(e => e.Title == "Offsite").Kind);
        Assert.Empty(calendar.Problems);
    }

    [Fact]
    public async Task A_tag_narrows_to_whole_notes()
    {
        var store = new InMemoryNoteStore(
            ("/Work/Inline.md", "---\nwhen: 2026-11-20\n---\nAbout #kumbu.\n\nAnd #kumbu again.\n"),
            ("/Work/Front.md", "---\nwhen: 2026-11-21\ntags: [kumbu/report]\n---\nThe report.\n"),
            ("/Work/Other.md", "---\nwhen: 2026-11-22\n---\nAbout #other.\n"),
            ("/Work/Broken.md", "---\nwhen: soon\n---\n"),
            ("/Work/Broken tagged.md", "---\nwhen: soon\n---\n#kumbu\n"));

        var plain = await CalendarReader.ReadAsync(store, "Work", "kumbu", CancellationToken.None);
        var shouted = await CalendarReader.ReadAsync(store, "Work", TagMatch.Normalise("#KUMBU"), CancellationToken.None);

        foreach (var calendar in new[] { plain, shouted })
        {
            Assert.Equal(["/Work/Front.md", "/Work/Inline.md"], Paths(calendar).Order(StringComparer.Ordinal));
            Assert.Equal("/Work/Broken tagged.md", Assert.Single(calendar.Problems).Path.Value);
        }
    }

    [Fact]
    public async Task Problems_are_listed_never_dropped()
    {
        var store = new InMemoryNoteStore(
            ("/Work/Tomorrow.md", "---\nwhen: tomorrow\n---\n"),
            ("/Work/Fine.md", "---\nwhen: 2026-11-20\n---\n"));

        var calendar = await CalendarReader.ReadAsync(store, "Work", null, CancellationToken.None);

        Assert.Equal("Fine", Assert.Single(calendar.Entries).Title);
        var problem = Assert.Single(calendar.Problems);
        Assert.Equal("/Work/Tomorrow.md", problem.Path.Value);
        Assert.Contains("when: tomorrow is not a date", problem.What);
    }

    [Fact]
    public async Task Reads_all_vaults_the_reader_gives_empty_ones_included()
    {
        var store = new InMemoryNoteStore(
            ("/Work/Calendar/2026-11-12.md", "# Thursday\n"),
            ("/Home/Zahnarzt.md", "---\nwhen: 2026-11-10T14:00\n---\n"));
        store.AddVault("Tagebuch");

        var all = await CalendarReader.ReadAllAsync(store, null, CancellationToken.None);

        Assert.Equal(["Home", "Tagebuch", "Work"], all.Select(c => c.Vault).Order(StringComparer.Ordinal));
        Assert.Empty(all.Single(c => c.Vault == "Tagebuch").Entries);
        Assert.Equal("Zahnarzt", Assert.Single(all.Single(c => c.Vault == "Home").Entries).Title);
    }
}
