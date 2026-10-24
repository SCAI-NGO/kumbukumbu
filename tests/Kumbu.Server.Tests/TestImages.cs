// SPDX-License-Identifier: AGPL-3.0-or-later
using Docker.DotNet.Models;
using Testcontainers.MongoDb;

namespace Kumbu.Server.Tests;

internal static class TestImages
{
    // MongoDB 8.x exits on startup on Linux kernels 6.19–7.0.13 (SERVER-121912 / SERVER-125742),
    // which includes current GitHub runners. 7.0 runs on every kernel and covers what the tests use.
    public const string Mongo = "mongo:7.0";

    /// <summary>
    /// A replica-set MongoDB for tests. Every test gets its own database, and each collection and index is a
    /// WiredTiger file kept open: a full run passes 2000 of them, and at the Docker default soft limit of 2048
    /// open files WiredTiger panics and mongod exits mid-run. 64000 is MongoDB's recommended limit.
    /// </summary>
    /// <remarks>Testcontainers fills <c>HostConfig</c> (ports, labels) before the modifier runs, hence the <c>!</c>.</remarks>
    public static MongoDbContainer MongoContainer() => new MongoDbBuilder(Mongo)
        .WithReplicaSet()
        .WithCreateParameterModifier(p => p.HostConfig!.Ulimits = [new Ulimit { Name = "nofile", Soft = 64000, Hard = 64000 }])
        .Build();
}
