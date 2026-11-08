// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Text;
using Kumbu.Core.Attachments;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Core.Sh;
using Kumbu.Server.Auth;
using Kumbu.Server.Tests.Api;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using static Kumbu.Server.Tests.Api.RestClient;

namespace Kumbu.Server.Tests.Dav;

/// <summary><c>PUT</c> and <c>MKCOL</c> into the store (WebDAV design §3.5, brief open point 6): notes, attachments and
/// folders through the writers every other path uses, with their limits, checks and refusals.</summary>
[Collection("app")]
public sealed class DavPutTests(KumbuAppFactory app) : IAsyncDisposable
{
    // Whole seconds, as the other timed tests: MongoDB keeps milliseconds.
    private readonly FakeTimeProvider _time = new(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));

    private WebApplicationFactory<Program>? _timed;

    private WebApplicationFactory<Program>? _raced;

    private readonly Race _race = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private WebApplicationFactory<Program> Api => app.PublicApi;

    /// <summary>A server of its own whose clock the test moves.</summary>
    private WebApplicationFactory<Program> Timed => _timed ??= app.PublicApi.WithWebHostBuilder(b => b.ConfigureServices(s =>
    {
        s.RemoveAll<TimeProvider>();
        s.AddSingleton<TimeProvider>(_time);
    }));

    /// <summary>A server whose writers' caller lets <see cref="_race"/> write in between a WebDAV write's look at the
    /// store and its write.</summary>
    private WebApplicationFactory<Program> Raced => _raced ??= app.PublicApi.WithWebHostBuilder(b => b.ConfigureServices(s =>
    {
        s.RemoveAll<ICaller>();
        s.AddScoped<ICaller>(sp => new RacingCaller(sp.GetRequiredService<RequestCaller>(), _race));
    }));

    public async ValueTask DisposeAsync()
    {
        if (_timed is not null) await _timed.DisposeAsync();
        if (_raced is not null) await _raced.DisposeAsync();
    }

    /// <summary>A write another client makes once, at the next chance <see cref="RacingCaller"/> gives it.</summary>
    private sealed class Race
    {
        public Func<Task>? Next { get; set; }
    }

    /// <summary>
    /// The request's caller, as the writers ask it. <see cref="Kumbu.Server.Dav.DavWrites"/> asks for the right first,
    /// then reads what is stored; the writer asks again before its own read and its compare-and-swap. That second ask
    /// is where <see cref="Race.Next"/> runs, and every ask after it while one is set: another write between the WebDAV
    /// write's look and the writer's.
    /// </summary>
    private sealed class RacingCaller(ICaller inner, Race race) : ICaller
    {
        private int _asked;

        public string Writer => inner.Writer;

        public WriterRef Reference => inner.Reference;

        public string WriterIn(string vault) => inner.WriterIn(vault);

        public WriterRef ReferenceIn(string vault) => inner.ReferenceIn(vault);

        public TimeSpan ChangesetIdle => inner.ChangesetIdle;

        public AccessLevel? Level(string vault) => inner.Level(vault);

        public bool CanWrite(string vault) => inner.CanWrite(vault);

        public string ReadOnlySentence(string vault) => inner.ReadOnlySentence(vault);

        public string? ReadOnlyCode(string vault) => inner.ReadOnlyCode(vault);

        public string StructuralLimit(string vault) => inner.StructuralLimit(vault);

        public ShellResult? Refuse(string command, VaultPath path)
        {
            if (++_asked >= 2 && race.Next is { } next)
            {
                race.Next = null;
                next().GetAwaiter().GetResult();
            }

            return inner.Refuse(command, path);
        }

        public ShellResult? RefuseFolder(string command, VaultPath folder) => inner.RefuseFolder(command, folder);
    }

    private static HttpContent Bytes(byte[] bytes) => new ByteArrayContent(bytes);

    private static HttpContent Text(string text) => Bytes(Encoding.UTF8.GetBytes(text));

    private static Dictionary<string, string> Headers(params (string Name, string Value)[] headers) => headers.ToDictionary(h => h.Name, h => h.Value);

    private static async Task<string> BodyAsync(HttpResponseMessage response) => await response.Content.ReadAsStringAsync(Ct);

    /// <summary>The note's text as the REST API gives it, or null if there is none.</summary>
    private static async Task<string?> NoteAsync(DavClient dav, string path)
    {
        using var response = await dav.Owner.GetAsync(dav.Owner.Url(path));
        return response.StatusCode == HttpStatusCode.NotFound ? null : (await JsonAsync(response))["content"]!.GetValue<string>();
    }

    private static async Task<HttpStatusCode> FileStatusAsync(DavClient dav, string path)
    {
        using var response = await dav.Owner.GetAsync(dav.Owner.Url(path, "files"));
        return response.StatusCode;
    }

    [Fact]
    public async Task Put_of_a_note_stores_it_with_lf_and_returns_its_etag()
    {
        var dav = await DavClient.NewAsync(Api);

        using var put = await dav.SendAsync(HttpMethod.Put, "/dav/Work/plan.md", Text("# Plan\r\n\r\nSee [[Routing]] #idea\r\n"));

        Assert.Equal(HttpStatusCode.Created, put.StatusCode);
        var note = await dav.Owner.ReadAsync("plan.md");
        Assert.Equal("# Plan\n\nSee [[Routing]] #idea\n", note["content"]!.GetValue<string>());
        Assert.Equal($"\"{note["version"]!.GetValue<string>()}\"", put.Headers.ETag?.Tag);

        // Over it: 204, and the ETag GET gives.
        using var again = await dav.SendAsync(HttpMethod.Put, "/dav/Work/plan.md", Text("# Plan\n\nTwo\n"));
        Assert.Equal(HttpStatusCode.NoContent, again.StatusCode);
        using var get = await dav.SendAsync(HttpMethod.Get, "/dav/Work/plan.md");
        Assert.Equal("# Plan\n\nTwo\n", await BodyAsync(get));
        Assert.Equal(get.Headers.ETag?.Tag, again.Headers.ETag?.Tag);
    }

    [Fact]
    public async Task Put_keeps_a_bom()
    {
        var dav = await DavClient.NewAsync(Api);
        byte[] bytes = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("# A\r\n")];

        using var put = await dav.SendAsync(HttpMethod.Put, "/dav/Work/a.md", Bytes(bytes));

        Assert.Equal(HttpStatusCode.Created, put.StatusCode);
        Assert.Equal("﻿# A\n", await NoteAsync(dav, "a.md"));
        using var get = await dav.SendAsync(HttpMethod.Get, "/dav/Work/a.md");
        Assert.Equal([0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("# A\n")], await get.Content.ReadAsByteArrayAsync(Ct));
    }

    [Fact]
    public async Task Put_of_non_utf8_markdown_is_415()
    {
        var dav = await DavClient.NewAsync(Api);

        // Latin-1 "Café".
        using var put = await dav.SendAsync(HttpMethod.Put, "/dav/Work/a.md", Bytes([0x43, 0x61, 0x66, 0xE9, 0x0A]));

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, put.StatusCode);
        Assert.Null(await NoteAsync(dav, "a.md"));
    }

    [Fact]
    public async Task Put_of_UPPER_MD_is_a_note()
    {
        var dav = await DavClient.NewAsync(Api);

        using var put = await dav.SendAsync(HttpMethod.Put, "/dav/Work/Plan.MD", Text("# Plan"));

        Assert.Equal(HttpStatusCode.Created, put.StatusCode);
        Assert.Equal("# Plan", await NoteAsync(dav, "Plan.md"));
        Assert.Equal("/dav/Work/Plan.md", put.Headers.Location?.OriginalString);
    }

    /// <summary>A name ending in <c>.MD</c>, or any other case, addresses the note ending in <c>.md</c> for every method
    /// (brief open point 6): the store keeps notes under <c>.md</c> only.</summary>
    [Fact]
    public async Task An_upper_MD_name_addresses_the_md_note_for_every_method()
    {
        var dav = await DavClient.NewAsync(Api);
        await dav.Owner.CreateAsync("Plan.md", "# Old");

        using (var put = await dav.SendAsync(HttpMethod.Put, "/dav/Work/Plan.MD", Text("# Plan")))
        {
            Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
            Assert.Equal("/dav/Work/Plan.md", put.Headers.Location?.OriginalString);
        }

        Assert.Equal("# Plan", await NoteAsync(dav, "Plan.md"));
        var version = (await dav.Owner.ReadAsync("Plan.md"))["version"]!.GetValue<string>();

        using (var get = await dav.SendAsync(HttpMethod.Get, "/dav/Work/Plan.MD"))
        {
            Assert.Equal(HttpStatusCode.OK, get.StatusCode);
            Assert.Equal("# Plan", await BodyAsync(get));
            Assert.Equal($"\"{version}\"", get.Headers.ETag?.Tag);
        }

        using (var head = await dav.SendAsync(HttpMethod.Head, "/dav/Work/Plan.mD")) Assert.Equal(HttpStatusCode.OK, head.StatusCode);

        // Depth 0 answers for the name the client asked about.
        var props = await DavAssert.MultistatusAsync(await dav.PropfindAsync("/dav/Work/Plan.MD", depth: "0"));
        Assert.Equal(["/dav/Work/Plan.MD"], DavAssert.Hrefs(props));
        Assert.Equal($"\"{version}\"", DavAssert.Prop(DavAssert.Response(props, "/dav/Work/Plan.MD"), "getetag")?.Value);

        using (var patch = await dav.SendAsync(DavClient.Proppatch, "/dav/Work/Plan.MD", DavClient.Body(
            "<?xml version=\"1.0\"?><D:propertyupdate xmlns:D=\"DAV:\"><D:set><D:prop><D:x/></D:prop></D:set></D:propertyupdate>")))
        {
            Assert.Equal(HttpStatusCode.MultiStatus, patch.StatusCode);
        }

        using (var locked = await dav.SendAsync(DavClient.Lock, "/dav/Work/Plan.MD", DavClient.Body(
            "<?xml version=\"1.0\"?><D:lockinfo xmlns:D=\"DAV:\"><D:lockscope><D:exclusive/></D:lockscope><D:locktype><D:write/></D:locktype></D:lockinfo>")))
        {
            Assert.Equal(HttpStatusCode.OK, locked.StatusCode);
            Assert.Contains("/dav/Work/Plan.md", await BodyAsync(locked), StringComparison.Ordinal);
        }

        // An exact name the store has is never taken for another.
        using var listing = await dav.PropfindAsync("/dav/Work/", depth: "1");
        Assert.DoesNotContain("/dav/Work/Plan.MD", DavAssert.Hrefs(await DavAssert.MultistatusAsync(listing)));
    }

    /// <summary>A folder's name ending in <c>.MD</c> is that folder: only a name the share sees nothing under is taken
    /// for the note.</summary>
    [Fact]
    public async Task An_upper_MD_name_the_store_has_is_its_own()
    {
        var dav = await DavClient.NewAsync(Api);
        await dav.Owner.CreateAsync("Notes.MD/inside.md", "# Inside");

        var props = await DavAssert.MultistatusAsync(await dav.PropfindAsync("/dav/Work/Notes.MD", depth: "1"));

        Assert.True(DavAssert.IsCollection(DavAssert.Response(props, "/dav/Work/Notes.MD/")));
        Assert.Contains("/dav/Work/Notes.MD/inside.md", DavAssert.Hrefs(props));
    }

    [Fact]
    public async Task An_upper_MD_name_over_a_folder_is_405()
    {
        var dav = await DavClient.NewAsync(Api);
        await dav.Owner.CreateAsync("Plan.md/inside.md", "# Inside");

        using var put = await dav.SendAsync(HttpMethod.Put, "/dav/Work/Plan.MD", Text("# Plan"));

        Assert.Equal(HttpStatusCode.MethodNotAllowed, put.StatusCode);
        Assert.Equal("# Inside", await NoteAsync(dav, "Plan.md/inside.md"));
    }

    [Fact]
    public async Task A_lost_race_without_if_match_is_retried_once_and_stored()
    {
        var dav = (await DavClient.NewAsync(Api)).On(Raced);
        var owner = dav.Owner.On(Raced);
        var first = await owner.CreateAsync("plan.md", "one");
        _race.Next = async () =>
        {
            using var other = await owner.SendAsync(HttpMethod.Put, owner.Url("plan.md"), new { content = "two", version = first });
            Assert.Equal(HttpStatusCode.OK, other.StatusCode);
        };

        using var put = await dav.SendAsync(HttpMethod.Put, "/dav/Work/plan.md", Text("three"));

        Assert.Null(_race.Next);
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
        var note = await owner.ReadAsync("plan.md");
        Assert.Equal("three", note["content"]!.GetValue<string>());
        Assert.Equal($"\"{note["version"]!.GetValue<string>()}\"", put.Headers.ETag?.Tag);
    }

    [Fact]
    public async Task A_lost_race_on_a_new_note_overwrites_what_came_in_between()
    {
        var dav = (await DavClient.NewAsync(Api)).On(Raced);
        var owner = dav.Owner.On(Raced);
        _race.Next = () => owner.CreateAsync("plan.md", "theirs");

        using var put = await dav.SendAsync(HttpMethod.Put, "/dav/Work/plan.md", Text("mine"));

        Assert.Null(_race.Next);
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
        Assert.Equal("mine", (await owner.ReadAsync("plan.md"))["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_lost_race_with_if_match_is_412()
    {
        var dav = (await DavClient.NewAsync(Api)).On(Raced);
        var owner = dav.Owner.On(Raced);
        var first = await owner.CreateAsync("plan.md", "one");
        _race.Next = async () =>
        {
            using var other = await owner.SendAsync(HttpMethod.Put, owner.Url("plan.md"), new { content = "two", version = first });
            Assert.Equal(HttpStatusCode.OK, other.StatusCode);
        };

        using var put = await dav.SendAsync(HttpMethod.Put, "/dav/Work/plan.md", Text("three"), Headers(("If-Match", $"\"{first}\"")));

        Assert.Null(_race.Next);
        Assert.Equal(HttpStatusCode.PreconditionFailed, put.StatusCode);
        Assert.Equal("two", (await owner.ReadAsync("plan.md"))["content"]!.GetValue<string>());
    }

    /// <summary>A note that arrives at a free destination between a <c>COPY</c>'s look and its write is not trashed to
    /// make room: the destination the request found free isn't any more (412), and the note stays.</summary>
    [Fact]
    public async Task A_copy_that_loses_its_free_destination_is_412_and_trashes_nothing()
    {
        var dav = (await DavClient.NewAsync(Api)).On(Raced);
        var owner = dav.Owner.On(Raced);
        await owner.CreateAsync("plan.md", "mine");
        _race.Next = () => owner.CreateAsync("copy.md", "theirs");

        using var copy = await dav.SendAsync(DavClient.Copy, "/dav/Work/plan.md", headers: Headers(("Destination", "https://localhost/dav/Work/copy.md")));

        Assert.Null(_race.Next);
        Assert.Equal(HttpStatusCode.PreconditionFailed, copy.StatusCode);
        Assert.Equal("theirs", (await owner.ReadAsync("copy.md"))["content"]!.GetValue<string>());
        var store = new Kumbu.Server.Vaults.EncryptedNoteStore(
            Api.Services.GetRequiredService<Kumbu.Server.Vaults.VaultCatalog>(), new Kumbu.Server.Vaults.UserCredential(owner.User.Id, owner.UserKey));
        Assert.True(VaultPath.TryParse("/Work", out var vault));
        Assert.Empty(await store.ListTrashAsync(vault, Ct));
    }

    [Fact]
    public async Task A_lost_race_of_an_attachment_is_retried_once_and_stored()
    {
        var dav = (await DavClient.NewAsync(Api)).On(Raced);
        var owner = dav.Owner.On(Raced);
        var first = await owner.CreateFileAsync("shot.png", FileApiTests.Png(2, 2));
        _race.Next = async () =>
        {
            using var other = await owner.PutFileAsync("shot.png", FileApiTests.Png(3, 3), first);
            Assert.Equal(HttpStatusCode.OK, other.StatusCode);
        };
        var mine = FileApiTests.Png(4, 4);

        using var put = await dav.SendAsync(HttpMethod.Put, "/dav/Work/shot.png", Bytes(mine));

        Assert.Null(_race.Next);
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
        using var get = await dav.SendAsync(HttpMethod.Get, "/dav/Work/shot.png");
        Assert.Equal(mine, await get.Content.ReadAsByteArrayAsync(Ct));
    }

    /// <summary>Only a lost compare-and-swap is tried again: a refusal stays one, even when the file changed meanwhile.
    /// The writer is asked once.</summary>
    [Fact]
    public async Task A_refused_write_is_not_retried()
    {
        var dav = (await DavClient.NewAsync(Api)).On(Raced);
        var owner = dav.Owner.On(Raced);
        var first = await owner.CreateFileAsync("shot.png", FileApiTests.Png(2, 2));
        var asked = 0;
        Func<Task>? count = null;
        count = async () =>
        {
            if (++asked == 1)
            {
                using var other = await owner.PutFileAsync("shot.png", FileApiTests.Png(3, 3), first);
                Assert.Equal(HttpStatusCode.OK, other.StatusCode);
            }

            _race.Next = count;
        };
        _race.Next = count;

        using var put = await dav.SendAsync(HttpMethod.Put, "/dav/Work/shot.png", Text("not a picture"));

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, put.StatusCode);
        Assert.Contains("isn't a PNG image", await BodyAsync(put), StringComparison.Ordinal);
        Assert.Equal(1, asked);
    }

    [Fact]
    public async Task An_empty_put_over_a_stored_attachment_writes_nothing()
    {
        var dav = await DavClient.NewAsync(Api);
        var bytes = FileApiTests.Png(2, 2);
        var version = await dav.Owner.CreateFileAsync("shot.png", bytes);

        using var put = await dav.SendAsync(HttpMethod.Put, "/dav/Work/shot.png", Bytes([]));

        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
        Assert.Equal($"\"{version}\"", put.Headers.ETag?.Tag);
        using var get = await dav.SendAsync(HttpMethod.Get, "/dav/Work/shot.png");
        Assert.Equal(bytes, await get.Content.ReadAsByteArrayAsync(Ct));
        using var history = await dav.Owner.GetAsync(dav.Owner.Url("shot.png", "history"));
        Assert.Single((await JsonAsync(history)).AsArray());
    }

    [Fact]
    public async Task The_413_messages_say_the_limits()
    {
        var dav = await DavClient.NewAsync(Api);

        using var note = await dav.SendAsync(HttpMethod.Put, "/dav/Work/big.md", Bytes(new byte[NoteWriter.MaxBytes + 1]));
        using var file = await dav.SendAsync(HttpMethod.Put, "/dav/Work/big.pdf", Bytes(new byte[AttachmentFormat.MaxBytes + 1]));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, note.StatusCode);
        Assert.Equal("A note can have at most 1 MiB.", await BodyAsync(note));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, file.StatusCode);
        Assert.Equal("A file can have at most 15 MB.", await BodyAsync(file));
    }

    [Fact]
    public async Task Put_over_1_MiB_is_413()
    {
        var dav = await DavClient.NewAsync(Api);

        using var put = await dav.SendAsync(HttpMethod.Put, "/dav/Work/big.md", Bytes(Encoding.UTF8.GetBytes(new string('a', 1_048_577))));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, put.StatusCode);
        Assert.Null(await NoteAsync(dav, "big.md"));

        // Exactly the limit is a note.
        using var limit = await dav.SendAsync(HttpMethod.Put, "/dav/Work/limit.md", Bytes(Encoding.UTF8.GetBytes(new string('a', 1_048_576))));
        Assert.Equal(HttpStatusCode.Created, limit.StatusCode);
    }

    [Fact]
    public async Task Chunked_put_over_the_limit_is_413_and_stores_nothing()
    {
        var dav = await DavClient.NewAsync(Api);

        using var put = await dav.SendAsync(HttpMethod.Put, "/dav/Work/big.md", new DavClient.Unsized(Encoding.UTF8.GetBytes(new string('a', 1_048_577))));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, put.StatusCode);
        Assert.Null(await NoteAsync(dav, "big.md"));
    }

    [Fact]
    public async Task Put_of_a_png_stores_an_attachment_and_history_names_the_share()
    {
        var dav = await DavClient.NewAsync(Api);
        var bytes = FileApiTests.Png(4, 3);

        using var put = await dav.SendAsync(HttpMethod.Put, "/dav/Work/Shot.PNG", Bytes(bytes));

        Assert.Equal(HttpStatusCode.Created, put.StatusCode);
        using var file = await dav.Owner.GetAsync(dav.Owner.Url("Shot.PNG", "files"));
        Assert.Equal(HttpStatusCode.OK, file.StatusCode);
        Assert.Equal(bytes, await file.Content.ReadAsByteArrayAsync(Ct));
        Assert.Equal($"\"{Kumbu.Core.Notes.VersionToken.Of(bytes)}\"", put.Headers.ETag?.Tag);

        using var history = await dav.Owner.GetAsync(dav.Owner.Url("Shot.PNG", "history"));
        var entry = Assert.Single((await JsonAsync(history)).AsArray());
        Assert.Equal("Work PC (dav)", entry!["writer"]!.GetValue<string>());
    }

    [Fact]
    public async Task Empty_put_of_a_png_is_parked_and_the_next_put_replaces_it()
    {
        var dav = await DavClient.NewAsync(Api);
        var other = await DavClient.NewAsync(Api, dav.Owner, label: "Laptop");

        using (var empty = await dav.SendAsync(HttpMethod.Put, "/dav/Work/shot.png", Bytes([]))) Assert.Equal(HttpStatusCode.Created, empty.StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, await FileStatusAsync(dav, "shot.png"));
        Assert.Contains("/dav/Work/shot.png", DavAssert.Hrefs(await DavAssert.MultistatusAsync(await dav.PropfindAsync("/dav/Work/"))));
        Assert.DoesNotContain("/dav/Work/shot.png", DavAssert.Hrefs(await DavAssert.MultistatusAsync(await other.PropfindAsync("/dav/Work/"))));

        var bytes = FileApiTests.Png(2, 2);
        using var put = await dav.SendAsync(HttpMethod.Put, "/dav/Work/shot.png", Bytes(bytes));
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
        Assert.Equal(HttpStatusCode.OK, await FileStatusAsync(dav, "shot.png"));
        using var get = await dav.SendAsync(HttpMethod.Get, "/dav/Work/shot.png");
        Assert.Equal("image/png", get.Content.Headers.ContentType?.MediaType);
        Assert.Equal(bytes, await get.Content.ReadAsByteArrayAsync(Ct));

        // The placeholder went: with the file removed from the store, nothing is left at its name.
        using (var removed = await dav.Owner.SendAsync(HttpMethod.Delete, dav.Owner.Url("shot.png", "files"))) Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        Assert.DoesNotContain("/dav/Work/shot.png", DavAssert.Hrefs(await DavAssert.MultistatusAsync(await dav.PropfindAsync("/dav/Work/"))));
    }

    [Fact]
    public async Task Empty_put_of_md_is_an_empty_note()
    {
        var dav = await DavClient.NewAsync(Api);

        using var put = await dav.SendAsync(HttpMethod.Put, "/dav/Work/empty.md", Bytes([]));

        Assert.Equal(HttpStatusCode.Created, put.StatusCode);
        Assert.Equal("", await NoteAsync(dav, "empty.md"));
    }

    [Fact]
    public async Task Put_of_xlsx_is_415_with_the_message()
    {
        var dav = await DavClient.NewAsync(Api);

        using var put = await dav.SendAsync(HttpMethod.Put, "/dav/Work/sheet.xlsx", Bytes([0x50, 0x4B, 0x03, 0x04]));

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, put.StatusCode);
        Assert.Equal("text/plain", put.Content.Headers.ContentType?.MediaType);
        Assert.Equal(AttachmentFormat.UnsupportedMessage, await BodyAsync(put));
        using var get = await dav.SendAsync(HttpMethod.Get, "/dav/Work/sheet.xlsx");
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
    }

    [Fact]
    public async Task Put_of_a_png_name_with_text_bytes_is_415()
    {
        var dav = await DavClient.NewAsync(Api);

        using var put = await dav.SendAsync(HttpMethod.Put, "/dav/Work/shot.png", Text("not a picture at all"));

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, put.StatusCode);
        Assert.Contains("isn't a PNG image", await BodyAsync(put), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NotFound, await FileStatusAsync(dav, "shot.png"));
    }

    [Fact]
    public async Task Put_with_a_stale_if_match_is_412()
    {
        var dav = await DavClient.NewAsync(Api);
        var first = await dav.Owner.CreateAsync("plan.md", "one");
        using (var moved = await dav.Owner.SendAsync(HttpMethod.Put, dav.Owner.Url("plan.md"), new { content = "two", version = first })) Assert.Equal(HttpStatusCode.OK, moved.StatusCode);

        using var stale = await dav.SendAsync(HttpMethod.Put, "/dav/Work/plan.md", Text("three"), Headers(("If-Match", $"\"{first}\"")));
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        Assert.Equal("two", await NoteAsync(dav, "plan.md"));

        // On a note that isn't there, too.
        using var missing = await dav.SendAsync(HttpMethod.Put, "/dav/Work/new.md", Text("x"), Headers(("If-Match", $"\"{first}\"")));
        Assert.Equal(HttpStatusCode.PreconditionFailed, missing.StatusCode);
        Assert.Null(await NoteAsync(dav, "new.md"));

        var current = (await dav.Owner.ReadAsync("plan.md"))["version"]!.GetValue<string>();
        using var fresh = await dav.SendAsync(HttpMethod.Put, "/dav/Work/plan.md", Text("three"), Headers(("If-Match", $"\"{current}\"")));
        Assert.Equal(HttpStatusCode.NoContent, fresh.StatusCode);
        Assert.Equal("three", await NoteAsync(dav, "plan.md"));
    }

    [Fact]
    public async Task If_none_match_star_on_an_existing_note_is_412()
    {
        var dav = await DavClient.NewAsync(Api);
        await dav.Owner.CreateAsync("plan.md", "one");

        using var put = await dav.SendAsync(HttpMethod.Put, "/dav/Work/plan.md", Text("two"), Headers(("If-None-Match", "*")));
        Assert.Equal(HttpStatusCode.PreconditionFailed, put.StatusCode);
        Assert.Equal("one", await NoteAsync(dav, "plan.md"));

        using var create = await dav.SendAsync(HttpMethod.Put, "/dav/Work/new.md", Text("new"), Headers(("If-None-Match", "*")));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
    }

    [Fact]
    public async Task Put_into_a_missing_folder_is_409()
    {
        var dav = await DavClient.NewAsync(Api);

        using var note = await dav.SendAsync(HttpMethod.Put, "/dav/Work/nowhere/plan.md", Text("# Plan"));
        using var file = await dav.SendAsync(HttpMethod.Put, "/dav/Work/nowhere/shot.png", Bytes(FileApiTests.Png(2, 2)));

        Assert.Equal(HttpStatusCode.Conflict, note.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, file.StatusCode);
        Assert.Null(await NoteAsync(dav, "nowhere/plan.md"));
    }

    [Fact]
    public async Task A_read_share_gets_403()
    {
        var dav = await DavClient.NewAsync(Api, right: "read");
        await dav.Owner.CreateAsync("plan.md", "one");

        using var note = await dav.SendAsync(HttpMethod.Put, "/dav/Work/plan.md", Text("two"));
        using var file = await dav.SendAsync(HttpMethod.Put, "/dav/Work/shot.png", Bytes(FileApiTests.Png(2, 2)));
        using var folder = await dav.SendAsync(DavClient.Mkcol, "/dav/Work/Drafts");

        Assert.Equal(HttpStatusCode.Forbidden, note.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, file.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, folder.StatusCode);
        Assert.Equal("one", await NoteAsync(dav, "plan.md"));
    }

    [Fact]
    public async Task Board_md_needs_maintain()
    {
        var writer = await DavClient.NewAsync(Api);
        var maintainer = await DavClient.NewAsync(Api, writer.Owner, label: "Desk", right: "maintain");
        await writer.Owner.CreateAsync("Board/Board.md", "# Board\n");

        using var refused = await writer.SendAsync(HttpMethod.Put, "/dav/Work/Board/Board.md", Text("# Mine\n"));
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal("# Board\n", await NoteAsync(writer, "Board/Board.md"));
        // The right comes first, before the body is read or judged.
        using var large = await writer.SendAsync(HttpMethod.Put, "/dav/Work/Board/Board.md", Bytes(new byte[NoteWriter.MaxBytes + 1]));
        Assert.Equal(HttpStatusCode.Forbidden, large.StatusCode);

        using var allowed = await maintainer.SendAsync(HttpMethod.Put, "/dav/Work/Board/Board.md", Text("# Board\n\nMore\n"));
        Assert.Equal(HttpStatusCode.NoContent, allowed.StatusCode);
    }

    [Fact]
    public async Task Put_on_a_vault_folder_or_root_is_403()
    {
        var dav = await DavClient.NewAsync(Api);

        using var vault = await dav.SendAsync(HttpMethod.Put, "/dav/Work", Text("x"));
        using var root = await dav.SendAsync(HttpMethod.Put, "/dav/", Text("x"));

        Assert.Equal(HttpStatusCode.Forbidden, vault.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, root.StatusCode);
    }

    [Fact]
    public async Task Mkcol_makes_a_folder()
    {
        var dav = await DavClient.NewAsync(Api);

        using var made = await dav.SendAsync(DavClient.Mkcol, "/dav/Work/Drafts");
        Assert.Equal(HttpStatusCode.Created, made.StatusCode);

        using var tree = await dav.Owner.GetAsync($"/api/vaults/Work/tree");
        Assert.Contains("Drafts", await BodyAsync(tree), StringComparison.Ordinal);
        var listing = await DavAssert.MultistatusAsync(await dav.PropfindAsync("/dav/Work/"));
        Assert.True(DavAssert.IsCollection(DavAssert.Response(listing, "/dav/Work/Drafts/")));

        // And then something into it.
        using var inside = await dav.SendAsync(HttpMethod.Put, "/dav/Work/Drafts/a.md", Text("# A"));
        Assert.Equal(HttpStatusCode.Created, inside.StatusCode);
    }

    [Fact]
    public async Task Mkcol_on_an_existing_path_is_405()
    {
        var dav = await DavClient.NewAsync(Api);
        await dav.Owner.CreateAsync("Drafts/a.md", "# A");

        using var folder = await dav.SendAsync(DavClient.Mkcol, "/dav/Work/Drafts");
        using var note = await dav.SendAsync(DavClient.Mkcol, "/dav/Work/Drafts/a.md");

        Assert.Equal(HttpStatusCode.MethodNotAllowed, folder.StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, note.StatusCode);
    }

    [Fact]
    public async Task Mkcol_on_an_existing_empty_folder_is_405()
    {
        var dav = await DavClient.NewAsync(Api);
        using (var made = await dav.SendAsync(DavClient.Mkcol, "/dav/Work/Drafts")) Assert.Equal(HttpStatusCode.Created, made.StatusCode);

        using var again = await dav.SendAsync(DavClient.Mkcol, "/dav/Work/Drafts");

        Assert.Equal(HttpStatusCode.MethodNotAllowed, again.StatusCode);
    }

    [Fact]
    public async Task Mkcol_with_a_missing_parent_is_409()
    {
        var dav = await DavClient.NewAsync(Api);

        using var made = await dav.SendAsync(DavClient.Mkcol, "/dav/Work/a/b");

        Assert.Equal(HttpStatusCode.Conflict, made.StatusCode);
        using var get = await dav.PropfindAsync("/dav/Work/a", depth: "0");
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
    }

    [Fact]
    public async Task Mkcol_with_a_body_is_415()
    {
        var dav = await DavClient.NewAsync(Api);

        using var made = await dav.SendAsync(DavClient.Mkcol, "/dav/Work/Drafts", Text("<x/>"));

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, made.StatusCode);
        using var get = await dav.PropfindAsync("/dav/Work/Drafts", depth: "0");
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
    }

    [Fact]
    public async Task Mkcol_on_a_vault_is_403()
    {
        var dav = await DavClient.NewAsync(Api);

        using var vault = await dav.SendAsync(DavClient.Mkcol, "/dav/Work/");
        using var other = await dav.SendAsync(DavClient.Mkcol, "/dav/Personal/");
        using var root = await dav.SendAsync(DavClient.Mkcol, "/dav/");

        Assert.Equal(HttpStatusCode.Forbidden, vault.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, other.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, root.StatusCode);
    }

    /// <summary>A share is a person at an editor (WebDAV design §3.2): its changeset closes after two idle minutes, not
    /// an agent's ten. So a write three minutes after another is a version of its own, as for a session
    /// (<see cref="ChangesetIdleTests"/>).</summary>
    [Fact]
    public async Task A_share_write_uses_the_human_changeset()
    {
        var dav = (await DavClient.NewAsync(Api)).On(Timed);
        var owner = dav.Owner.On(Timed);

        using (var one = await dav.SendAsync(HttpMethod.Put, "/dav/Work/plan.md", Text("one\n"))) Assert.Equal(HttpStatusCode.Created, one.StatusCode);
        _time.Advance(TimeSpan.FromMinutes(15));
        using (var two = await dav.SendAsync(HttpMethod.Put, "/dav/Work/plan.md", Text("two\n"))) Assert.Equal(HttpStatusCode.NoContent, two.StatusCode);
        _time.Advance(TimeSpan.FromMinutes(3));
        using (var three = await dav.SendAsync(HttpMethod.Put, "/dav/Work/plan.md", Text("three\n"))) Assert.Equal(HttpStatusCode.NoContent, three.StatusCode);

        using var history = await owner.GetAsync(owner.Url("plan.md", "history"));
        Assert.Equal(HttpStatusCode.OK, history.StatusCode);
        var entries = (await JsonAsync(history)).AsArray();
        Assert.Equal(3, entries.Count);
        Assert.Equal(3, entries.Select(e => e!["changeset"]?.GetValue<string>()).Distinct().Count());
    }
}
