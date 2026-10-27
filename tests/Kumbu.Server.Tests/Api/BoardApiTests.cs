// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kumbu.Server.Accounts;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Kumbu.Server.Tests.Api;

/// <summary>The board over REST: what the web app reads and changes, mirroring the board tool.</summary>
[Collection("app")]
public sealed class BoardApiTests(KumbuAppFactory app)
{
    private const string Definition = "---\nkumbu-board: 1\ncolumns:\n  - Ideas\n  - Doing\n  - Blocked: [reason]\n  - Done: [archivable]\n---\n# Roadmap\n\n## Instructions\n\nKeep it small.\n";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private WebApplicationFactory<Program> Api => app.PublicApi;

    private static string Board(RestClient user, string tail = "") => $"/api/vaults/{user.Vault}/board{tail}";

    private async Task<RestClient> BoardUserAsync(string definition = Definition)
    {
        var user = await RestClient.NewAsync(app.PublicApi, "Work");
        await user.CreateAsync("Board/Board.md", definition);
        return user;
    }

    private static Task CardAsync(RestClient user, string title, string column, string rank, string extra = "") =>
        user.CreateAsync($"Board/{title}.md", $"---\ncolumn: {column}\nrank: {rank}\n{extra}---\nText of {title}.\n");

    private static string[] Titles(JsonNode board, string column) =>
        [.. board["columns"]!.AsArray().Single(c => c!["name"]!.GetValue<string>() == column)!["cards"]!.AsArray().Select(c => c!["title"]!.GetValue<string>())];

    [Fact]
    public async Task Gets_the_board()
    {
        var user = await BoardUserAsync();
        await CardAsync(user, "Late", "Ideas", "a1", "due: 2000-01-01\ntags: [security, ops]\n");
        await CardAsync(user, "Early", "Ideas", "Zz");
        await CardAsync(user, "Stuck", "Blocked", "a1", "reason: waiting for legal\n");
        await user.CreateAsync("Board/Loose.md", "No frontmatter.\n");

        using var response = await user.GetAsync(Board(user));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var board = await RestClient.JsonAsync(response);
        Assert.Equal("Roadmap", board["title"]!.GetValue<string>());
        Assert.Equal("Keep it small.", board["instructions"]!.GetValue<string>());
        Assert.Equal(["Ideas", "Doing", "Blocked", "Done"], board["columns"]!.AsArray().Select(c => c!["name"]!.GetValue<string>()));
        Assert.Equal(["Early", "Late"], Titles(board, "Ideas"));
        var blocked = board["columns"]![2]!;
        Assert.True(blocked["needsReason"]!.GetValue<bool>());
        Assert.False(blocked["archivable"]!.GetValue<bool>());
        Assert.True(board["columns"]![3]!["archivable"]!.GetValue<bool>());
        Assert.Equal("waiting for legal", blocked["cards"]![0]!["reason"]!.GetValue<string>());
        var late = board["columns"]![0]!["cards"]![1]!;
        Assert.Equal("/Work/Board/Late.md", late["path"]!.GetValue<string>());
        Assert.Equal("2000-01-01", late["due"]!.GetValue<string>());
        Assert.True(late["overdue"]!.GetValue<bool>());
        Assert.Equal(["security", "ops"], late["tags"]!.AsArray().Select(t => t!.GetValue<string>()));
        Assert.False(board["columns"]![0]!["cards"]![0]!["overdue"]!.GetValue<bool>());
        Assert.Equal("/Work/Board/Loose.md", board["problems"]![0]!["path"]!.GetValue<string>());
    }

    [Fact]
    public async Task The_title_falls_back_to_the_vault_name()
    {
        var user = await BoardUserAsync("---\nkumbu-board: 1\ncolumns: [Ideas]\n---\nNo heading.\n");

        var board = await RestClient.JsonAsync(await user.GetAsync(Board(user)));

        Assert.Equal("Work", board["title"]!.GetValue<string>());
        Assert.Equal(JsonValueKind.Null, board["instructions"]?.GetValueKind() ?? JsonValueKind.Null);
    }

    [Fact]
    public async Task Gets_a_checkpoints_board()
    {
        var user = await BoardUserAsync();
        await CardAsync(user, "Old", "Ideas", "a1");
        using var made = await user.SendAsync(HttpMethod.Post, $"/api/vaults/{user.Vault}/checkpoints", new { name = "before" });
        Assert.Equal(HttpStatusCode.Created, made.StatusCode);
        await CardAsync(user, "New", "Ideas", "b1");

        var then = await RestClient.JsonAsync(await user.GetAsync(Board(user, "?checkpoint=before")));
        var now = await RestClient.JsonAsync(await user.GetAsync(Board(user)));
        using var unknown = await user.GetAsync(Board(user, "?checkpoint=nope"));

        Assert.Equal(["Old"], Titles(then, "Ideas"));
        Assert.Equal(["Old", "New"], Titles(now, "Ideas"));
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task No_board_is_404_and_create_makes_one()
    {
        var user = await RestClient.NewAsync(Api, "Work");

        using var none = await user.GetAsync(Board(user));
        using var created = await user.SendAsync(HttpMethod.Post, Board(user));
        using var again = await user.SendAsync(HttpMethod.Post, Board(user));
        using var now = await user.GetAsync(Board(user));

        Assert.Equal(HttpStatusCode.NotFound, none.StatusCode);
        Assert.Equal($"There is no board in {user.Vault}.", await RestClient.ErrorAsync(none));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.NotEmpty((await RestClient.JsonAsync(created))["columns"]!.AsArray());
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.DoesNotContain("board: ", await RestClient.ErrorAsync(again), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, now.StatusCode);
    }

    [Fact]
    public async Task Adds_a_card_and_returns_201_with_the_board()
    {
        var user = await BoardUserAsync();
        await CardAsync(user, "First", "Ideas", "a1");

        using var added = await user.SendAsync(HttpMethod.Post, Board(user, "/cards"),
            new { title = "Second", column = "Ideas", text = "Hello.", position = "top", due = "2030-05-01", tags = new[] { "ops" } });
        using var badDate = await user.SendAsync(HttpMethod.Post, Board(user, "/cards"), new { title = "Third", column = "Ideas", due = "tomorrow" });
        using var badName = await user.SendAsync(HttpMethod.Post, Board(user, "/cards"), new { title = "a/b", column = "Ideas" });
        using var noColumn = await user.SendAsync(HttpMethod.Post, Board(user, "/cards"), new { title = "Third", column = "Nowhere" });
        using var taken = await user.SendAsync(HttpMethod.Post, Board(user, "/cards"), new { title = "First", column = "Ideas" });

        Assert.Equal(HttpStatusCode.Created, added.StatusCode);
        Assert.Equal(["Second", "First"], Titles(await RestClient.JsonAsync(added), "Ideas"));
        Assert.Equal(HttpStatusCode.BadRequest, badDate.StatusCode);
        Assert.Contains("YYYY-MM-DD", await RestClient.ErrorAsync(badDate), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.BadRequest, badName.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, noColumn.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, taken.StatusCode);
        Assert.EndsWith("Hello.", (await user.ReadAsync("Board/Second.md"))["content"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Move_returns_the_new_board()
    {
        var user = await BoardUserAsync();
        await CardAsync(user, "Task", "Ideas", "a1");
        await CardAsync(user, "Other", "Doing", "a1");

        using var moved = await user.SendAsync(HttpMethod.Post, Board(user, "/move"), new { card = "Task", column = "Doing", position = "before:Other" });
        using var noReason = await user.SendAsync(HttpMethod.Post, Board(user, "/move"), new { card = "Task", column = "Blocked" });
        using var withReason = await user.SendAsync(HttpMethod.Post, Board(user, "/move"), new { card = "Task", column = "Blocked", reason = "waiting" });
        using var missing = await user.SendAsync(HttpMethod.Post, Board(user, "/move"), new { card = "Ghost", column = "Doing" });

        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
        var board = await RestClient.JsonAsync(moved);
        Assert.Equal(["Task", "Other"], Titles(board, "Doing"));
        Assert.Empty(Titles(board, "Ideas"));
        Assert.Equal(HttpStatusCode.BadRequest, noReason.StatusCode);
        Assert.Contains("needs a reason", await RestClient.ErrorAsync(noReason), StringComparison.Ordinal);
        Assert.Equal("waiting", (await RestClient.JsonAsync(withReason))["columns"]![2]!["cards"]![0]!["reason"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task A_read_key_can_read_but_not_move()
    {
        var user = await BoardUserAsync();
        await CardAsync(user, "Task", "Ideas", "a1");
        var reader = await user.KeyAsync("reader", KeyRight.Read);

        using var read = await user.GetAsync(Board(user), bearer: reader);
        using var move = await user.SendAsync(HttpMethod.Post, Board(user, "/move"), new { card = "Task", column = "Doing" }, bearer: reader);

        using var add = await user.SendAsync(HttpMethod.Post, Board(user, "/cards"), new { title = "Fresh", column = "Ideas" }, bearer: reader);
        using var update = await user.SendAsync(HttpMethod.Post, Board(user, "/update"), new { card = "Task", clearDue = true }, bearer: reader);
        using var rename = await user.SendAsync(HttpMethod.Post, Board(user, "/rename-column"), new { from = "Doing", to = "Active" }, bearer: reader);
        using var create = await user.SendAsync(HttpMethod.Post, Board(user), bearer: reader);

        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.All([move, add, update, rename, create], r => Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode));
        Assert.Equal(["Task"], Titles(await RestClient.JsonAsync(await user.GetAsync(Board(user))), "Ideas"));
    }

    [Fact]
    public async Task Updates_set_and_clear_due_and_tags_and_a_missing_field_removes_nothing()
    {
        var user = await BoardUserAsync();
        await CardAsync(user, "Task", "Ideas", "a1", "due: 2030-01-01\ntags: [a, b]\n");

        var kept = await RestClient.JsonAsync(await user.SendAsync(HttpMethod.Post, Board(user, "/update"), new { card = "Task", due = "2031-02-03" }));
        var card = kept["columns"]![0]!["cards"]![0]!;
        using var cleared = await user.SendAsync(HttpMethod.Post, Board(user, "/update"), new { card = "Task", clearDue = true, clearTags = true });
        using var both = await user.SendAsync(HttpMethod.Post, Board(user, "/update"), new { card = "Task", due = "2031-02-03", clearDue = true });
        using var nothing = await user.SendAsync(HttpMethod.Post, Board(user, "/update"), new { card = "Task" });

        Assert.Equal("2031-02-03", card["due"]!.GetValue<string>());
        Assert.Equal(["a", "b"], card["tags"]!.AsArray().Select(t => t!.GetValue<string>()));
        var after = (await RestClient.JsonAsync(cleared))["columns"]![0]!["cards"]![0]!;
        Assert.Null(after["due"]);
        Assert.Empty(after["tags"]!.AsArray());
        Assert.Equal(HttpStatusCode.BadRequest, both.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, nothing.StatusCode);
    }

    [Fact]
    public async Task Renames_a_column()
    {
        var user = await BoardUserAsync();
        await CardAsync(user, "Task", "Doing", "a1");

        using var renamed = await user.SendAsync(HttpMethod.Post, Board(user, "/rename-column"), new { from = "Doing", to = "Active" });
        using var taken = await user.SendAsync(HttpMethod.Post, Board(user, "/rename-column"), new { from = "Active", to = "Ideas" });
        using var unknown = await user.SendAsync(HttpMethod.Post, Board(user, "/rename-column"), new { from = "Nowhere", to = "X" });
        using var bad = await user.SendAsync(HttpMethod.Post, Board(user, "/rename-column"), new { from = "Active", to = "a/b" });

        Assert.Equal(["Task"], Titles(await RestClient.JsonAsync(renamed), "Active"));
        Assert.Equal(HttpStatusCode.Conflict, taken.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public async Task A_problem_board_is_409()
    {
        var user = await BoardUserAsync("---\nkumbu-board: 1\ncolumns: [Ideas, ideas]\n---\n# Broken\n");

        using var get = await user.GetAsync(Board(user));
        using var move = await user.SendAsync(HttpMethod.Post, Board(user, "/move"), new { card = "Task", column = "Ideas" });

        Assert.Equal(HttpStatusCode.Conflict, get.StatusCode);
        Assert.Contains("Board.md", await RestClient.ErrorAsync(get), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Conflict, move.StatusCode);
    }

    [Fact]
    public async Task An_unknown_vault_is_404()
    {
        var user = await RestClient.NewAsync(Api, "Work");

        using var get = await user.GetAsync("/api/vaults/Nowhere/board");
        using var post = await user.SendAsync(HttpMethod.Post, "/api/vaults/Nowhere/board");

        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, post.StatusCode);
    }
}
