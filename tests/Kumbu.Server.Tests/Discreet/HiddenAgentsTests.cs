// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Text;
using Kumbu.Server.Accounts;
using Kumbu.Server.Tests.Api;
using Kumbu.Server.Tests.Dav;
using Microsoft.AspNetCore.Mvc.Testing;
using ModelContextProtocol.Protocol;

namespace Kumbu.Server.Tests.Discreet;

/// <summary>
/// Only the web session hides discreet vaults (discreet vaults design, global constraints): an API key over MCP, a
/// device key and a WebDAV share read and write a vault while her session hides it, exactly as before.
/// </summary>
[Collection("app")]
public sealed class HiddenAgentsTests(KumbuAppFactory app)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private WebApplicationFactory<Program> Api => app.PublicApi;

    [Fact]
    public async Task An_API_key_reads_and_writes_a_hidden_vault_over_MCP()
    {
        var w = await HiddenWorld.NewAsync(Api);
        var key = await w.In("Tagebuch").KeyAsync("Agent", KeyRight.Write);
        await w.HideAsync();

        var cat = await w.Alice.ShAsync("cat /Tagebuch/geheim.md", key);
        var written = await w.Alice.ToolAsync("write", new Dictionary<string, object?> { ["path"] = "/Tagebuch/agent.md", ["content"] = "vom Agenten\n" }, key);
        var ls = await w.Alice.ShAsync("ls /", key);

        Assert.False(cat.IsError);
        Assert.Contains(HiddenWorld.TagebuchWord, cat.Text, StringComparison.Ordinal);
        Assert.False(written.IsError ?? false, Assert.IsType<TextContentBlock>(written.Content[0]).Text);
        Assert.Contains("Tagebuch", ls.Text, StringComparison.Ordinal);
        await w.RevealAsync();
        Assert.Equal("vom Agenten\n", (await w.In("Tagebuch").ReadAsync("agent.md"))["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_device_key_reads_and_writes_a_hidden_vault()
    {
        var w = await HiddenWorld.NewAsync(Api);
        var device = await w.In("Tagebuch").KeyAsync("Tagebuch – Obsidian Laptop", KeyRight.Write, ApiKeyKind.Device);
        await w.HideAsync();

        using var versions = await w.Alice.GetAsync("/api/vaults/Tagebuch/versions", device);
        using var put = await w.Alice.SendAsync(HttpMethod.Put, "/api/notes/Tagebuch/laptop.md", new { content = "vom Laptop\n" }, bearer: device);
        using var read = await w.Alice.GetAsync("/api/notes/Tagebuch/geheim.md", device);

        Assert.Equal(HttpStatusCode.OK, versions.StatusCode);
        Assert.Contains("/Tagebuch/geheim.md", await versions.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Created, put.StatusCode);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        // Her session still doesn't have it.
        using var bySession = await w.Alice.GetAsync("/api/notes/Tagebuch/laptop.md");
        Assert.Equal(HttpStatusCode.NotFound, bySession.StatusCode);
    }

    [Fact]
    public async Task A_WebDAV_share_lists_and_writes_a_hidden_vault()
    {
        var w = await HiddenWorld.NewAsync(Api);
        var dav = await DavClient.NewAsync(Api, w.In("Tagebuch"), label: "Tablet");
        await w.HideAsync();

        using var listed = await dav.PropfindAsync("/dav/Tagebuch/");
        using var put = await dav.SendAsync(HttpMethod.Put, "/dav/Tagebuch/tablet.md", new StringContent("vom Tablet\n", Encoding.UTF8, "text/markdown"));
        using var read = await dav.SendAsync(HttpMethod.Get, "/dav/Tagebuch/geheim.md");

        Assert.Equal((HttpStatusCode)207, listed.StatusCode);
        Assert.Contains("geheim.md", await listed.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        Assert.True(put.IsSuccessStatusCode, put.StatusCode.ToString());
        Assert.Contains(HiddenWorld.TagebuchWord, await read.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        await w.RevealAsync();
        Assert.Equal("vom Tablet\n", (await w.In("Tagebuch").ReadAsync("tablet.md"))["content"]!.GetValue<string>());
    }
}
