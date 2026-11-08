// SPDX-License-Identifier: AGPL-3.0-or-later
using System.IO.Compression;
using System.Net;
using System.Text;
using Kumbu.Core.Attachments;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Server.Accounts;
using Kumbu.Server.Api;
using Kumbu.Server.Tests.Api;
using Kumbu.Server.Vaults;
using Microsoft.Extensions.DependencyInjection;

namespace Kumbu.Server.Tests.Skills;

/// <summary>Text files in a skill's folder through <c>/api/files</c> and the store's last check: kept as plain text
/// there, refused (as before) anywhere else.</summary>
[Collection("app")]
public sealed class TextFileChannelTests(KumbuAppFactory app)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly byte[] Script = "print(1)\n"u8.ToArray();

    private static VaultPath P(string path) => VaultPath.TryParse(path, out var p) ? p : throw new ArgumentException(path);

    [Fact]
    public async Task PUT_api_files_keeps_a_text_file_in__skills_and_serves_it_as_plain_text()
    {
        var alice = await RestClient.NewAsync(app.PublicApi, vault: "Work");
        using var put = await alice.PutFileAsync("_skills/x/run.py", Script);
        Assert.Equal(HttpStatusCode.Created, put.StatusCode);
        using var get = await alice.GetAsync(alice.Url("_skills/x/run.py", "files"));
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal("text/plain; charset=utf-8", get.Content.Headers.ContentType!.ToString());
        Assert.Equal(Security.FileContentPolicy, get.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("nosniff", get.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal(Script, await get.Content.ReadAsByteArrayAsync(Ct));
    }

    [Theory, InlineData("index.html", "<script>alert(1)</script>"), InlineData("logo.svg", "<svg xmlns='http://www.w3.org/2000/svg'><script>alert(1)</script></svg>")]
    public async Task Markup_in__skills_is_served_as_plain_text(string name, string text)
    {
        var alice = await RestClient.NewAsync(app.PublicApi, vault: "Work");
        using var put = await alice.PutFileAsync($"_skills/x/{name}", Encoding.UTF8.GetBytes(text));
        Assert.Equal(HttpStatusCode.Created, put.StatusCode);
        using var get = await alice.GetAsync(alice.Url($"_skills/x/{name}", "files"));
        Assert.Equal("text/plain; charset=utf-8", get.Content.Headers.ContentType!.ToString());
    }

    [Fact]
    public async Task A_text_file_outside__skills_is_400_as_before()
    {
        var alice = await RestClient.NewAsync(app.PublicApi, vault: "Work");
        using var put = await alice.PutFileAsync("run.py", Script);
        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
        Assert.Contains(AttachmentFormat.UnsupportedMessage, await RestClient.ErrorAsync(put));
    }

    [Fact]
    public async Task A_text_file_counts_towards_the_quota()
    {
        var admin = await RestClient.NewAsync(app.PublicApi, vault: "Admin", role: Roles.Admin);
        var alice = await RestClient.NewAsync(app.PublicApi, vault: "Work");
        using var limit = await admin.SendAsync(HttpMethod.Patch, $"/api/admin/users/{alice.User.Id}", new { quota = 10, password = RestClient.Password });
        Assert.True(limit.IsSuccessStatusCode, await limit.Content.ReadAsStringAsync(Ct));
        using var put = await alice.PutFileAsync("_skills/x/big.txt", new byte[64].Select(_ => (byte)'a').ToArray());
        Assert.Equal(HttpStatusCode.InsufficientStorage, put.StatusCode);
    }

    [Fact]
    public async Task An_export_holds_the_text_file()
    {
        var alice = await RestClient.NewAsync(app.PublicApi, vault: "Work");
        await alice.CreateFileAsync("_skills/x/run.py", Script);
        using var ticket = await alice.SendAsync(HttpMethod.Post, "/api/export/ticket", new { password = RestClient.Password });
        var token = (await RestClient.JsonAsync(ticket))["ticket"]!.GetValue<string>();
        using var export = await alice.GetAsync($"/api/vaults/Work/export?ticket={Uri.EscapeDataString(token)}");
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        using var zip = new ZipArchive(new MemoryStream(await export.Content.ReadAsByteArrayAsync(Ct)));
        using var entry = zip.GetEntry("Work/_skills/x/run.py")!.Open();
        using var copy = new MemoryStream();
        await entry.CopyToAsync(copy, Ct);
        Assert.Equal(Script, copy.ToArray());
    }

    [Fact]
    public async Task The_store_refuses_a_text_file_outside_a_skill_folder()
    {
        var alice = await RestClient.NewAsync(app.PublicApi, vault: "Work");
        var store = new EncryptedNoteStore(app.PublicApi.Services.GetRequiredService<VaultCatalog>(), new UserCredential(alice.User.Id, alice.UserKey));
        var blob = await store.PutBlobAsync("Work", Script, Ct);
        var state = new AttachmentState(AttachmentFormat.TextMediaType, Script.Length, null, null, VersionToken.Of(Script), blob);
        var created = new StoredAttachment(P("/Work/run.py"), state, 1, "alice", DateTime.UtcNow, [], null, WriterRef.ForOwner(WriterRef.Session, null));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.TryCommitAsync(new NoteBatch { AttachmentCreates = { created } }, "alice", WriterRef.ForOwner(WriterRef.Session, null), DateTime.UtcNow, Ct));
        Assert.Null(await store.GetAttachmentAsync(P("/Work/run.py"), Ct));
    }

    private async Task<(EncryptedNoteStore Store, StoredAttachment Kept)> KeptScriptAsync()
    {
        var alice = await RestClient.NewAsync(app.PublicApi, vault: "Work");
        var store = new EncryptedNoteStore(app.PublicApi.Services.GetRequiredService<VaultCatalog>(), new UserCredential(alice.User.Id, alice.UserKey));
        var blob = await store.PutBlobAsync("Work", Script, Ct);
        var state = new AttachmentState(AttachmentFormat.TextMediaType, Script.Length, null, null, VersionToken.Of(Script), blob);
        var kept = new StoredAttachment(P("/Work/_skills/x/run.py"), state, 1, "alice", DateTime.UtcNow, [], null, WriterRef.ForOwner(WriterRef.Session, null));
        Assert.Empty(await store.TryCommitAsync(new NoteBatch { AttachmentCreates = { kept } }, "alice", WriterRef.ForOwner(WriterRef.Session, null), DateTime.UtcNow, Ct));
        return (store, (await store.GetAttachmentAsync(kept.Path, Ct))!);
    }

    [Fact]
    public async Task The_store_refuses_to_move_a_text_file_out_of_a_skill_folder()
    {
        var (store, kept) = await KeptScriptAsync();
        var moved = kept with { Path = P("/Work/run.py"), Revision = kept.Revision + 1 };
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.TryCommitAsync(new NoteBatch { AttachmentMoves = { (kept, moved) } }, "alice", WriterRef.ForOwner(WriterRef.Session, null), DateTime.UtcNow, Ct));
        Assert.NotNull(await store.GetAttachmentAsync(P("/Work/_skills/x/run.py"), Ct));
        Assert.Null(await store.GetAttachmentAsync(P("/Work/run.py"), Ct));
    }

    [Fact]
    public async Task The_store_refuses_to_restore_a_text_file_to_a_place_outside_a_skill_folder()
    {
        var (store, kept) = await KeptScriptAsync();
        var by = WriterRef.ForOwner(WriterRef.Session, null);
        Assert.Empty(await store.TryCommitAsync(new NoteBatch { AttachmentTrashes = { kept } }, "alice", by, DateTime.UtcNow, Ct));
        var trashed = Assert.Single(await store.ListAttachmentTrashAsync(P("/Work"), Ct));
        var restored = trashed.Attachment with { Path = P("/Work/run.py"), Revision = trashed.Attachment.Revision + 1, Changeset = null };
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.TryCommitAsync(new NoteBatch { AttachmentRestores = { (trashed, restored) } }, "alice", by, DateTime.UtcNow, Ct));
        Assert.Null(await store.GetAttachmentAsync(P("/Work/run.py"), Ct));
        Assert.Single(await store.ListAttachmentTrashAsync(P("/Work"), Ct));
    }
}
