// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Notes;
using Kumbu.Server.Accounts;

namespace Kumbu.Server.Tests.Accounts;

/// <summary>A credential's right per vault (collab design §3.3): ordered, persisted as text.</summary>
public sealed class KeyRightTests
{
    [Fact]
    public void Levels_are_ordered_read_write_maintain()
    {
        Assert.True(KeyRight.Read < KeyRight.Write);
        Assert.True(KeyRight.Write < KeyRight.Maintain);
        Assert.True(AccessLevel.Maintain < AccessLevel.Owner);
        Assert.Equal(AccessLevel.Maintain, KeyRights.ToLevel(KeyRight.Maintain));
    }

    [Theory]
    [InlineData(KeyRight.Read, "read")]
    [InlineData(KeyRight.Write, "write")]
    [InlineData(KeyRight.Maintain, "maintain")]
    public void Maintain_round_trips_as_text(KeyRight right, string text)
    {
        Assert.Equal(text, KeyRights.ToText(right));
        Assert.Equal(right, KeyRights.Parse(KeyRights.ToText(right)));
    }

    [Theory]
    [InlineData("admin")]
    [InlineData("Write")]
    [InlineData("")]
    public void An_unknown_right_parses_to_null(string text) => Assert.Null(KeyRights.Parse(text));
}
