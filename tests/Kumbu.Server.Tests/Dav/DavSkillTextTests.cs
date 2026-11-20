// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using Kumbu.Core.Attachments;
using Kumbu.Server.Tests.Api;

namespace Kumbu.Server.Tests.Dav;

/// <summary>Text files over WebDAV: kept inside a skill's folder, 415 anywhere else as before.</summary>
[Collection("app")]
public sealed class DavSkillTextTests(KumbuAppFactory app)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly byte[] Script = "print(1)\n"u8.ToArray();

    private static async Task SkillFolderAsync(DavClient dav)
    {
        foreach (var folder in new[] { "/dav/Work/_skills/", "/dav/Work/_skills/x/" })
        {
            using var made = await dav.SendAsync(new HttpMethod("MKCOL"), folder);
        }
    }

    [Fact]
    public async Task Put_of_a_script_into__skills_is_201_and_reads_back()
    {
        var dav = await DavClient.NewAsync(app.PublicApi);
        await SkillFolderAsync(dav);
        using var put = await dav.SendAsync(HttpMethod.Put, "/dav/Work/_skills/x/run.py", new ByteArrayContent(Script));
        Assert.Equal(HttpStatusCode.Created, put.StatusCode);
        using var get = await dav.SendAsync(HttpMethod.Get, "/dav/Work/_skills/x/run.py");
        Assert.Equal(Script, await get.Content.ReadAsByteArrayAsync(Ct));
        Assert.Equal("text/plain; charset=utf-8", get.Content.Headers.ContentType!.ToString());
    }

    [Fact]
    public async Task Put_of_LICENSE_into__skills_is_201()
    {
        var dav = await DavClient.NewAsync(app.PublicApi);
        await SkillFolderAsync(dav);
        using var put = await dav.SendAsync(HttpMethod.Put, "/dav/Work/_skills/x/LICENSE", new ByteArrayContent("MIT\n"u8.ToArray()));
        Assert.Equal(HttpStatusCode.Created, put.StatusCode);
    }

    [Fact]
    public async Task Put_of_an_unlisted_name_in_a_skill_is_415_and_outside_one_a_file()
    {
        var dav = await DavClient.NewAsync(app.PublicApi);
        await SkillFolderAsync(dav);
        using var put = await dav.SendAsync(HttpMethod.Put, "/dav/Work/_skills/x/notes", new ByteArrayContent(Script));
        using var outside = await dav.SendAsync(HttpMethod.Put, "/dav/Work/run.py", new ByteArrayContent(Script));
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, put.StatusCode);
        Assert.Contains(AttachmentFormat.SkillFolderMessage, await put.Content.ReadAsStringAsync(Ct));
        Assert.Equal(HttpStatusCode.Created, outside.StatusCode);
    }

    /// <summary>A share of a skills vault marked under the name Tools and of a vault named Skills that isn't marked,
    /// each with a folder x.</summary>
    internal static async Task<DavClient> MarkedUnderAnotherNameAsync(KumbuAppFactory app)
    {
        var owner = await RestClient.MarkedUnderAnotherNameAsync(app.PublicApi);
        var dav = await DavClient.NewAsync(app.PublicApi, owner, vaults: new Dictionary<string, string> { ["Tools"] = "write", ["Skills"] = "write" });
        foreach (var folder in new[] { "/dav/Tools/x/", "/dav/Skills/x/" })
        {
            using var made = await dav.SendAsync(new HttpMethod("MKCOL"), folder);
            Assert.Equal(HttpStatusCode.Created, made.StatusCode);
        }

        return dav;
    }

    [Fact]
    public async Task Put_finds_a_skills_folder_by_the_mark_not_the_name()
    {
        var dav = await MarkedUnderAnotherNameAsync(app);
        using var marked = await dav.SendAsync(HttpMethod.Put, "/dav/Tools/x/notes", new ByteArrayContent(Script));
        using var unmarked = await dav.SendAsync(HttpMethod.Put, "/dav/Skills/x/notes", new ByteArrayContent(Script));
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, marked.StatusCode);
        Assert.Contains(AttachmentFormat.SkillFolderMessage, await marked.Content.ReadAsStringAsync(Ct));
        Assert.Equal(HttpStatusCode.Created, unmarked.StatusCode);
    }

    [Fact]
    public async Task Propfind_lists_the_script_as_text_plain()
    {
        var dav = await DavClient.NewAsync(app.PublicApi);
        await SkillFolderAsync(dav);
        (await dav.SendAsync(HttpMethod.Put, "/dav/Work/_skills/x/run.py", new ByteArrayContent(Script))).Dispose();
        using var found = await dav.PropfindAsync("/dav/Work/_skills/x/", "1");
        Assert.Contains("getcontenttype>text/plain; charset=utf-8<", await found.Content.ReadAsStringAsync(Ct));
    }

    // A text file leaves a skill's folder as a file, its media type kept (spec 2026-11-18).
    [Fact]
    public async Task Move_of_a_folder_with_a_script_out_of__skills_takes_it_along_as_a_file()
    {
        var dav = await DavClient.NewAsync(app.PublicApi);
        await SkillFolderAsync(dav);
        using var made = await dav.SendAsync(new HttpMethod("MKCOL"), "/dav/Work/_skills/x/scripts/");
        (await dav.SendAsync(HttpMethod.Put, "/dav/Work/_skills/x/scripts/run.py", new ByteArrayContent(Script))).Dispose();
        using var move = await dav.SendAsync(DavClient.Move, "/dav/Work/_skills/x/", headers: new Dictionary<string, string> { ["Destination"] = "https://localhost/dav/Work/x/" });
        Assert.Equal(HttpStatusCode.Created, move.StatusCode);
        await AssertTextFileAsync(dav, "/dav/Work/x/scripts/run.py");
    }

    [Fact]
    public async Task Copy_of_a_script_out_of__skills_is_a_text_file()
    {
        var dav = await DavClient.NewAsync(app.PublicApi);
        await SkillFolderAsync(dav);
        (await dav.SendAsync(HttpMethod.Put, "/dav/Work/_skills/x/run.py", new ByteArrayContent(Script))).Dispose();
        using var copy = await dav.SendAsync(DavClient.Copy, "/dav/Work/_skills/x/run.py", headers: new Dictionary<string, string> { ["Destination"] = "https://localhost/dav/Work/run.py" });
        Assert.Equal(HttpStatusCode.Created, copy.StatusCode);
        await AssertTextFileAsync(dav, "/dav/Work/run.py");
    }

    [Fact]
    public async Task Copy_of_a_folder_with_a_script_out_of__skills_keeps_it()
    {
        var dav = await DavClient.NewAsync(app.PublicApi);
        await SkillFolderAsync(dav);
        (await dav.SendAsync(HttpMethod.Put, "/dav/Work/_skills/x/run.py", new ByteArrayContent(Script))).Dispose();
        using var copy = await dav.SendAsync(DavClient.Copy, "/dav/Work/_skills/x/", headers: new Dictionary<string, string> { ["Destination"] = "https://localhost/dav/Work/y/" });
        Assert.Equal(HttpStatusCode.Created, copy.StatusCode);
        await AssertTextFileAsync(dav, "/dav/Work/y/run.py");
    }

    private static async Task AssertTextFileAsync(DavClient dav, string path)
    {
        using var got = await dav.SendAsync(HttpMethod.Get, path);
        Assert.Equal(HttpStatusCode.OK, got.StatusCode);
        Assert.Equal("text/plain; charset=utf-8", got.Content.Headers.ContentType!.ToString());
        Assert.Equal(Script, await got.Content.ReadAsByteArrayAsync(Ct));
    }
}
