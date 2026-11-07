// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Notes;
using Kumbu.Core.Sh;
using Kumbu.Core.Tests.Fakes;

namespace Kumbu.Core.Tests.Sh;

/// <summary><c>du</c> (WebDAV design §3.12): the size of the current content below a path.</summary>
public class DuTests
{
    /// <summary>5,118 bytes in /Work: projects/ 5,000 (x.md 3,000, deep/ 2,000), a.md 100, zz.md 10, Screens/ 8 (a PNG).</summary>
    private static InMemoryNoteStore Store()
    {
        var store = new InMemoryNoteStore(
            ("/Work/a.md", new string('a', 100)), ("/Work/zz.md", new string('z', 10)),
            ("/Work/projects/x.md", new string('x', 3000)), ("/Work/projects/deep/y.md", new string('y', 2000)),
            ("/Personal/p.md", "ü"));
        store.AddAttachment("/Work/Screens/shot.png");
        return store;
    }

    private static Task<ShellResult> Run(string line, InMemoryNoteStore? store = null) =>
        TestShell.Of(store ?? Store()).RunAsync(line, CancellationToken.None);

    [Fact]
    public async Task Du_lists_children_largest_first_with_a_total()
    {
        Assert.Equal(
            ShellResult.Ok("""
                 5 KB  /Work/projects/
                100 B  /Work/a.md
                 10 B  /Work/zz.md
                  8 B  /Work/Screens/
                 5 KB  total
                """),
            await Run("du /Work"));
        Assert.Equal(await Run("du /Work"), await Run("du -h /Work"));
    }

    [Fact]
    public async Task Du_without_a_path_sizes_the_vaults()
    {
        Assert.Equal(
            ShellResult.Ok("""
                5118  /Work/
                   2  /Personal/
                5120  total
                """),
            await Run("du -b"));
    }

    [Theory]
    [InlineData("du -s /Work")]
    [InlineData("du -sh /Work")]
    [InlineData("du -d 0 /Work")]
    public async Task Du_dash_s_gives_the_total(string line) => Assert.Equal(ShellResult.Ok("5 KB  /Work"), await Run(line));

    [Fact]
    public async Task Du_dash_b_gives_bytes()
    {
        Assert.Equal(ShellResult.Ok("5118  /Work"), await Run("du -sb /Work"));
        Assert.Equal(ShellResult.Ok("3000  /Work/projects/x.md"), await Run("du -b /Work/projects/x.md"));
    }

    [Fact]
    public async Task Du_dash_d_2_goes_deeper()
    {
        Assert.Equal(
            ShellResult.Ok("""
                5000  /Work/projects/
                3000  /Work/projects/x.md
                2000  /Work/projects/deep/
                 100  /Work/a.md
                  10  /Work/zz.md
                   8  /Work/Screens/
                   8  /Work/Screens/shot.png
                5118  total
                """),
            await Run("du -b -d 2 /Work"));
    }

    [Fact]
    public async Task Du_with_a_star_explains_there_is_no_globbing()
    {
        var result = await Run("du -sh /Work/*");

        Assert.True(result.IsError);
        Assert.Contains("no globbing", result.Output);
        Assert.Contains("du lists a folder's children itself: du /Work", result.Output);
    }

    [Fact]
    public async Task Du_refuses_what_is_not_there()
    {
        Assert.Equal(ShellResult.Error("du: cannot access '/Work/nope': No such file or directory"), await Run("du /Work/nope"));
        Assert.Equal(ShellResult.Error("du: invalid maximum depth 'x'"), await Run("du -d x /Work"));
    }

    [Fact]
    public async Task Du_reads_shared_vaults()
    {
        var store = new InMemoryNoteStore(("/garten/plan.md", "Beete planen\n"));
        store.Infos["garten"] = new VaultInfo("\"Alice\"", AccessLevel.Read, true);

        Assert.Equal(ShellResult.Ok("13  /garten"), await Run("du -sb /garten", store));
    }
}
