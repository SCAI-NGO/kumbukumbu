// SPDX-License-Identifier: AGPL-3.0-or-later
using MongoDB.Driver;
using Testcontainers.MongoDb;

namespace Kumbu.Server.Tests;

public sealed class MongoFixture : IAsyncLifetime
{
    private readonly MongoDbContainer _container = new MongoDbBuilder("mongo:8").Build();

    public string ConnectionString => _container.GetConnectionString();

    public IMongoDatabase NewDatabase() =>
        new MongoClient(ConnectionString).GetDatabase("t" + Guid.NewGuid().ToString("N"));

    public async ValueTask InitializeAsync() => await _container.StartAsync();

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();
}

[CollectionDefinition("mongo")]
public sealed class MongoCollection : ICollectionFixture<MongoFixture>;
