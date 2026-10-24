// SPDX-License-Identifier: AGPL-3.0-or-later
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging;

namespace Kumbu.Server.Tests.Startup;

/// <summary>
/// Spec §2: the server refuses to start with an unusable dev token (it may be absent) or without a Mongo connection string.
/// The rules and messages are tested on <see cref="KumbuOptionsValidator"/> directly; these only prove
/// the host enforces them. A failed start races host disposal under WebApplicationFactory, so the
/// surfaced exception type varies (OptionsValidationException or ObjectDisposedException): ThrowsAny.
/// </summary>
public sealed class StartupValidationTests
{
    // Validation runs before any hosted service, so no MongoDB is needed: nothing ever connects to this.
    private const string UnusedMongo = "mongodb://localhost:1";

    private sealed class Factory(string token, string connectionString) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Kumbu:Mongo:ConnectionString", connectionString);
            builder.UseSetting("Kumbu:DevToken", token);
            builder.UseSetting("Kumbu:Seed:Path", "");
            builder.ConfigureLogging(l => l.ClearProviders());
        }
    }

    [Fact]
    public void Refuses_to_start_with_a_short_token()
    {
        using var factory = new Factory("0123456789abcde", UnusedMongo);

        Assert.ThrowsAny<Exception>(() => factory.Server);
    }

    // The dev token is optional (accounts design: off on a public instance).
    [Fact]
    public void Starts_without_a_token()
    {
        using var factory = new Factory("", UnusedMongo);

        Assert.NotNull(factory.Server);
    }

    [Fact]
    public void Refuses_to_start_without_a_mongo_connection_string()
    {
        using var factory = new Factory(KumbuAppFactory.Token, "");

        Assert.ThrowsAny<Exception>(() => factory.Server);
    }

    [Fact]
    public void Starts_with_a_16_character_token()
    {
        using var factory = new Factory("0123456789abcdef", UnusedMongo);

        Assert.NotNull(factory.Server);
    }
}
