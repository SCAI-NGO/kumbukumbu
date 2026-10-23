// SPDX-License-Identifier: AGPL-3.0-or-later
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Kumbu.Server.Tests.Mcp;

/// <summary>End to end with the official MCP C# SDK client: what an agent experiences.</summary>
[Collection("app")]
public sealed class McpClientTests(KumbuAppFactory app)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<CallToolResult> ShAsync(string command)
    {
        await using var client = await app.ConnectAsync();
        return await client.CallToolAsync(
            "sh", new Dictionary<string, object?> { ["command"] = command }, cancellationToken: Ct);
    }

    private static string Text(CallToolResult result) =>
        Assert.IsType<TextContentBlock>(result.Content[0]).Text;

    /// <summary>The second content block: "version of &lt;path&gt;: &lt;token&gt;".</summary>
    private static string Token(CallToolResult result)
    {
        Assert.Equal(2, result.Content.Count);
        var text = Assert.IsType<TextContentBlock>(result.Content[1]).Text;
        Assert.StartsWith("\nversion of ", text, StringComparison.Ordinal);
        return text[(text.LastIndexOf(' ') + 1)..];
    }

    private async Task<CallToolResult> CallAsync(string tool, Dictionary<string, object?> args)
    {
        await using var client = await app.ConnectAsync();
        return await client.CallToolAsync(tool, args, cancellationToken: Ct);
    }

    [Fact]
    public async Task Lists_sh_write_and_edit_with_their_manuals()
    {
        await using var client = await app.ConnectAsync();

        var tools = await client.ListToolsAsync(cancellationToken: Ct);

        Assert.Equal(["edit", "sh", "write"], tools.Select(t => t.Name).Order());
        Assert.Contains("ls [path]", tools.Single(t => t.Name == "sh").Description, StringComparison.Ordinal);
        Assert.StartsWith("Create a note", tools.Single(t => t.Name == "write").Description, StringComparison.Ordinal);
        Assert.StartsWith("Change part of a note", tools.Single(t => t.Name == "edit").Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Write_and_edit_take_their_parameters_by_these_names()
    {
        await using var client = await app.ConnectAsync();
        var tools = (await client.ListToolsAsync(cancellationToken: Ct)).ToDictionary(t => t.Name);

        static string[] Properties(McpClientTool tool) =>
            [.. tool.ProtocolTool.InputSchema.GetProperty("properties").EnumerateObject().Select(p => p.Name).Order()];
        static string[] Required(McpClientTool tool) =>
            [.. tool.ProtocolTool.InputSchema.GetProperty("required").EnumerateArray().Select(p => p.GetString()!).Order()];

        Assert.Equal(["content", "path", "version"], Properties(tools["write"]));
        Assert.Equal(["content", "path"], Required(tools["write"]));
        Assert.Equal(["new_text", "old_text", "path", "replace_section", "version"], Properties(tools["edit"]));
        Assert.Equal(["new_text", "path", "version"], Required(tools["edit"]));
    }

    [Fact]
    public async Task Cat_then_edit_then_conflict_then_history_and_restore()
    {
        var path = "/Demo/scratch-edit.md";
        var created = await CallAsync("write", new() { ["path"] = path, ["content"] = "# Scratch\n\n- one\n" });
        Assert.NotEqual(true, created.IsError);
        Assert.Equal($"created {path} (3 lines)", Text(created));

        var read = await ShAsync($"cat {path}");
        var v1 = Token(read);
        Assert.Equal(Token(created), v1);

        var edited = await CallAsync("edit", new() { ["path"] = path, ["version"] = v1, ["old_text"] = "- one", ["new_text"] = "- eins" });
        Assert.NotEqual(true, edited.IsError);
        Assert.Equal($"edited {path} at line 3", Text(edited));

        var stale = await CallAsync("edit", new() { ["path"] = path, ["version"] = v1, ["new_text"] = "- two" });
        Assert.True(stale.IsError);
        Assert.StartsWith($"edit: {path} has changed since version {v1}", Text(stale), StringComparison.Ordinal);

        var appended = await CallAsync("edit", new() { ["path"] = path, ["version"] = Token(edited), ["new_text"] = "- two\n" });
        Assert.Equal("# Scratch\n\n- eins\n- two\n", Text(await ShAsync($"cat {path}")));
        Assert.Equal($"appended to {path} (now 4 lines)", Text(appended));

        // Creating and both edits came from the same writer within ten minutes: one session, one version.
        var history = Text(await ShAsync($"history {path}"));
        Assert.Single(history.Split('\n'));

        var restored = await ShAsync($"restore {path} {v1}");
        Assert.True(restored.IsError);
        Assert.StartsWith($"restore: {path} has no version {v1}", Text(restored), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rm_trash_and_restore_over_mcp()
    {
        var path = "/Demo/scratch-rm.md";
        await CallAsync("write", new() { ["path"] = path, ["content"] = "bye\n" });

        var removed = await ShAsync($"rm {path}");
        Assert.NotEqual(true, removed.IsError);
        Assert.Contains(path, Text(await ShAsync("trash /Demo")), StringComparison.Ordinal);
        Assert.True((await ShAsync($"cat {path}")).IsError);

        var restored = await ShAsync($"restore {path}");
        Assert.Equal($"restored {path} from the trash", Text(restored));
        Assert.Equal("bye\n", Text(await ShAsync($"cat {path}")));
    }

    [Fact]
    public async Task Write_refuses_to_create_a_vault()
    {
        var result = await CallAsync("write", new() { ["path"] = "/demo/typo.md", ["content"] = "x" });

        Assert.True(result.IsError);
        Assert.Equal("write: there is no vault 'demo' (ls lists the vaults; writing does not create new ones)", Text(result));
        Assert.Single(result.Content);
    }

    [Fact]
    public async Task Ls_root_lists_the_vaults()
    {
        var result = await ShAsync("ls /");

        Assert.NotEqual(true, result.IsError);
        Assert.Equal("Demo/\nIdeas/", Text(result));
    }

    [Fact]
    public async Task Cat_of_a_quoted_path_with_a_space_returns_the_note()
    {
        var expected = (await File.ReadAllTextAsync(Path.Combine(KumbuAppFactory.SamplesPath, "Demo", "Meeting Notes.md"), Ct))
            .Replace("\r\n", "\n", StringComparison.Ordinal);

        var result = await ShAsync("cat \"/Demo/Meeting Notes.md\"");

        Assert.NotEqual(true, result.IsError);
        Assert.Equal(expected, Text(result));
    }

    [Fact]
    public async Task Cat_of_a_missing_note_is_a_tool_error_not_a_protocol_error()
    {
        var result = await ShAsync("cat /Demo/nope.md");

        Assert.True(result.IsError);
        Assert.Equal("cat: /Demo/nope.md: No such file or directory", Text(result));
    }

    // The sample's file name is committed in NFC; the command is typed in NFC too, as a model would.
    [Fact]
    public async Task Ls_of_a_vault_lists_a_note_with_umlauts()
    {
        var result = await ShAsync("ls /Ideas");

        Assert.NotEqual(true, result.IsError);
        Assert.Equal("retrofuturism.md\n\u00DCberlegungen.md", Text(result));
    }

    [Fact]
    public async Task Cat_of_a_note_with_umlauts_returns_its_exact_content()
    {
        var expected = (await File.ReadAllTextAsync(Path.Combine(KumbuAppFactory.SamplesPath, "Ideas", "\u00DCberlegungen.md"), Ct))
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.Contains("\u00E4", expected, StringComparison.Ordinal);

        var result = await ShAsync("cat \"/Ideas/\u00DCberlegungen.md\"");

        Assert.NotEqual(true, result.IsError);
        Assert.Equal(expected, Text(result));
    }

    [Fact]
    public async Task Rg_files_piped_into_rg_finds_a_note_by_its_wikilink_name()
    {
        var result = await ShAsync("rg --files | rg -i '/kumbu\\.md$'");

        Assert.NotEqual(true, result.IsError);
        Assert.Equal("/Demo/projects/kumbu.md", Text(result));
    }

    [Fact]
    public async Task Rg_searches_every_vault_and_pipes_into_wc()
    {
        var listed = await ShAsync("rg -l '\\[\\[kumbu\\]\\]'");
        var counted = await ShAsync("rg -l '\\[\\[kumbu\\]\\]' | wc -l");

        Assert.Equal("/Demo/Meeting Notes.md\n/Demo/welcome.md", Text(listed));
        Assert.Equal("2", Text(counted));
    }

    [Fact]
    public async Task Rg_matches_umlauts_case_insensitively()
    {
        var result = await ShAsync("rg -i '^# \u00FCberlegungen:' /Ideas");

        Assert.NotEqual(true, result.IsError);
        Assert.Equal("/Ideas/\u00DCberlegungen.md:1:# \u00DCberlegungen: thoughts on memory", Text(result));
    }

    [Fact]
    public async Task Unsupported_operator_is_a_tool_error()
    {
        var result = await ShAsync("ls /Demo && ls /Ideas");

        Assert.True(result.IsError);
        Assert.StartsWith("sh: '&&' is not supported", Text(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unknown_command_points_to_help()
    {
        var result = await ShAsync("frobnicate /Demo/welcome.md");

        Assert.True(result.IsError);
        Assert.Equal("command not found: frobnicate \u2014 try 'help'", Text(result));
    }

    [Theory]
    [InlineData("edit", "path=/Demo/welcome.md,new_text=x", "edit: version is required; get it from 'cat <path>' (it comes as a separate block)")]
    [InlineData("edit", "version=abc,new_text=x", "edit: path is required; an absolute path like /Work/plan.md")]
    [InlineData("edit", "path=/Demo/welcome.md,version=abc", "edit: new_text is required; the replacement text, or the text to append")]
    [InlineData("write", "path=/Demo/x.md", "write: content is required; the note's full Markdown text")]
    [InlineData("sh", "", "sh: command is required; a command line like ls /")]
    public async Task A_missing_required_argument_says_what_to_pass(string tool, string args, string message)
    {
        var arguments = args.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(a => a.Split('='))
            .ToDictionary(a => a[0], a => (object?)a[1]);

        var result = await CallAsync(tool, arguments);

        Assert.True(result.IsError);
        Assert.Equal(message, Text(result));
    }

    [Fact]
    public async Task Outline_cat_a_section_and_edit_it()
    {
        var path = "/Demo/scratch-sections.md";
        var created = await CallAsync("write", new() { ["path"] = path, ["content"] = "# Plan\n\n## Goals\n- fast\n\n## Notes\nx\n" });

        Assert.StartsWith("1-7  # Plan", Text(await ShAsync($"outline {path}")), StringComparison.Ordinal);
        var section = await ShAsync($"cat {path}#Goals");
        Assert.Equal("## Goals\n- fast", Text(section));
        Assert.Equal(Token(created), Token(section));

        var appended = await CallAsync("edit", new() { ["path"] = path + "#Goals", ["version"] = Token(section), ["new_text"] = "- small" });
        Assert.Equal($"appended to section #Plan#Goals of {path} at line 5", Text(appended));

        var replaced = await CallAsync("edit", new()
        {
            ["path"] = path + "#Notes", ["version"] = Token(appended), ["new_text"] = "## Notes\ny", ["replace_section"] = true,
        });
        Assert.NotEqual(true, replaced.IsError);
        Assert.Equal("# Plan\n\n## Goals\n- fast\n- small\n\n## Notes\ny\n", Text(await ShAsync($"cat {path}")));
    }

    [Fact]
    public async Task Links_backlinks_and_mv_over_mcp()
    {
        await CallAsync("write", new() { ["path"] = "/Demo/scratch-mv-target.md", ["content"] = "# Target\n" });
        await CallAsync("write", new() { ["path"] = "/Demo/scratch-mv-source.md", ["content"] = "See [[scratch-mv-target#Target|it]].\n" });

        Assert.Equal(
            "1: [[scratch-mv-target#Target|it]] → /Demo/scratch-mv-target.md",
            Text(await ShAsync("links /Demo/scratch-mv-source.md")));
        Assert.Equal(
            "/Demo/scratch-mv-source.md:1:See [[scratch-mv-target#Target|it]].",
            Text(await ShAsync("backlinks /Demo/scratch-mv-target.md")));

        var moved = await ShAsync("mv /Demo/scratch-mv-target.md /Demo/scratch-dir/renamed.md");

        Assert.Equal(
            "moved /Demo/scratch-mv-target.md to /Demo/scratch-dir/renamed.md; rewrote 1 link in /Demo/scratch-mv-source.md",
            Text(moved));
        Assert.Equal("See [[renamed#Target|it]].\n", Text(await ShAsync("cat /Demo/scratch-mv-source.md")));
    }
}
