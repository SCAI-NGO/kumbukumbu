// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Kumbu.Server.Accounts;
using Kumbu.Server.Api;
using Kumbu.Server.Vaults;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Kumbu.Server.Tests.Api;

/// <summary><c>/api/files</c>: attachments' bytes in and out, and attachments in the tree, history, trash, moves,
/// activity and checkpoints of the REST API.</summary>
[Collection("app")]
public sealed class FileApiTests(KumbuAppFactory app)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private WebApplicationFactory<Program> Api => app.PublicApi;

    private Task<RestClient> UserAsync() => RestClient.NewAsync(Api);

    private static Task<JsonNode> JsonAsync(HttpResponseMessage response) => RestClient.JsonAsync(response);

    /// <summary>A PNG of signature, IHDR and IEND: its header says <paramref name="width"/> × <paramref name="height"/>.</summary>
    internal static byte[] Png(int width, int height)
    {
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0), width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), height);
        ihdr[8] = 8;
        ihdr[9] = 6;
        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        Chunk(png, "IHDR"u8, ihdr);
        Chunk(png, "IEND"u8, []);
        return png.ToArray();

        static void Chunk(Stream s, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
        {
            var length = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
            s.Write(length);
            s.Write(type);
            s.Write(data);
            s.Write(new byte[4]);
        }
    }

    private static readonly byte[] Pdf = "%PDF-1.4\n1 0 obj << >> endobj\ntrailer << >>\n%%EOF\n"u8.ToArray();

    private async Task<long> BlobCountAsync(RestClient user)
    {
        var vault = (await Api.Services.GetRequiredService<VaultCatalog>().OpenForUserAsync(user.User.Id, user.UserKey, Ct)).Single(v => v.Name == user.Vault);
        // A sweep with the grace period long over: only blobs something references are left.
        await new BlobSweep(Api.Services.GetRequiredService<VaultCatalog>(), NullLogger.Instance).RunAsync(vault, DateTime.UtcNow.AddHours(1), Ct);
        return await Api.Services.GetRequiredService<IMongoDatabase>().GetCollection<BsonDocument>("blobs")
            .CountDocumentsAsync(Builders<BsonDocument>.Filter.Eq("vault", vault.Id), cancellationToken: Ct);
    }

    [Fact]
    public async Task A_png_round_trips_with_its_media_type_nosniff_and_no_store()
    {
        var user = await UserAsync();
        var bytes = Png(640, 480);

        // The client's Content-Type says nothing about what is stored.
        using var put = await user.SendContentAsync(
            HttpMethod.Put, user.Url("shots/screen.png", "files"), new ByteArrayContent(bytes) { Headers = { ContentType = new MediaTypeHeaderValue("text/html") } });
        Assert.True(put.StatusCode == HttpStatusCode.Created, await put.Content.ReadAsStringAsync(Ct));
        var state = await JsonAsync(put);
        Assert.Equal("/Arbeit/shots/screen.png", state["path"]!.GetValue<string>());
        Assert.Equal("image/png", state["mediaType"]!.GetValue<string>());
        Assert.Equal(bytes.Length, state["size"]!.GetValue<long>());
        Assert.Equal(640, state["width"]!.GetValue<int>());
        Assert.Equal(480, state["height"]!.GetValue<int>());
        Assert.Equal(Kumbu.Core.Notes.VersionToken.Of(bytes), state["version"]!.GetValue<string>());
        Assert.Equal(user.Username, state["writtenBy"]!.GetValue<string>());

        using var get = await user.GetAsync(user.Url("shots/screen.png", "files"));

        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal(bytes, await get.Content.ReadAsByteArrayAsync(Ct));
        Assert.Equal("image/png", get.Content.Headers.ContentType!.MediaType);
        Assert.Equal("nosniff", get.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.True(get.Headers.CacheControl!.NoStore);
        Assert.Equal("inline", get.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Equal($"\"{state["version"]!.GetValue<string>()}\"", get.Headers.ETag!.Tag);
        // Opened in a tab of its own, the file runs nothing at the app's origin.
        Assert.Equal("sandbox; default-src 'none'; frame-ancestors 'none'", get.Headers.GetValues("Content-Security-Policy").Single());

        // A PDF too, and an API key with read access reads it.
        await user.CreateFileAsync("report.pdf", Pdf);
        var key = await user.KeyAsync("reader", KeyRight.Read);
        using var pdf = await user.GetAsync(user.Url("report.pdf", "files"), bearer: key);
        Assert.Equal("application/pdf", pdf.Content.Headers.ContentType!.MediaType);
        Assert.Equal(Pdf, await pdf.Content.ReadAsByteArrayAsync(Ct));
    }

    [Fact]
    public async Task A_jpeg_with_its_frame_header_past_300_KB_of_metadata_uploads()
    {
        var user = await UserAsync();
        var jpeg = new List<byte> { 0xFF, 0xD8 };
        for (var i = 0; i < 5; i++) jpeg.AddRange([0xFF, 0xE2, 0xFF, 0xFF, .. new byte[0xFFFD]]);
        jpeg.AddRange([0xFF, 0xC0, 0, 11, 8, 0x0B, 0xB8, 0x0F, 0xA0, 1, 1, 0x11, 0, 0xFF, 0xD9]);

        using var put = await user.PutFileAsync("photo.jpg", [.. jpeg]);

        Assert.True(put.StatusCode == HttpStatusCode.Created, await put.Content.ReadAsStringAsync(Ct));
        var state = await JsonAsync(put);
        Assert.Equal((4000, 3000), (state["width"]!.GetValue<int>(), state["height"]!.GetValue<int>()));
    }

    [Fact]
    public async Task A_png_named_pdf_is_a_400_naming_the_mismatch()
    {
        var user = await UserAsync();

        using var response = await user.PutFileAsync("fake.pdf", Png(10, 10));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("fake.pdf isn't a PDF document", await RestClient.ErrorAsync(response), StringComparison.Ordinal);
        using var get = await user.GetAsync(user.Url("fake.pdf", "files"));
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
    }

    [Fact]
    public async Task A_16_MB_file_is_a_413_saying_15_MB()
    {
        var user = await UserAsync();
        var bytes = new byte[16 * 1024 * 1024];
        Pdf.CopyTo(bytes, 0);

        using var response = await user.PutFileAsync("big.pdf", bytes);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Contains("15 MB", await RestClient.ErrorAsync(response), StringComparison.Ordinal);
        Assert.Equal(0, await BlobCountAsync(user));
    }

    [Fact]
    public async Task A_docx_is_a_400_naming_the_formats()
    {
        var user = await UserAsync();

        using var docx = await user.PutFileAsync("letter.docx", "PK\u0003\u0004"u8.ToArray());
        using var note = await user.PutFileAsync("plan.md", "# Plan\n"u8.ToArray());

        Assert.Equal(HttpStatusCode.BadRequest, docx.StatusCode);
        Assert.Contains("PDFs and images (PNG, JPEG, GIF, WebP)", await RestClient.ErrorAsync(docx), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.BadRequest, note.StatusCode);
        Assert.Contains("/api/notes", await RestClient.ErrorAsync(note), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_image_over_the_pixel_limit_is_a_400_and_stores_nothing()
    {
        var user = await UserAsync();

        using var response = await user.PutFileAsync("bomb.png", Png(100_000, 100_000));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("16,384 px", await RestClient.ErrorAsync(response), StringComparison.Ordinal);
        Assert.Equal(0, await BlobCountAsync(user));
    }

    [Fact]
    public async Task A_read_only_key_gets_403_and_nothing_is_stored()
    {
        var user = await UserAsync();
        var key = await user.KeyAsync("reader", KeyRight.Read);

        using var response = await user.PutFileAsync("shot.png", Png(4, 4), bearer: key);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("can only read vault Arbeit", await RestClient.ErrorAsync(response), StringComparison.Ordinal);
        Assert.Equal(0, await BlobCountAsync(user));

        await user.CreateFileAsync("shot.png", Png(4, 4));
        using var delete = await user.SendAsync(HttpMethod.Delete, user.Url("shot.png", "files"), bearer: key);
        Assert.Equal(HttpStatusCode.Forbidden, delete.StatusCode);
    }

    [Fact]
    public async Task A_stale_version_is_a_409_with_the_current_state_and_no_bytes()
    {
        var user = await UserAsync();
        var first = await user.CreateFileAsync("shot.png", Png(4, 4));
        using var second = await user.PutFileAsync("shot.png", Png(8, 8), first);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var now = (await JsonAsync(second))["version"]!.GetValue<string>();

        using var stale = await user.PutFileAsync("shot.png", Png(16, 16), first);
        using var again = await user.PutFileAsync("shot.png", Png(16, 16));
        using var staleDelete = await user.SendAsync(HttpMethod.Delete, user.Url("shot.png", "files") + $"?version={first}");

        foreach (var response in new[] { stale, again, staleDelete })
        {
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Equal("application/json", response.Content.Headers.ContentType!.MediaType);
            var body = (await JsonAsync(response)).AsObject();
            Assert.Equal(["error", "current"], body.Select(p => p.Key));
            var current = body["current"]!.AsObject();
            Assert.Equal(["path", "mediaType", "size", "width", "height", "version", "writtenAt", "writtenBy"], current.Select(p => p.Key));
            Assert.Equal(now, current["version"]!.GetValue<string>());
            Assert.Equal(8, current["width"]!.GetValue<int>());
        }

        using var missing = await user.PutFileAsync("other.png", Png(4, 4), first);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    /// <summary>Writes the first half of the body, then fails: the connection is cut mid-upload.</summary>
    private sealed class CutOffContent(byte[] bytes) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context)
        {
            await stream.WriteAsync(bytes.AsMemory(0, bytes.Length / 2));
            await stream.FlushAsync();
            throw new IOException("The connection was cut.");
        }

        protected override bool TryComputeLength(out long length)
        {
            length = bytes.Length;
            return true;
        }
    }

    [Fact]
    public async Task A_body_cut_off_midway_writes_nothing()
    {
        var user = await UserAsync();
        var bytes = new byte[200_000];
        Pdf.CopyTo(bytes, 0);

        try
        {
            using var response = await user.SendContentAsync(HttpMethod.Put, user.Url("cut.pdf", "files"), new CutOffContent(bytes));
            Assert.NotEqual(HttpStatusCode.Created, response.StatusCode);
        }
        catch (Exception e) when (e is IOException or HttpRequestException or OperationCanceledException)
        {
            // The client sees its own failure; what counts is what the server kept.
        }

        using var get = await user.GetAsync(user.Url("cut.pdf", "files"));
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
        Assert.Equal(0, await BlobCountAsync(user));
    }

    /// <summary>A body shorter than its Content-Length, ending cleanly: what a proxy may pass on from a cut connection.</summary>
    private sealed class ShortContent(byte[] bytes, long claimed) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) => stream.WriteAsync(bytes).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = claimed;
            return true;
        }
    }

    [Fact]
    public async Task A_body_shorter_than_its_content_length_writes_nothing()
    {
        var user = await UserAsync();

        try
        {
            using var response = await user.SendContentAsync(HttpMethod.Put, user.Url("short.pdf", "files"), new ShortContent(Pdf, Pdf.Length * 2));
            Assert.True(response.StatusCode == HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync(Ct));
        }
        catch (Exception e) when (e is IOException or HttpRequestException or InvalidOperationException)
        {
            // The client's own transport may refuse the mismatch first.
        }

        using var get = await user.GetAsync(user.Url("short.pdf", "files"));
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
        Assert.Equal(0, await BlobCountAsync(user));
    }

    [Fact]
    public async Task An_nfd_name_and_its_nfc_spelling_are_one_file()
    {
        var user = await UserAsync();
        var nfd = "Bespréchung.png";
        var nfc = nfd.Normalize(NormalizationForm.FormC);
        Assert.NotEqual(nfd, nfc);

        using var created = await user.PutFileAsync(nfd, Png(4, 4));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal($"/Arbeit/{nfc}", (await JsonAsync(created))["path"]!.GetValue<string>());

        using var read = await user.GetAsync(user.Url(nfc, "files"));
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        using var twice = await user.PutFileAsync(nfc, Png(8, 8));
        Assert.Equal(HttpStatusCode.Conflict, twice.StatusCode);

        using var tree = await user.GetAsync($"/api/vaults/{user.Vault}/tree");
        var files = (await JsonAsync(tree))["root"]!["notes"]!.AsArray();
        Assert.Equal([$"/Arbeit/{nfc}"], files.Select(f => f!["path"]!.GetValue<string>()));
    }

    [Fact]
    public async Task Download_sets_a_safe_content_disposition()
    {
        var user = await UserAsync();
        const string name = "Jürgen's Bericht; 100% 日本.png";
        await user.CreateFileAsync(name, Png(4, 4));

        using var download = await user.GetAsync(user.Url(name, "files") + "?download=1");

        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        var header = download.Content.Headers.GetValues("Content-Disposition").Single();
        Assert.StartsWith("attachment; ", header, StringComparison.Ordinal);
        Assert.True(header.All(c => c is >= ' ' and <= '~'), header);
        var disposition = download.Content.Headers.ContentDisposition!;
        Assert.Equal(name, disposition.FileNameStar);
        // Accents dropped, other non-ASCII and '%' (which some browsers decode) left out of the plain name.
        Assert.Equal("Jurgen's Bericht; 100 .png", disposition.FileName);

        // A double quote or backslash can't end the quoted name early: they are left out of it, and escaped in filename*.
        var odd = FileApi.Disposition("a\"b\\c.png", download: false);
        Assert.Equal("inline; filename=\"abc.png\"; filename*=UTF-8''a%22b%5Cc.png", odd);
        Assert.Equal("inline; filename=\"file.png\"; filename*=UTF-8''%E6%97%A5.png", FileApi.Disposition("日.png", download: false));
    }

    [Fact]
    public async Task An_earlier_version_is_read_by_its_token_and_a_trashed_one_by_a_session()
    {
        var user = await UserAsync();
        var one = Png(4, 4);
        var first = await user.CreateFileAsync("shot.png", one);
        using var second = await user.PutFileAsync("shot.png", Png(8, 8), first);

        using var old = await user.GetAsync(user.Url("shot.png", "files") + $"?version={first}");
        Assert.Equal(HttpStatusCode.OK, old.StatusCode);
        Assert.Equal(one, await old.Content.ReadAsByteArrayAsync(Ct));
        using var unknown = await user.GetAsync(user.Url("shot.png", "files") + "?version=000000000000");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);

        using var history = await user.GetAsync(user.Url("shot.png", "history"));
        var entries = (await JsonAsync(history)).AsArray();
        Assert.Equal(2, entries.Count);
        Assert.All(entries, e => Assert.Equal("image", e!["kind"]!.GetValue<string>()));
        Assert.All(entries, e => Assert.Equal("image/png", e!["mediaType"]!.GetValue<string>()));

        using var removed = await user.SendAsync(HttpMethod.Delete, user.Url("shot.png", "files"));
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        using var gone = await user.GetAsync(user.Url("shot.png", "files"));
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
        using var trashed = await user.GetAsync(user.Url("shot.png", "files") + $"?version={first}");
        Assert.Equal(HttpStatusCode.OK, trashed.StatusCode);

        using var trash = await user.GetAsync($"/api/vaults/{user.Vault}/trash");
        var entry = Assert.Single((await JsonAsync(trash))["notes"]!.AsArray());
        Assert.Equal("/Arbeit/shot.png", entry!["path"]!.GetValue<string>());
        Assert.Equal("image", entry["kind"]!.GetValue<string>());
        Assert.Equal("image/png", entry["mediaType"]!.GetValue<string>());

        using var restored = await user.SendAsync(HttpMethod.Post, user.Url("shot.png", "restore"), new { });
        Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
        Assert.Equal(8, (await JsonAsync(restored))["width"]!.GetValue<int>());
        using var back = await user.SendAsync(HttpMethod.Post, user.Url("shot.png", "restore"), new { version = first });
        Assert.Equal(HttpStatusCode.OK, back.StatusCode);
        Assert.Equal(4, (await JsonAsync(back))["width"]!.GetValue<int>());

        using var version = await user.GetAsync(user.Url("shot.png", "version"));
        var state = await JsonAsync(version);
        Assert.Equal(first, state["version"]!.GetValue<string>());
        Assert.Equal("image", state["kind"]!.GetValue<string>());
        Assert.Equal("image/png", state["mediaType"]!.GetValue<string>());
        Assert.Equal(one.Length, state["size"]!.GetValue<long>());
        // The dimensions of the version restored last, as the lightbox's info shows them.
        Assert.Equal(4, state["width"]!.GetValue<int>());
        Assert.Equal(4, state["height"]!.GetValue<int>());
    }

    [Fact]
    public async Task An_attachment_moves_through_the_real_store_and_its_links_follow()
    {
        var user = await UserAsync();
        await user.CreateFileAsync("shot.png", Png(4, 4));
        await user.CreateAsync("plan.md", "# Plan\n\n![Shot](/shot.png)\n");

        using var backlinks = await user.GetAsync(user.Url("shot.png", "backlinks"));
        Assert.Equal(["/Arbeit/plan.md"], (await JsonAsync(backlinks)).AsArray().Select(b => b!["path"]!.GetValue<string>()));
        using var links = await user.GetAsync(user.Url("shot.png", "links"));
        Assert.Empty((await JsonAsync(links)).AsArray());

        using var moved = await user.SendAsync(HttpMethod.Post, "/api/move", new { from = "/Arbeit/shot.png", to = "/Arbeit/img/" });

        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
        Assert.Equal("/Arbeit/img/shot.png", (await JsonAsync(moved))["path"]!.GetValue<string>());
        using var old = await user.GetAsync(user.Url("shot.png", "files"));
        Assert.Equal(HttpStatusCode.NotFound, old.StatusCode);
        Assert.Contains("img/shot.png", await RestClient.ErrorAsync(old), StringComparison.Ordinal);
        using var now = await user.GetAsync(user.Url("img/shot.png", "files"));
        Assert.Equal(HttpStatusCode.OK, now.StatusCode);
        Assert.Contains("(/img/shot.png)", (await user.ReadAsync("plan.md"))["content"]!.GetValue<string>(), StringComparison.Ordinal);

        using var folder = await user.SendAsync(HttpMethod.Post, "/api/move", new { from = "/Arbeit/img/", to = "/Arbeit/pictures/" });
        Assert.Equal(HttpStatusCode.OK, folder.StatusCode);
        using var inFolder = await user.GetAsync(user.Url("pictures/shot.png", "files"));
        Assert.Equal(HttpStatusCode.OK, inFolder.StatusCode);
    }

    [Fact]
    public async Task A_move_that_changes_the_files_format_by_name_is_a_400()
    {
        var user = await UserAsync();
        await user.CreateFileAsync("shot.png", Png(4, 4));

        using var refused = await user.SendAsync(HttpMethod.Post, "/api/move", new { from = "/Arbeit/shot.png", to = "/Arbeit/shot.jpg" });

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal(
            "shot.png is a PNG image; its new name must end in .png (a new name doesn't change what the file is).", await RestClient.ErrorAsync(refused));
        using var kept = await user.GetAsync(user.Url("shot.png", "files"));
        Assert.Equal("image/png", kept.Content.Headers.ContentType!.MediaType);
        using var none = await user.GetAsync(user.Url("shot.jpg", "files"));
        Assert.Equal(HttpStatusCode.NotFound, none.StatusCode);

        // Into a folder, the name stays: allowed.
        using var moved = await user.SendAsync(HttpMethod.Post, "/api/move", new { from = "/Arbeit/shot.png", to = "/Arbeit/img/" });
        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
    }

    [Fact]
    public async Task Activity_and_checkpoints_include_attachments()
    {
        var user = await UserAsync();
        var bytes = Png(4, 4);
        await user.CreateFileAsync("shot.png", bytes);
        await user.CreateAsync("plan.md", "# Plan\n");

        using var activity = await user.GetAsync($"/api/vaults/{user.Vault}/activity");
        var files = (await JsonAsync(activity))["rows"]!.AsArray().SelectMany(r => r!["files"]!.AsArray()).ToList();
        var shot = Assert.Single(files, f => f!["path"]!.GetValue<string>() == "/Arbeit/shot.png");
        Assert.Equal("added", shot!["kind"]!.GetValue<string>());
        Assert.Equal("image", shot["fileKind"]!.GetValue<string>());
        Assert.Equal("note", files.Single(f => f!["path"]!.GetValue<string>() == "/Arbeit/plan.md")!["fileKind"]!.GetValue<string>());

        using var made = await user.SendAsync(HttpMethod.Post, $"/api/vaults/{user.Vault}/checkpoints", new { name = "before" });
        Assert.Equal(HttpStatusCode.Created, made.StatusCode);
        Assert.Equal(1, (await JsonAsync(made))["attachments"]!.GetValue<int>());
        using var removed = await user.SendAsync(HttpMethod.Delete, user.Url("shot.png", "files"));

        using var tree = await user.GetAsync($"/api/vaults/{user.Vault}/checkpoints/before/tree");
        var entries = (await JsonAsync(tree))["root"]!["notes"]!.AsArray();
        var entry = Assert.Single(entries, e => e!["path"]!.GetValue<string>() == "/Arbeit/shot.png");
        Assert.Equal("image", entry!["kind"]!.GetValue<string>());
        Assert.Equal("image/png", entry["mediaType"]!.GetValue<string>());
        using var held = await user.GetAsync($"/api/vaults/{user.Vault}/checkpoints/before/files/shot.png");
        Assert.Equal(HttpStatusCode.OK, held.StatusCode);
        Assert.Equal(bytes, await held.Content.ReadAsByteArrayAsync(Ct));
        Assert.Equal("image/png", held.Content.Headers.ContentType!.MediaType);
        Assert.Equal(Security.FileContentPolicy, held.Headers.GetValues("Content-Security-Policy").Single());

        using var preview = await user.SendAsync(HttpMethod.Post, $"/api/vaults/{user.Vault}/checkpoints/before/preview");
        var change = Assert.Single((await JsonAsync(preview))["changes"]!.AsArray());
        Assert.Equal("back", change!["kind"]!.GetValue<string>());
        Assert.Equal("image", change["fileKind"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_file_whose_bytes_are_gone_is_a_500_not_a_404()
    {
        var user = await UserAsync();
        await user.CreateFileAsync("shot.png", Png(4, 4));
        // Removed outside Kumbu²: the record stays, its blob doesn't.
        var vault = (await Api.Services.GetRequiredService<VaultCatalog>().OpenForUserAsync(user.User.Id, user.UserKey, Ct)).Single(v => v.Name == user.Vault);
        await Api.Services.GetRequiredService<IMongoDatabase>().GetCollection<BsonDocument>("blobs").DeleteManyAsync(Builders<BsonDocument>.Filter.Eq("vault", vault.Id), Ct);

        using var get = await user.GetAsync(user.Url("shot.png", "files"));

        // A client takes a 404 for a deletion: this file exists, its content is what is missing.
        Assert.Equal(HttpStatusCode.InternalServerError, get.StatusCode);
        Assert.Contains("The content of /Arbeit/shot.png doesn't open", await RestClient.ErrorAsync(get), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_not_modified_answer_gives_its_read_slot_back()
    {
        var user = await UserAsync();
        await user.CreateFileAsync("shot.png", Png(4, 4));
        using var first = await user.GetAsync(user.Url("shot.png", "files"));
        var tag = first.Headers.ETag!.Tag;

        // More than the server's slots (4 by default): a 304 that kept its slot would make the last read wait for good.
        for (var i = 0; i < 6; i++)
        {
            using var unchanged = await user.SendAsync(HttpMethod.Get, user.Url("shot.png", "files"), headers: new Dictionary<string, string> { ["If-None-Match"] = tag });
            Assert.Equal(HttpStatusCode.NotModified, unchanged.StatusCode);
        }

        using var again = await user.GetAsync(user.Url("shot.png", "files")).WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
    }

    [Fact]
    public async Task A_checkpoint_restore_whose_files_content_is_gone_is_a_409_and_writes_nothing()
    {
        var user = await UserAsync();
        await user.CreateFileAsync("shot.png", Png(4, 4));
        using var made = await user.SendAsync(HttpMethod.Post, $"/api/vaults/{user.Vault}/checkpoints", new { name = "before" });
        using var removed = await user.SendAsync(HttpMethod.Delete, user.Url("shot.png", "files") + "?version=" + Kumbu.Core.Notes.VersionToken.Of(Png(4, 4)));
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        using var preview = await user.SendAsync(HttpMethod.Post, $"/api/vaults/{user.Vault}/checkpoints/before/preview");
        var plan = (await JsonAsync(preview))["plan"]!.GetValue<string>();
        // Gone between the preview and the restore.
        var vault = (await Api.Services.GetRequiredService<VaultCatalog>().OpenForUserAsync(user.User.Id, user.UserKey, Ct)).Single(v => v.Name == user.Vault);
        await Api.Services.GetRequiredService<IMongoDatabase>().GetCollection<BsonDocument>("blobs").DeleteManyAsync(Builders<BsonDocument>.Filter.Eq("vault", vault.Id), Ct);

        using var restore = await user.SendAsync(HttpMethod.Post, $"/api/vaults/{user.Vault}/checkpoints/before/restore", new { plan });

        Assert.Equal(HttpStatusCode.Conflict, restore.StatusCode);
        Assert.Equal("The content of /Arbeit/shot.png is no longer stored (it was removed meanwhile); nothing was written. Try again.", await RestClient.ErrorAsync(restore));
        using var none = await user.GetAsync(user.Url("shot.png", "files"));
        Assert.Equal(HttpStatusCode.NotFound, none.StatusCode);
    }
}
