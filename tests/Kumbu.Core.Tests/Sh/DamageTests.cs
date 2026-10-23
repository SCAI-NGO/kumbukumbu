// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Core.Sh;
using Kumbu.Core.Tests.Fakes;

namespace Kumbu.Core.Tests.Sh;

/// <summary>A stored note that doesn't open: every command says so instead of "No such file".</summary>
public class DamageTests
{
    private const string Suffix = ": damaged: the stored note doesn't open (changed or damaged outside Kumbu²; the server log has details). "
        + "Its path stays taken until the document is repaired or removed in the database";

    private readonly InMemoryNoteStore _store = new(("/Demo/welcome.md", "See [[kumbu]].\n"), ("/Demo/ok.md", "ok\n"));
    private readonly Shell _sh;
    private readonly NoteWriter _writer;

    public DamageTests()
    {
        _store.Damaged.Add(VaultPath.TryParse("/Demo/projects/kumbu.md", out var p) ? p : null!);
        _sh = TestShell.Of(_store);
        _writer = new NoteWriter(_store, TimeProvider.System);
    }

    private Task<ShellResult> Run(string line) => _sh.RunAsync(line, CancellationToken.None);

    [Theory]
    [InlineData("cat /Demo/projects/kumbu.md", "cat")]
    [InlineData("head /Demo/projects/kumbu.md", "head")]
    [InlineData("history /Demo/projects/kumbu.md", "history")]
    [InlineData("rm /Demo/projects/kumbu.md", "rm")]
    [InlineData("restore /Demo/projects/kumbu.md", "restore")]
    [InlineData("mv /Demo/projects/kumbu.md /Demo/x.md", "mv")]
    public async Task ShellCommandsSayDamaged(string line, string tool) =>
        Assert.Equal(ShellResult.Error($"{tool}: /Demo/projects/kumbu.md{Suffix}"), await Run(line));

    [Fact]
    public async Task MvOntoADamagedPath() =>
        Assert.Equal(ShellResult.Error($"mv: /Demo/projects/kumbu.md{Suffix}"), await Run("mv /Demo/ok.md /Demo/projects/kumbu.md"));

    [Fact]
    public async Task WriteToADamagedPath() => Assert.Equal(
        ShellResult.Error($"write: /Demo/projects/kumbu.md{Suffix}"),
        await _writer.WriteAsync("/Demo/projects/kumbu.md", "new", null, Writers.Dev, CancellationToken.None));

    [Fact]
    public async Task EditOfADamagedPath() => Assert.Equal(
        ShellResult.Error($"edit: /Demo/projects/kumbu.md{Suffix}"),
        await _writer.EditAsync("/Demo/projects/kumbu.md", "abc", null, "x", Writers.Dev, CancellationToken.None));

    [Fact]
    public async Task LsOfTheVaultAndTheRootCountThem()
    {
        Assert.Equal("ok.md\nwelcome.md\n(1 note here doesn't open: changed or damaged outside Kumbu²; the server log has details)", (await Run("ls /Demo")).Output);
        Assert.EndsWith("(1 note here doesn't open: changed or damaged outside Kumbu²; the server log has details)", (await Run("ls")).Output);
    }
}
