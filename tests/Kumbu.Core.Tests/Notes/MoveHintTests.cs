// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Core.Sh;
using Kumbu.Core.Tests.Fakes;
using Microsoft.Extensions.Time.Testing;

namespace Kumbu.Core.Tests.Notes;

/// <summary>The "moved to" sentence of an error names where the note went, never who moved it (spec §1.4).</summary>
public class MoveHintTests
{
    private const string Label = "MARKER-key";

    private readonly InMemoryNoteStore _store = new(("/W/a.md", "a\n"));

    private readonly Shell _sh;

    public MoveHintTests()
    {
        var time = new FakeTimeProvider(InMemoryNoteStore.Seeded.AddDays(1));
        var caller = new FixedCaller(Label);
        _sh = new Shell(_store, new NoteWriter(_store, time, caller), new NoteMover(_store, time, caller));
    }

    private Task<ShellResult> Run(string line) => _sh.RunAsync(line, CancellationToken.None);

    [Fact]
    public async Task A_moved_path_names_no_writer_for_anyone()
    {
        Assert.False((await Run("mv /W/a.md /W/b.md")).IsError);

        var hint = await Moves.HintAsync(_store, VaultPath.TryParse("/W/a.md", out var path) ? path : throw new InvalidOperationException(), CancellationToken.None);

        Assert.StartsWith("; it was moved to /W/b.md", hint);
        Assert.DoesNotContain(Label, hint, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("(", hint, StringComparison.Ordinal);
        Assert.DoesNotContain(Label, (await Run("cat /W/a.md")).Output, StringComparison.OrdinalIgnoreCase);

        await Run("rm /W/b.md");
        var gone = (await Run("cat /W/a.md")).Output;
        Assert.DoesNotContain(Label, gone, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(", and is not there any more ('trash' may list it)", gone);
    }
}
