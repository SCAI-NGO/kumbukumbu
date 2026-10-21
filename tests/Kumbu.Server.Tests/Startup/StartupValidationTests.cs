// SPDX-License-Identifier: AGPL-3.0-or-later
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Options;

namespace Kumbu.Server.Tests.Startup;

/// <summary>Spec §2: the server refuses to start without a usable dev token or a Mongo connection string.</summary>
public sealed class StartupValidationTests
{
    private const string TokenMessage =
        "Kumbu:DevToken must be at least 16 characters (generate one with: openssl rand -hex 32)";

    private const string MongoMessage = "Kumbu:Mongo:ConnectionString must be set";

    // Validation runs before any hosted service, so no MongoDB is needed: nothing ever connects to this.
    private const string UnusedMongo = "mongodb://localhost:1";

    private sealed class Factory(string token, string connectionString) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Kumbu:Mongo:ConnectionString", connectionString);
            builder.UseSetting("Kumbu:DevToken", token);
            builder.UseSetting("Kumbu:Seed:Path", "");
        }
    }

    private static OptionsValidationException StartFails(string token, string connectionString)
    {
        using var factory = new Factory(token, connectionString);
        return Assert.Throws<OptionsValidationException>(() => factory.Server);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("short-token")]
    [InlineData("0123456789abcde")]
    [InlineData("change-me")]
    public void Refuses_to_start_with_an_empty_short_or_placeholder_token(string token)
    {
        var ex = StartFails(token, UnusedMongo);

        Assert.Contains(TokenMessage, ex.Failures);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void Refuses_to_start_without_a_mongo_connection_string(string connectionString)
    {
        var ex = StartFails(KumbuAppFactory.Token, connectionString);

        Assert.Contains(MongoMessage, ex.Failures);
    }

    [Fact]
    public void Starts_with_a_16_character_token()
    {
        using var factory = new Factory("0123456789abcdef", UnusedMongo);

        Assert.NotNull(factory.Server);
    }
}
