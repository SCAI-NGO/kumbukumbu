// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Server.Accounts;
using Kumbu.Server.Publishing;
using Kumbu.Server.Sharing;
using Kumbu.Server.Tests.Api;
using Kumbu.Server.Tests.Dav;
using Kumbu.Server.Tests.Sharing;
using Kumbu.Server.Vaults;
using Microsoft.Extensions.DependencyInjection;

namespace Kumbu.Server.Tests.Publishing;

/// <summary>A skill's link follows its folder within its place and is withdrawn when the folder goes, leaves its place or
/// a mark covers any of it, whoever does it (Decision 31): no write is refused for it. The sealed skill state of the
/// vault's record follows and goes with the folder too (Decision 13).</summary>
[Collection("app")]
public sealed class SkillLinkWithdrawalTests(KumbuAppFactory app)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static VaultPath P(string path) => VaultPath.TryParse(path, out var p) ? p : throw new ArgumentException(path);

    private Publications Publications => app.PublicApi.Services.GetRequiredService<Publications>();

    private PublicationStore Store => app.PublicApi.Services.GetRequiredService<PublicationStore>();

    private async Task<(VaultAccess Access, EncryptedNoteStore Store)> AccessAsync(RestClient alice, string vault = "Skills")
    {
        var store = new EncryptedNoteStore(app.PublicApi.Services.GetRequiredService<VaultCatalog>(), new UserCredential(alice.User.Id, alice.UserKey), null, null, null,
            Publications, app.PublicApi.Services.GetRequiredService<StorageQuota>());
        return ((await store.UnlockedVaultsAsync(Ct)).Single(a => a.Name == vault), store);
    }

    private Task<(VaultAccess Access, EncryptedNoteStore Store)> SkillsAccessAsync(RestClient alice) => AccessAsync(alice);

    private async Task<string> SkillsIdAsync(RestClient alice) =>
        (await app.PublicApi.Services.GetRequiredService<VaultCatalog>().OpenForUserAsync(alice.User.Id, alice.UserKey, Ct)).Single(v => v.Name == "Skills").Id;

    private async Task<(RestClient Alice, string Code)> SharedAsync(string vault = "Skills", string folder = "/Skills/x")
    {
        var alice = await RestClient.NewAsync(app.PublicApi, vault: "Work");
        await PutNoteAsync(alice, $"{folder}/SKILL.md", "---\nname: x\ndescription: y\n---\n");
        await PutNoteAsync(alice, $"{folder}/examples/a.md", "# A\n");
        var (access, store) = await AccessAsync(alice, vault);
        var shared = await Publications.PublishSkillAsync(access, store, P(folder), alice.User.Id, alice.Username,
            WriterRef.ForOwner(WriterRef.Session, null), KumbuAppFactory.PublicUrl, Ct);
        return (alice, ShareCode.Parse(shared.Link[(shared.Link.IndexOf('#') + 1)..])!);
    }

    private static async Task PutNoteAsync(RestClient user, string path, string content)
    {
        using var put = await user.SendAsync(HttpMethod.Put, "/api/notes" + path, new { content });
        Assert.Equal(HttpStatusCode.Created, put.StatusCode);
    }

    private async Task<bool> OpensAsync(string code) => await Store.OpenSkillAsync(code, Ct) is not null;

    private async Task WriteRecordAsync(VaultAccess access, Func<PublishingRecord, PublishingRecord> change) => await Store.ChangeAsync(access, change, Ct);

    [Fact]
    public async Task An_agents_rm_of_SKILL_md_withdraws_the_link()
    {
        var (alice, code) = await SharedAsync();
        var (key, _) = await app.PublicApi.Services.GetRequiredService<ApiKeys>().CreateAsync(alice.User, alice.UserKey, DateTime.UtcNow, "Claude",
            new Dictionary<string, KeyRight> { [await SkillsIdAsync(alice)] = KeyRight.Write }, Ct);
        var (isError, text) = await alice.ShAsync("rm /Skills/x/SKILL.md", key);
        Assert.False(isError, text);
        Assert.False(await OpensAsync(code));
        var (access, store) = await SkillsAccessAsync(alice);
        Assert.Empty(await Publications.SkillStatesAsync(access, store, Ct));
        var log = (await Store.ReadAsync(null, access.Vault, Ct)).Record.Log[^1];
        Assert.Equal((PublishingKinds.Withdrawn, WithdrawalReasons.Removed, "/Skills/x"), (log.Kind, log.Reason, log.Note));
    }

    [Fact]
    public async Task A_dav_delete_of_the_folder_withdraws_the_link()
    {
        var (alice, code) = await SharedAsync();
        var dav = await DavClient.NewAsync(app.PublicApi, alice, vaults: new Dictionary<string, string> { ["Skills"] = "write" });
        using var delete = await dav.SendAsync(HttpMethod.Delete, "/dav/Skills/x/");
        Assert.True(delete.IsSuccessStatusCode, delete.StatusCode.ToString());
        Assert.False(await OpensAsync(code));
    }

    [Fact]
    public async Task A_devices_removal_withdraws_the_link()
    {
        var (alice, code) = await SharedAsync();
        var device = await app.PublicApi.Services.GetRequiredService<ApiKeys>().CreateAsync(alice.User, alice.UserKey, DateTime.UtcNow, "Skills – Obsidian Laptop",
            new Dictionary<string, KeyRight> { [await SkillsIdAsync(alice)] = KeyRight.Write }, Ct, kind: ApiKeyKind.Device);
        using var delete = await alice.SendAsync(HttpMethod.Delete, "/api/notes/Skills/x/SKILL.md", bearer: device.Key);
        Assert.True(delete.IsSuccessStatusCode, delete.StatusCode.ToString());
        Assert.False(await OpensAsync(code));
    }

    [Fact]
    public async Task A_members_removal_withdraws_the_link()
    {
        var (alice, code) = await SharedAsync("Work", "/Work/_skills/x");
        var bob = await RestClient.NewAsync(app.PublicApi, vault: "Private");
        await alice.ShareWithAsync(bob, "alice-work", MemberRole.Write);
        using var delete = await bob.SendAsync(HttpMethod.Delete, "/api/notes/alice-work/_skills/x/SKILL.md");
        Assert.True(delete.IsSuccessStatusCode, delete.StatusCode.ToString());
        Assert.False(await OpensAsync(code));
    }

    [Fact]
    public async Task Moving_the_folder_out_of_a_skill_location_withdraws_it()
    {
        var (alice, code) = await SharedAsync("Work", "/Work/_skills/x");
        using var move = await alice.SendAsync(HttpMethod.Post, "/api/move", new { from = "/Work/_skills/x/", to = "/Work/old-x/" });
        Assert.True(move.IsSuccessStatusCode, await move.Content.ReadAsStringAsync(Ct));
        Assert.False(await OpensAsync(code));
    }

    [Fact]
    public async Task Moving_SKILL_md_out_of_its_folder_withdraws_it()
    {
        var (alice, code) = await SharedAsync();
        using var move = await alice.SendAsync(HttpMethod.Post, "/api/move", new { from = "/Skills/x/SKILL.md", to = "/Skills/x/examples/SKILL.md" });
        Assert.True(move.IsSuccessStatusCode, await move.Content.ReadAsStringAsync(Ct));
        Assert.False(await OpensAsync(code));
    }

    [Fact]
    public async Task Renaming_the_folder_follows_it()
    {
        var (alice, code) = await SharedAsync();
        using var move = await alice.SendAsync(HttpMethod.Post, "/api/move", new { from = "/Skills/x/", to = "/Skills/y/" });
        Assert.True(move.IsSuccessStatusCode, await move.Content.ReadAsStringAsync(Ct));
        Assert.True(await OpensAsync(code));
        var (access, store) = await SkillsAccessAsync(alice);
        var state = (await Publications.SkillStatesAsync(access, store, Ct)).Single();
        Assert.Equal("/Skills/y", state.Folder.Value);
        Assert.False(state.Changed);
    }

    [Fact]
    public async Task Marking_a_file_withdraws_the_link()
    {
        var (alice, code) = await SharedAsync();
        using var mark = await alice.SendAsync(HttpMethod.Put, "/api/unpublishable/Skills/x/examples");
        Assert.True(mark.IsSuccessStatusCode, await mark.Content.ReadAsStringAsync(Ct));
        Assert.False(await OpensAsync(code));
        var (access, _) = await SkillsAccessAsync(alice);
        var log = (await Store.ReadAsync(null, access.Vault, Ct)).Record.Log[^1];
        Assert.Equal((PublishingKinds.Withdrawn, WithdrawalReasons.Unpublishable), (log.Kind, log.Reason));
    }

    [Fact]
    public async Task Marking_a_file_added_after_sharing_withdraws_the_link()
    {
        var (alice, code) = await SharedAsync();
        await PutNoteAsync(alice, "/Skills/x/later.md", "# Later\n");
        using var mark = await alice.SendAsync(HttpMethod.Put, "/api/unpublishable/Skills/x/later.md");
        Assert.True(mark.IsSuccessStatusCode, await mark.Content.ReadAsStringAsync(Ct));
        Assert.False(await OpensAsync(code));
    }

    [Fact]
    public async Task Removing_another_file_keeps_the_link()
    {
        var (alice, code) = await SharedAsync();
        using var delete = await alice.SendAsync(HttpMethod.Delete, "/api/notes/Skills/x/examples/a.md");
        Assert.True(delete.IsSuccessStatusCode, delete.StatusCode.ToString());
        Assert.True(await OpensAsync(code));
        var (access, store) = await SkillsAccessAsync(alice);
        Assert.True((await Publications.SkillStatesAsync(access, store, Ct)).Single().Changed);
    }

    [Fact]
    public async Task A_renamed_copy_keeps_its_sealed_entries_and_a_trashed_one_drops_them()
    {
        var alice = await RestClient.NewAsync(app.PublicApi, vault: "Work");
        await PutNoteAsync(alice, "/Skills/pdf/SKILL.md", "---\nname: pdf\ndescription: y\n---\n");
        var (access, _) = await SkillsAccessAsync(alice);
        await WriteRecordAsync(access, r => r with
        {
            SkillCodes = [new SkillCode("/Skills/pdf", "K7QM4XDA0000111122223333444455556")], Origins = [new SkillOrigin("/Skills/pdf", "link:ab", "sha256:cd")],
            Updates = ["/Skills/pdf"], Skipped = [new SkippedUpdate("/Skills/pdf", "sha256:ef")],
        });
        using var move = await alice.SendAsync(HttpMethod.Post, "/api/move", new { from = "/Skills/pdf/", to = "/Skills/pdf-tools/" });
        Assert.True(move.IsSuccessStatusCode, await move.Content.ReadAsStringAsync(Ct));
        var (_, moved) = await Store.ReadAsync(null, access.Vault, Ct);
        Assert.Equal("/Skills/pdf-tools", moved.SkillCodes.Single().Path);
        Assert.Equal("/Skills/pdf-tools", moved.Origins.Single().Path);
        Assert.Equal(["/Skills/pdf-tools"], moved.Updates);
        Assert.Equal("/Skills/pdf-tools", moved.Skipped.Single().Path);
        using var rm = await alice.SendAsync(HttpMethod.Delete, "/api/notes/Skills/pdf-tools/SKILL.md");
        Assert.True(rm.IsSuccessStatusCode, rm.StatusCode.ToString());
        var (_, trashed) = await Store.ReadAsync(null, access.Vault, Ct);
        Assert.Empty(trashed.SkillCodes);
        Assert.Empty(trashed.Origins);
        Assert.Empty(trashed.Updates);
        Assert.Empty(trashed.Skipped);
    }

    [Fact]
    public async Task A_copy_moved_out_of_its_place_drops_its_sealed_entries()
    {
        var alice = await RestClient.NewAsync(app.PublicApi, vault: "Work");
        await PutNoteAsync(alice, "/Work/_skills/pdf/SKILL.md", "---\nname: pdf\ndescription: y\n---\n");
        var (access, _) = await AccessAsync(alice, "Work");
        await WriteRecordAsync(access, r => r with { Origins = [new SkillOrigin("/Work/_skills/pdf", "link:ab", "sha256:cd")] });
        using var move = await alice.SendAsync(HttpMethod.Post, "/api/move", new { from = "/Work/_skills/pdf/", to = "/Work/pdf/" });
        Assert.True(move.IsSuccessStatusCode, await move.Content.ReadAsStringAsync(Ct));
        Assert.Empty((await Store.ReadAsync(null, access.Vault, Ct)).Record.Origins);
    }
}
