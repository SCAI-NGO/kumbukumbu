// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Kumbu.Server.Tests.Startup;

public sealed class KumbuOptionsValidatorTests
{
    private const string ValidMongo = "mongodb://localhost:27017";

    private static KumbuOptions Options(string? token, string connectionString) =>
        new() { DevToken = token, Mongo = new MongoOptions { ConnectionString = connectionString } };

    private static Microsoft.Extensions.Options.ValidateOptionsResult Validate(KumbuOptions o) =>
        new KumbuOptionsValidator().Validate(null, o);

    [Theory]
    [InlineData("   ")]
    [InlineData("short-token")]
    [InlineData("0123456789abcde")]
    [InlineData("change-me")]
    public void Rejects_a_blank_short_or_placeholder_token(string token)
    {
        var result = Validate(Options(token, ValidMongo));

        Assert.True(result.Failed);
        Assert.Equal([KumbuOptions.DevTokenMessage], result.Failures);
        Assert.Equal(
            "Kumbu:DevToken must be at least 16 characters (generate one with: openssl rand -hex 32)",
            KumbuOptions.DevTokenMessage);
    }

    [Fact]
    public void Rejects_a_negative_checkpoint_limit()
    {
        var options = Options(KumbuAppFactory.Token, ValidMongo);
        options.Vaults.CheckpointLimit = -1;

        Assert.Equal(["Kumbu:Vaults:CheckpointLimit must be 0 or more"], Validate(options).Failures);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void Rejects_an_empty_mongo_connection_string(string connectionString)
    {
        var result = Validate(Options(KumbuAppFactory.Token, connectionString));

        Assert.True(result.Failed);
        Assert.Equal(["Kumbu:Mongo:ConnectionString must be set"], result.Failures);
    }

    [Fact]
    public void UnknownSearchModelFails()
    {
        var options = Options(KumbuAppFactory.Token, ValidMongo);
        options.Search.Model = "gpt";

        var failure = Assert.Single(Validate(options).Failures!);
        Assert.Contains("arctic-m", failure);
        Assert.Contains("arctic-l", failure);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void Accepts_no_dev_token_at_all(string? token)
    {
        Assert.True(Validate(Options(token, ValidMongo)).Succeeded);
    }

    [Fact]
    public void Reports_both_failures_together()
    {
        var result = Validate(Options("short", ""));

        Assert.Equal([KumbuOptions.DevTokenMessage, KumbuOptionsValidator.MongoMessage], result.Failures);
    }

    [Theory]
    [InlineData("0123456789abcdef")]
    [InlineData(KumbuAppFactory.Token)]
    public void Accepts_a_token_of_at_least_16_characters(string token)
    {
        Assert.True(Validate(Options(token, ValidMongo)).Succeeded);
    }

    [Theory]
    [InlineData("kumbu.example.org")]
    [InlineData("/join")]
    [InlineData("ftp://kumbu.example.org")]
    [InlineData("https://kumbu.example.org/?x=1")]
    [InlineData("https://kumbu.example.org/kumbu")]
    [InlineData("https://user@kumbu.example.org")]
    public void Rejects_a_public_url_that_isnt_an_absolute_http_url(string url)
    {
        var options = Options(KumbuAppFactory.Token, ValidMongo);
        options.PublicUrl = url;

        Assert.Equal([KumbuOptionsValidator.PublicUrlMessage], Validate(options).Failures);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("https://kumbu.example.org")]
    [InlineData("http://localhost:8080/")]
    public void Accepts_no_public_url_or_an_absolute_http_url(string? url)
    {
        var options = Options(KumbuAppFactory.Token, ValidMongo);
        options.PublicUrl = url;

        Assert.True(Validate(options).Succeeded);
    }

    // Behind a proxy that terminates TLS the request's own scheme is http, so without a PublicUrl the Origin
    // check would refuse every state-changing request from the web app.
    [Fact]
    public void A_trusted_proxy_needs_a_public_url()
    {
        var options = Options(KumbuAppFactory.Token, ValidMongo);
        options.Proxy.Trusted = true;

        Assert.Equal([KumbuOptionsValidator.ProxyNeedsPublicUrlMessage], Validate(options).Failures);

        options.PublicUrl = "https://kumbu.example.org";
        Assert.True(Validate(options).Succeeded);
    }
}
