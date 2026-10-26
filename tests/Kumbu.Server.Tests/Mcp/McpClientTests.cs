// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Sh;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using MongoDB.Driver;
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
    public async Task Lists_sh_write_edit_and_replace_with_their_manuals()
    {
        await using var client = await app.ConnectAsync();

        var tools = await client.ListToolsAsync(cancellationToken: Ct);

        Assert.Equal(["edit", "replace", "sh", "write"], tools.Select(t => t.Name).Order());
        Assert.Contains("ls [path]", tools.Single(t => t.Name == "sh").Description, StringComparison.Ordinal);
        Assert.StartsWith("Create a note", tools.Single(t => t.Name == "write").Description, StringComparison.Ordinal);
        Assert.StartsWith("Change part of a note", tools.Single(t => t.Name == "edit").Description, StringComparison.Ordinal);
        Assert.StartsWith("Replace a text in every note", tools.Single(t => t.Name == "replace").Description, StringComparison.Ordinal);
        Assert.True(tools.Single(t => t.Name == "replace").ProtocolTool.Annotations?.DestructiveHint);
    }

    [Fact]
    public async Task Initialize_returns_the_instructions()
    {
        await using var client = await app.ConnectAsync();

        Assert.Equal(ServerInstructions.Text, client.ServerInstructions);
        Assert.Contains("semsearch", client.ServerInstructions, StringComparison.Ordinal);
        Assert.Contains("rg", client.ServerInstructions, StringComparison.Ordinal);
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
        Assert.Equal(["new_text", "old_text", "path", "replace_all", "replace_section", "version"], Properties(tools["edit"]));
        Assert.Equal(["new_text", "path", "version"], Required(tools["edit"]));
        Assert.Equal(["new_text", "old_text", "path", "plan", "word"], Properties(tools["replace"]));
        Assert.Equal(["new_text", "old_text", "path"], Required(tools["replace"]));
    }

    [Fact]
    public async Task Replace_previews_then_applies_in_one_transaction_and_diff_shows_it()
    {
        await CallAsync("write", new() { ["path"] = "/Demo/rename/a.md", ["content"] = "Project Hecate starts.\n" });
        await CallAsync("write", new() { ["path"] = "/Demo/rename/b.md", ["content"] = "Hecate, again: Hecate.\n" });
        Assert.StartsWith("checkpoint 'before-rename' of /Demo/rename/ (2 notes)", Text(await ShAsync("mkcp /Demo/rename before-rename")), StringComparison.Ordinal);
        var args = new Dictionary<string, object?> { ["path"] = "/Demo/rename", ["old_text"] = "Hecate", ["new_text"] = "Selene" };

        var preview = await CallAsync("replace", args);
        Assert.NotEqual(true, preview.IsError);
        Assert.StartsWith("3 occurrences of \"Hecate\" in 2 notes below /Demo/rename", Text(preview), StringComparison.Ordinal);
        var plan = Text(preview).Split('\n').Single(l => l.StartsWith("plan: ", StringComparison.Ordinal))[6..];

        var applied = await CallAsync("replace", new(args) { ["plan"] = plan });
        Assert.Equal("replaced 3 occurrences in 2 notes", Text(applied));
        Assert.Equal("Selene, again: Selene.\n", Text(await ShAsync("cat /Demo/rename/b.md")));

        // Written and replaced in one changeset: one version. The checkpoint still has the state before.
        var diff = Text(await ShAsync("diff --word --checkpoint before-rename /Demo/rename/a.md"));
        Assert.Contains("dev (replace)", diff, StringComparison.Ordinal);
        Assert.Contains("[-Hecate-]{+Selene+}", diff, StringComparison.Ordinal);
        Assert.Equal("Hecate, again: Hecate.\n", Text(await ShAsync("cat --checkpoint before-rename /Demo/rename/b.md")));

        var restorePreview = Text(await ShAsync("restore --checkpoint before-rename /Demo/rename"));
        var restorePlan = restorePreview.Split('\n').Single(l => l.StartsWith("plan: ", StringComparison.Ordinal))[6..];
        Assert.StartsWith("restored /Demo/rename/ to checkpoint 'before-rename': 2 changed", Text(await ShAsync($"restore --checkpoint before-rename --plan {restorePlan} /Demo/rename")), StringComparison.Ordinal);
        Assert.Equal("Project Hecate starts.\n", Text(await ShAsync("cat /Demo/rename/a.md")));
        Assert.StartsWith("removed checkpoint 'before-rename'", Text(await ShAsync("rmcp /Demo before-rename")), StringComparison.Ordinal);
        Assert.Contains("/Demo/rename/b.md", Text(await ShAsync("history /Demo/rename --since 1h")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Semsearch_ranks_sections_by_words_and_meaning()
    {
        await ShAsync("ls /");
        await app.IndexedAsync();

        var result = Text(await ShAsync("semsearch -n 3 agents read through a shell"));

        Assert.StartsWith("1. /Demo/welcome.md#Welcome to Kumbu²  lines 1-", result, StringComparison.Ordinal);
        Assert.EndsWith("(ranked by words and meaning; cat \"path#Section\" reads one)", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Edit_replace_all_over_mcp()
    {
        var path = "/Demo/scratch-all.md";
        var created = await CallAsync("write", new() { ["path"] = path, ["content"] = "a b a\n" });

        var edited = await CallAsync("edit", new() { ["path"] = path, ["version"] = Token(created), ["old_text"] = "a", ["new_text"] = "c", ["replace_all"] = true });

        Assert.Equal($"replaced 2 occurrences in {path}", Text(edited));
        Assert.Equal("c b c\n", Text(await ShAsync($"cat {path}")));
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

        // Both edits came from the same writer within ten minutes: one version. The created state is one of its own.
        var history = Text(await ShAsync($"history {path}")).Split('\n');
        Assert.Equal(2, history.Length);
        Assert.StartsWith(v1, history[1], StringComparison.Ordinal);

        var restored = await ShAsync($"restore {path} {Token(edited)}");
        Assert.True(restored.IsError);
        Assert.StartsWith($"restore: {path} has no version {Token(edited)}", Text(restored), StringComparison.Ordinal);
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
        // Only the vault lines: users' vaults that other tests in this collection create are neither listed nor counted
        // as locked, but a footer below the list (a hint, a locked count) isn't part of what this test checks.
        Assert.Equal(["Demo/", "Ideas/"], Text(result).Split('\n').TakeWhile(l => l.EndsWith('/')));
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

    [Fact]
    public async Task A_document_damaged_behind_the_servers_back_gives_clear_errors()
    {
        var path = "/Demo/scratch-damaged.md";
        var created = await CallAsync("write", new() { ["path"] = path, ["content"] = "fine\n" });
        var notes = app.Database.GetCollection<MongoDB.Bson.BsonDocument>("notes");
        var doc = await notes.Find(MongoDB.Driver.FilterDefinition<MongoDB.Bson.BsonDocument>.Empty)
            .Sort(new MongoDB.Bson.BsonDocument("_id", -1)).FirstAsync(Ct);
        await notes.UpdateOneAsync(
            new MongoDB.Bson.BsonDocument("_id", doc["_id"]),
            new MongoDB.Bson.BsonDocument("$set", new MongoDB.Bson.BsonDocument
            {
                ["rev"] = doc["rev"].ToInt64() + 7,
                ["blob"] = new MongoDB.Bson.BsonBinaryData(new byte[60]),
            }),
            cancellationToken: Ct);

        // The swap fails, the refresh finds the document damaged: a message, no exception.
        var edited = await CallAsync("edit", new() { ["path"] = path, ["version"] = Token(created), ["new_text"] = "more" });
        Assert.True(edited.IsError);
        Assert.StartsWith($"edit: {path}: damaged: the stored note doesn't open", Text(edited), StringComparison.Ordinal);

        var written = await CallAsync("write", new() { ["path"] = path, ["content"] = "new" });
        Assert.StartsWith($"write: {path}: damaged:", Text(written), StringComparison.Ordinal);
        Assert.StartsWith($"cat: {path}: damaged:", Text(await ShAsync($"cat {path}")), StringComparison.Ordinal);
        Assert.EndsWith("(1 note here doesn't open: changed or damaged outside Kumbu²; the server log has details)", Text(await ShAsync("ls /Demo")), StringComparison.Ordinal);

        // Removed in the database: the next attempt notices without a restart.
        await notes.DeleteOneAsync(new MongoDB.Bson.BsonDocument("_id", doc["_id"]), Ct);
        Assert.NotEqual(true, (await CallAsync("write", new() { ["path"] = path, ["content"] = "again" })).IsError);
        Assert.NotEqual(true, (await ShAsync($"rm {path}")).IsError);
        Assert.DoesNotContain("doesn't open", Text(await ShAsync("ls /Demo")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_read_only_key_is_refused_every_write_with_the_missing_right_named()
    {
        var users = new Kumbu.Server.Accounts.UserCatalog(
            app.Database,
            new Kumbu.Server.Accounts.Argon2Kdf(new Kumbu.Server.Accounts.Argon2Parameters(MemoryKiB: 1024, Iterations: 1, Parallelism: 1)),
            app.Services.GetRequiredService<Kumbu.Server.Accounts.ApiKeys>(),
            app.Services.GetRequiredService<Kumbu.Server.Accounts.Connections>(),
            TimeProvider.System,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Kumbu.Server.Accounts.UserCatalog>.Instance);
        var username = "u" + Guid.NewGuid().ToString("N")[..12];
        await users.CreateAsync(username, "correct horse battery staple", Kumbu.Server.Accounts.Roles.User, Ct);
        var (user, userKey, readAt) = (await users.TrySignInAsync(username, "correct horse battery staple", Ct))!.Value;
        var catalog = app.Services.GetRequiredService<Kumbu.Server.Vaults.VaultCatalog>();
        var reading = (await catalog.CreateForUserAsync("Reading", user.Id, userKey, Ct)).Id;
        var writing = (await catalog.CreateForUserAsync("Writing", user.Id, userKey, Ct)).Id;
        var keys = app.Services.GetRequiredService<Kumbu.Server.Accounts.ApiKeys>();
        var (writer, _) = await keys.CreateAsync(user, userKey, readAt, "seeder", new Dictionary<string, Kumbu.Server.Accounts.KeyRight> { [reading] = Kumbu.Server.Accounts.KeyRight.Write }, Ct);
        var (reader, _) = await keys.CreateAsync(
            user, userKey, readAt, "reader",
            new Dictionary<string, Kumbu.Server.Accounts.KeyRight> { [reading] = Kumbu.Server.Accounts.KeyRight.Read, [writing] = Kumbu.Server.Accounts.KeyRight.Write },
            Ct);
        string version;
        await using (var seeder = await app.ConnectAsync(writer))
        {
            version = Token(await seeder.CallToolAsync("write", new Dictionary<string, object?> { ["path"] = "/Reading/a.md", ["content"] = "Hecate\n" }, cancellationToken: Ct));
            Assert.NotEqual(true, (await seeder.CallToolAsync("sh", new Dictionary<string, object?> { ["command"] = "mkcp /Reading before" }, cancellationToken: Ct)).IsError);
        }

        await using var client = await app.ConnectAsync(reader);
        async Task<string> Refused(string tool, Dictionary<string, object?> args)
        {
            var result = await client.CallToolAsync(tool, args, cancellationToken: Ct);
            Assert.True(result.IsError, tool);
            return Text(result);
        }

        Task<string> Sh(string command) => Refused("sh", new() { ["command"] = command });

        Assert.Equal("write: this key can only read vault Reading", await Refused("write", new() { ["path"] = "/Reading/b.md", ["content"] = "x" }));
        Assert.Equal("edit: this key can only read vault Reading", await Refused("edit", new() { ["path"] = "/Reading/a.md", ["version"] = version, ["new_text"] = "x" }));
        Assert.Equal("replace: this key can only read vault Reading", await Refused("replace", new() { ["path"] = "/Reading", ["old_text"] = "Hecate", ["new_text"] = "Selene" }));
        Assert.Equal("rm: this key can only read vault Reading", await Sh("rm /Reading/a.md"));
        Assert.Equal("mv: this key can only read vault Reading", await Sh("mv /Reading/a.md /Reading/c.md"));
        Assert.Equal("restore: this key can only read vault Reading", await Sh($"restore /Reading/a.md {version}"));
        Assert.Equal("mkcp: this key can only read vault Reading", await Sh("mkcp /Reading later"));
        Assert.Equal("rmcp: this key can only read vault Reading", await Sh("rmcp /Reading before"));

        // Nothing was written; reading still works, and so does writing where the key may write.
        var cat = await client.CallToolAsync("sh", new Dictionary<string, object?> { ["command"] = "cat /Reading/a.md" }, cancellationToken: Ct);
        Assert.Equal("Hecate\n", Text(cat));
        Assert.Equal(version, Token(cat));
        Assert.DoesNotContain("b.md", Text(await client.CallToolAsync("sh", new Dictionary<string, object?> { ["command"] = "ls /Reading" }, cancellationToken: Ct)), StringComparison.Ordinal);
        var allowed = await client.CallToolAsync("write", new Dictionary<string, object?> { ["path"] = "/Writing/b.md", ["content"] = "fine\n" }, cancellationToken: Ct);
        Assert.NotEqual(true, allowed.IsError);
    }
}
