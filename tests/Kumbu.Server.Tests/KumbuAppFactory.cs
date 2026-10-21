// SPDX-License-Identifier: AGPL-3.0-or-later
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using Testcontainers.MongoDb;

namespace Kumbu.Server.Tests;

/// <summary>
/// The real server in memory: Program.cs wiring, a MongoDB container, and the repo's samples/ seeded.
/// </summary>
public sealed class KumbuAppFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string Token = "test-token";

    private readonly MongoDbContainer _mongo = new MongoDbBuilder("mongo:8").Build();

    public static string SamplesPath { get; } = Path.Combine(FindRepoRoot(), "samples");

    public async ValueTask InitializeAsync() => await _mongo.StartAsync();

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await _mongo.DisposeAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Kumbu:Mongo:ConnectionString", _mongo.GetConnectionString());
        builder.UseSetting("Kumbu:Mongo:Database", "t" + Guid.NewGuid().ToString("N"));
        builder.UseSetting("Kumbu:DevToken", Token);
        builder.UseSetting("Kumbu:Seed:Path", SamplesPath);
    }

    /// <summary>Connects the official SDK client over the in-memory test server.</summary>
    public async Task<McpClient> ConnectAsync(string? token = Token)
    {
        var headers = new Dictionary<string, string>();
        if (token is not null)
        {
            headers["Authorization"] = $"Bearer {token}";
        }

        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri(Server.BaseAddress, "mcp"),
                AdditionalHeaders = headers,
            },
            CreateClient(),
            NullLoggerFactory.Instance,
            ownsHttpClient: false);

        return await McpClient.CreateAsync(transport, cancellationToken: TestContext.Current.CancellationToken);
    }

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Kumbu.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Could not find Kumbu.slnx above " + AppContext.BaseDirectory);
    }
}

[CollectionDefinition("app")]
public sealed class AppCollection : ICollectionFixture<KumbuAppFactory>;
