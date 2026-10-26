// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using Kumbu.Server.Accounts;
using Kumbu.Server.Sync;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using static Kumbu.Server.Tests.Api.RestClient;

namespace Kumbu.Server.Tests.Api;

/// <summary>WebSocket hints (sync spec §5): a ticket per connection, a "changed" message per vault of the key.</summary>
[Collection("app")]
public sealed class SyncSocketTests(KumbuAppFactory app) : IAsyncDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeTimeProvider _time = new(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));

    private WebApplicationFactory<Program>? _api;

    private WebApplicationFactory<Program> Api => _api ??= app.PublicApi.WithWebHostBuilder(b => b.ConfigureServices(s =>
    {
        s.RemoveAll<TimeProvider>();
        s.AddSingleton<TimeProvider>(_time);
    }));

    public async ValueTask DisposeAsync()
    {
        if (_api is not null) await _api.DisposeAsync();
    }

    private static async Task<string> TicketAsync(RestClient user, string key)
    {
        using var response = await user.SendAsync(HttpMethod.Post, "/api/ws-ticket", bearer: key);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await JsonAsync(response);
        Assert.NotNull(body["expiresAt"]);
        return body["ticket"]!.GetValue<string>();
    }

    private async Task<WebSocket> ConnectAsync(string ticket)
    {
        var client = Api.Server.CreateWebSocketClient();
        return await client.ConnectAsync(new Uri($"wss://kumbu.test/api/ws?ticket={Uri.EscapeDataString(ticket)}"), Ct);
    }

    /// <summary>The next text message, or null if none arrives within <paramref name="wait"/>.</summary>
    private static async Task<string?> ReceiveAsync(WebSocket socket, TimeSpan wait)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(wait);
        var buffer = new byte[4096];
        try
        {
            var result = await socket.ReceiveAsync(buffer, timeout.Token);
            return result.MessageType == WebSocketMessageType.Close ? $"close {(int?)result.CloseStatus}" : Encoding.UTF8.GetString(buffer, 0, result.Count);
        }
        catch (OperationCanceledException) when (!Ct.IsCancellationRequested)
        {
            return null;
        }
    }

    private static string Changed(string vault) => new JsonObject { ["type"] = "changed", ["vault"] = vault }.ToJsonString();

    [Fact]
    public async Task A_write_reaches_the_socket_as_a_hint()
    {
        var user = await NewAsync(Api);
        var key = await user.KeyAsync("Arbeit – Obsidian Laptop", KeyRight.Write, ApiKeyKind.Device);
        using var socket = await ConnectAsync(await TicketAsync(user, key));

        await user.CreateAsync("plan.md", "x\n");

        Assert.Equal(Changed("Arbeit"), await ReceiveAsync(socket, TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task Hints_never_name_another_vault()
    {
        var user = await NewAsync(Api);
        using var second = await user.SendAsync(HttpMethod.Post, "/api/vaults", new { name = "Privat" });
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        var other = await NewAsync(Api, "Fremd");
        var key = await user.KeyAsync("Arbeit – Obsidian Laptop", KeyRight.Write, ApiKeyKind.Device);
        using var socket = await ConnectAsync(await TicketAsync(user, key));

        await other.CreateAsync("theirs.md", "x\n");
        using var privat = await user.SendAsync(HttpMethod.Put, "/api/notes/Privat/mine.md", new { content = "y\n" });
        Assert.Equal(HttpStatusCode.Created, privat.StatusCode);

        Assert.Null(await ReceiveAsync(socket, TimeSpan.FromSeconds(1.5)));
        await user.CreateAsync("plan.md", "x\n");
        Assert.Equal(Changed("Arbeit"), await ReceiveAsync(socket, TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task Thirty_writes_make_few_hints()
    {
        var user = await NewAsync(Api);
        var key = await user.KeyAsync("Arbeit – Obsidian Laptop", KeyRight.Write, ApiKeyKind.Device);
        using var socket = await ConnectAsync(await TicketAsync(user, key));
        var catalog = Api.Services.GetRequiredService<Kumbu.Server.Vaults.VaultCatalog>();
        var vault = (await catalog.OpenForUserAsync(user.User.Id, user.UserKey, Ct)).Single();

        // The events of thirty commits within 100 ms, as an agent writing many notes makes them.
        for (var i = 0; i < 30; i++)
        {
            catalog.RaiseNotesChanged(vault.Id);
            await Task.Delay(3, Ct);
        }

        var hints = 0;
        while (await ReceiveAsync(socket, TimeSpan.FromSeconds(1.5)) is { } message)
        {
            Assert.Equal(Changed("Arbeit"), message);
            hints++;
        }

        Assert.InRange(hints, 1, 2);
    }

    [Fact]
    public async Task A_ticket_works_once_and_for_30_seconds()
    {
        var user = await NewAsync(Api);
        var key = await user.KeyAsync("Arbeit – Obsidian Laptop", KeyRight.Write, ApiKeyKind.Device);
        var ticket = await TicketAsync(user, key);
        using var first = await ConnectAsync(ticket);

        var again = await Assert.ThrowsAnyAsync<Exception>(() => ConnectAsync(ticket));
        var late = await TicketAsync(user, key);
        _time.Advance(TimeSpan.FromSeconds(31));
        var expired = await Assert.ThrowsAnyAsync<Exception>(() => ConnectAsync(late));

        Assert.Contains("401", again.Message, StringComparison.Ordinal);
        Assert.Contains("401", expired.Message, StringComparison.Ordinal);
        Assert.Equal(WebSocketState.Open, first.State);
    }

    [Fact]
    public async Task Only_api_keys_get_tickets()
    {
        var user = await NewAsync(Api);

        using var asSession = await user.SendAsync(HttpMethod.Post, "/api/ws-ticket");
        using var asDev = await user.SendAsync(HttpMethod.Post, "/api/ws-ticket", bearer: KumbuAppFactory.Token);

        Assert.Equal(HttpStatusCode.Forbidden, asSession.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, asDev.StatusCode);
    }

    [Fact]
    public async Task A_revoked_key_is_disconnected()
    {
        var user = await NewAsync(Api);
        var key = await user.KeyAsync("Arbeit – Obsidian Laptop", KeyRight.Write, ApiKeyKind.Device);
        using var socket = await ConnectAsync(await TicketAsync(user, key));
        var keys = Api.Services.GetRequiredService<ApiKeys>();
        var id = Assert.Single(await keys.ListAsync(user.User, user.UserKey, Ct)).Id;

        Assert.True(await keys.RevokeAsync(user.User, id, Ct));
        await Api.Services.GetRequiredService<SyncHub>().CheckAsync(Ct);

        Assert.Equal("close 4401", await ReceiveAsync(socket, TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task A_request_without_an_upgrade_is_400()
    {
        var user = await NewAsync(Api);
        var key = await user.KeyAsync("Arbeit – Obsidian Laptop", KeyRight.Write, ApiKeyKind.Device);
        var ticket = await TicketAsync(user, key);

        using var response = await user.GetAsync($"/api/ws?ticket={Uri.EscapeDataString(ticket)}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task An_unknown_ticket_is_401()
    {
        var error = await Assert.ThrowsAnyAsync<Exception>(() => ConnectAsync("kumbu_wst_" + new string('A', 43)));

        Assert.Contains("401", error.Message, StringComparison.Ordinal);
    }
}
