// SPDX-License-Identifier: AGPL-3.0-or-later
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using Testcontainers.MongoDb;

namespace Kumbu.Server.Tests;

/// <summary>
/// The real server in memory: Program.cs wiring, a MongoDB container, and the repo's samples/ seeded.
/// </summary>
public sealed class KumbuAppFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string Token = "test-token-0123456789abcdef";

    private readonly MongoDbContainer _mongo = TestImages.MongoContainer();

    public static string SamplesPath { get; } = Path.Combine(FindRepoRoot(), "samples");

    public string RecoveryPath { get; } = Directory.CreateTempSubdirectory("kumbu-app-recovery-").FullName;

    public string DatabaseName { get; } = "t" + Guid.NewGuid().ToString("N");

    /// <summary>The app's database, for tests that change it behind the server's back.</summary>
    public MongoDB.Driver.IMongoDatabase Database => new MongoDB.Driver.MongoClient(_mongo.GetConnectionString()).GetDatabase(DatabaseName);

    public async ValueTask InitializeAsync() => await _mongo.StartAsync();

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await _mongo.DisposeAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Kumbu:Mongo:ConnectionString", _mongo.GetConnectionString());
        builder.UseSetting("Kumbu:Mongo:Database", DatabaseName);
        builder.UseSetting("Kumbu:DevToken", Token);
        builder.UseSetting("Kumbu:Seed:Path", SamplesPath);
        builder.UseSetting("Kumbu:Recovery:Path", RecoveryPath);
        // No download in tests: words hashed into vectors stand in for the model.
        builder.UseSetting("Kumbu:Search:Enabled", "false");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<Kumbu.Server.Search.IEmbedder>();
            services.AddSingleton<Kumbu.Server.Search.IEmbedder>(Embedder);
        });
    }

    public Search.FakeEmbedder Embedder { get; } = new();

    /// <summary>The origin <see cref="PublicApi"/> is reached at.</summary>
    public const string PublicUrl = "https://kumbu.test";

    private WebApplicationFactory<Program>? _publicApi;

    /// <summary>
    /// The app as a public instance: <c>Kumbu:PublicUrl</c> set, the client IP taken from <c>X-Forwarded-For</c>
    /// (so each test can count against an address of its own) and a cheap Argon2. Built once and shared, so its
    /// in-memory rate limits are shared too.
    /// </summary>
    public WebApplicationFactory<Program> PublicApi => _publicApi ??= WithWebHostBuilder(b =>
    {
        b.UseSetting("Kumbu:PublicUrl", PublicUrl);
        b.UseSetting("Kumbu:Proxy:Trusted", "true");
        b.ConfigureServices(services =>
        {
            services.RemoveAll<Kumbu.Server.Accounts.Argon2Kdf>();
            services.AddSingleton(new Kumbu.Server.Accounts.Argon2Kdf(new Kumbu.Server.Accounts.Argon2Parameters(MemoryKiB: 1024, Iterations: 1, Parallelism: 1)));
        });
    });

    /// <summary>Waits until the notes written so far are indexed.</summary>
    public Task IndexedAsync() => Services.GetRequiredService<Kumbu.Server.Search.SearchIndex>().IdleAsync(TimeSpan.FromSeconds(10), CancellationToken.None);

    /// <summary>Connects the official SDK client over the in-memory test server, with a bearer token, a
    /// session cookie (the secret from <c>Sessions.StartAsync</c>), both or neither.</summary>
    public async Task<McpClient> ConnectAsync(string? token = Token, string? session = null)
    {
        var headers = new Dictionary<string, string>();
        if (token is not null)
        {
            headers["Authorization"] = $"Bearer {token}";
        }

        if (session is not null)
        {
            headers["Cookie"] = $"{Kumbu.Server.Auth.CallerMiddleware.SessionCookie}={session}";
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
