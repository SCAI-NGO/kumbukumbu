// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using Kumbu.Server.Tests.Api;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Kumbu.Server.Tests.Dav;

/// <summary>Files larger than 15 MB over WebDAV (files spec, decision 11): a <c>PUT</c> streamed to the store past the
/// 64 MiB every other request under <c>/dav</c> is held to, up to the bound; a <c>GET</c> streamed out; a <c>COPY</c>
/// and a <c>MOVE</c> that name the blob and read no bytes.</summary>
[Collection("app")]
public sealed class DavLargeFileTests(KumbuAppFactory app)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private WebApplicationFactory<Program> Api => app.PublicApi;

    private static async Task AssertGetAsync(DavClient dav, string path, byte[] bytes)
    {
        using var get = await dav.SendAsync(HttpMethod.Get, path);
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal(bytes.Length, get.Content.Headers.ContentLength);
        var read = await get.Content.ReadAsByteArrayAsync(Ct);
        Assert.True(bytes.AsSpan().SequenceEqual(read), $"{path} came back other than it went in");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_40_MB_put_is_stored_and_read_byte_for_byte(bool declare)
    {
        var dav = await DavClient.NewAsync(Api);
        var bytes = LargeFiles.Of(LargeFiles.Size, 21, FileApiTests.Pdf);

        using var put = await dav.SendAsync(HttpMethod.Put, "/dav/Work/big.pdf", new LargeFiles.Streamed(bytes, declare));

        Assert.True(put.StatusCode == HttpStatusCode.Created, await put.Content.ReadAsStringAsync(Ct));
        Assert.Equal($"\"{Kumbu.Core.Notes.VersionToken.Of(bytes)}\"", put.Headers.ETag?.Tag);
        await AssertGetAsync(dav, "/dav/Work/big.pdf", bytes);
    }

    [Fact]
    public async Task A_large_put_finds_a_skills_folder_by_the_mark_not_the_name()
    {
        var dav = await DavSkillTextTests.MarkedUnderAnotherNameAsync(app);
        var bytes = LargeFiles.Of(16 * 1024 * 1024, 23);

        using var unmarked = await dav.SendAsync(HttpMethod.Put, "/dav/Skills/x/big.bin", new LargeFiles.Streamed(bytes, true));
        using var marked = await dav.SendAsync(HttpMethod.Put, "/dav/Tools/x/big.bin", new LargeFiles.Streamed(bytes, true));

        Assert.True(unmarked.StatusCode == HttpStatusCode.Created, await unmarked.Content.ReadAsStringAsync(Ct));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, marked.StatusCode);
    }

    [Fact]
    public async Task A_put_larger_than_the_64_MiB_of_other_requests_is_stored()
    {
        var dav = await DavClient.NewAsync(Api);
        var bytes = LargeFiles.Of(65 * 1024 * 1024, 22);

        using var put = await dav.SendAsync(HttpMethod.Put, "/dav/Work/big.bin", new LargeFiles.Streamed(bytes, true));

        Assert.True(put.StatusCode == HttpStatusCode.Created, await put.Content.ReadAsStringAsync(Ct));
        await AssertGetAsync(dav, "/dav/Work/big.bin", bytes);
    }

    [Fact]
    public async Task A_large_put_over_the_bound_is_a_413_before_it_is_read()
    {
        await using var bounded = LargeFiles.Bounded(app);
        var dav = (await DavClient.NewAsync(Api)).On(bounded);

        using var put = await dav.SendAsync(HttpMethod.Put, "/dav/Work/big.bin", new LargeFiles.Withheld(LargeFiles.Bound + 1)).WaitAsync(TimeSpan.FromSeconds(30), Ct);
        using var chunked = await dav.SendAsync(HttpMethod.Put, "/dav/Work/big.bin", new LargeFiles.Streamed(LargeFiles.Of((int)LargeFiles.Bound + 1, 23), false));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, put.StatusCode);
        Assert.Equal("A file can have at most 20 MiB.", await put.Content.ReadAsStringAsync(Ct));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, chunked.StatusCode);
        Assert.Equal("A file can have at most 20 MiB.", await chunked.Content.ReadAsStringAsync(Ct));
        using var get = await dav.SendAsync(HttpMethod.Get, "/dav/Work/big.bin");
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
    }

    [Fact]
    public async Task A_large_pdf_without_its_magic_and_a_large_svg_are_refused()
    {
        var dav = await DavClient.NewAsync(Api);

        using var pdf = await dav.SendAsync(HttpMethod.Put, "/dav/Work/fake.pdf", new LargeFiles.Streamed(LargeFiles.Of(LargeFiles.Size, 24), true));
        using var svg = await dav.SendAsync(HttpMethod.Put, "/dav/Work/big.svg", new LargeFiles.Streamed(LargeFiles.Of(LargeFiles.Size, 25, "<svg"u8), false));

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, pdf.StatusCode);
        Assert.Contains("fake.pdf isn't a PDF document", await pdf.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, svg.StatusCode);
        Assert.Contains("an SVG image is read whole to be checked", await svg.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Copy_and_move_of_a_large_file_keep_its_bytes()
    {
        var dav = await DavClient.NewAsync(Api);
        var bytes = LargeFiles.Of(LargeFiles.Size, 26);
        using var put = await dav.SendAsync(HttpMethod.Put, "/dav/Work/big.bin", new LargeFiles.Streamed(bytes, true));
        Assert.Equal(HttpStatusCode.Created, put.StatusCode);

        using var copy = await dav.SendAsync(DavClient.Copy, "/dav/Work/big.bin", headers: new Dictionary<string, string> { ["Destination"] = "/dav/Work/copy.bin" });
        using var move = await dav.SendAsync(DavClient.Move, "/dav/Work/copy.bin", headers: new Dictionary<string, string> { ["Destination"] = "/dav/Work/moved.bin" });

        Assert.Equal(HttpStatusCode.Created, copy.StatusCode);
        Assert.Equal(HttpStatusCode.Created, move.StatusCode);
        await AssertGetAsync(dav, "/dav/Work/moved.bin", bytes);
        await AssertGetAsync(dav, "/dav/Work/big.bin", bytes);
    }
}
