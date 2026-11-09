// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using Kumbu.Core.Attachments;

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

    [Theory, InlineData("/dav/Work/run.py"), InlineData("/dav/Work/_skills/x/notes")]
    public async Task Put_outside_or_of_an_unlisted_name_is_415(string path)
    {
        var dav = await DavClient.NewAsync(app.PublicApi);
        await SkillFolderAsync(dav);
        using var put = await dav.SendAsync(HttpMethod.Put, path, new ByteArrayContent(Script));
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, put.StatusCode);
        Assert.Contains(AttachmentFormat.UnsupportedMessage, await put.Content.ReadAsStringAsync(Ct));
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

    [Fact]
    public async Task Move_of_a_folder_with_a_script_out_of__skills_is_400_naming_it()
    {
        var dav = await DavClient.NewAsync(app.PublicApi);
        await SkillFolderAsync(dav);
        using var made = await dav.SendAsync(new HttpMethod("MKCOL"), "/dav/Work/_skills/x/scripts/");
        (await dav.SendAsync(HttpMethod.Put, "/dav/Work/_skills/x/scripts/run.py", new ByteArrayContent(Script))).Dispose();
        using var move = await dav.SendAsync(DavClient.Move, "/dav/Work/_skills/x/", headers: new Dictionary<string, string> { ["Destination"] = "https://localhost/dav/Work/x/" });
        Assert.Equal(HttpStatusCode.BadRequest, move.StatusCode);
        Assert.Contains("scripts/run.py", await move.Content.ReadAsStringAsync(Ct));
        using var still = await dav.SendAsync(HttpMethod.Get, "/dav/Work/_skills/x/scripts/run.py");
        Assert.Equal(HttpStatusCode.OK, still.StatusCode);
    }

    [Fact]
    public async Task Copy_of_a_script_out_of__skills_is_refused()
    {
        var dav = await DavClient.NewAsync(app.PublicApi);
        await SkillFolderAsync(dav);
        (await dav.SendAsync(HttpMethod.Put, "/dav/Work/_skills/x/run.py", new ByteArrayContent(Script))).Dispose();
        using var copy = await dav.SendAsync(DavClient.Copy, "/dav/Work/_skills/x/run.py", headers: new Dictionary<string, string> { ["Destination"] = "https://localhost/dav/Work/run.py" });
        Assert.True(copy.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.UnsupportedMediaType, copy.StatusCode.ToString());
        using var none = await dav.SendAsync(HttpMethod.Get, "/dav/Work/run.py");
        Assert.Equal(HttpStatusCode.NotFound, none.StatusCode);
    }

    [Fact]
    public async Task Copy_of_a_folder_with_a_script_out_of__skills_is_refused_naming_it()
    {
        var dav = await DavClient.NewAsync(app.PublicApi);
        await SkillFolderAsync(dav);
        (await dav.SendAsync(HttpMethod.Put, "/dav/Work/_skills/x/run.py", new ByteArrayContent(Script))).Dispose();
        using var copy = await dav.SendAsync(DavClient.Copy, "/dav/Work/_skills/x/", headers: new Dictionary<string, string> { ["Destination"] = "https://localhost/dav/Work/y/" });
        Assert.Equal(HttpStatusCode.BadRequest, copy.StatusCode);
        Assert.Contains("run.py", await copy.Content.ReadAsStringAsync(Ct));
        using var none = await dav.SendAsync(HttpMethod.Get, "/dav/Work/y/run.py");
        Assert.Equal(HttpStatusCode.NotFound, none.StatusCode);
    }
}
