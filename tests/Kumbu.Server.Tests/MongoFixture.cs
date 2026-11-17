// SPDX-License-Identifier: AGPL-3.0-or-later
using MongoDB.Driver;
using Testcontainers.MongoDb;

namespace Kumbu.Server.Tests;

public sealed class MongoFixture : IAsyncLifetime
{
    private readonly MongoDbContainer? _container = TestImages.ExternalMongo is null ? TestImages.MongoContainer() : null;

    public string ConnectionString => TestImages.ExternalMongo ?? _container!.GetConnectionString();

    /// <summary>A fresh database, dropped when the test that asked for it is done.</summary>
    public IMongoDatabase NewDatabase() => TestDatabases.Create(ConnectionString);

    public async ValueTask InitializeAsync()
    {
        if (_container is not null)
        {
            await _container.StartAsync();
        }
        else
        {
            await TestImages.EnsureReplicaSetAsync(ConnectionString, TestContext.Current.CancellationToken);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }
}

[CollectionDefinition("mongo")]
public sealed class MongoCollection : ICollectionFixture<MongoFixture>;
