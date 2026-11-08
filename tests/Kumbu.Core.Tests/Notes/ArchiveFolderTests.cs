// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;

namespace Kumbu.Core.Tests.Notes;

public class ArchiveFolderTests
{
    private static VaultPath P(string s) => VaultPath.TryParse(s, out var p) ? p : throw new ArgumentException(s);

    [Theory]
    [InlineData("/Work/_archive", true)]
    [InlineData("/Work/_archive/Inbox/a.md", true)]
    [InlineData("/Work", false)]
    [InlineData("/Work/Inbox/_archive/a.md", false)]
    [InlineData("/Work/_archived/a.md", false)]
    [InlineData("/Work/_Archive/a.md", false)]
    [InlineData("/_archive", false)]
    public void Contains_only_the_folder_in_the_vaults_root(string path, bool expected) =>
        Assert.Equal(expected, ArchiveFolder.Contains(P(path)));

    [Fact]
    public void The_root_is_in_no_archive() => Assert.False(ArchiveFolder.Contains(VaultPath.Root));

    [Fact]
    public void Of_names_the_vaults_archive() => Assert.Equal("/Work/_archive", ArchiveFolder.Of("Work").Value);

    [Fact]
    public void A_scope_inside_the_archive_shows_it()
    {
        Assert.True(ArchiveFolder.Shown(P("/Work/_archive/Inbox"), false));
        Assert.True(ArchiveFolder.Shown(P("/Work"), true));
        Assert.False(ArchiveFolder.Shown(P("/Work"), false));
    }
}
