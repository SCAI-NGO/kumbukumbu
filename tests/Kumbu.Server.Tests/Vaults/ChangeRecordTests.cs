// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.Concurrent;
using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Kumbu.Core.Paths;
using Kumbu.Server.Accounts;
using Kumbu.Server.Tests.Api;
using Kumbu.Server.Vaults;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Kumbu.Server.Tests.Vaults;

/// <summary>
/// The change record (sync plan, Task 4): every way a note changes leaves the time of its commit on the unlocked vault
/// for each path it touched, and raises <see cref="VaultCatalog.NotesChanged"/> once per commit.
/// </summary>
[Collection("app")]
public sealed class ChangeRecordTests(KumbuAppFactory app) : IDisposable
{
    private readonly List<Action<string>> _handlers = [];

    /// <summary>The handlers go with the test: the catalog is the whole collection's.</summary>
    public void Dispose()
    {
        foreach (var handler in _handlers) Catalog.NotesChanged -= handler;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private WebApplicationFactory<Program> Api => app.PublicApi;

    private VaultCatalog Catalog => Api.Services.GetRequiredService<VaultCatalog>();

    /// <summary>A user with a vault, the vault in memory, and the hints raised for it from now on.</summary>
    private sealed class World(RestClient user, UnlockedVault vault, ConcurrentQueue<string> hints)
    {
        public RestClient User { get; } = user;

        public UnlockedVault Vault { get; } = vault;

        public ConcurrentQueue<string> Hints { get; } = hints;

        public VaultPath Path(string name) => VaultPath.Root.Child(User.Vault).Child(name);
    }

    private async Task<World> WorldAsync()
    {
        var user = await RestClient.NewAsync(Api);
        var vault = (await Catalog.OpenForUserAsync(user.User.Id, user.UserKey, Ct)).Single(v => v.Name == user.Vault).Vault;
        var hints = new ConcurrentQueue<string>();
        Action<string> handler = id =>
        {
            if (id == vault.Id) hints.Enqueue(id);
        };
        Catalog.NotesChanged += handler;
        _handlers.Add(handler);
        return new World(user, vault, hints);
    }

    /// <summary>Runs <paramref name="change"/> and asserts each path is recorded at or after the moment before it, with one hint.</summary>
    private static async Task AssertRecordedAsync(World world, Func<Task> change, params string[] names)
    {
        world.Hints.Clear();
        var before = DateTime.UtcNow.AddMilliseconds(-1);

        await change();

        foreach (var name in names)
        {
            Assert.True(world.Vault.ChangedAt.TryGetValue(world.Path(name), out var at), $"{name} has no change time");
            Assert.True(at >= before, $"{name} was recorded at {at:O}, before {before:O}");
        }

        Assert.Single(world.Hints);
    }

    private static async Task SendOkAsync(RestClient user, HttpMethod method, string path, object? body = null)
    {
        using var response = await user.SendAsync(method, path, body);
        Assert.True(response.IsSuccessStatusCode, $"{method} {path}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync(Ct)}");
    }

    private async Task<McpClient> McpAsync(string key)
    {
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri(KumbuAppFactory.PublicUrl + "/mcp"),
                AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {key}", ["X-Forwarded-For"] = $"198.28.{Random.Shared.Next(256)}.{Random.Shared.Next(1, 255)}" },
            },
            Api.CreateClient(),
            NullLoggerFactory.Instance,
            ownsHttpClient: false);
        return await McpClient.CreateAsync(transport, cancellationToken: Ct);
    }

    private static string Text(CallToolResult result) => Assert.IsType<TextContentBlock>(result.Content[0]).Text;

    [Fact]
    public async Task A_write_and_a_replace_over_rest_are_recorded()
    {
        var world = await WorldAsync();
        string version = "";

        await AssertRecordedAsync(world, async () => version = await world.User.CreateAsync("plan.md", "one\n"), "plan.md");
        await AssertRecordedAsync(world, () => SendOkAsync(world.User, HttpMethod.Put, world.User.Url("plan.md"), new { content = "two\n", version }), "plan.md");
    }

    [Fact]
    public async Task An_edit_and_a_replace_across_notes_are_recorded()
    {
        var world = await WorldAsync();
        var key = await world.User.KeyAsync("agent", KeyRight.Write);
        var version = await world.User.CreateAsync("plan.md", "car at 14:00\n");
        await world.User.CreateAsync("other.md", "car at 14:00 too\n");
        await using var client = await McpAsync(key);

        await AssertRecordedAsync(
            world,
            async () => Assert.NotEqual(true, (await client.CallToolAsync("edit", new Dictionary<string, object?> { ["path"] = $"/{world.User.Vault}/plan.md", ["version"] = version, ["old_text"] = "car", ["new_text"] = "bike" }, cancellationToken: Ct)).IsError),
            "plan.md");

        var preview = Text(await client.CallToolAsync("replace", new Dictionary<string, object?> { ["path"] = $"/{world.User.Vault}", ["old_text"] = "14:00", ["new_text"] = "16:00" }, cancellationToken: Ct));
        var plan = Regex.Match(preview, @"plan: (\S+)").Groups[1].Value;
        await AssertRecordedAsync(
            world,
            async () => Assert.NotEqual(true, (await client.CallToolAsync("replace", new Dictionary<string, object?> { ["path"] = $"/{world.User.Vault}", ["old_text"] = "14:00", ["new_text"] = "16:00", ["plan"] = plan }, cancellationToken: Ct)).IsError),
            "plan.md",
            "other.md");
    }

    [Fact]
    public async Task A_move_records_its_source_and_target_and_the_notes_whose_links_it_rewrote()
    {
        var world = await WorldAsync();
        await world.User.CreateAsync("a.md", "a\n");
        await world.User.CreateAsync("b.md", "see [[a]]\n");
        await world.User.CreateAsync("c.md", "c\n");

        await AssertRecordedAsync(world, () => SendOkAsync(world.User, HttpMethod.Post, "/api/move", new { from = $"/{world.User.Vault}/c.md", to = $"/{world.User.Vault}/d.md", keepLinks = true }), "c.md", "d.md");
        await AssertRecordedAsync(world, () => SendOkAsync(world.User, HttpMethod.Post, "/api/move", new { from = $"/{world.User.Vault}/a.md", to = $"/{world.User.Vault}/renamed.md" }), "a.md", "renamed.md", "b.md");
        Assert.Contains("[[renamed]]", (await world.User.ReadAsync("b.md"))["content"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_removal_and_the_restores_are_recorded()
    {
        var world = await WorldAsync();
        var first = await world.User.CreateAsync("plan.md", "one\n");
        // Another writer, so the history keeps the first version apart.
        var agent = await world.User.KeyAsync("agent", KeyRight.Write);
        using (var second = await world.User.SendAsync(HttpMethod.Put, world.User.Url("plan.md"), new { content = "two\n", version = first }, bearer: agent))
        {
            Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        }

        await AssertRecordedAsync(world, () => SendOkAsync(world.User, HttpMethod.Post, world.User.Url("plan.md", "restore"), new { version = first }), "plan.md");
        await AssertRecordedAsync(world, () => SendOkAsync(world.User, HttpMethod.Delete, world.User.Url("plan.md")), "plan.md");
        await AssertRecordedAsync(world, () => SendOkAsync(world.User, HttpMethod.Post, world.User.Url("plan.md", "restore"), new { }), "plan.md");
    }

    [Fact]
    public async Task A_checkpoint_restore_and_an_upload_are_recorded()
    {
        var world = await WorldAsync();
        var version = await world.User.CreateAsync("plan.md", "then\n");
        world.Hints.Clear();
        await SendOkAsync(world.User, HttpMethod.Post, $"/api/vaults/{world.User.Vault}/checkpoints", new { name = "cp" });
        // Making a checkpoint changes no note.
        Assert.Empty(world.Hints);
        await SendOkAsync(world.User, HttpMethod.Put, world.User.Url("plan.md"), new { content = "now\n", version });
        await world.User.CreateAsync("late.md", "late\n");
        using var preview = await world.User.SendAsync(HttpMethod.Post, $"/api/vaults/{world.User.Vault}/checkpoints/cp/preview");
        var plan = (await RestClient.JsonAsync(preview))["plan"]!.GetValue<string>();

        await AssertRecordedAsync(world, () => SendOkAsync(world.User, HttpMethod.Post, $"/api/vaults/{world.User.Vault}/checkpoints/cp/restore", new { plan }), "plan.md", "late.md");
        await AssertRecordedAsync(
            world,
            () => SendOkAsync(world.User, HttpMethod.Post, $"/api/upload/{world.User.Vault}", new { files = new[] { new { path = "x.md", content = "x\n" }, new { path = "y/z.md", content = "z\n" } } }),
            "x.md",
            "y/z.md");
    }

    [Fact]
    public async Task A_stale_write_records_nothing_and_raises_nothing()
    {
        var world = await WorldAsync();
        await world.User.CreateAsync("plan.md", "one\n");
        var path = world.Path("plan.md");
        var seen = world.Vault.Notes[path].Note;
        // Another writer gets there first: the compare-and-swap on the revision seen fails inside the transaction.
        using (var other = await world.User.SendAsync(HttpMethod.Put, world.User.Url("plan.md"), new { content = "two\n", version = seen.Version }))
        {
            Assert.Equal(HttpStatusCode.OK, other.StatusCode);
        }

        var recorded = world.Vault.ChangedAt[path];
        world.Hints.Clear();
        var store = new EncryptedNoteStore(Catalog, new UserCredential(world.User.User.Id, world.User.UserKey));

        Assert.False(await store.TryReplaceAsync(seen, seen with { Content = "three\n", Revision = seen.Revision + 1 }, Ct));

        Assert.Equal(recorded, world.Vault.ChangedAt[path]);
        Assert.Empty(world.Hints);
    }

    [Fact]
    public async Task Deleting_a_vault_raises_once()
    {
        var world = await WorldAsync();
        await world.User.CreateAsync("plan.md", "one\n");
        world.Hints.Clear();

        await SendOkAsync(world.User, HttpMethod.Post, $"/api/vaults/{world.User.Vault}/delete", new { password = RestClient.Password, confirm = world.User.Vault });

        Assert.Single(world.Hints);
    }

    [Fact]
    public async Task The_horizon_is_when_the_vault_was_unlocked()
    {
        var before = DateTime.UtcNow.AddMilliseconds(-1);
        var world = await WorldAsync();

        Assert.True(world.Vault.Horizon >= before && world.Vault.Horizon <= DateTime.UtcNow);
        Assert.Empty(world.Vault.ChangedAt);
    }
}
