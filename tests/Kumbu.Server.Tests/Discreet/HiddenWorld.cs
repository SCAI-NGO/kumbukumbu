// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Security.Cryptography;
using Kumbu.Server.Accounts;
using Kumbu.Server.Sharing;
using Kumbu.Server.Tests.Api;
using Kumbu.Server.Tests.Sharing;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;

namespace Kumbu.Server.Tests.Discreet;

/// <summary>
/// Alice's vaults for the tests of what a web session hides: her own <c>Work</c> and <c>Tagebuch</c>, and Bob's
/// <c>Familie</c>, shared with her under that name. Each of <c>Tagebuch</c> and <c>Familie</c> holds <c>geheim.md</c>
/// with a word found nowhere else. <see cref="HideAsync"/> marks both discreet, written straight into her user document.
/// </summary>
internal sealed record HiddenWorld(RestClient Alice, RestClient Bob, string TagebuchId, string FamilieId, string FamilieMembership)
{
    /// <summary>The word in <c>Tagebuch/geheim.md</c>; <see cref="FamilieWord"/> is <c>Familie/geheim.md</c>'s.</summary>
    public const string TagebuchWord = "Liebeskummer";

    public const string FamilieWord = "Erbschaftsstreit";

    /// <summary>A name Alice never had, and no word of any answer: what a hidden vault must answer as.</summary>
    public const string Nowhere = "Nowhere-7f3a";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public WebApplicationFactory<Program> Api => Alice.Api;

    /// <summary>Alice's session on one of her vaults by name, for <see cref="RestClient.Url"/>.</summary>
    public RestClient In(string vault) => RestClient.Existing(Api, Alice.User, Alice.UserKey, Alice.Session, vault);

    public static async Task<HiddenWorld> NewAsync(WebApplicationFactory<Program> api)
    {
        var alice = await RestClient.NewAsync(api, vault: "Work");
        await alice.CreateAsync("plan.md", "# Plan\n\nArbeit #arbeit\n");
        using (var created = await alice.SendAsync(HttpMethod.Post, "/api/vaults", new { name = "Tagebuch" }))
        {
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        }

        var tagebuch = RestClient.Existing(api, alice.User, alice.UserKey, alice.Session, "Tagebuch");
        await tagebuch.CreateAsync("geheim.md", $"# Geheim\n\n{TagebuchWord} #privat\n\nSiehe [[alt]]\n");
        await tagebuch.CreateAsync("alt.md", "# Alt\n\nSiehe [[geheim]]\n");
        await tagebuch.CreateFileAsync("bild.png", FileApiTests.Png(4, 4));

        var bob = await RestClient.NewAsync(api, vault: "Familie");
        await bob.CreateAsync("geheim.md", $"# Geheim\n\n{FamilieWord} #privat\n");
        var membership = await bob.ShareWithAsync(alice, "Familie");

        return new HiddenWorld(alice, bob, await tagebuch.VaultIdAsync(), await bob.VaultIdAsync(), membership);
    }

    private IMongoCollection<UserDocument> Users => Api.Services.GetRequiredService<IMongoDatabase>().GetCollection<UserDocument>("users");

    /// <summary>Marks <c>Tagebuch</c> and <c>Familie</c> discreet (and <paramref name="also"/>, e.g. a vault that is gone).</summary>
    public Task HideAsync(params string[] also) =>
        Users.UpdateOneAsync(
            u => u.Id == Alice.User.Id,
            Builders<UserDocument>.Update.Set(u => u.DiscreetVaults, DiscreetVaults.SealList(Alice.User.Id, Alice.UserKey, [TagebuchId, FamilieId, .. also])),
            cancellationToken: Ct);

    /// <summary>A list that doesn't open, as only tampering with the database leaves it.</summary>
    public Task SpoilListAsync() =>
        Users.UpdateOneAsync(
            u => u.Id == Alice.User.Id, Builders<UserDocument>.Update.Set(u => u.DiscreetVaults, RandomNumberGenerator.GetBytes(80)), cancellationToken: Ct);

    /// <summary>Shows the discreet vaults in Alice's session until it ends.</summary>
    public async Task RevealAsync() =>
        Assert.True((await Api.Services.GetRequiredService<Sessions>().RevealAsync(Alice.Session, null, Ct)).Revealed);

    /// <summary>The names <c>GET /api/vaults</c> lists, in order, each with its <c>discreet</c> flag.</summary>
    public async Task<(string Name, bool Discreet)[]> VaultsAsync()
    {
        using var response = await Alice.GetAsync("/api/vaults");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return [.. (await RestClient.JsonAsync(response)).AsArray()
            .Select(v => (v!["name"]!.GetValue<string>(), v["discreet"]!.GetValue<bool>()))
            .OrderBy(v => v.Item1, StringComparer.Ordinal)];
    }

    /// <summary>
    /// The request for <paramref name="vault"/> (<paramref name="path"/> with <c>{v}</c> for the name, <paramref name="body"/>
    /// made from the name) answers exactly as the same request for <see cref="Nowhere"/>, once that name is replaced
    /// by the vault's in its answer: the same status, the same body. With <paramref name="ticket"/>, each request gets
    /// a fresh one for <c>{t}</c> in the path.
    /// </summary>
    public async Task AsNowhereAsync(string vault, HttpMethod method, string path, Func<string, object?>? body = null, Func<Task<string>>? ticket = null)
    {
        var (hiddenStatus, hiddenBody) = await SendAsync(vault, method, path, body, ticket);
        var (nowhereStatus, nowhereBody) = await SendAsync(Nowhere, method, path, body, ticket);
        Assert.Equal((nowhereStatus, nowhereBody.Replace(Nowhere, vault, StringComparison.Ordinal)), (hiddenStatus, hiddenBody));
    }

    private async Task<(HttpStatusCode, string)> SendAsync(string vault, HttpMethod method, string path, Func<string, object?>? body, Func<Task<string>>? ticket)
    {
        path = path.Replace("{v}", vault, StringComparison.Ordinal);
        if (ticket is not null) path = path.Replace("{t}", await ticket(), StringComparison.Ordinal);
        using var response = await Alice.SendAsync(method, path, body?.Invoke(vault));
        return (response.StatusCode, await response.Content.ReadAsStringAsync(Ct));
    }

    /// <summary>What the console answers to <paramref name="command"/>, its text parts joined.</summary>
    public async Task<string> ConsoleAsync(string command)
    {
        using var response = await Alice.SendAsync(HttpMethod.Post, "/api/console", new { call = "sh", command });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return string.Join("\n", (await RestClient.JsonAsync(response))["parts"]!.AsArray().Select(p => p!["text"]?.GetValue<string>() ?? ""));
    }
}
