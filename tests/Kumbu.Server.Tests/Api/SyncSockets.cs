// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Kumbu.Server.Tests.Api;

/// <summary>Sync sockets for tests: a ticket for a key or a session, the connection, and what it receives.</summary>
internal static class SyncSockets
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A ticket for <paramref name="bearer"/>'s key, or for the user's session without one.</summary>
    public static async Task<string> TicketAsync(RestClient user, string? bearer = null)
    {
        using var response = await user.SendAsync(HttpMethod.Post, "/api/ws-ticket", bearer: bearer);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await RestClient.JsonAsync(response))["ticket"]!.GetValue<string>();
    }

    public static Task<WebSocket> ConnectAsync(WebApplicationFactory<Program> api, string ticket) =>
        api.Server.CreateWebSocketClient().ConnectAsync(new Uri($"wss://kumbu.test/api/ws?ticket={Uri.EscapeDataString(ticket)}"), Ct);

    /// <summary>A socket of the user's session, or of <paramref name="bearer"/>'s key.</summary>
    public static async Task<WebSocket> OpenAsync(WebApplicationFactory<Program> api, RestClient user, string? bearer = null) =>
        await ConnectAsync(api, await TicketAsync(user, bearer));

    /// <summary>The next message, "close N" for a close, or null if none comes within <paramref name="wait"/>.</summary>
    public static async Task<string?> ReceiveAsync(WebSocket socket, TimeSpan wait)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(wait);
        var buffer = new byte[8192];
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

    /// <summary>Everything that arrives until nothing has for <paramref name="quiet"/>.</summary>
    public static async Task<IReadOnlyList<string>> DrainAsync(WebSocket socket, TimeSpan quiet)
    {
        var messages = new List<string>();
        while (await ReceiveAsync(socket, quiet) is { } message)
        {
            messages.Add(message);
            if (message.StartsWith("close", StringComparison.Ordinal)) break;
        }

        return messages;
    }

    /// <summary>The next presence message, skipping hints; null if none comes within <paramref name="wait"/>.</summary>
    public static async Task<JsonNode?> PresenceAsync(WebSocket socket, TimeSpan wait)
    {
        var until = DateTime.UtcNow + wait;
        while (DateTime.UtcNow < until && await ReceiveAsync(socket, until - DateTime.UtcNow) is { } message)
        {
            if (message.StartsWith("close", StringComparison.Ordinal)) return null;
            var node = JsonNode.Parse(message)!;
            if (node["type"]!.GetValue<string>() == "presence") return node;
        }

        return null;
    }

    /// <summary>The editors of a presence message.</summary>
    public static string[] Editors(JsonNode presence) => [.. presence["editors"]!.AsArray().Select(e => e!.GetValue<string>())];

    public static Task EditingAsync(WebSocket socket, string vault, string path, bool on) =>
        SendAsync(socket, new JsonObject { ["type"] = "editing", ["vault"] = vault, ["path"] = path, ["on"] = on }.ToJsonString());

    public static Task SendAsync(WebSocket socket, string text) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, endOfMessage: true, Ct);

    public static string Changed(string vault) => new JsonObject { ["type"] = "changed", ["vault"] = vault }.ToJsonString();
}
