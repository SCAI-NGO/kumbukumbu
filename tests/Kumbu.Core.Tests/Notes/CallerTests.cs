// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Core.Sh;
using Kumbu.Core.Tests.Fakes;
using Microsoft.Extensions.Time.Testing;

namespace Kumbu.Core.Tests.Notes;

/// <summary>The caller decides the writer label and which vaults may be written.</summary>
public class CallerTests
{
    private readonly InMemoryNoteStore _store = new(("/Work/plan.md", "one\n"), ("/Work/gone.md", "bye\n"), ("/Home/a.md", "a\n"));
    private readonly FakeTimeProvider _time = new(InMemoryNoteStore.Seeded.AddDays(1));

    private static CancellationToken Ct => CancellationToken.None;

    private static VaultPath P(string s) => VaultPath.TryParse(s, out var p) ? p : throw new ArgumentException(s);

    private Shell ShellFor(ICaller caller)
    {
        var writer = new NoteWriter(_store, _time, caller);
        return new Shell(_store, writer, new NoteMover(_store, _time, caller));
    }

    private static ICaller ReadsWork => new FixedCaller("reader", vault => vault != "Work");

    private async Task<string> SnapshotAsync()
    {
        var notes = await _store.ReadNotesUnderAsync(VaultPath.Root, Ct);
        var trash = await _store.ListTrashAsync(VaultPath.Root, Ct);
        var checkpoints = await _store.ListCheckpointsAsync(VaultPath.Root, Ct);
        return string.Join('|', notes.OrderBy(n => n.Path.Value, StringComparer.Ordinal).Select(n => $"{n.Path.Value}={n.Content}"))
            + $"|trash={trash.Count}|checkpoints={string.Join(',', checkpoints.Select(c => c.Name))}";
    }

    [Fact]
    public async Task WritesCarryTheCallersWriterLabel()
    {
        var sh = ShellFor(new FixedCaller("claude-laptop"));
        var writer = new NoteWriter(_store, _time, new FixedCaller("claude-laptop"));

        var result = await writer.WriteAsync("/Work/plan.md", "two\n", VersionToken.Of("one\n"), Ct);

        Assert.False(result.IsError);
        Assert.Equal("claude-laptop", (await _store.GetAsync(P("/Work/plan.md"), Ct))!.LastWriter);
        Assert.Contains("claude-laptop", (await sh.RunAsync("history /Work/plan.md", Ct)).Output);
    }

    [Fact]
    public async Task AReadOnlyCallerIsRefusedEveryWrite()
    {
        // Set up with a caller that may write: a trashed note, a checkpoint to remove and one to restore from.
        var admin = ShellFor(new FixedCaller("admin"));
        await admin.RunAsync("rm /Work/gone.md", Ct);
        await admin.RunAsync("mkcp /Work before", Ct);
        var before = await SnapshotAsync();

        var caller = ReadsWork;
        var writer = new NoteWriter(_store, _time, caller);
        var sh = ShellFor(caller);
        var version = VersionToken.Of("one\n");

        (string Command, ShellResult Result)[] results =
        [
            ("write", await writer.WriteAsync("/Work/plan.md", "two\n", version, Ct)),
            ("write", await writer.WriteAsync("/Work/new.md", "new\n", null, Ct)),
            ("edit", await writer.EditAsync("/Work/plan.md", version, "one", "two", Ct)),
            ("replace", await new Replacer(_store, _time, new Plans(), caller).RunAsync("/Work", "one", "two", false, null, Ct)),
            ("replace", await new Replacer(_store, _time, new Plans(), caller).RunAsync("/Work", "one", "two", false, "plan-token", Ct)),
            ("mv", await sh.RunAsync("mv /Work/plan.md /Work/other.md", Ct)),
            ("mv", await sh.RunAsync("mv /Work/ /Work/sub/", Ct)),
            ("rm", await sh.RunAsync("rm /Work/plan.md", Ct)),
            ("restore", await sh.RunAsync("restore /Work/gone.md", Ct)),
            ("restore", await sh.RunAsync("restore --checkpoint before /Work/plan.md", Ct)),
            ("mkcp", await sh.RunAsync("mkcp /Work after", Ct)),
            ("rmcp", await sh.RunAsync("rmcp /Work before", Ct)),
        ];

        foreach (var (command, result) in results)
        {
            Assert.Equal(ShellResult.Error($"{command}: this key can only read vault Work"), result);
        }

        Assert.Equal(before, await SnapshotAsync());
    }

    [Fact]
    public async Task AReadOnlyCallerCanStillWriteWhereTheKeyAllowsIt()
    {
        var result = await ShellFor(ReadsWork).RunAsync("mkcp /Home mine", Ct);

        Assert.False(result.IsError);
    }

    [Fact]
    public async Task ReadingStillWorksForAReadOnlyCaller()
    {
        var sh = ShellFor(ReadsWork);

        Assert.Equal("one\n", (await sh.RunAsync("cat /Work/plan.md", Ct)).Output);
        Assert.False((await sh.RunAsync("ls /Work", Ct)).IsError);
        Assert.False((await sh.RunAsync("history /Work/plan.md", Ct)).IsError);
        Assert.False((await sh.RunAsync("lscp /Work", Ct)).IsError);
    }
}
