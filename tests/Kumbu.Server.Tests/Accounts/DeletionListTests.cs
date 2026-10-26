// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Server.Accounts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Kumbu.Server.Tests.Accounts;

/// <summary>The list of deleted user ids, kept in a file outside the database so a restore can't roll it back.</summary>
public sealed class DeletionListTests : IDisposable
{
    private const string A = "0123456789abcdef0123456789abcdef";
    private const string B = "fedcba9876543210fedcba9876543210";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "kumbu-deletions-" + Guid.NewGuid().ToString("N"));
    private readonly CapturedLog _log = new();

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private DeletionList List(string? path) =>
        new(Options.Create(new KumbuOptions { Deletions = new DeletionsOptions { Path = path } }), LoggerFactory.Create(b => b.AddProvider(_log)).CreateLogger<DeletionList>());

    [Fact]
    public async Task Added_ids_read_back()
    {
        var dir = Path.Combine(_root, "deletions");
        var list = List(dir);

        Assert.True(list.Configured);
        Assert.True(await list.AddAsync(A, Ct));
        Assert.True(await list.AddAsync(B, Ct));

        Assert.Equal(new HashSet<string> { A, B }, await list.ReadAsync(Ct));
        // Another process (a restart) reads the same file.
        Assert.Equal(new HashSet<string> { A, B }, await List(dir).ReadAsync(Ct));
        Assert.Equal($"{A}\n{B}\n", await File.ReadAllTextAsync(Path.Combine(dir, "deleted-users"), Ct));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(dir, "deleted-users")));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(dir));
        }
    }

    [Fact]
    public async Task Concurrent_additions_keep_one_id_per_line()
    {
        var list = List(_root);
        var ids = Enumerable.Range(0, 40).Select(i => i.ToString("x32")).ToArray();

        Assert.All(await Task.WhenAll(ids.Select(id => list.AddAsync(id, Ct))), Assert.True);

        Assert.Equal(ids.ToHashSet(), await list.ReadAsync(Ct));
        Assert.Equal(40, (await File.ReadAllLinesAsync(Path.Combine(_root, "deleted-users"), Ct)).Length);
    }

    [Fact]
    public async Task A_messy_list_reads_as_its_ids()
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(
            Path.Combine(_root, "deleted-users"), $"{A}\r\n\n  {B} \njunk with spaces\n0123456789abcdef 0123456789abcdef\nABCDEF0123456789ABCDEF0123456789\n{A}\n", Ct);

        Assert.Equal(new HashSet<string> { A, B }, await List(_root).ReadAsync(Ct));
    }

    [Fact]
    public async Task Without_a_path_nothing_is_written()
    {
        var list = List(null);

        Assert.False(list.Configured);
        Assert.False(await list.AddAsync(A, Ct));
        Assert.Empty(await list.ReadAsync(Ct));
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public async Task A_missing_file_reads_as_empty()
    {
        Assert.Empty(await List(Path.Combine(_root, "never-written")).ReadAsync(Ct));
    }

    [Fact]
    public async Task An_unwritable_list_answers_false_and_logs_an_error()
    {
        Directory.CreateDirectory(_root);
        var file = Path.Combine(_root, "a-file");
        await File.WriteAllTextAsync(file, "not a directory", Ct);

        Assert.False(await List(file).AddAsync(A, Ct));

        Assert.Contains(_log.Lines, l => l.StartsWith("Error", StringComparison.Ordinal) && l.Contains(A, StringComparison.Ordinal));
    }
}
