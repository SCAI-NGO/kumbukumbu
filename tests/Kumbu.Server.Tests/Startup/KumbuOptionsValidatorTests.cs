// SPDX-License-Identifier: AGPL-3.0-or-later
using Microsoft.Extensions.Configuration;

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

    [Theory]
    [InlineData(4, false)]
    [InlineData(5, true)]
    [InlineData(60, true)]
    [InlineData(1440, true)]
    [InlineData(1441, false)]
    public void Access_token_minutes_are_validated(int minutes, bool valid)
    {
        var options = Options(KumbuAppFactory.Token, ValidMongo);
        options.OAuth.AccessTokenMinutes = minutes;

        var result = Validate(options);

        if (valid) Assert.False(result.Failed);
        else Assert.Equal(["Kumbu:OAuth:AccessTokenMinutes must be between 5 and 1440"], result.Failures);
    }

    [Theory]
    [InlineData("Production", false)]
    [InlineData("Development", true)]
    [InlineData("Staging", true)]
    public void Loopback_clients_are_refused_in_production(string environment, bool valid)
    {
        var options = Options(KumbuAppFactory.Token, ValidMongo);
        options.OAuth.AllowLoopbackClients = true;
        var host = new Microsoft.Extensions.Hosting.Internal.HostingEnvironment { EnvironmentName = environment };

        var result = new KumbuOptionsValidator(host).Validate(null, options);

        if (valid) Assert.False(result.Failed);
        else Assert.Equal([KumbuOptionsValidator.LoopbackClientsMessage], result.Failures);
        // Off, it is fine anywhere.
        options.OAuth.AllowLoopbackClients = false;
        Assert.False(new KumbuOptionsValidator(host).Validate(null, options).Failed);
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

    /// <summary>A key file of its own per test; the validator reads it.</summary>
    private static string KeyFile(string content = "sk-test-key")
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory("kumbu-key-").FullName, "key");
        File.WriteAllText(path, content);
        return path;
    }

    private static EmbeddingProviderOptions Provider(Action<EmbeddingProviderOptions>? change = null)
    {
        var provider = new EmbeddingProviderOptions
        {
            Driver = "openai-compatible", BaseUrl = "https://api.tensorx.ai/v1", Model = "qwen/qwen3-embedding-8b", ApiKeyFile = KeyFile(),
        };
        change?.Invoke(provider);
        return provider;
    }

    private static IReadOnlyList<string> ProviderFailures(EmbeddingProviderOptions provider, string name = "tensorx")
    {
        var options = Options(KumbuAppFactory.Token, ValidMongo);
        options.Embeddings.Providers[name] = provider;
        return [.. Validate(options).Failures ?? []];
    }

    [Fact]
    public void The_default_embedding_provider_is_internal_or_a_configured_one()
    {
        var options = Options(KumbuAppFactory.Token, ValidMongo);
        options.Embeddings.Default = "tensorx";
        Assert.Equal(
            "Kumbu:Embeddings:Default (KUMBU_EMBEDDINGS_DEFAULT) must be 'internal' or the name of a provider under Kumbu:Embeddings:Providers (there are none)",
            Assert.Single(Validate(options).Failures!));

        options.Embeddings.Providers["tensorx"] = Provider();
        Assert.True(Validate(options).Succeeded);
        options.Embeddings.Default = "openai";
        Assert.Contains("(tensorx)", Assert.Single(Validate(options).Failures!), StringComparison.Ordinal);
    }

    [Fact]
    public void Accepts_an_external_provider_over_https_with_a_key_file()
    {
        Assert.Empty(ProviderFailures(Provider(p => p.Dimensions = 1024)));
    }

    [Theory]
    [InlineData("http://localhost:11434/v1")]
    [InlineData("http://127.0.0.1:8000/v1")]
    [InlineData("http://[::1]:8000/v1")]
    public void A_provider_on_this_machine_may_use_http_and_no_key(string url)
    {
        Assert.Empty(ProviderFailures(Provider(p => (p.BaseUrl, p.ApiKeyFile) = (url, null))));
    }

    [Theory]
    [InlineData("http://api.tensorx.ai/v1", "must use https")]
    [InlineData("http://10.0.0.5/v1", "must use https")]
    [InlineData("ftp://api.tensorx.ai/v1", "absolute https URL")]
    [InlineData("api.tensorx.ai/v1", "absolute https URL")]
    [InlineData("https://user:pass@api.tensorx.ai/v1", "absolute https URL")]
    [InlineData("https://api.tensorx.ai/v1?key=abc", "absolute https URL")]
    public void Refuses_a_base_url_that_is_not_https_to_another_machine(string url, string message)
    {
        var failure = Assert.Single(ProviderFailures(Provider(p => p.BaseUrl = url)));
        Assert.Contains("Kumbu:Embeddings:Providers:tensorx:BaseUrl", failure);
        Assert.Contains(message, failure);
    }

    [Fact]
    public void Refuses_a_missing_unreadable_or_empty_key_file_without_repeating_its_content()
    {
        Assert.Contains("must name a file", Assert.Single(ProviderFailures(Provider(p => p.ApiKeyFile = null))));
        Assert.Contains("can't be read", Assert.Single(ProviderFailures(Provider(p => p.ApiKeyFile = "/nonexistent/key"))));
        Assert.Contains("is empty", Assert.Single(ProviderFailures(Provider(p => p.ApiKeyFile = KeyFile(" \n")))));

        var directory = Directory.CreateTempSubdirectory("kumbu-key-dir-").FullName;
        Assert.Contains("can't be read", Assert.Single(ProviderFailures(Provider(p => p.ApiKeyFile = directory))));
    }

    [Theory]
    [InlineData("internal", "internal model's name")]
    [InlineData("TensorX", "lowercase letters")]
    [InlineData("a b", "lowercase letters")]
    [InlineData("-x", "lowercase letters")]
    public void Refuses_names_that_are_not_simple_or_are_taken(string name, string message)
    {
        Assert.Contains(message, Assert.Single(ProviderFailures(Provider(), name)));
    }

    [Theory]
    [InlineData("onnx", "internal model only")]
    [InlineData("", "must be one of: openai-compatible")]
    [InlineData("cohere", "must be one of: openai-compatible")]
    public void Refuses_a_driver_that_is_not_an_external_protocol(string driver, string message)
    {
        Assert.Contains(message, Assert.Single(ProviderFailures(Provider(p => p.Driver = driver))));
    }

    [Fact]
    public void Refuses_a_missing_model_and_out_of_range_numbers()
    {
        var failures = ProviderFailures(Provider(p => (p.Model, p.Dimensions, p.MaxBatch, p.TimeoutSeconds) = ("", 0, 0, 0)));

        Assert.Equal(4, failures.Count);
        Assert.Contains(failures, f => f.Contains("Model must be set"));
    }

    [Fact]
    public void Providers_bind_from_configuration_as_the_environment_gives_them()
    {
        var configuration = new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Kumbu:Embeddings:Providers:tensorx:Driver"] = "openai-compatible",
            ["Kumbu:Embeddings:Providers:tensorx:BaseUrl"] = "https://api.tensorx.ai/v1",
            ["Kumbu:Embeddings:Providers:tensorx:Model"] = "qwen/qwen3-embedding-8b",
            ["Kumbu:Embeddings:Providers:tensorx:Dimensions"] = "1024",
            ["Kumbu:Embeddings:Providers:tensorx:Description"] = "EU, zero data retention",
        }).Build();
        var options = new KumbuOptions();
        Microsoft.Extensions.Configuration.ConfigurationBinder.Bind(configuration.GetSection("Kumbu"), options);

        var provider = Assert.Single(options.Embeddings.Providers);
        Assert.Equal(("tensorx", "qwen/qwen3-embedding-8b", 1024, "EU, zero data retention", 32), (provider.Key, provider.Value.Model, provider.Value.Dimensions, provider.Value.Description, provider.Value.MaxBatch));
    }
}
