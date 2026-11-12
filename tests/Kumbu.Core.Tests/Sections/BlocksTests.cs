// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json;
using Kumbu.Core.Paths;
using Kumbu.Core.Sections;
using Kumbu.Core.Sh;

namespace Kumbu.Core.Tests.Sections;

public class BlocksTests
{
    private sealed record BlockCase(string Name, string Note, string Id, int[]? Lines);

    // The web's block finder reads the same cases (web/src/note/blocks.cases.json).
    private static readonly Dictionary<string, BlockCase> SharedCases = JsonSerializer
        .Deserialize<List<BlockCase>>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Sections", "blocks.cases.json")), new JsonSerializerOptions(JsonSerializerDefaults.Web))!
        .ToDictionary(c => c.Name);

    public static TheoryData<string> SharedCaseNames => [.. SharedCases.Keys];

    [Theory]
    [MemberData(nameof(SharedCaseNames))]
    public void Every_shared_case(string name)
    {
        var c = SharedCases[name];
        var block = Blocks.Find(Lines.Split(c.Note), c.Id);
        // The cases count lines from 1, both ends included; a Block from 0, its end excluded.
        Assert.Equal(c.Lines is null ? null : ((int, int)?)(c.Lines[0], c.Lines[1]), block is null ? null : (block.Start + 1, block.End));
    }

    [Fact]
    public void Parse_lists_the_blocks_in_order()
    {
        var lines = Lines.Split("# Plan ^plan\n\n- pump ^pump\n- valve\n\n^list\n\nDone. ^done\n");
        Assert.Equal(
            [new Block("plan", 0, 1), new Block("pump", 2, 3), new Block("list", 2, 6), new Block("done", 7, 8)],
            Blocks.Parse(lines));
    }

    [Theory]
    [InlineData("^controller", "controller")]
    [InlineData("%5Econtroller", "controller")]
    [InlineData("%5econtroller", "controller")]
    [InlineData("^Pump-2", "Pump-2")]
    public void A_caret_fragment_is_a_block_id(string fragment, string id)
    {
        Assert.True(Blocks.TryId(fragment, out var found));
        Assert.Equal(id, found);
    }

    [Theory]
    [InlineData("Plan")]
    [InlineData("^")]
    [InlineData("^a b")]
    [InlineData("^a_b")]
    [InlineData("^a#b")]
    [InlineData("Plan#^a")]
    [InlineData("%5E")]
    public void Anything_else_is_not(string fragment) => Assert.False(Blocks.TryId(fragment, out _));

    [Fact]
    public void A_block_is_found_with_its_marker()
    {
        var path = VaultPath.TryParse("/Work/Pump/Plan.md", out var p) ? p : throw new InvalidOperationException();
        Assert.True(SectionLookup.TryFindBlock(path, "- a\n- b\n\n^list\n", "list", out var text, out _));
        Assert.Equal("- a\n- b\n\n^list", text);
        Assert.False(SectionLookup.TryFindBlock(path, "Text.\n", "list", out _, out var error));
        Assert.Equal("/Work/Pump/Plan.md#^list: no such block (a block id is written as ^list at the end of a paragraph, item or heading)", error);
    }
}
