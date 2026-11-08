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

    [Theory]
    [InlineData("report 2026-11-08 213005.pdf", "report.pdf", "2026-11-08T21:30:05")]
    [InlineData("v1.2 notes 2026-11-08 213005.md", "v1.2 notes.md", "2026-11-08T21:30:05")]
    [InlineData("shot 2026-11-08 213005.JPEG", "shot.JPEG", "2026-11-08T21:30:05")]
    [InlineData("Manual 2026-11-08 213005", "Manual", "2026-11-08T21:30:05")]
    [InlineData("report 2026-11-08 213005-2.pdf", "report.pdf", "2026-11-08T21:30:05")]
    [InlineData("Manual 2026-01-31 000000-12", "Manual", "2026-01-31T00:00:00")]
    [InlineData("a 2026 plan 2026-11-08 213005.md", "a 2026 plan.md", "2026-11-08T21:30:05")]
    public void A_stamped_name_gives_the_original_and_the_local_time(string name, string original, string local)
    {
        Assert.True(ArchiveFolder.TryParseStamp(name, out var parsed, out var at));
        Assert.Equal(original, parsed);
        Assert.Equal(DateTime.Parse(local, System.Globalization.CultureInfo.InvariantCulture), at);
        Assert.Equal(DateTimeKind.Unspecified, at.Kind);
    }

    [Theory]
    [InlineData("report.pdf")]
    [InlineData("report 2026-11-08.pdf")]
    [InlineData("2026-11-08 213005.pdf")]
    [InlineData("report 2026-13-08 213005.pdf")]
    [InlineData("report 2026-11-08 246005.pdf")]
    [InlineData("report 2026-11-08 213005.docx")]
    [InlineData("report 2026-11-08 213005-.pdf")]
    [InlineData("report 2026-11-08 213005-x")]
    [InlineData("report2026-11-08 213005")]
    [InlineData("report 2026-11-08 2130051")]
    [InlineData(".vaultkeep")]
    public void Any_other_name_carries_no_stamp(string name) => Assert.False(ArchiveFolder.TryParseStamp(name, out _, out _));
}
