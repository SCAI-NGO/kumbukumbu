// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Server.Notes;

namespace Kumbu.Server.Tests.Notes;

public class SeedDecisionTests
{
    private static StoredNote Note(string content, string? seeded) =>
        new(VaultPath.Root.Child("V").Child("a.md"), content, 1, Writers.Seed, DateTime.UnixEpoch, [], seeded);

    [Theory]
    // disk, database, last seeded → action
    [InlineData("a", "a", "a", SeedAction.None)]
    [InlineData("b", "a", "a", SeedAction.Update)]       // only the file changed
    [InlineData("a", "b", "a", SeedAction.None)]         // only the database changed
    [InlineData("c", "b", "a", SeedAction.Conflict)]     // both changed
    [InlineData("b", "b", "a", SeedAction.Remember)]     // both changed the same way
    [InlineData("a", "a", null, SeedAction.Remember)]    // seeded before step 3, unchanged
    [InlineData("b", "a", null, SeedAction.Update)]      // seeded before step 3, file changed
    public void Decides_by_which_side_changed(string disk, string db, string? seeded, SeedAction expected) =>
        Assert.Equal(expected, SeedDecision.Decide(disk, Note(db, seeded), inTrash: false));

    [Fact]
    public void Creates_a_missing_note() => Assert.Equal(SeedAction.Create, SeedDecision.Decide("a", null, inTrash: false));

    [Fact]
    public void Leaves_a_removed_note_in_the_trash() => Assert.Equal(SeedAction.SkipTrashed, SeedDecision.Decide("a", null, inTrash: true));
}
