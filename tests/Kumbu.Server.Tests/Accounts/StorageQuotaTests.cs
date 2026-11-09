// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using Kumbu.Server.Accounts;
using Kumbu.Server.Tests.Api;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Kumbu.Server.Tests.Accounts;

/// <summary>The quota's sizes and its default (WebDAV design §3.10): a server whose admin set none behaves as before.</summary>
[Collection("app")]
public sealed class StorageQuotaTests(KumbuAppFactory app)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private WebApplicationFactory<Program> Api => app.PublicApi;

    [Theory]
    [InlineData("2GB", 2147483648L)]
    [InlineData("500MB", 524288000L)]
    [InlineData("15 MB", 15728640L)]
    [InlineData("1.5GB", 1610612736L)]
    [InlineData("2gb", 2147483648L)]
    [InlineData(" 1 TB ", 1099511627776L)]
    [InlineData("512KB", 524288L)]
    [InlineData("100B", 100L)]
    [InlineData("4096", 4096L)]
    [InlineData("0", null)]
    [InlineData("0MB", null)]
    [InlineData("", null)]
    [InlineData("  ", null)]
    [InlineData(null, null)]
    public void Parse_reads_sizes_with_binary_prefixes(string? text, long? bytes) => Assert.Equal(bytes, QuotaSize.Parse(text));

    [Theory]
    [InlineData("lots")]
    [InlineData("-1GB")]
    [InlineData("2 PB")]
    [InlineData("GB")]
    [InlineData("1,5GB")]
    [InlineData("2GB extra")]
    [InlineData("2 GiB")]
    [InlineData("99999999999TB")]
    public void Parse_refuses_nonsense(string text) => Assert.Throws<FormatException>(() => QuotaSize.Parse(text));

    [Fact]
    public void The_validator_refuses_an_unparsable_default()
    {
        var options = new KumbuOptions { Mongo = { ConnectionString = "mongodb://localhost" }, Quota = { Default = "lots" } };
        var result = new KumbuOptionsValidator().Validate(null, options);
        Assert.True(result.Failed);
        Assert.Contains(KumbuOptionsValidator.QuotaDefaultMessage, result.Failures!);
        options.Quota.Default = "2GB";
        Assert.True(new KumbuOptionsValidator().Validate(null, options).Succeeded);
    }

    /// <summary>No default and no override: every write goes through, and nothing is counted on the way.</summary>
    [Fact]
    public async Task An_unset_default_refuses_nothing()
    {
        var quota = Api.Services.GetRequiredService<StorageQuota>();
        var user = await RestClient.NewAsync(Api, vault: "Work");
        var big = new string('x', 1_000_000);
        for (var i = 0; i < 3; i++) await user.CreateAsync($"n{i}.md", big);
        await quota.SettledAsync();
        using (var file = await user.PutFileAsync("pic.png", FileApiTests.Png(2, 2))) Assert.Equal(HttpStatusCode.Created, file.StatusCode);

        await quota.SettledAsync();
        Assert.Null(await quota.LimitAsync(user.User.Id, Ct));
        Assert.Null(quota.KnownUsage(user.User.Id));
    }
}
