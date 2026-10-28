// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Notes;
using Kumbu.Core.Sh;
using Kumbu.Core.Tests.Fakes;
using Microsoft.Extensions.Time.Testing;

namespace Kumbu.Core.Tests.Sh;

public class ShellObserverTests
{
    private sealed class Recorder : IShellObserver
    {
        public List<IReadOnlyList<string>> Lines { get; } = [];
        public List<string> Timings { get; } = [];

        public void Ran(IReadOnlyList<string> commands) => Lines.Add(commands);

        public void Timed(string command, TimeSpan elapsed) => Timings.Add(command);
    }

    private static (Shell Shell, Recorder Seen) Make(InMemoryNoteStore? store = null)
    {
        store ??= new InMemoryNoteStore(("/V/a.md", "one\ntwo\n"));
        var time = new FakeTimeProvider(InMemoryNoteStore.Seeded.AddDays(1));
        var seen = new Recorder();
        var shell = new Shell(store, new NoteWriter(store, time, new FixedCaller(Writers.Dev)), new NoteMover(store, time, new FixedCaller(Writers.Dev)), observer: seen);
        return (shell, seen);
    }

    [Fact]
    public async Task A_pipeline_reports_its_commands_and_times_each_stage()
    {
        var (shell, seen) = Make();

        await shell.RunAsync("cat /V/a.md | head -1", CancellationToken.None);

        Assert.Equal(["cat", "head"], Assert.Single(seen.Lines));
        Assert.Equal(["cat", "head"], seen.Timings);
    }

    [Fact]
    public async Task An_unknown_command_is_reported_but_not_timed()
    {
        var (shell, seen) = Make();

        await shell.RunAsync("grep x", CancellationToken.None);

        Assert.Equal(["grep"], Assert.Single(seen.Lines));
        Assert.Empty(seen.Timings);
    }

    [Fact]
    public async Task A_line_that_does_not_parse_reports_nothing()
    {
        var (shell, seen) = Make();

        await shell.RunAsync("cat \"/V/a.md", CancellationToken.None);

        Assert.Empty(seen.Lines);
        Assert.Empty(seen.Timings);
    }

    [Fact]
    public async Task A_stage_that_throws_is_still_timed()
    {
        var store = new InMemoryNoteStore(("/V/a.md", "one\ntwo\n")) { Contended = _ => throw new InvalidOperationException("unexpected") };
        var (shell, seen) = Make(store);

        await Assert.ThrowsAsync<InvalidOperationException>(() => shell.RunAsync("rm /V/a.md", CancellationToken.None));

        Assert.Equal(["rm"], seen.Timings);
    }
}
