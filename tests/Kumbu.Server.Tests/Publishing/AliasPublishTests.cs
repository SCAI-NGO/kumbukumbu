// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json;
using System.Text.RegularExpressions;
using Kumbu.Server.Accounts;
using Kumbu.Server.Publishing;
using Kumbu.Server.Sharing;
using Kumbu.Server.Tests.Api;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Kumbu.Server.Tests.Publishing;

/// <summary>A published note that links by an alias (aliases spec §1): the fair copy turns the links into their words
/// as before, and holds nothing of the note the aliases lead to.</summary>
[Collection("app")]
public sealed partial class AliasPublishTests(KumbuAppFactory app)
{
    private const string Diary = "/Work/Diary/2026-11-12.md";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private WebApplicationFactory<Program> Api => app.PublicApi;

    private PublicationStore Store => Api.Services.GetRequiredService<PublicationStore>();

    [GeneratedRegex(@"^link \(shown once\): https?://[^/]+/s#(?<code>([0-9A-HJKMNP-TV-Z]{4}-){7}[0-9A-HJKMNP-TV-Z]{4})$", RegexOptions.Multiline)]
    private static partial Regex LinkLine();

    [Fact]
    public async Task An_alias_link_in_a_published_note_stays_plain_text()
    {
        var alice = await RestClient.NewAsync(Api, vault: "Work");
        await alice.CreateAsync("Projects/Kumbukumbu.md", "---\naliases: [Kumbu, K²]\n---\n# Kumbukumbu\n## Roadmap\nx\n");
        await alice.CreateAsync("Diary/2026-11-12.md", "Talked with Bob about [[Kumbu]] and the [[K²#Roadmap|roadmap]].\nKumbu needs a calendar.\n");
        var key = await alice.KeyAsync("Claude", KeyRight.Write, mayPublish: true);

        var added = await alice.ShAsync($"publish add {Diary}", key);

        Assert.False(added.IsError, added.Text);
        var code = ShareCode.Parse(LinkLine().Match(added.Text).Groups["code"].Value)!;
        var copy = (await Store.OpenCopyAsync(code, Ct))!.Value.Copy;
        Assert.Equal("Talked with Bob about Kumbu and the roadmap.\nKumbu needs a calendar.\n", copy.Markdown);
        Assert.Empty(copy.Files);

        var whole = JsonSerializer.Serialize(copy) + added.Text;
        Assert.DoesNotContain("Projects", whole, StringComparison.Ordinal);
        Assert.DoesNotContain("Kumbukumbu", whole, StringComparison.Ordinal);
    }
}
