// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using Kumbu.Server.Accounts;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Kumbu.Server.Tests.Api;

/// <summary>Every refused write over REST says why in <c>code</c> (collab design §3.5); MCP keeps the sentence.</summary>
[Collection("app")]
public sealed class RefusalCodeTests(KumbuAppFactory app)
{
    private const string Board = "---\nkumbu-board: 1\ncolumns: [Ideas, Doing]\n---\n# Gartenarbeit\n";

    private WebApplicationFactory<Program> Api => app.PublicApi;

    private sealed record World(RestClient User, string Plan, string Picture, string BoardVersion);

    /// <summary>A vault with a board, a note, a picture, a note in the trash and a checkpoint.</summary>
    private async Task<World> WorldAsync()
    {
        var user = await RestClient.NewAsync(Api, "Gartenarbeit");
        var board = await user.CreateAsync("Board/Board.md", Board);
        var plan = await user.CreateAsync("plan.md", "plan\n");
        var picture = await user.CreateFileAsync("pic.png", FileApiTests.Png(4, 4));
        await user.CreateAsync("gone.md", "gone\n");
        using var removed = await user.SendAsync(HttpMethod.Delete, user.Url("gone.md"));
        Assert.True(removed.IsSuccessStatusCode);
        using var made = await user.SendAsync(HttpMethod.Post, $"/api/vaults/{user.Vault}/checkpoints", new { name = "cp" });
        Assert.Equal(HttpStatusCode.Created, made.StatusCode);
        return new World(user, plan, picture, board);
    }

    private static Task<HttpResponseMessage> RouteAsync(World world, string route, string key)
    {
        var user = world.User;
        var checkpoints = $"/api/vaults/{user.Vault}/checkpoints";
        return route switch
        {
            "PUT note" => user.SendAsync(HttpMethod.Put, user.Url("plan.md"), new { content = "new\n", version = world.Plan }, bearer: key),
            "DELETE note" => user.SendAsync(HttpMethod.Delete, user.Url("plan.md"), bearer: key),
            "POST move" => user.SendAsync(HttpMethod.Post, "/api/move", new { from = "/Gartenarbeit/plan.md", to = "/Gartenarbeit/plan-2.md" }, bearer: key),
            "PUT file" => user.PutFileAsync("pic.png", FileApiTests.Png(8, 8), world.Picture, bearer: key),
            "DELETE file" => user.SendAsync(HttpMethod.Delete, user.Url("pic.png", "files"), bearer: key),
            "upload" => user.SendAsync(HttpMethod.Post, "/api/upload/Gartenarbeit/In", new { onConflict = "replace", files = new[] { new { path = "new.md", content = "new\n" } } }, bearer: key),
            "restore" => user.SendAsync(HttpMethod.Post, "/api/restore/Gartenarbeit/gone.md", new { }, bearer: key),
            "mkcp" => user.SendAsync(HttpMethod.Post, checkpoints, new { name = "mine" }, bearer: key),
            "rmcp" => user.SendAsync(HttpMethod.Delete, $"{checkpoints}/cp", bearer: key),
            "board card add" => user.SendAsync(HttpMethod.Post, $"/api/vaults/{user.Vault}/board/cards", new { title = "Card", column = "Ideas" }, bearer: key),
            _ => throw new ArgumentException(route),
        };
    }

    private static async Task AssertRefusedAsync(HttpResponseMessage response, string code)
    {
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await RestClient.JsonAsync(response);
        Assert.Equal(code, body["code"]?.GetValue<string>());
        Assert.False(string.IsNullOrWhiteSpace(body["error"]!.GetValue<string>()));
    }

    [Theory]
    [InlineData("PUT note")]
    [InlineData("DELETE note")]
    [InlineData("POST move")]
    [InlineData("PUT file")]
    [InlineData("DELETE file")]
    [InlineData("upload")]
    [InlineData("restore")]
    [InlineData("mkcp")]
    [InlineData("rmcp")]
    [InlineData("board card add")]
    public async Task Every_write_route_refuses_a_read_key_with_code_read_only(string route)
    {
        var world = await WorldAsync();
        var key = await world.User.KeyAsync("reader", KeyRight.Read);

        using var response = await RouteAsync(world, route, key);

        await AssertRefusedAsync(response, "read-only");
    }

    [Theory]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    [InlineData("move from")]
    [InlineData("move to")]
    [InlineData("restore")]
    [InlineData("whole checkpoint restore")]
    [InlineData("file upload")]
    public async Task Every_route_to_Board_md_refuses_a_write_key_with_code_structural(string route)
    {
        var world = await WorldAsync();
        var user = world.User;
        // The board differs from the checkpoint's, for the whole restore.
        using var changed = await user.SendAsync(HttpMethod.Put, user.Url("Board/Board.md"), new { content = Board.Replace("Doing", "Active"), version = world.BoardVersion });
        var version = (await RestClient.JsonAsync(changed))["version"]!.GetValue<string>();
        var key = await user.KeyAsync("writer", KeyRight.Write);

        using var response = route switch
        {
            "PUT" => await user.SendAsync(HttpMethod.Put, user.Url("Board/Board.md"), new { content = Board, version }, bearer: key),
            "DELETE" => await user.SendAsync(HttpMethod.Delete, user.Url("Board/Board.md"), bearer: key),
            "move from" => await user.SendAsync(HttpMethod.Post, "/api/move", new { from = "/Gartenarbeit/Board/Board.md", to = "/Gartenarbeit/Old.md" }, bearer: key),
            "move to" => await user.SendAsync(HttpMethod.Post, "/api/move", new { from = "/Gartenarbeit/plan.md", to = "/Gartenarbeit/Board/Board.md" }, bearer: key),
            "restore" => await user.SendAsync(HttpMethod.Post, "/api/restore/Gartenarbeit/Board/Board.md", new { version = world.BoardVersion }, bearer: key),
            "whole checkpoint restore" => await user.SendAsync(HttpMethod.Post, $"/api/vaults/{user.Vault}/checkpoints/cp/preview", bearer: key),
            "file upload" => await user.SendAsync(HttpMethod.Post, "/api/upload/Gartenarbeit/Board", new { onConflict = "replace", files = new[] { new { path = "Board.md", content = "---\nkumbu-board: 1\ncolumns: [X]\n---\n" } } }, bearer: key),
            _ => throw new ArgumentException(route),
        };

        await AssertRefusedAsync(response, "structural");
        Assert.Contains("Active", (await user.ReadAsync("Board/Board.md"))["content"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_read_key_replacing_a_file_is_refused_before_its_bytes_are_looked_at()
    {
        var world = await WorldAsync();
        var key = await world.User.KeyAsync("reader", KeyRight.Read);

        using var response = await world.User.PutFileAsync("pic.png", "not a picture"u8.ToArray(), world.Picture, bearer: key);

        await AssertRefusedAsync(response, "read-only");
    }

    [Fact]
    public async Task A_refusal_over_MCP_keeps_its_sentence()
    {
        var world = await WorldAsync();
        var key = await world.User.KeyAsync("writer", KeyRight.Write);

        var (isError, text) = await world.User.ShAsync("rm /Gartenarbeit/Board/Board.md", key);

        Assert.True(isError);
        Assert.Equal("rm: Only those who may maintain *Gartenarbeit* can change its board's columns.", text);
    }

    [Fact]
    public async Task An_error_that_is_no_refusal_has_no_code()
    {
        var world = await WorldAsync();

        using var missing = await world.User.GetAsync(world.User.Url("nowhere.md"));

        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        var body = await RestClient.JsonAsync(missing);
        Assert.Equal(["error"], body.AsObject().Select(p => p.Key));
    }
}
