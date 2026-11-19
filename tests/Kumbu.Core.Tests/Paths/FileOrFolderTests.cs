// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Paths;

namespace Kumbu.Core.Tests.Paths;

public class FileOrFolderTests
{
    private static VaultPath P(string s) => VaultPath.TryParse(s, out var p) ? p : throw new ArgumentException(s);

    private static string? Clash(string[] placed, params string[] others)
    {
        var stored = others.Concat(placed).Select(P).ToHashSet();
        var bulk = FileOrFolder.Clash([.. placed.Select(P)], stored)?.Value;
        // The few-paths form answers the same, from what is stored before the write.
        var now = others.Select(P).ToHashSet();
        Assert.Equal(bulk, FileOrFolder.Clash([.. placed.Select(P)], new HashSet<VaultPath>(), now.Contains, now)?.Value);
        return bulk;
    }

    [Theory]
    // A file where a folder is: by a note, a file or a kept folder's marker below it.
    [InlineData("/W/v1.2", "/W/v1.2", "/W/v1.2/notes.md")]
    [InlineData("/W/v1.2", "/W/v1.2", "/W/v1.2/a/shot.png")]
    [InlineData("/W/v1.2", "/W/v1.2", "/W/v1.2/.vaultkeep")]
    // Anything below a file or a note: the one in the way.
    [InlineData("/W/Makefile/x.md", "/W/Makefile", "/W/Makefile")]
    [InlineData("/W/Makefile/a/.vaultkeep", "/W/Makefile", "/W/Makefile")]
    [InlineData("/W/plan.md/x.png", "/W/plan.md", "/W/plan.md")]
    public void A_file_and_a_folder_never_share_a_name(string placed, string expected, string other) =>
        Assert.Equal(expected, Clash([placed], other));

    [Fact]
    public void Side_by_side_is_fine()
    {
        Assert.Null(Clash(["/W/Makefile"], "/W/v1.2/notes.md", "/W/Makefile.md", "/W/Make/x.md"));
        Assert.Null(Clash(["/W/v1.2/x.md", "/W/v1.2/y.csv"], "/W/v1.2/notes.md"));
        Assert.Null(Clash([], "/W/a", "/W/a/b"));
    }

    [Fact]
    public void What_leaves_in_the_batch_is_in_nobodys_way()
    {
        HashSet<VaultPath> now = [P("/W/v1.2/notes.md"), P("/W/Makefile")];
        Assert.Null(FileOrFolder.Clash([P("/W/v1.2")], new HashSet<VaultPath> { P("/W/v1.2/notes.md") }, now.Contains, now));
        Assert.Null(FileOrFolder.Clash([P("/W/Makefile/x.md")], new HashSet<VaultPath> { P("/W/Makefile") }, now.Contains, now));
        Assert.Equal(P("/W/Makefile"), FileOrFolder.Clash([P("/W/Makefile/x.md")], new HashSet<VaultPath>(), now.Contains, now));
    }

    [Fact]
    public void Two_placed_paths_clash_with_each_other() =>
        Assert.Equal("/W/data", Clash(["/W/data", "/W/data/x.csv"]));

    [Fact]
    public void The_sentences_name_the_other()
    {
        Assert.Equal("upload: /W/v1.2 is a folder; a file and a folder can't share a name", FileOrFolder.IsFolder("upload", P("/W/v1.2")));
        Assert.Equal("write: /W/Makefile is a file, not a folder; nothing can be kept below it", FileOrFolder.NotFolder("write", P("/W/Makefile")));
        Assert.Equal("mv: /W/plan.md is a note, not a folder; nothing can be kept below it", FileOrFolder.NotFolder("mv", P("/W/plan.md")));
    }
}
